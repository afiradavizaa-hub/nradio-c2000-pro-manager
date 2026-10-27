using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NRadioManager.Core;

/// <summary>
/// NRadio C2000 Pro（NROS）适配器。字段只取当前固件真实响应；
/// 页面级 CBI 配置通过受保护 HTML 中嵌入的生成数据读取，写入后再回读校验。
/// </summary>
public sealed class C2000ProAdapter : IDeviceAdapter
{
    private readonly C2000ProClient _client;
    private readonly C2000CompatibilityApi _api;
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private C2000ProRuntimeSnapshot? _lastSnapshot;
    private C2000DeviceIdentity? _identity;
    private DateTime _identityLoadedUtc;

    public C2000ProAdapter(string host, string username, string password, int timeoutSeconds = 10)
    {
        _client = new C2000ProClient(host, username, password, timeoutSeconds);
        _client.RequestLogged += e => RequestLogged?.Invoke(e);
        _client.DiagnosticLogged += e => DiagnosticLogged?.Invoke(e);
        _client.SessionLost += () => SessionLost?.Invoke();
        _api = new C2000CompatibilityApi(this);
    }

    public DeviceKind Kind => DeviceKind.C2000Pro;
    public string Host => _client.Host;
    public string Username => _client.Username;
    public string DisplayName => "NRadio C2000 Pro";
    public DeviceCapabilities Capabilities => DeviceCapabilities.C2000Pro;
    public IDeviceApi Api => _api;
    public bool IsAuthenticated => _client.IsAuthenticated;
    public DateTime? LastSuccessUtc => _client.LastSuccessUtc;
    public C2000ProClient Client => _client;
    public C2000ProRuntimeSnapshot? LastSnapshot => _lastSnapshot;

    public event Action<RouterLogEntry>? RequestLogged;
    public event Action<RouterDiagnosticEntry>? DiagnosticLogged;
    public event Action? SessionLost;

    public Task AuthenticateAsync(CancellationToken ct = default) => _client.LoginAsync(ct);

