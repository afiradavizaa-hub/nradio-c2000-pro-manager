using System.IO;
using System.Text.Json.Nodes;
using NRadioManager.Core;

namespace NRadioManager.SelfTest;

/// <summary>
/// 真机集成自检：用与桌面软件完全相同的通信层访问真实设备，逐接口核对。
/// 用法：
///   SelfTest [host] [user] [password]              —— 只读验证（默认）
///   SelfTest [host] [user] [password] --write      —— 额外执行"原值回写"，验证写入链路
/// </summary>
internal static class Program
{
    private static int _pass;
    private static int _fail;

    private static async Task<int> Main(string[] args)
    {
        if (args.Any(a => a == "--offline"))
        {
            Console.WriteLine("=== C2000 Pro 离线协议自检 ===");
            var ok = await C2000OfflineTests.RunAsync();
            Console.WriteLine(ok ? "RESULT: OK" : "RESULT: FAIL");
            return ok ? 0 : 1;
        }

        if (args.Any(a => a == "--c2000"))
            return await RunC2000Async(args);

        if (args.Any(a => a is "--log-persistence-write" or "--log-persistence-read"))
            return RunLogPersistenceTest(args);

        var host = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "http://192.168.1.1";
        var user = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "admin";
        var pass = args.Length > 2 && !args[2].StartsWith("--")
            ? args[2]
            : Environment.GetEnvironmentVariable("NRADIO_TEST_PASSWORD") ?? "";
        var doWrite = args.Any(a => a == "--write");

        if (string.IsNullOrEmpty(pass))
        {
            Console.Error.WriteLine("FAIL  未提供测试密码。请通过 NRADIO_TEST_PASSWORD 环境变量注入，避免写入源码、命令行历史或报告。");
            return 2;
        }

        Console.WriteLine($"=== NRadio 真机自检 · {RouterClient.NormalizeHost(host)} · 写入测试={doWrite} ===");

        if (args.Any(a => a == "--network"))
        {
            var quality = await NetworkQualityMonitor.ProbeAsync(host);
            Console.WriteLine($"网络质量状态：{quality.State}");
            Console.WriteLine("  " + quality.Summary);
            Console.WriteLine("  " + quality.RouterText);
            Console.WriteLine("  " + quality.InternetText);
            Console.WriteLine("  " + quality.LatencyText);
            Console.WriteLine("  " + quality.LossText);
            Console.WriteLine($"=== 结果：{(quality.State == NetworkQualityState.Healthy ? "PASS" : "INFO（非 Healthy 不等于断网，详见以上分类）")} ===");
            return quality.State == NetworkQualityState.Healthy ? 0 : 3;
        }

        using var client = new RouterClient(host, user, pass);
        var api = new RouterApi(client);

        try
        {
            await client.LoginAsync();
            Console.WriteLine("PASS  Digest 登录");
            _pass++;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  Digest 登录：" + ex.Message);
            return 2;
        }

        // ---------- 只读接口逐项验证 ----------
        await Check("status_info", async () =>
        {
            var s = await api.GetStatusAsync(default);
            Console.WriteLine($"      信号档位={s.SignalQuality} RSSI={s.Rssi}dBm 在线={s.WifiClientsNum} " +
                              $"运行={J.Duration(J.Long(s.RunSeconds))} 电量={s.BatteryPercent}% " +
                              $"累计↓{J.Bytes(J.Long(s.RxByteAll))} ↑{J.Bytes(J.Long(s.TxByteAll))}");
        });

        await Check("homepage_info", async () =>
        {
            var h = await api.GetHomepageAsync(default);
            Console.WriteLine($"      设备={h.DeviceName} 运营商={h.NetworkName} IMEI={h.Imei} SSID={h.Ssid} " +
                              $"WAN={h.WanIp} LAN={h.LanIp}");
        });

        await Check("firmware_info", async () =>
        {
            var f = await api.GetFirmwareAsync(default);
            Console.WriteLine($"      {f.VersionNum} / {f.HardwareVersion} / {f.VersionDate}");
        });

        await Check("mss_status + mss_support_info", async () =>
        {
            var st = await api.GetMssStatusAsync(default);
            var sup = await api.GetMssSupportAsync(default);
            Console.WriteLine($"      卡位数={st.TotalSimCount} 当前卡={sup.CurrentSimId} " +
                              $"卡位={string.Join(",", (sup.SimList ?? new List<SimCard>()).Select(s => s.SimName))}");
        });

        await Check("engineering_info", async () =>
        {
            var e = await api.GetEngineeringAsync(default);
            Console.WriteLine($"      Band={e.Lte?.Band} PCI={e.Lte?.PhyCellId} EARFCN={e.Lte?.DlEuArfcn} " +
                              $"原始 RSRP={e.Lte?.Rsrp} SINR={e.Lte?.Sinr}");
        });

        await Check("wan_info", async () =>
        {
            var w = await api.GetWanInfoAsync(default);
            Console.WriteLine($"      proto={w.Proto} NW_mode={w.NwMode} (默认档 {w.NwModeDefault}) MTU={w.Mtu}");
        });

        await Check("wan_apn_profile_info", async () =>
        {
            var a = await api.GetApnProfilesAsync(default);
            Console.WriteLine($"      auto_apn={a.AutoApn} 当前 APN={a.Apn} 配置数={a.ProfileList?.Count ?? 0}");
        });

        await Check("wan_pdp_context_info", async () =>
        {
            var p = await api.GetPdpContextAsync(default);
            Console.WriteLine($"      {p.NetworkName} IPv4={p.PdpContextList?.FirstOrDefault()?.Ipv4}");
        });

        await Check("wlan_info", async () =>
        {
            var w = await api.GetWlanInfoAsync(default);
            Console.WriteLine($"      开关={w.WlanEnable} net_mode={w.NetMode} 信道={w.Channel} 最大终端={w.MaxClients}");
        });

        await Check("wlan_security_info", async () =>
        {
            var s = await api.GetWlanSecurityAsync(default);
            Console.WriteLine($"      SSID={s.Ssid} 模式={s.Mode} 密钥长度={s.CurrentKey?.Length ?? 0} 隐藏={s.SsidBcast}");
        });

        await Check("wlan_wps_info / wlan_mac_filters_info", async () =>
        {
            var wps = await api.GetWpsAsync(default);
            var mac = await api.GetMacFiltersAsync(default);
            Console.WriteLine($"      WPS={wps.WpsStatus} MAC 过滤={mac.Enable}/mode={mac.Mode} " +
                              $"白={mac.AllowList?.Count ?? 0} 黑={mac.DenyList?.Count ?? 0}");
        });

        await Check("dhcp_info", async () =>
        {
            var d = await api.GetDhcpAsync(default);
            Console.WriteLine($"      {d.Ip}/{d.Mask} 池 {d.Start}-{d.End} 租期={d.LeaseTime} 静态绑定={d.FixedIpList?.Count ?? 0}");
        });

        await Check("device_management_info", async () =>
        {
            var d = await api.GetDevicesAsync(default);
            Console.WriteLine($"      记录设备={d.KnownDevices?.Count ?? 0} 在线={d.ClientList?.Count ?? 0}");
            foreach (var c in (d.ClientList ?? new List<ClientEntry>()).Take(3))
                Console.WriteLine($"        - {c.Name} {c.Ip} 状态={c.Status} ↓{J.Bytes(J.Long(c.Rx))} ↑{J.Bytes(J.Long(c.Tx))}");
        });

        await Check("account_management_info", async () =>
        {
            var a = await api.GetAccountsAsync(default);
            Console.WriteLine($"      多账号={a.MultiAccount} 当前用户={a.RouterUsername} 账号数={a.UserList?.Count ?? 0}");
        });

        await Check("custom_fw_info", async () =>
        {
            var f = await api.GetCustomFwAsync(default);
            Console.WriteLine($"      自定义规则模式={f.RulesMode} 规则数={f.Rules?.Count ?? 0}");
        });

        await Check("login_info", async () =>
        {
            var l = await api.GetLoginInfoAsync(default);
            Console.WriteLine($"      login_status={l.LoginStatus} user={l.Username}");
        });

        // ---------- 写入链路验证（原值回写，不改变设备状态） ----------
        if (doWrite)
        {
            await Check("wan_set 原值回写（验证写入链路，不改变任何设置）", async () =>
            {
                var w = await api.GetWanInfoAsync(default);
                await api.SetNetworkAsync(w.Proto ?? "cellular", w.NwMode ?? "2", w.PreferMode ?? "0",
                    w.PreferLteType ?? "1", J.Int(w.Mtu), default);

                // 回读校验：确认写入后设备读回的值与写入前一致
                var after = await api.GetWanInfoAsync(default);
                if (after.NwMode != w.NwMode || after.Mtu != w.Mtu)
                    throw new Exception($"回读结果与写入前不一致（NW_mode {w.NwMode}->{after.NwMode}，MTU {w.Mtu}->{after.Mtu}）");
                Console.WriteLine($"      写入成功且回读一致：NW_mode={after.NwMode} MTU={after.Mtu}");
            });

            await Check("wlan_set 原值回写", async () =>
            {
                var w = await api.GetWlanInfoAsync(default);
                RouterException? interrupted = null;
                try
                {
                    await api.SetWlanAsync(w.RfBand ?? "1", w.NetMode ?? "4", w.Channel ?? "0", w.Bandwidth ?? "0",
                        J.Int(w.MaxClients) ?? 10, J.Bool(w.ApIsolate) ?? false, null, default);
                }
                catch (RouterException ex) when (ex.Kind is RouterErrorKind.Network or RouterErrorKind.Timeout)
                {
                    // 设备应用 Wi-Fi 配置时会重启射频；电脑此时断网，写入响应可能收不到。
                    // 不能直接判定失败，必须等设备恢复后回读核对。
                    interrupted = ex;
                }

                if (interrupted is not null)
                {
                    if (!await WaitForRouterAsync(host, TimeSpan.FromSeconds(30)))
                        throw new Exception("Wi-Fi 射频重启后 30 秒内未恢复：" + interrupted.UserMessage);
                    await Task.Delay(1000);
                    await client.LoginAsync();
                }

                var after = await api.GetWlanInfoAsync(default);
                if (after.NetMode != w.NetMode || after.Channel != w.Channel)
                    throw new Exception("回读结果与写入前不一致");
                Console.WriteLine(interrupted is null
                    ? $"      写入成功且回读一致：net_mode={after.NetMode} channel={after.Channel}"
                    : $"      Wi-Fi 射频重启导致响应中断，恢复后回读一致：net_mode={after.NetMode} channel={after.Channel}");
            });

            // wlan_set 会让设备短暂重启 Wi-Fi 射频，电脑的无线连接也会重新关联。
            // 后续用例必须等设备重新可达并重新登录，否则会被误判成接口/认证失败。
            if (!await WaitForRouterAsync(host, TimeSpan.FromSeconds(30)))
                Console.WriteLine("WARN  Wi-Fi 回写后 30 秒内未重新连通设备，后续用例可能失败。");
            else
            {
                await Task.Delay(1000);
                await client.LoginAsync();
                Console.WriteLine("      设备 Wi-Fi 已恢复，测试会话已重新登录。");
            }

            await Check("认证强制性探测（安全结论以真机行为为准）", async () =>
            {
                using var bad = new RouterClient(host, user, "wrong-password-for-test");
                var rejected = false;
                try
                {
                    await bad.LoginAsync();
                }
                catch (RouterException ex) when (ex.Kind == RouterErrorKind.AuthFailed)
                {
                    rejected = true;
                }

                if (rejected)
                {
                    Console.WriteLine("      设备正确拒绝错误密码（AuthEnforced=true）");
                }
                else
                {
                    if (bad.AuthEnforced)
                        throw new Exception("错误密码可用，但认证探测却报告设备已校验凭据，探测逻辑与事实不符");
                    Console.WriteLine("      ⚠ 设备未校验登录凭据：错误密码同样可以读写配置（AuthEnforced=false）");
                    Console.WriteLine("        该结论来自本机实测：不带 Authorization 头的请求也能拿到完整数据。");
                }
            });

            await Check("不存在的接口应报 NotSupported", async () =>
            {
                try
                {
                    await client.GetJsonAsync("this_api_does_not_exist", default);
                    throw new Exception("不存在的接口未报错");
                }
                catch (RouterException ex) when (ex.Kind == RouterErrorKind.NotSupported)
                {
                    Console.WriteLine("      接口缺失被正确识别：" + ex.UserMessage);
                }
            });
        }

        Console.WriteLine($"=== 结果：通过 {_pass} 项，失败 {_fail} 项 ===");
        return _fail == 0 ? 0 : 1;
    }