    public async Task<C2000ProRuntimeSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        await _snapshotGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lastSnapshot is not null &&
                DateTime.Now - _lastSnapshot.SampledAt < TimeSpan.FromMilliseconds(800))
                return _lastSnapshot;

            var runtimeTask = _client.GetRuntimeAsync(ct);
            var cellTask = _client.GetCellInfoAsync(ct);
            var refreshIdentity = _identity is null || DateTime.UtcNow - _identityLoadedUtc > TimeSpan.FromMinutes(5);
            var overviewTask = refreshIdentity ? _client.GetOverviewHtmlAsync(ct) : null;
            await Task.WhenAll(runtimeTask, cellTask).ConfigureAwait(false);

            var runtime = (await runtimeTask.ConfigureAwait(false)).Result
                          ?? throw new RouterException(RouterErrorKind.Protocol, "运行时接口没有 result。", "runtime");
            var cell = (await cellTask.ConfigureAwait(false)).Result?.FirstOrDefault()
                       ?? throw new RouterException(RouterErrorKind.Protocol, "蜂窝接口没有 result。", "cellinfo");
            if (overviewTask is not null)
            {
                _identity = ParseIdentity(await overviewTask.ConfigureAwait(false), runtime);
                _identityLoadedUtc = DateTime.UtcNow;
            }
            var identity = _identity ?? ParseIdentity("", runtime);

            _lastSnapshot = new C2000ProRuntimeSnapshot
            {
                Runtime = runtime,
                Cell = cell,
                Identity = identity,
                SampledAt = DateTime.Now,
            };
            return _lastSnapshot;
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    public async Task<C2000LockSnapshot> GetLockSnapshotAsync(string channel = "wan0", CancellationToken ct = default)
    {
        var html = await _client.GetLockHtmlAsync(channel, ct).ConfigureAwait(false);
        return ParseLockSnapshot(html, _lastSnapshot?.Cell.Status);
    }

    internal static C2000LockSnapshot ParseLockSnapshot(string html, C2000Cpe? cellStatus = null)
    {
        var marker = html.IndexOf("cellular_earfcn", StringComparison.Ordinal);
        var start = marker >= 0 ? html.IndexOf('{', marker) : -1;
        var jsonText = start >= 0 ? ExtractBalancedObject(html, start) : null;
        if (string.IsNullOrWhiteSpace(jsonText))
            throw new RouterException(RouterErrorKind.Protocol, "锁频页没有找到可解析的 cellular_earfcn 数据。", "cpelock");

        JsonNode root;
        try
        {
            root = JsonNode.Parse(jsonText)
                   ?? throw new JsonException("null");
        }
        catch (JsonException ex)
        {
            throw new RouterException(RouterErrorKind.Protocol, "锁频页数据解析失败。", "cpelock", ex);
        }

        var cfg = root["cfg"];
        var nrCfg = cfg?["nr"];
        var lteCfg = cfg?["lte"];
        var configured = nrCfg?["EARFCN"]?.ToString();
        if (string.IsNullOrWhiteSpace(configured)) configured = lteCfg?["EARFCN"]?.ToString();
        var mode = string.IsNullOrWhiteSpace(configured) ? "自动选频（未读取到锁定配置）" : "小区锁定";

        var status = root["status"];
        var neighbours = new List<C2000CellEntry>();
        var groups = root["neighbour"] as JsonArray;
        if (groups is not null)
        {
            foreach (var group in groups.OfType<JsonArray>())
            {
                foreach (var item in group.OfType<JsonObject>())
                {
                    var earfcn = item["EARFCN"]?.ToString() ?? "";
                    var pci = item["PCI"]?.ToString() ?? "";
                    var band = item["BAND"]?.ToString() ?? "";
                    if (band.Length == 0 && earfcn.Length > 0)
                        band = GuessNrBand(earfcn);
                    var cellMode = item["MODE"]?.ToString() ?? "";
                    var configuredCell = cellMode.Contains("NR", StringComparison.OrdinalIgnoreCase) ? nrCfg : lteCfg;
                    var locked = string.Equals(earfcn, configuredCell?["EARFCN"]?.ToString(), StringComparison.Ordinal) &&
                                 string.Equals(pci, configuredCell?["PCI"]?.ToString(), StringComparison.Ordinal);
                    neighbours.Add(new C2000CellEntry(
                        cellMode,
                        band,
                        pci,
                        earfcn,
                        item["RSRP"]?.ToString() ?? "",
                        item["RSRQ"]?.ToString() ?? "",
                        item["SINR"]?.ToString() ?? "",
                        locked,
                        string.Equals(earfcn, status?["EARFCN"]?.ToString(), StringComparison.Ordinal) &&
                        string.Equals(pci, status?["PCI"]?.ToString(), StringComparison.Ordinal)));
                }
            }
        }

        var bandwidthKhz = cellStatus?.DownlinkBandwidthKhz ?? "";
        var bandwidth = TryLong(bandwidthKhz) is { } khz ? $"{khz / 1000d:0.###} MHz" : "—";
        return new C2000LockSnapshot(
            mode,
            cellStatus?.Mode ?? "",
            status?["CELL"]?.ToString() ?? "—",
            status?["BAND"]?.ToString() ?? "—",
            status?["PCI"]?.ToString() ?? "—",
            status?["EARFCN"]?.ToString() ?? "—",
            status?["LAC"]?.ToString() ?? "—",
            status?["RSRP"]?.ToString() ?? "—",
            status?["RSRQ"]?.ToString() ?? cellStatus?.Rsrq ?? "—",
            status?["SINR"]?.ToString() ?? "—",
            cellStatus?.Cqi ?? "—",
            bandwidth,
            neighbours.OrderByDescending(x => TryLong(x.Rsrp) ?? long.MinValue).ToArray());
    }

    public async Task<C2000SimSnapshot> GetSimSnapshotAsync(string channel = "wan0", CancellationToken ct = default)
    {
        if (_lastSnapshot is null)
            await GetSnapshotAsync(ct).ConfigureAwait(false);
        var html = await _client.GetSimSwitchHtmlAsync(channel, ct).ConfigureAwait(false);
        var cpe = _lastSnapshot?.Cell.Status ?? _lastSnapshot?.Runtime.Cpe?.FirstOrDefault();
        var switchMode = Regex.Match(html,
            @"id=""cbid\.cpecfg\.config\.dail_mode""[^>]*>(?<body>.*?)</select>",
            RegexOptions.Singleline);
        var selectedMode = switchMode.Success
            ? Regex.Match(switchMode.Groups["body"].Value, @"<option[^>]*selected=""selected""[^>]*>(?<v>.*?)</option>", RegexOptions.Singleline).Groups["v"].Value.Trim()
            : "—";
        var current = cpe?.SimNo switch
        {
            "1" => "Embedded SIM1",
            "2" => "Embedded SIM2",
            _ => "—",
        };
        var registration = cpe?.Status == 0 && !string.IsNullOrWhiteSpace(cpe?.Isp)
            ? "已注册 / 已连接"
            : "未确认或未连接";
        var pin = cpe?.Cpin switch
        {
            "0" or null or "" => "未锁定",
            _ => $"设备原始状态 {cpe.Cpin}",
        };
        return new C2000SimSnapshot(
            Supported: cpe is not null,
            SlotCount: cpe?.SimCount ?? 0,
            CurrentSlot: current,
            SwitchMode: string.IsNullOrWhiteSpace(selectedMode) ? "—" : selectedMode,
            Registration: registration,
            PinState: pin,
            SimOperator: cpe?.SimIsp ?? "—",
            Imsi: cpe?.Imsi ?? "—",
            Iccid: cpe?.Iccid ?? "—");
    }

    public async Task<C2000CpestatusResult> GetCpestatusAsync(string channel = "wan0", CancellationToken ct = default)
    {
        var body = await _client.PostFormAsync(
            "/cgi-bin/luci/nradio/cellular/cpestatus/info",
            new Dictionary<string, string>
            {
                ["model"] = channel,
                ["sim_id"] = "1",
                ["earfcn"] = "1",
                ["sim"] = "1",
            },
            "cpestatus",
            ct).ConfigureAwait(false);
        return C2000ProClient.Deserialize<C2000CpestatusEnvelope>(body, "cpestatus").Result
               ?? throw new RouterException(RouterErrorKind.Protocol, "cpestatus 没有 result。", "cpestatus");
    }

    public Task<C2000WriteResult> SetSimModeAsync(int? manualSlot, CancellationToken ct = default)
        => RunWriteAsync(() => SetSimModeCoreAsync(manualSlot, ct), ct);

    private async Task<C2000WriteResult> SetSimModeCoreAsync(int? manualSlot, CancellationToken ct)
    {
        if (manualSlot is not (null or 1 or 2))
            throw new RouterException(RouterErrorKind.Device, "C2000 Pro 当前固件只提供 SIM1/SIM2 两个内嵌卡位。");

        var html = await _client.GetSimSwitchHtmlAsync("wan0", ct).ConfigureAwait(false);
        var fields = ExtractCbiFields(html);
        var before = await GetSimSnapshotAsync("wan0", ct).ConfigureAwait(false);
        var beforeText = $"{before.CurrentSlot}; mode={before.SwitchMode}; operator={before.SimOperator}";

        fields["cbid.cpecfg.config.dail_mode"] = manualSlot is null ? "0" : "1";
        if (manualSlot is { } slot)
        {
            fields["cbid.cpecfg.config.cur"] = slot.ToString(CultureInfo.InvariantCulture);
            fields["cbid.cpecfg.config.default"] = slot.ToString(CultureInfo.InvariantCulture);
        }
        fields["cbi.submit"] = "1";

        var response = await _client.PostMultipartAsync(
            "/cgi-bin/luci/nradio/cellular/switch/model/wan0",
            fields, "sim-switch", ct).ConfigureAwait(false);
        RejectCbiError(response, "sim-switch");

        var deadline = DateTime.UtcNow.AddSeconds(90);
        C2000SimSnapshot? current = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(3000, ct).ConfigureAwait(false);
            try
            {
                current = await GetSimSnapshotAsync("wan0", ct).ConfigureAwait(false);
            }
            catch (RouterException ex) when (ex.Kind is RouterErrorKind.Network or RouterErrorKind.Timeout)
            {
                continue;
            }

            var slotConfirmed = manualSlot is null ||
                                string.Equals(current.CurrentSlot, $"Embedded SIM{manualSlot}", StringComparison.Ordinal);
            if (slotConfirmed && !string.IsNullOrWhiteSpace(current.SimOperator) && current.SimOperator != "—")
            {
                return new C2000WriteResult(true,
                    manualSlot is null ? "已切回自动选卡并确认新状态。" : $"已切换到 Embedded SIM{manualSlot} 并确认网络状态。",
                    beforeText,
                    $"{current.CurrentSlot}; mode={current.SwitchMode}; operator={current.SimOperator}; {current.Registration}");
            }
        }

        return new C2000WriteResult(false,
            "切卡请求已发送，但 90 秒内未回读确认目标卡位与网络注册；结果待确认。",
            beforeText,
            current is null ? "回读失败" : $"{current.CurrentSlot}; mode={current.SwitchMode}; {current.Registration}");
    }

    public async Task<string> GetNetworkModeSelectionAsync(string channel = "wan0", CancellationToken ct = default)
    {
        var html = await _client.GetTextAsync(
            $"/cgi-bin/luci/nradio/cellular/cpecfg/model/{Uri.EscapeDataString(channel)}",
            "cpecfg", ct).ConfigureAwait(false);
        return SelectedOption(html, "cbid.cpecfg.wan0sim1.mode") ?? "";
    }

    public Task<C2000WriteResult> SetNetworkModeAsync(string mode, CancellationToken ct = default)
        => RunWriteAsync(() => SetNetworkModeCoreAsync(mode, ct), ct);

    private async Task<C2000WriteResult> SetNetworkModeCoreAsync(string mode, CancellationToken ct)
    {
        if (mode is not ("auto" or "sa_only" or "lte"))
            throw new RouterException(RouterErrorKind.Device, "网络模式必须是 auto、sa_only 或 lte。");
        var path = "/cgi-bin/luci/nradio/cellular/cpecfg/model/wan0";
        var html = await _client.GetTextAsync(path, "cpecfg", ct).ConfigureAwait(false);
        var before = SelectedOption(html, "cbid.cpecfg.wan0sim1.mode") ?? "—";
        var fields = ExtractCbiFields(html);
        fields["cbid.cpecfg.wan0sim1.mode"] = mode;
        fields["cbi.submit"] = "1";
        var response = await _client.PostMultipartAsync(path, fields, "network-mode", ct).ConfigureAwait(false);
        RejectCbiError(response, "network-mode");

        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000, ct).ConfigureAwait(false);
            try
            {
                var after = await GetNetworkModeSelectionAsync("wan0", ct).ConfigureAwait(false);
                if (string.Equals(after, mode, StringComparison.Ordinal))
                    return new C2000WriteResult(true, "网络模式已写入且页面回读一致。", before, after);
            }
            catch (RouterException ex) when (ex.Kind is RouterErrorKind.Network or RouterErrorKind.Timeout)
            {
                continue;
            }
        }
        return new C2000WriteResult(false, "网络模式表单已提交，但 40 秒内未取得一致回读；结果待确认。",
            before, "未取得一致回读");
    }

    public Task<C2000WriteResult> SetWifiProfileAsync(int radioIndex, string ssid, string? password,
        string? channel, string? bandwidth, bool enabled, CancellationToken ct = default)
        => RunWriteAsync(() => SetWifiProfileCoreAsync(radioIndex, ssid, password, channel, bandwidth, enabled, ct), ct);

    private async Task<C2000WriteResult> SetWifiProfileCoreAsync(int radioIndex, string ssid, string? password,
        string? channel, string? bandwidth, bool enabled, CancellationToken ct)
    {
        if (radioIndex is not (0 or 1))
            throw new RouterException(RouterErrorKind.Device, "C2000 Pro 仅支持 2.4GHz(0) 与 5GHz(1) 两个射频。");
        if (string.IsNullOrWhiteSpace(ssid) || ssid.Length > 32)
            throw new RouterException(RouterErrorKind.Device, "SSID 长度必须为 1–32 个字符。");
        if (!string.IsNullOrEmpty(password) && password.Length is < 8 or > 63)
            throw new RouterException(RouterErrorKind.Device, "Wi-Fi 密码长度必须为 8–63 位；留空表示保持原密码。");
        if (!string.IsNullOrWhiteSpace(channel) &&
            !int.TryParse(channel, out var channelValue))
            throw new RouterException(RouterErrorKind.Device, "信道必须为整数或 auto。");

        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        var radio = snapshot.Runtime.Wlan?.Radios?.FirstOrDefault(x =>
            radioIndex == 0
                ? x.InterfaceName?.EndsWith('0') == true
                : x.InterfaceName?.EndsWith('1') == true);
        var before = radio is null
            ? $"射频 {radioIndex} 未读取"
            : $"{radio.InterfaceName}: SSID={radio.Ssid}, channel={radio.Channel}, mode={radio.Encryption}";

        var html = await _client.GetWifiHtmlAsync(ct).ConfigureAwait(false);
        var fields = ExtractCbiFields(html);
        fields[$"cbid.cloudd.d0.ssid_g1_wlan{radioIndex}"] = ssid;
        var disabledKey = $"cbid.cloudd.d0.disabled_g1_wlan{radioIndex}";
        if (enabled) fields.Remove(disabledKey);
        else fields[disabledKey] = "1";
        if (!string.IsNullOrEmpty(password))
            fields[$"cbid.cloudd.d0.key_g1_wlan{radioIndex}"] = password;
        if (!string.IsNullOrWhiteSpace(channel) && !string.Equals(channel, "auto", StringComparison.OrdinalIgnoreCase))
            fields[$"cbid.cloudd.d0.channel_g1_radio{radioIndex}"] = channel;
        if (!string.IsNullOrWhiteSpace(bandwidth))
            fields[$"cbid.cloudd.d0.htmode_g1_radio{radioIndex}"] = bandwidth;
        fields["cbi.submit"] = "1";

        string response;
        try
        {
            response = await _client.PostMultipartAsync(
                "/cgi-bin/luci/nradio/basic/wifi", fields, "wifi-save", ct).ConfigureAwait(false);
        }
        catch (RouterException ex) when (ex.Kind is RouterErrorKind.Network or RouterErrorKind.Timeout)
        {
            return new C2000WriteResult(false,
                "写入后本机与设备的无线连接已中断，无法立即回读；结果待确认。请重新连接后核对 SSID/信道。",
                before, "未取得回读");
        }
        RejectCbiError(response, "wifi-save");

        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000, ct).ConfigureAwait(false);
            try
            {
                var afterSnapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
                var afterRadio = afterSnapshot.Runtime.Wlan?.Radios?.FirstOrDefault(x =>
                    radioIndex == 0
                        ? x.InterfaceName?.EndsWith('0') == true
                        : x.InterfaceName?.EndsWith('1') == true);
                var ssidOk = string.Equals(afterRadio?.Ssid, ssid, StringComparison.Ordinal);
                var channelOk = string.IsNullOrWhiteSpace(channel) ||
                                string.Equals(channel, "auto", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(afterRadio?.Channel, channel, StringComparison.Ordinal);
                var page = await _client.GetWifiHtmlAsync(ct).ConfigureAwait(false);
                var bandwidthOk = string.IsNullOrWhiteSpace(bandwidth) ||
                                  string.Equals(SelectedOption(page,
                                      $"cbid.cloudd.d0.htmode_g1_radio{radioIndex}"), bandwidth, StringComparison.Ordinal);
                var enabledOk = radioIndex == 0
                    ? (afterSnapshot.Runtime.Wlan?.Radio2Disabled != 1) == enabled
                    : (afterSnapshot.Runtime.Wlan?.Radio5Disabled != 1) == enabled;
                if (ssidOk && channelOk && bandwidthOk && enabledOk)
                {
                    var after = $"{afterRadio?.InterfaceName}: SSID={afterRadio?.Ssid}, " +
                                $"channel={afterRadio?.Channel}, bandwidth={bandwidth}, mode={afterRadio?.Encryption}";
                    return new C2000WriteResult(true,
                        "Wi-Fi 配置已写入并回读确认；密码内容未记录。", before, after);
                }
            }
            catch (RouterException ex) when (ex.Kind is RouterErrorKind.Network or RouterErrorKind.Timeout)
            {
                continue;
            }
        }
        return new C2000WriteResult(false,
            "Wi-Fi 表单已提交，但 40 秒内未回读确认；结果待确认。",
            before, "未取得一致回读");
    }

    public Task<C2000WriteResult> RebootAsync(CancellationToken ct = default)
        => RunWriteAsync(() => RebootCoreAsync(ct), ct);

    private async Task<C2000WriteResult> RebootCoreAsync(CancellationToken ct)
    {
        var beforeSnapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        var beforeUptime = beforeSnapshot.Runtime.Global?.Uptime ?? 0;
        var html = await _client.GetTextAsync("/cgi-bin/luci/nradioadv/system/restart", "reboot-page", ct)
            .ConfigureAwait(false);
        var fields = ExtractCbiFields(html);
        fields["cbi.submit"] = "1";

        string? submitNotice = null;
        try
        {
            var response = await _client.PostMultipartAsync(
                "/cgi-bin/luci/nradioadv/system/restart", fields, "reboot", ct).ConfigureAwait(false);
            RejectCbiError(response, "reboot");
        }
        catch (RouterException ex) when (ex.Kind is RouterErrorKind.Network or RouterErrorKind.Timeout)
        {
            submitNotice = "重启命令提交后连接中断（符合预期）。";
        }

        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(5000, ct).ConfigureAwait(false);
            try
            {
                var after = await GetSnapshotAsync(ct).ConfigureAwait(false);
                var afterUptime = after.Runtime.Global?.Uptime;
                if (afterUptime is not null && afterUptime.Value < beforeUptime)
                {
                    return new C2000WriteResult(true,
                        (submitNotice ?? "") + $"设备已恢复，运行时间由 {beforeUptime} 秒回退为 {afterUptime} 秒，重启得到回读确认。",
                        $"uptime={beforeUptime}", $"uptime={afterUptime}, model={after.Identity.Model}");
                }
            }
            catch (RouterException ex) when (ex.Kind is RouterErrorKind.Network or RouterErrorKind.Timeout or RouterErrorKind.SessionExpired)
            {
                // 重启窗口内设备离线是预期路径。
            }
        }

        return new C2000WriteResult(false,
            (submitNotice ?? "") + "3 分钟内未取得运行时间回退的设备响应；结果待确认。",
            $"uptime={beforeUptime}", "未恢复");
    }

    /// <summary>
    /// 使用专业版页面自身调用的 earfcn_lock 接口写入小区锁。
    /// 写入后必须轮询回读；只有 cfg 中目标 EARFCN/PCI 或清空状态得到确认才报告成功。
    /// </summary>
    public Task<C2000WriteResult> SetCellLockAsync(string channel, int modelType, string earfcn, string pci,
        string band,
        bool enable, CancellationToken ct = default)
        => RunWriteAsync(() => SetCellLockCoreAsync(channel, modelType, earfcn, pci, band, enable, ct), ct);

    private async Task<C2000WriteResult> SetCellLockCoreAsync(string channel, int modelType, string earfcn, string pci,
        string band, bool enable, CancellationToken ct)
    {
        if (modelType is not (4 or 5))
            throw new RouterException(RouterErrorKind.Device, "只支持 LTE(4) 或 NR(5) 小区锁。");
        if (!int.TryParse(earfcn, NumberStyles.Integer, CultureInfo.InvariantCulture, out var earfcnValue) ||
            earfcnValue is < 0 or > 875000)
            throw new RouterException(RouterErrorKind.Device, "EARFCN/NR-ARFCN 必须在 0–875000 之间。");
        var maxPci = modelType == 5 ? 1007 : 503;
        if (!int.TryParse(pci, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pciValue) ||
            pciValue < 0 || pciValue > maxPci)
            throw new RouterException(RouterErrorKind.Device, $"PCI 必须在 0–{maxPci} 之间。");

        var before = await GetCpestatusAsync(channel, ct).ConfigureAwait(false);
        var beforeText = DescribeLockConfig(before.Cfg);
        var body = await _client.PostFormAsync(
            "/cgi-bin/luci/nradio/cellular/cpestatus/earfcn_lock",
            new Dictionary<string, string>
            {
                ["model"] = channel,
                ["sim_id"] = "1",
                ["action"] = enable ? "1" : "0",
                ["model_type"] = modelType.ToString(CultureInfo.InvariantCulture),
                ["earfcn"] = earfcnValue.ToString(CultureInfo.InvariantCulture),
                ["pci"] = pciValue.ToString(CultureInfo.InvariantCulture),
                ["band"] = band.Replace("n", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("B", "", StringComparison.OrdinalIgnoreCase),
            },
            "earfcn-lock",
            ct).ConfigureAwait(false);

        JsonNode root;
        try
        {
            root = JsonNode.Parse(body) ?? throw new JsonException("空响应");
        }
        catch (JsonException ex)
        {
            throw new RouterException(RouterErrorKind.Protocol,
                "锁频接口返回了非 JSON 响应；结果待确认。", "earfcn-lock", ex);
        }
        var code = root["result"]?["code"]?.ToString();
        if (code is not null && code != "0")
            throw new RouterException(RouterErrorKind.Device,
                $"设备拒绝锁频请求（code={code}）；未报告成功。", "earfcn-lock");

        var deadline = DateTime.UtcNow.AddSeconds(20);
        C2000CpestatusResult? after = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1500, ct).ConfigureAwait(false);
            after = await GetCpestatusAsync(channel, ct).ConfigureAwait(false);
            var cfg = modelType == 5 ? after.Cfg?.Nr : after.Cfg?.Lte;
            var confirmed = enable
                ? string.Equals(cfg?.Earfcn, earfcnValue.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                  string.Equals(cfg?.Pci, pciValue.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                : string.IsNullOrWhiteSpace(after.Cfg?.Nr?.Earfcn) && string.IsNullOrWhiteSpace(after.Cfg?.Lte?.Earfcn);
            if (confirmed)
            {
                var afterText = DescribeLockConfig(after.Cfg);
                return new C2000WriteResult(true,
                    enable ? "小区锁已写入并回读确认。" : "设备已恢复自动选频并回读确认。",
                    beforeText, afterText);
            }
        }

        var pending = DescribeLockConfig(after?.Cfg);
        return new C2000WriteResult(false,
            "写入请求已发送，但 20 秒内回读未确认目标状态；结果待确认，不能视为成功。",
            beforeText, pending);
    }

    private static string DescribeLockConfig(C2000LockConfig? cfg)
    {
        var nr = cfg?.Nr;
        var lte = cfg?.Lte;
        return $"NR: BAND={nr?.Band ?? ""}, EARFCN={nr?.Earfcn ?? ""}, PCI={nr?.Pci ?? ""}; " +
               $"LTE: BAND={lte?.Band ?? ""}, EARFCN={lte?.Earfcn ?? ""}, PCI={lte?.Pci ?? ""}";
    }

    private static Dictionary<string, string> ExtractCbiFields(string html)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match input in Regex.Matches(html, @"<input\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase))
        {
            var attrs = input.Groups["attrs"].Value;
            var type = Attr(attrs, "type")?.ToLowerInvariant() ?? "text";
            if (type is "submit" or "button" or "reset" or "file" or "image") continue;
            if (type is "checkbox" or "radio" && !Regex.IsMatch(attrs, @"\bchecked(?:=""checked"")?", RegexOptions.IgnoreCase))
                continue;
            var name = Attr(attrs, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            fields[name] = System.Net.WebUtility.HtmlDecode(Attr(attrs, "value") ?? "");
        }

        foreach (Match select in Regex.Matches(html,
                     @"<select\b(?<attrs>[^>]*)>(?<body>.*?)</select>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var name = Attr(select.Groups["attrs"].Value, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var options = Regex.Matches(select.Groups["body"].Value, @"<option\b(?<attrs>[^>]*)>(?<text>.*?)</option>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var selected = options.Cast<Match>().FirstOrDefault(x =>
                Regex.IsMatch(x.Groups["attrs"].Value, @"\bselected(?:=""selected"")?", RegexOptions.IgnoreCase))
                ?? options.Cast<Match>().FirstOrDefault();
            if (selected is not null)
                fields[name] = System.Net.WebUtility.HtmlDecode(Attr(selected.Groups["attrs"].Value, "value") ?? "");
        }
        return fields;
    }

    private static string? Attr(string attrs, string name)
    {
        var match = Regex.Match(attrs, $@"\b{Regex.Escape(name)}\s*=\s*""(?<v>[^""]*)""",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value : null;
    }

    private static string? SelectedOption(string html, string name)
    {
        var select = Regex.Match(html,
            $@"<select\b(?=[^>]*\bname\s*=\s*""{Regex.Escape(name)}"")(?<attrs>[^>]*)>(?<body>.*?)</select>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!select.Success) return null;
        var options = Regex.Matches(select.Groups["body"].Value, @"<option\b(?<attrs>[^>]*)>(?<text>.*?)</option>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var selected = options.Cast<Match>().FirstOrDefault(x =>
            Regex.IsMatch(x.Groups["attrs"].Value, @"\bselected(?:=""selected"")?", RegexOptions.IgnoreCase))
            ?? options.Cast<Match>().FirstOrDefault();
        return selected is null ? null
            : System.Net.WebUtility.HtmlDecode(Attr(selected.Groups["attrs"].Value, "value") ?? "");
    }

    private static void RejectCbiError(string response, string endpoint)
    {
        if (response.Contains("Invalid token", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("Token mismatch", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("cbi-input-invalid", StringComparison.OrdinalIgnoreCase))
            throw new RouterException(RouterErrorKind.Device,
                "设备拒绝了配置表单（token 或参数校验失败）；未报告成功。", endpoint);
    }

    private static string? AttributeValue(string html, string tag, string name, string attribute)
    {
        var element = Regex.Match(html,
            $@"<{Regex.Escape(tag)}\b(?=[^>]*\bname\s*=\s*""{Regex.Escape(name)}"")(?<attrs>[^>]*)>",
            RegexOptions.IgnoreCase);
        return element.Success
            ? System.Net.WebUtility.HtmlDecode(Attr(element.Groups["attrs"].Value, attribute) ?? "")
            : null;
    }

    private static void RejectJsonCode(string response, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(response)) return;
        JsonNode root;
        try { root = JsonNode.Parse(response) ?? throw new JsonException("空响应"); }
        catch (JsonException ex)
        {
            if (response.Contains("Invalid token", StringComparison.OrdinalIgnoreCase) ||
                response.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                response.Contains("failed", StringComparison.OrdinalIgnoreCase))
                throw new RouterException(RouterErrorKind.Device,
                    "设备拒绝该操作；结果未确认。", endpoint, ex);
            return;
        }
        var code = root["result"]?["code"]?.ToString() ?? root["code"]?.ToString();
        if (code is not null && code != "0")
            throw new RouterException(RouterErrorKind.Device,
                $"设备拒绝流量清零（code={code}）。", endpoint);
    }

    private static string DescribeDataUsage(C2000DataUsageSnapshot snapshot)
        => string.Join("; ", snapshot.Sims.Select(x =>
            $"{x.Alias ?? $"SIM{x.Index}"}: 月 {x.MonthlyUsedGb:0.###} GB / 累计 {x.TotalUsedGb:0.###} GB"));

    public async Task<C2000SmsSnapshot> GetSmsSnapshotAsync(string channel = "wan0", CancellationToken ct = default)
    {
        var unreadTask = _client.GetSmsUnreadAsync(ct);
        var listTask = _client.GetSmsListAsync(channel, ct);
        await Task.WhenAll(unreadTask, listTask).ConfigureAwait(false);
        var unread = (await unreadTask.ConfigureAwait(false)).Result
            ?.FirstOrDefault(x => string.Equals(x.InterfaceName, channel, StringComparison.OrdinalIgnoreCase));
        var list = (await listTask.ConfigureAwait(false)).Result;
        var on = J.Bool(list?.On) ?? false;
        var ready = (list?.Ready ?? 0) != 0;
        var status = !on
            ? "短信服务当前未启用（设备返回 on=0；可能未开启 IMS 或当前 SIM/运营商不支持）"
            : !ready
                ? "短信服务已启用，但当前不可发送/接收（ready=0）"
                : "短信服务可用";
        return new C2000SmsSnapshot(on, ready, unread?.Count ?? 0, unread?.Used ?? 0,
            unread?.Max ?? 0, list?.Messages ?? new List<C2000SmsMessage>(), status);
    }

    public async Task<C2000DataUsageSnapshot> GetDataUsageAsync(string channel = "wan0",
        CancellationToken ct = default)
    {
        var body = await _client.PostFormAsync(
            "/cgi-bin/luci/nradio/cellular/cpedata/refresh",
            new Dictionary<string, string> { ["channel"] = channel },
            "cpedata-refresh",
            ct).ConfigureAwait(false);
        var envelope = C2000ProClient.Deserialize<C2000DataUsageEnvelope>(body, "cpedata-refresh");
        var sims = envelope.Result?.Data?.SelectMany(x => x).ToList() ?? new List<C2000SimDataUsage>();
        return new C2000DataUsageSnapshot(sims, DateTime.Now);
    }

    public async Task<(string? Bandwidth24, string? Bandwidth5)> GetWifiBandwidthsAsync(
        CancellationToken ct = default)
    {
        var html = await _client.GetWifiHtmlAsync(ct).ConfigureAwait(false);
        return (
            SelectedOption(html, "cbid.cloudd.d0.htmode_g1_radio0"),
            SelectedOption(html, "cbid.cloudd.d0.htmode_g1_radio1"));
    }

    public Task<C2000WriteResult> ResetDataUsageAsync(bool resetMonthly, bool resetTotal,
        CancellationToken ct = default)
        => RunWriteAsync(() => ResetDataUsageCoreAsync(resetMonthly, resetTotal, ct), ct);

    public Task<C2000WriteResult> SetClientAccessAsync(string mac, bool allowed,
        CancellationToken ct = default)
        => RunWriteAsync(() => SetClientAccessCoreAsync(mac, allowed, ct), ct);

    private async Task<C2000WriteResult> SetClientAccessCoreAsync(string mac, bool allowed, CancellationToken ct)
    {
        if (!Regex.IsMatch(mac, @"^[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}$"))
            throw new RouterException(RouterErrorKind.Device, "终端 MAC 地址格式无效。", "client-access");
        var before = await GetSnapshotAsync(ct).ConfigureAwait(false);
        var old = before.Runtime.TerminalAll?.Clients?.GetValueOrDefault(mac);
        var page = await _client.GetTextAsync("/nradio/system/client", "client-page", ct).ConfigureAwait(false);
        var token = AttributeValue(page, "input", "token", "value");
        if (string.IsNullOrWhiteSpace(token))
            throw new RouterException(RouterErrorKind.Protocol, "终端管理页缺少 CSRF token。", "client-access");
        var response = await _client.PostFormAsync(
            "/cgi-bin/luci/nradio/client/access_set",
            new Dictionary<string, string>
            {
                ["token"] = token,
                ["enabled"] = allowed ? "1" : "0",
                ["mac"] = mac,
            },
            "client-access",
            ct).ConfigureAwait(false);
        RejectJsonCode(response, "client-access");
        await Task.Delay(700, ct).ConfigureAwait(false);
        _lastSnapshot = null;
        var after = await GetSnapshotAsync(ct).ConfigureAwait(false);
        var now = after.Runtime.TerminalAll?.Clients?.GetValueOrDefault(mac);
        var confirmed = now is not null && (now.Switch != 0) == allowed;
        return new C2000WriteResult(confirmed,
            confirmed ? (allowed ? "已允许该终端上网并回读确认。" : "已禁止该终端上网并回读确认。")
                : "终端权限请求已发送，但回读未确认；结果待确认。",
            old is null ? "终端不在当前列表" : $"switch={old.Switch}",
            now is null ? "终端不在当前列表" : $"switch={now.Switch}");
    }

    private async Task<C2000WriteResult> ResetDataUsageCoreAsync(bool resetMonthly, bool resetTotal,
        CancellationToken ct)
    {
        var before = await GetDataUsageAsync("wan0", ct).ConfigureAwait(false);
        var beforeText = DescribeDataUsage(before);
        var page = await _client.GetTextAsync("/cgi-bin/luci/nradioadv/network/cpedata", "cpedata", ct)
            .ConfigureAwait(false);
        var token = AttributeValue(page, "input", "token", "value");
        if (string.IsNullOrWhiteSpace(token))
            throw new RouterException(RouterErrorKind.Protocol, "流量管理页缺少 CSRF token。", "cpedata-clean");

        var types = new List<string>();
        if (resetMonthly) types.Add("0");
        if (resetTotal) types.Add("1");
        if (types.Count == 0)
            return new C2000WriteResult(false, "未选择要清零的流量类型。", beforeText, beforeText);

        foreach (var sim in before.Sims)
        {
            foreach (var type in types)
            {
                var response = await _client.PostFormAsync(
                    "/cgi-bin/luci/nradio/cellular/cpedata/clean",
                    new Dictionary<string, string>
                    {
                        ["token"] = token,
                        ["channel"] = sim.ModelName ?? "wan0",
                        ["cur"] = sim.Index.ToString(CultureInfo.InvariantCulture),
                        ["type"] = type,
                    },
                    "cpedata-clean",
                    ct).ConfigureAwait(false);
                RejectJsonCode(response, "cpedata-clean");
            }
        }

        await Task.Delay(800, ct).ConfigureAwait(false);
        var after = await GetDataUsageAsync("wan0", ct).ConfigureAwait(false);
        var confirmed = after.Sims.All(x =>
            (!resetMonthly || x.MonthlyUsedGb < 0.011) &&
            (!resetTotal || x.TotalUsedGb < 0.011));
        return new C2000WriteResult(confirmed,
            confirmed ? "设备流量统计已清零并回读确认。" : "清零请求已发送，但回读仍发现非零统计，结果待确认。",
            beforeText, DescribeDataUsage(after));
    }

    public Task<C2000WriteResult> SendSmsAsync(string channel, string phoneNumber, string message,
        CancellationToken ct = default)
        => RunWriteAsync(() => SendSmsCoreAsync(channel, phoneNumber, message, ct), ct);

    private async Task<C2000WriteResult> SendSmsCoreAsync(string channel, string phoneNumber, string message,
        CancellationToken ct)
    {
        phoneNumber = phoneNumber.Trim();
        if (phoneNumber.Length is < 5 or > 32 ||
            phoneNumber.Any(c => !(char.IsDigit(c) || c is '+' or '-' or ' ')))
            throw new RouterException(RouterErrorKind.Device, "收件号码格式无效。");
        if (string.IsNullOrWhiteSpace(message))
            throw new RouterException(RouterErrorKind.Device, "短信正文不能为空。");
        if (message.Length > 1600)
            throw new RouterException(RouterErrorKind.Device, "短信正文过长；为控制分段风险，单次最多 1600 字符。");

        var availability = await GetSmsSnapshotAsync(channel, ct).ConfigureAwait(false);
        if (!availability.ServiceEnabled || !availability.Ready)
            throw new RouterException(RouterErrorKind.NotSupported, availability.StatusText, "sms-send");

        var body = await _client.PostFormAsync(
            "/cgi-bin/luci/nradio/cellular/sms/send",
            new Dictionary<string, string>
            {
                ["msg"] = message,
                ["phone_num"] = phoneNumber,
                ["channel"] = channel,
            },
            "sms-send",
            ct).ConfigureAwait(false);
        JsonNode root;
        try { root = JsonNode.Parse(body) ?? throw new JsonException("空响应"); }
        catch (JsonException ex)
        {
            throw new RouterException(RouterErrorKind.Protocol,
                "短信接口返回非 JSON，发送结果无法确认。", "sms-send", ex);
        }
        var code = root["result"]?["code"]?.ToString();
        if (code is null || code != "0")
        {
            var reason = code switch
            {
                "-2" => "模块信息读取失败",
                "-4" => "短信存储空间不足",
                _ => "当前 SIM/运营商无法收发短信",
            };
            throw new RouterException(RouterErrorKind.Device, $"短信发送失败：{reason}（code={code ?? "无"}）。", "sms-send");
        }

        var items = root["result"]?["item"] as JsonArray;
        if (items is not null && items.Any(x => x?["code"]?.ToString() is { } c && c != "0"))
            return new C2000WriteResult(false,
                "设备返回了部分分段发送失败标记；结果待确认，请联系收件方核对。",
                "短信未发送", "部分分段失败");

        var after = await GetSmsSnapshotAsync(channel, ct).ConfigureAwait(false);
        return new C2000WriteResult(true,
            $"短信接口返回成功；短信列表已刷新（未读 {after.Unread}，消息 {after.Messages.Count} 条）。正文与号码未写入日志。",
            "发送前", "设备 code=0，item 无错误");
    }

    internal static C2000DeviceIdentity ParseIdentity(string html, C2000Runtime runtime)
    {
        var model = MatchValue(html, @"Model:\s*</div>\s*<div[^>]*>\s*(?<v>[^<]+)</div>")
                    ?? runtime.Cpe?.FirstOrDefault()?.Model
                    ?? "C2000 Pro";
        var firmware = MatchValue(html, @"Firmware Version:\s*</div>\s*<div[^>]*>\s*(?<v>[^<]+)</div>")
                       ?? "—";
        var mac = MatchValue(html, @"MAC Address:\s*</div>\s*<div[^>]*>\s*(?<v>[^<]+)</div>")
                  ?? runtime.Cpe?.FirstOrDefault()?.Mac
                  ?? "—";
        var board = MatchValue(html, @"var\s+board_name\s*=\s*""(?<v>[^""]+)""")
                    ?? "—";
        var hardware = runtime.Cpe?.FirstOrDefault()?.Model ?? board;
        return new C2000DeviceIdentity(model.Trim(), firmware.Trim(), "—", hardware, board, mac.Trim());
    }

    private static string? MatchValue(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups["v"].Value).Trim() : null;
    }

    private static string? ExtractBalancedObject(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }
        return null;
    }

    private static string GuessNrBand(string earfcn)
    {
        // 仅用于页面展示当前邻居的频段提示；设备返回 BAND 时始终优先使用设备值。
        var n = TryLong(earfcn);
        if (n is null) return "";
        return n switch
        {
            >= 499200 and <= 537999 => "n41",
            >= 422000 and <= 434000 => "n1",
            >= 620000 and <= 653333 => "n78",
            _ => "—",
        };
    }

    internal static long? TryLong(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private async Task<T> RunWriteAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (!await _writeGate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new RouterException(RouterErrorKind.Device,
                "已有配置写入正在执行，请等待其完成后再试。", "write-gate");
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _snapshotGate.Dispose();
        _writeGate.Dispose();
    }
}

/// <summary>
/// 将 C2000 Pro 响应映射为旧页面可消费的统一模型，保证旧 UI 模块逐步迁移时不中断。
/// 只返回设备真实字段；缺失字段保持空值，不补造 0。
/// </summary>
internal sealed class C2000CompatibilityApi : IDeviceApi
{
    private readonly C2000ProAdapter _adapter;

    public C2000CompatibilityApi(C2000ProAdapter adapter) => _adapter = adapter;

    private async Task<(C2000Runtime Runtime, C2000CellInfoItem Cell, C2000DeviceIdentity Identity)>
        SnapshotAsync(CancellationToken ct)
    {
        var snapshot = await _adapter.GetSnapshotAsync(ct).ConfigureAwait(false);
        return (snapshot.Runtime, snapshot.Cell, snapshot.Identity);
    }

    public async Task<LoginInfo> GetLoginInfoAsync(CancellationToken ct)
    {
        await SnapshotAsync(ct).ConfigureAwait(false);
        return new LoginInfo { LoginStatus = "1", Username = _adapter.Username };
    }

    public async Task<StatusInfo> GetStatusAsync(CancellationToken ct)
    {
        var (runtime, cell, _) = await SnapshotAsync(ct).ConfigureAwait(false);
        var cpe = cell.Status ?? runtime.Cpe?.FirstOrDefault();
        var rsrp = C2000ProAdapter.TryLong(cpe?.Rsrp);
        var charging = runtime.Battery?.Charging == 1 ? "1" : "0";
        var percent = runtime.Battery?.Percent is >= 0 ? runtime.Battery.Percent.Value.ToString(CultureInfo.InvariantCulture) : "";
        return new StatusInfo
        {
            WifiClientsNum = runtime.TerminalAll?.Count?.ToString(CultureInfo.InvariantCulture) ?? "",
            RunSeconds = runtime.Global?.Uptime?.ToString(CultureInfo.InvariantCulture) ?? "",
            BatteryPercent = percent,
            BatteryCharging = charging,
            BatteryCharge = runtime.Battery?.Charging == 1 ? "1" : "0",
            TxByteAll = cpe?.Upload ?? "",
            RxByteAll = cpe?.Download ?? "",
            SignalQuality = SignalQualityFromRsrp(rsrp)?.ToString(CultureInfo.InvariantCulture) ?? "",
            Rssi = cpe?.Rsrp ?? "",
            SysMode = cpe?.Mode ?? runtime.Global?.NetType ?? "",
            Roaming = "",
            NewSmsNum = "",
            TxSpeed = "",
            RxSpeed = "",
            LoginStatus = "1",
            CpuPercent = runtime.Global?.CpuPercent ?? cpe?.CpuPercent ?? "",
            MemoryPercent = runtime.Global?.MemPercent ?? cpe?.MemPercent ?? "",
            DeviceTemperatureC = cpe?.ModelTemp ?? runtime.Global?.DeviceTemp ?? "",
        };
    }

    public async Task<HomepageInfo> GetHomepageAsync(CancellationToken ct)
    {
        var (runtime, cell, identity) = await SnapshotAsync(ct).ConfigureAwait(false);
        var cpe = cell.Status ?? runtime.Cpe?.FirstOrDefault();
        var radio = PreferredRadio(runtime);
        var lan = runtime.Lans?.FirstOrDefault();
        return new HomepageInfo
        {
            WlanEnable = radio is null ? "" : (IsRadioEnabled(runtime, radio) ? "1" : "0"),
            NetworkName = cpe?.Isp ?? "",
            ConnectDisconnect = cpe is not null && !string.IsNullOrWhiteSpace(cpe.Isp) ? "cellular" : "disabled",
            SimStatus = cpe?.Cpin switch { "0" => "0", null or "" => "", _ => "1" },
            PinStatus = cpe?.Cpin ?? "",
            Mac = identity.MacAddress,
            Imei = cpe?.Imei ?? "",
            Imsi = cpe?.Imsi ?? "",
            Iccid = cpe?.Iccid ?? "",
            SwVersion = identity.FirmwareVersion,
            HwVersion = identity.Hardware,
            DeviceName = identity.Model,
            DeviceModel = identity.Model,
            LanIp = lan?.Addresses?.FirstOrDefault()?.IpAddress ?? "",
            WanIp = cpe?.V4?.Addresses?.FirstOrDefault() ?? "",
            SsidRaw = radio?.Ssid ?? "",
            MaxAccessNumber = "",
            SerialNumber = "",
        };
    }

    public async Task<FirmwareInfo> GetFirmwareAsync(CancellationToken ct)
    {
        var (_, _, identity) = await SnapshotAsync(ct).ConfigureAwait(false);
        return new FirmwareInfo
        {
            VersionNum = identity.FirmwareVersion,
            VersionDate = identity.FirmwareDate,
            HardwareVersion = identity.Hardware,
            TwoPartition = "",
        };
    }

    public async Task<MssStatus> GetMssStatusAsync(CancellationToken ct)
    {
        var (runtime, cell, _) = await SnapshotAsync(ct).ConfigureAwait(false);
        var cpe = cell.Status ?? runtime.Cpe?.FirstOrDefault();
        return new MssStatus
        {
            TotalSimCount = cpe?.SimCount?.ToString(CultureInfo.InvariantCulture) ?? "",
            VirtualIccid = "",
        };
    }

    public async Task<MssSupportInfo> GetMssSupportAsync(CancellationToken ct)
    {
        var (runtime, cell, _) = await SnapshotAsync(ct).ConfigureAwait(false);
        var cpe = cell.Status ?? runtime.Cpe?.FirstOrDefault();
        var count = Math.Max(0, cpe?.SimCount ?? 0);
        var list = Enumerable.Range(1, count).Select(i => new SimCard
        {
            SimId = i.ToString(CultureInfo.InvariantCulture),
            SimName = $"Embedded SIM{i}",
            SimType = "embedded",
            SimPresent = "1",
            SimBanned = "0",
            SimIccid = string.Equals(cpe?.SimNo, i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ? cpe?.Iccid ?? "" : "",
            SimImsi = string.Equals(cpe?.SimNo, i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ? cpe?.Imsi ?? "" : "",
        }).ToList();
        return new MssSupportInfo
        {
            SwitchMode = "",
            CurrentSimId = cpe?.SimNo ?? "",
            MasterSimId = cpe?.SimNo ?? "",
            SoleSimId = "",
            SimList = list,
        };
    }

    public async Task<EngineeringInfo> GetEngineeringAsync(CancellationToken ct)
    {
        var (_, cell, _) = await SnapshotAsync(ct).ConfigureAwait(false);
        var c = cell.Status;
        return new EngineeringInfo
        {
            EngineeringMode = "1",
            SysMode = c?.Mode ?? "",
            Lte = c is null ? null : new LteCell
            {
                Tac = c.Tac ?? c.Lac ?? "",
                PhyCellId = c.Pci ?? "",
                DlEuArfcn = c.Earfcn ?? "",
                Band = c.Band ?? "",
                DlBandwidth = c.DownlinkBandwidthKhz ?? "",
                CellId = c.Cell ?? "",
                Ecgi = c.Cell ?? "",
                Rsrp = c.Rsrp ?? "",
                Rsrq = c.Rsrq ?? "",
                Sinr = c.Sinr ?? "",
                Rssi = c.Rssi ?? "",
                Cqi = c.Cqi ?? "",
            },
        };
    }

    public async Task<WanInfo> GetWanInfoAsync(CancellationToken ct)
    {
        var (runtime, cell, _) = await SnapshotAsync(ct).ConfigureAwait(false);
        var cpe = cell.Status ?? runtime.Cpe?.FirstOrDefault();
        return new WanInfo
        {
            Proto = runtime.Wans?.FirstOrDefault()?.Proto ?? "",
            NwMode = cpe?.Mode ?? "",
            NwModeDefault = "",
            PreferMode = "",
            PreferLteType = "",
            ConnectMode = "",
            RoamingDisableAutoDial = "",
            Mtu = "",
        };
    }

    public async Task<WanApnProfileInfo> GetApnProfilesAsync(CancellationToken ct)
    {
        var (runtime, cell, _) = await SnapshotAsync(ct).ConfigureAwait(false);
        var cpe = cell.Status ?? runtime.Cpe?.FirstOrDefault();
        var profiles = new List<ApnProfile>();
        if (!string.IsNullOrWhiteSpace(cpe?.Apn))
            profiles.Add(new ApnProfile
            {
                ProfileName = "当前连接 APN（设备上报）",
                Apn = cpe.Apn,
                LteApn = cpe.Apn,
                Enable = "1",
                IsDefault = "1",
            });
        return new WanApnProfileInfo { Apn = cpe?.Apn ?? "", ProfileList = profiles };
    }

    public async Task<WanPdpContextInfo> GetPdpContextAsync(CancellationToken ct)
    {
        var (runtime, cell, _) = await SnapshotAsync(ct).ConfigureAwait(false);
        var cpe = cell.Status ?? runtime.Cpe?.FirstOrDefault();
        var wan = runtime.Wans?.FirstOrDefault(x => string.Equals(x.ClassName, cpe?.Name, StringComparison.OrdinalIgnoreCase))
                  ?? runtime.Wans?.FirstOrDefault();
        var contexts = cpe is null ? new List<PdpContext>() : new List<PdpContext>
        {
            new()
            {
                RuleName = cpe.Name ?? "wan0",
                Success = cpe.Status == 0 ? "1" : "0",
                IsDefault = "1",
                Ipv4 = cpe.V4?.Addresses?.FirstOrDefault() ?? "",
                V4Dns1 = cpe.V4?.Dns?.ElementAtOrDefault(0) ?? "",
                V4Dns2 = cpe.V4?.Dns?.ElementAtOrDefault(1) ?? "",
                V4Gateway = wan?.Gateway ?? "",
                Ipv6 = cpe.V6?.Addresses?.FirstOrDefault() ?? "",
                V6Dns1 = cpe.V6?.Dns?.ElementAtOrDefault(0) ?? "",
                V6Dns2 = cpe.V6?.Dns?.ElementAtOrDefault(1) ?? "",
                CurConnTime = wan?.Uptime?.ToString(CultureInfo.InvariantCulture) ?? "",
            },
        };
        return new WanPdpContextInfo { NetworkName = cpe?.Isp ?? "", PdpContextList = contexts };
    }

    public async Task<WlanInfo> GetWlanInfoAsync(CancellationToken ct)
    {
        var (runtime, _, _) = await SnapshotAsync(ct).ConfigureAwait(false);
        var radio = PreferredRadio(runtime);
        return new WlanInfo
        {
            DualBandSupport = runtime.Wlan?.Radios?.Count > 1 ? "1" : "0",
            RfBand = radio?.FrequencyGhz ?? "",
            NetMode = radio?.Encryption ?? "",
            Channel = radio?.Channel ?? "",
            Bandwidth = "",
            WlanEnable = radio is null ? "" : (IsRadioEnabled(runtime, radio) ? "1" : "0"),
            MaxClients = radio?.StationCount?.ToString(CultureInfo.InvariantCulture) ?? "",
            InitialMaxClients = "",
            ApIsolate = "",
        };
    }

    public async Task<WlanSecurityInfo> GetWlanSecurityAsync(CancellationToken ct)
    {
        var (runtime, _, _) = await SnapshotAsync(ct).ConfigureAwait(false);
        var radio = PreferredRadio(runtime);
        var mode = radio?.Encryption switch
        {
            "psk-mixed" => "Mixed",
            "psk2" => "WPA2-PSK",
            "sae" or "sae-mixed" => "WPA3-SAE",
            null or "" => "",
            var raw => raw,
        };
        return new WlanSecurityInfo
        {
            NetMode = mode,
            SsidRaw = radio?.Ssid ?? "",
            SsidBcast = radio?.Hidden == 1 ? "0" : "1",
            Mode = mode,
            // 密码不映射到兼容层，避免旧页面把设备密钥当作普通配置明文绑定。
        };
    }

    public Task<WlanWpsInfo> GetWpsAsync(CancellationToken ct)
        => Task.FromResult(new WlanWpsInfo { WpsStatus = "" });

    public Task<WlanMacFiltersInfo> GetMacFiltersAsync(CancellationToken ct)
        => Task.FromResult(new WlanMacFiltersInfo());

    public async Task<DhcpInfo> GetDhcpAsync(CancellationToken ct)
    {
        var (runtime, _, _) = await SnapshotAsync(ct).ConfigureAwait(false);
        var lan = runtime.Lans?.FirstOrDefault();
        var address = lan?.Addresses?.FirstOrDefault();
        return new DhcpInfo
        {
            Ip = address?.IpAddress?.Split('/')[0] ?? "",
            Mask = address?.NetMask ?? "",
            Status = lan?.Status?.ToString(CultureInfo.InvariantCulture) ?? "",
        };
    }

    public async Task<DeviceManagementInfo> GetDevicesAsync(CancellationToken ct)
    {
        var (runtime, _, _) = await SnapshotAsync(ct).ConfigureAwait(false);
        var list = new List<ClientEntry>();
        foreach (var item in runtime.TerminalAll?.Clients?.Values ??
                     Enumerable.Empty<C2000Terminal>())
        {
            list.Add(new ClientEntry
            {
                Index = "",
                Mac = item.Mac ?? "",
                NameRaw = item.Hostname ?? "",
                NameText = item.Hostname ?? "",
                Status = item.Switch == 0 ? "2" : item.Exist == 1 ? "1" : "0",
                ConnType = item.Type == "wireless" ? "WIFI" : item.Type ?? "",
                Ip = item.IpAddress ?? "",
                AccessTime = item.AssocTime?.ToString(CultureInfo.InvariantCulture) ?? "",
                Rx = item.RxBytes ?? "",
                Tx = item.TxBytes ?? "",
                Rssi = item.Rssi ?? "",
            });
        }
        return new DeviceManagementInfo { ClientList = list, KnownDevices = new List<KnownDevice>() };
    }

    public Task<AccountManagementInfo> GetAccountsAsync(CancellationToken ct)
        => Task.FromResult(new AccountManagementInfo { RouterUsername = "root" });

    public Task<CustomFwInfo> GetCustomFwAsync(CancellationToken ct)
        => Task.FromResult(new CustomFwInfo());

    private static C2000Radio? PreferredRadio(C2000Runtime runtime)
        => runtime.Wlan?.Radios?
            .OrderByDescending(r => r.FrequencyGhz is not null && r.FrequencyGhz.StartsWith("5", StringComparison.Ordinal))
            .FirstOrDefault();

    private static bool IsRadioEnabled(C2000Runtime runtime, C2000Radio radio)
        => radio.InterfaceName?.EndsWith('0') == true
            ? runtime.Wlan?.Radio2Disabled != 1
            : runtime.Wlan?.Radio5Disabled != 1;

    private static int? SignalQualityFromRsrp(long? rsrp) => rsrp switch
    {
        null => null,
        >= -80 => 5,
        >= -90 => 4,
        >= -100 => 3,
        >= -110 => 2,
        _ => 1,
    };

    private static Task Unsupported(string operation)
        => Task.FromException(new RouterException(RouterErrorKind.NotSupported,
            "当前 C2000 Pro 页面尚未确认该写操作的安全载荷，已阻止写入。", operation));

    public Task SetConnectionAsync(bool connect, CancellationToken ct) => Unsupported("wan/connect");
    public Task SetConnectModeAsync(string connectMode, bool disableAutoDialOnRoaming, CancellationToken ct) => Unsupported("wan/mode");
    public Task SetNetworkAsync(string proto, string nwMode, string preferMode, string preferLteType, int? mtu, CancellationToken ct) => Unsupported("wan/network");
    public Task SetMtuAsync(int mtu, CancellationToken ct) => Unsupported("wan/mtu");
    public Task SetAutoApnAsync(bool auto, CancellationToken ct) => Unsupported("wan/apn");
    public Task SetApnProfilesAsync(IEnumerable<ApnProfile> profiles, CancellationToken ct) => Unsupported("wan/apn-profiles");
    public Task SetWlanAsync(string rfBand, string netMode, string channel, string bandwidth, int maxClients, bool apIsolate, string? secChannel, CancellationToken ct) => Unsupported("wifi/basic");
    public Task SetWlanEnableAsync(bool enable, CancellationToken ct) => Unsupported("wifi/enable");
    public Task SetWifiSleepAsync(bool enable, int minutes, CancellationToken ct) => Unsupported("wifi/sleep");
    public Task SetWlanSecurityAsync(string ssid, bool broadcastSsid, string securityMode, string? password, string? encryption, CancellationToken ct) => Unsupported("wifi/security");
    public Task SetWpsAsync(string connectMethod, string? pin, CancellationToken ct) => Unsupported("wifi/wps");
    public Task SetMacFiltersAsync(bool enable, string mode, IEnumerable<string>? allow, IEnumerable<string>? deny, IEnumerable<int>? deleteAllowIndexes, IEnumerable<int>? deleteDenyIndexes, CancellationToken ct) => Unsupported("wifi/acl");
    public Task SetLanAsync(string? ip, string? start, string? end, int? leaseTime, bool? dnsEnable, string? dns1, string? dns2, bool? dhcpV6, bool? redirectEnable, string? redirectUrl, CancellationToken ct) => Unsupported("lan/dhcp");
    public Task AddStaticIpAsync(string mac, string ip, CancellationToken ct) => Unsupported("lan/fixed-ip");
    public Task DeleteStaticIpAsync(string mac, CancellationToken ct) => Unsupported("lan/fixed-ip");
    public Task DeviceActionAsync(string action, string? mac, CancellationToken ct) => Unsupported("client/action");
    public Task SetAccountsAsync(string accountAction, IEnumerable<UserAccount> users, CancellationToken ct) => Unsupported("account");
    public Task SwitchSimAsync(string switchMode, string? soleSimId, CancellationToken ct) => Unsupported("sim/switch");
    public Task SetCustomFwAsync(JsonArray rules, CancellationToken ct) => Unsupported("firewall");
    public Task RestartAsync(CancellationToken ct) => Unsupported("system/reboot");
    public Task RestoreFactoryAsync(CancellationToken ct) => Unsupported("system/factory-reset");
}