    private static async Task<int> RunC2000Async(string[] args)
    {
        var host = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "http://192.168.66.1";
        var user = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "root";
        var pass = Environment.GetEnvironmentVariable("NRADIO_TEST_PASSWORD") ?? "";
        if (string.IsNullOrEmpty(pass))
        {
            Console.Error.WriteLine("FAIL  未提供 NRADIO_TEST_PASSWORD；不会把密码写入命令行或测试报告。");
            return 2;
        }

        Console.WriteLine($"=== C2000 Pro 只读真机自检 · {C2000ProClient.NormalizeHost(host)} ===");
        _pass = 0;
        _fail = 0;
        using var adapter = new C2000ProAdapter(host, user, pass, 10);
        adapter.RequestLogged += e =>
            Console.WriteLine($"      HTTP {e.Method} {e.Endpoint} -> {e.StatusCode} " +
                              (string.IsNullOrWhiteSpace(e.Error) ? "" : $"错误={e.Error}"));
        adapter.DiagnosticLogged += e => Console.WriteLine($"      DIAG {e.Level}: {e.Message}");
        try
        {
            var kind = await DeviceConnector.DetectAsync(host, 8);
            CheckResult("设备协议指纹", kind == DeviceKind.C2000Pro, $"识别为 {kind}");
            await adapter.AuthenticateAsync();
            CheckResult("Cookie 登录", adapter.IsAuthenticated, "sysauth 会话已通过受保护接口复核");

            C2000ProRuntimeSnapshot? snapshot = null;
            await Check("运行时 + 小区", async () =>
            {
                snapshot = await adapter.GetSnapshotAsync();
                var c = snapshot.Cell.Status;
                Console.WriteLine($"      型号={snapshot.Identity.Model} 固件={snapshot.Identity.FirmwareVersion} " +
                                  $"模式={c?.Mode} Band={c?.Band} PCI={c?.Pci} EARFCN={c?.Earfcn}");
                Console.WriteLine($"      RSRP={c?.Rsrp}dBm RSRQ={c?.Rsrq}dB SINR={c?.Sinr}dB " +
                                  $"运营商={c?.Isp} SIM={c?.SimNo}/{c?.SimCount}");
                Console.WriteLine($"      终端={snapshot.Runtime.TerminalAll?.Count} WLAN 射频={snapshot.Runtime.Wlan?.Radios?.Count}");
                if (snapshot.Identity.Model == "—" || string.IsNullOrWhiteSpace(c?.Rsrp))
                    throw new Exception("关键身份或信号字段为空");
            });

            await Check("兼容层统一模型", async () =>
            {
                var home = await adapter.Api.GetHomepageAsync(default);
                var status = await adapter.Api.GetStatusAsync(default);
                var cell = await adapter.Api.GetEngineeringAsync(default);
                var devices = await adapter.Api.GetDevicesAsync(default);
                if (string.IsNullOrWhiteSpace(home.DeviceModel) || string.IsNullOrWhiteSpace(home.WanIp))
                    throw new Exception("homepage 映射缺少型号或 WAN IP");
                Console.WriteLine($"      {home.DeviceModel} · {home.NetworkName} · WAN {home.WanIp} · " +
                                  $"在线 {status.WifiClientsNum} · 终端 {devices.ClientList?.Count ?? 0}");
                Console.WriteLine($"      小区映射 Band={cell.Lte?.Band} PCI={cell.Lte?.PhyCellId} RSRP={cell.Lte?.Rsrp}");
            });

            await Check("Band & Cell Lock 只读状态", async () =>
            {
                var l = await adapter.GetLockSnapshotAsync();
                Console.WriteLine($"      模式={l.LockMode} Cell={l.NciEci} Band={l.Band} PCI={l.Pci} " +
                                  $"EARFCN={l.Earfcn} 邻居={l.Neighbours.Count}");
            });

            await Check("SIM 管理状态", async () =>
            {
                var s = await adapter.GetSimSnapshotAsync();
                Console.WriteLine($"      卡位数={s.SlotCount} 当前={s.CurrentSlot} 模式={s.SwitchMode} " +
                                  $"注册={s.Registration} PIN={s.PinState} 运营商={s.SimOperator}");
            });

            await Check("短信真实能力", async () =>
            {
                var s = await adapter.GetSmsSnapshotAsync();
                Console.WriteLine($"      {s.StatusText} 未读={s.Unread} 容量={s.Used}/{s.Capacity} 消息={s.Messages.Count}");
            });

            await Check("Wi-Fi / 局域网只读", async () =>
            {
                var w = await adapter.Api.GetWlanInfoAsync(default);
                var lan = await adapter.Api.GetDhcpAsync(default);
                Console.WriteLine($"      射频={w.RfBand} 信道={w.Channel} 在线={w.MaxClients} LAN={lan.Ip}/{lan.Mask}");
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  未捕获异常：" + ex.Message);
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                Console.WriteLine("      INNER  " + ActivityLog.Sanitize(inner.Message));
            _fail++;
        }

        Console.WriteLine($"=== 结果：通过 {_pass} 项，失败 {_fail} 项 ===");
        return _fail == 0 ? 0 : 1;
    }

    private static void CheckResult(string name, bool ok, string detail)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}：{detail}");
        if (ok) _pass++; else _fail++;
    }

    /// <summary>
    /// 跨进程日志持久化自检：一次进程写入并正常冲刷/关闭，另一次进程重新加载读取。
    /// 该模式不连接设备。
    /// </summary>
    private static int RunLogPersistenceTest(string[] args)
    {
        var write = args.Contains("--log-persistence-write");
        var dirIndex = Array.FindIndex(args, a => a is "--log-persistence-write" or "--log-persistence-read");
        if (dirIndex < 0 || dirIndex + 1 >= args.Length)
        {
            Console.WriteLine("FAIL  缺少日志测试目录参数");
            return 1;
        }

        var dir = args[dirIndex + 1];
        try
        {
            if (write)
            {
                var log = new ActivityLog(
                    dir, maxFileBytes: 256 * 1024, maxFiles: 3, maxMemoryItems: 500,
                    loadExisting: false, publishToUi: false);
                log.Info(LogCategory.Application, "进程写入", "第一次进程启动并写入事件。");
                log.Success(LogCategory.Connection, "设备在线", "连接成功（本地检测时间）。");
                log.Warn(LogCategory.Network, "网络质量", "警告事件。");
                log.Error(LogCategory.Sampling, "采样", "错误事件 password=ShouldNeverPersist token=HiddenToken");
                var flushed = log.Flush(TimeSpan.FromSeconds(5));
                log.Dispose();
                Console.WriteLine(flushed
                    ? $"WRITE_OK 写入 {log.PersistedCount} 条并已冲刷：{log.CurrentFilePath}"
                    : "WRITE_FAIL 日志未能冲刷");
                return flushed ? 0 : 1;
            }

            var loaded = new ActivityLog(
                dir, maxFileBytes: 256 * 1024, maxFiles: 3, maxMemoryItems: 500,
                publishToUi: false);
            var text = File.ReadAllText(loaded.CurrentFilePath);
            var ok = loaded.LoadedCount >= 4 &&
                     loaded.Items.Any(x => x.Category == LogCategory.Connection) &&
                     loaded.Items.Any(x => x.Category == LogCategory.Sampling) &&
                     loaded.Items.Any(x => x.Level == LogLevel.Warning) &&
                     !text.Contains("ShouldNeverPersist", StringComparison.Ordinal) &&
                     !text.Contains("HiddenToken", StringComparison.Ordinal);
            Console.WriteLine(ok
                ? $"READ_OK 跨进程读取 {loaded.LoadedCount} 条；类别、级别和脱敏均正确"
                : $"READ_FAIL 读取 {loaded.LoadedCount} 条；脱敏={!text.Contains("ShouldNeverPersist", StringComparison.Ordinal)}");
            loaded.Dispose();
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  日志持久化跨进程自检异常：" + ex);
            return 1;
        }
    }

    private static async Task Check(string name, Func<Task> action)
    {
        try
        {
            await action();
            Console.WriteLine($"PASS  {name}");
            _pass++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  {name}：{ex.Message}");
            _fail++;
        }
    }

    private static async Task<bool> WaitForRouterAsync(string host, TimeSpan timeout)
    {
        var uri = new Uri(RouterClient.NormalizeHost(host));
        var deadline = DateTime.UtcNow + timeout;
        using var ping = new System.Net.NetworkInformation.Ping();
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var reply = await ping.SendPingAsync(uri.Host, 1000);
                if (reply.Status == System.Net.NetworkInformation.IPStatus.Success) return true;
            }
            catch
            {
                // 无线重新关联期间出现异常是预期情况，继续等待。
            }
            await Task.Delay(1000);
        }
        return false;
    }
}
