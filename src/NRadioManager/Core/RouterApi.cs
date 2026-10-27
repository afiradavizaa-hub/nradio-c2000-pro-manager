using System.Text.Json;
using System.Text.Json.Nodes;

namespace NRadioManager.Core;

/// <summary>
/// 设备业务接口层：把 RouterClient 的原始 JSON 映射为强类型模型，
/// 并把写操作的载荷形状集中在这里（形状来自原厂前端 base.js 的实测提取）。
/// </summary>
public sealed class RouterApi : IDeviceApi
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly RouterClient _client;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public RouterApi(RouterClient client) => _client = client;

    public RouterClient Client => _client;

    // ---------------- 只读 ----------------

    private async Task<T> GetAsync<T>(string endpoint, CancellationToken ct) where T : class
    {
        var body = await _client.GetJsonAsync(endpoint, ct).ConfigureAwait(false);
        return Deserialize<T>(body, endpoint);
    }

    public static T Deserialize<T>(string body, string endpoint) where T : class
    {
        var obj = RouterClient.ParseObject(body, endpoint);
        try
        {
            return obj.Deserialize<T>(Options) ?? throw new RouterException(RouterErrorKind.Protocol, "响应反序列化为空。", endpoint);
        }
        catch (JsonException ex)
        {
            throw new RouterException(RouterErrorKind.Protocol, "字段解析失败：" + ex.Message, endpoint, ex);
        }
    }

    public Task<LoginInfo> GetLoginInfoAsync(CancellationToken ct) => GetAsync<LoginInfo>(RouterEndpoints.LoginInfo, ct);
    public Task<StatusInfo> GetStatusAsync(CancellationToken ct) => GetAsync<StatusInfo>(RouterEndpoints.StatusInfo, ct);
    public Task<HomepageInfo> GetHomepageAsync(CancellationToken ct) => GetAsync<HomepageInfo>(RouterEndpoints.HomepageInfo, ct);
    public Task<FirmwareInfo> GetFirmwareAsync(CancellationToken ct) => GetAsync<FirmwareInfo>(RouterEndpoints.FirmwareInfo, ct);
    public Task<MssStatus> GetMssStatusAsync(CancellationToken ct) => GetAsync<MssStatus>(RouterEndpoints.MssStatus, ct);
    public Task<MssSupportInfo> GetMssSupportAsync(CancellationToken ct) => GetAsync<MssSupportInfo>(RouterEndpoints.MssSupportInfo, ct);
    public Task<EngineeringInfo> GetEngineeringAsync(CancellationToken ct) => GetAsync<EngineeringInfo>(RouterEndpoints.EngineeringInfo, ct);
    public Task<WanInfo> GetWanInfoAsync(CancellationToken ct) => GetAsync<WanInfo>(RouterEndpoints.WanInfo, ct);
    public Task<WanApnProfileInfo> GetApnProfilesAsync(CancellationToken ct) => GetAsync<WanApnProfileInfo>(RouterEndpoints.WanApnProfileInfo, ct);
    public Task<WanPdpContextInfo> GetPdpContextAsync(CancellationToken ct) => GetAsync<WanPdpContextInfo>(RouterEndpoints.WanPdpContextInfo, ct);
    public Task<WlanInfo> GetWlanInfoAsync(CancellationToken ct) => GetAsync<WlanInfo>(RouterEndpoints.WlanInfo, ct);
    public Task<WlanSecurityInfo> GetWlanSecurityAsync(CancellationToken ct) => GetAsync<WlanSecurityInfo>(RouterEndpoints.WlanSecurityInfo, ct);
    public Task<WlanWpsInfo> GetWpsAsync(CancellationToken ct) => GetAsync<WlanWpsInfo>(RouterEndpoints.WlanWpsInfo, ct);
    public Task<WlanMacFiltersInfo> GetMacFiltersAsync(CancellationToken ct) => GetAsync<WlanMacFiltersInfo>(RouterEndpoints.WlanMacFiltersInfo, ct);
    public Task<DhcpInfo> GetDhcpAsync(CancellationToken ct) => GetAsync<DhcpInfo>(RouterEndpoints.DhcpInfo, ct);
    public Task<DeviceManagementInfo> GetDevicesAsync(CancellationToken ct) => GetAsync<DeviceManagementInfo>(RouterEndpoints.DeviceManagementInfo, ct);
    public Task<AccountManagementInfo> GetAccountsAsync(CancellationToken ct) => GetAsync<AccountManagementInfo>(RouterEndpoints.AccountManagementInfo, ct);
    public Task<CustomFwInfo> GetCustomFwAsync(CancellationToken ct) => GetAsync<CustomFwInfo>(RouterEndpoints.CustomFwInfo, ct);

    // ---------------- 写入 ----------------

    /// <summary>提交配置。设备成功时返回 {"result":"success"}；这里会校验返回值，不把 HTTP 200 当作成功。</summary>
    private async Task PostAsync(string endpoint, JsonObject payload, CancellationToken ct)
    {
        if (!await _writeGate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new RouterException(RouterErrorKind.Device, "已有配置写入正在执行，请等待其完成后再试。", endpoint);
        try
        {
            var body = await _client.PostJsonAsync(endpoint, payload, ct).ConfigureAwait(false);
            var obj = RouterClient.ParseObject(body, endpoint);
            var result = obj["result"]?.ToString();
            if (!string.Equals(result, "success", StringComparison.OrdinalIgnoreCase))
            {
                var msg = obj["msg"]?.ToString() ?? obj.ToJsonString();
                throw new RouterException(RouterErrorKind.Device, $"设备拒绝了该设置（result={result ?? "无"}，{msg}）", endpoint);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // 拨号：原厂用 "cellular" 表示拨号，"disabled" 表示断开
    public Task SetConnectionAsync(bool connect, CancellationToken ct)
        => PostAsync(RouterEndpoints.WanSet, new JsonObject
        {
            ["connect_disconnect"] = connect ? "cellular" : "disabled",
        }, ct);

    public Task SetConnectModeAsync(string connectMode, bool disableAutoDialOnRoaming, CancellationToken ct)
        => PostAsync(RouterEndpoints.WanSet, new JsonObject
        {
            ["connect_mode"] = connectMode,
            ["Roaming_disable_auto_dial"] = disableAutoDialOnRoaming ? "1" : "0",
            ["Roaming_disable_auto_dial_action"] = "1",
        }, ct);

    public Task SetNetworkAsync(string proto, string nwMode, string preferMode, string preferLteType, int? mtu, CancellationToken ct)
    {
        var p = new JsonObject
        {
            ["proto"] = proto,
            ["NW_mode"] = nwMode,
            ["NW_mode_action"] = "1",
            ["prefer_mode"] = preferMode,
            ["prefer_mode_action"] = "1",
            ["prefer_lte_type"] = preferLteType,
            ["prefer_lte_type_action"] = "1",
        };
        if (mtu is not null)
        {
            p["mtu"] = mtu.Value.ToString();
            p["mtu_action"] = "1";
        }
        return PostAsync(RouterEndpoints.WanSet, p, ct);
    }

    public Task SetMtuAsync(int mtu, CancellationToken ct)
        => PostAsync(RouterEndpoints.WanSet, new JsonObject
        {
            ["mtu"] = mtu.ToString(),
            ["mtu_action"] = "1",
        }, ct);

    public Task SetAutoApnAsync(bool auto, CancellationToken ct)
        => PostAsync(RouterEndpoints.WanSet, new JsonObject
        {
            ["auto_apn"] = auto ? "1" : "0",
            ["auto_apn_action"] = "1",
        }, ct);

    /// <summary>提交完整 APN 配置列表（新增/修改/删除都通过整体下发该列表完成）。</summary>
    public Task SetApnProfilesAsync(IEnumerable<ApnProfile> profiles, CancellationToken ct)
    {
        var list = new JsonArray();
        foreach (var p in profiles)
        {
            list.Add(new JsonObject
            {
                ["profile_name"] = p.ProfileName ?? "",
                ["enable"] = p.Enable ?? "1",
                ["default"] = p.IsDefault ?? "0",
                ["apn"] = p.Apn ?? "",
                ["lte_apn"] = p.LteApn ?? p.Apn ?? "",
                ["iptype"] = p.IpType ?? "1",
                ["qci"] = p.Qci ?? "0",
                ["authtype2g3"] = p.AuthType2G3 ?? "NONE",
                ["usr2g3"] = p.User2G3 ?? "",
                ["paswd2g3"] = p.Password2G3 ?? "",
                ["authtype4g"] = p.AuthType4G ?? "NONE",
                ["usr4g"] = p.User4G ?? "",
                ["paswd4g"] = p.Password4G ?? "",
            });
        }
        return PostAsync(RouterEndpoints.WanSet, new JsonObject
        {
            ["profile_list"] = list,
            ["auto_apn"] = "0",
            ["auto_apn_action"] = "0",
        }, ct);
    }

    /// <summary>Wi-Fi 基础设置。channel=0 表示自动信道。</summary>
    public Task SetWlanAsync(string rfBand, string netMode, string channel, string bandwidth,
        int maxClients, bool apIsolate, string? secChannel, CancellationToken ct)
    {
        var p = new JsonObject
        {
            ["wlan_enable"] = "1",
            ["rf_band"] = rfBand,
            ["net_mode"] = netMode,
            ["channel"] = channel,
            ["bandwidth"] = bandwidth,
            ["max_clients"] = maxClients.ToString(),
            ["ap_isolate"] = apIsolate ? "1" : "0",
        };
        if (secChannel is not null) p["sec_channel"] = secChannel;
        return PostAsync(RouterEndpoints.WlanSet, p, ct);
    }

    public Task SetWlanEnableAsync(bool enable, CancellationToken ct)
        => PostAsync(RouterEndpoints.WlanSet, new JsonObject
        {
            ["wlan_enable"] = enable ? "1" : "0",
        }, ct);

    /// <summary>Wi-Fi 休眠（自动关闭热点）。enable=false 时下发 action=0 关闭该功能。</summary>
    public Task SetWifiSleepAsync(bool enable, int minutes, CancellationToken ct)
    {
        var p = new JsonObject
        {
            ["wifi_sleep_action"] = "1",
            ["wifi_sleep_time"] = enable ? minutes.ToString() : "0",
        };
        return PostAsync(RouterEndpoints.WlanSet, p, ct);
    }

    /// <summary>
    /// 修改 SSID / 密码 / 加密方式 / 是否隐藏。
    /// 设备载荷形如 {"ssid":"004D...","ssid_bcast":"1","mode":"Mixed","Mixed.key":"...","Mixed.mode":"AES-CCMP"}
    /// —— 密钥字段用「模式名.key」这种扁平键名，且 SSID 必须是 UTF-16LE HEX。
    /// </summary>
    public Task SetWlanSecurityAsync(string ssid, bool broadcastSsid, string securityMode, string? password, string? encryption, CancellationToken ct)
    {
        var p = new JsonObject
        {
            ["ssid"] = HexText.Encode(ssid),
            ["ssid_bcast"] = broadcastSsid ? "1" : "0",
            ["mode"] = securityMode,
        };
        if (!string.IsNullOrEmpty(securityMode) && securityMode != "None" && !string.IsNullOrEmpty(password))
        {
            p[$"{securityMode}.key"] = password;
            p[$"{securityMode}.mode"] = string.IsNullOrWhiteSpace(encryption) ? "AES-CCMP" : encryption;
        }
        return PostAsync(RouterEndpoints.WlanSecuritySet, p, ct);
    }

    /// <summary>WPS。connectMethod 取值 1/2/3 的语义原厂前端未标注，尚未在真机验证，属于实验性功能。</summary>
    public Task SetWpsAsync(string connectMethod, string? pin, CancellationToken ct)
    {
        var p = new JsonObject { ["connect_method"] = connectMethod };
        if (!string.IsNullOrEmpty(pin)) p["wps_pin"] = pin;
        return PostAsync(RouterEndpoints.WlanSecuritySet, p, ct);
    }

    /// <summary>MAC 过滤。mode：1=白名单，2=黑名单（原厂默认 2）。</summary>
    public Task SetMacFiltersAsync(bool enable, string mode, IEnumerable<string>? allow, IEnumerable<string>? deny,
        IEnumerable<int>? deleteAllowIndexes, IEnumerable<int>? deleteDenyIndexes, CancellationToken ct)
    {
        var p = new JsonObject
        {
            ["enable"] = enable ? "1" : "0",
            ["mode"] = mode,
        };
        if (allow is not null) p["allow_list"] = ToMacArray(allow);
        if (deny is not null) p["deny_list"] = ToMacArray(deny);
        if (deleteAllowIndexes is not null) p["allow_delete_index"] = string.Join(",", deleteAllowIndexes);
        if (deleteDenyIndexes is not null) p["deny_delete_index"] = string.Join(",", deleteDenyIndexes);
        return PostAsync(RouterEndpoints.WlanMacFiltersSet, p, ct);
    }

    private static JsonArray ToMacArray(IEnumerable<string> macs)
    {
        var arr = new JsonArray();
        foreach (var m in macs) arr.Add(m);
        return arr;
    }

    /// <summary>LAN / DHCP / DNS / 强制门户。传入空字符串表示不下发该项（保持设备现值）。</summary>
    public Task SetLanAsync(string? ip, string? start, string? end, int? leaseTime, bool? dnsEnable,
        string? dns1, string? dns2, bool? dhcpV6, bool? redirectEnable, string? redirectUrl, CancellationToken ct)
    {
        var p = new JsonObject();
        if (ip is not null) p["ip"] = ip;
        if (start is not null) p["start"] = start;
        if (end is not null) p["end"] = end;
        if (leaseTime is not null) p["lease_time"] = leaseTime.Value.ToString();
        if (dnsEnable is not null) p["dns_enable"] = dnsEnable.Value ? "1" : "0";
        if (dns1 is not null) p["dns1"] = dns1;
        if (dns2 is not null) p["dns2"] = dns2;
        if (dhcpV6 is not null) p["dhcpv6server"] = dhcpV6.Value ? "1" : "0";
        if (redirectEnable is not null) p["redirect_enable"] = redirectEnable.Value ? "1" : "0";
        if (redirectUrl is not null) p["redirect_url"] = redirectUrl;
        return PostAsync(RouterEndpoints.LanSet, p, ct);
    }

    public Task AddStaticIpAsync(string mac, string ip, CancellationToken ct)
        => PostAsync(RouterEndpoints.LanSet, new JsonObject
        {
            ["Fixed_IP_list"] = new JsonArray { new JsonObject { ["index"] = "1", ["mac"] = mac, ["ip"] = ip } },
        }, ct);

    public Task DeleteStaticIpAsync(string mac, CancellationToken ct)
        => PostAsync(RouterEndpoints.LanSet, new JsonObject
        {
            ["Fixed_IP_list"] = new JsonArray { new JsonObject { ["delete"] = "1", ["mac"] = mac } },
        }, ct);

    /// <summary>在线设备操作。action：2=拉黑禁止上网，3=解除拉黑，4=重置全部设备统计（无需 mac）。</summary>
    public Task DeviceActionAsync(string action, string? mac, CancellationToken ct)
    {
        var p = new JsonObject { ["action"] = action };
        if (!string.IsNullOrEmpty(mac)) p["mac"] = mac;
        return PostAsync(RouterEndpoints.DeviceManagementSet, p, ct);
    }

    /// <summary>账号管理。accountAction：1=新增，2=修改/删除，其余沿用原厂取值。</summary>
    public Task SetAccountsAsync(string accountAction, IEnumerable<UserAccount> users, CancellationToken ct)
    {
        var list = new JsonArray();
        foreach (var u in users)
        {
            list.Add(new JsonObject
            {
                ["username"] = u.Username ?? "",
                ["password"] = u.Password ?? "",
                ["authority"] = u.Authority ?? "1",
            });
        }
        return PostAsync(RouterEndpoints.AccountManagementSet, new JsonObject
        {
            ["account_action"] = accountAction,
            ["router_user_list"] = list,
        }, ct);
    }

    /// <summary>eSIM / SIM 卡切换。</summary>
    public Task SwitchSimAsync(string switchMode, string? soleSimId, CancellationToken ct)
    {
        var p = new JsonObject { ["switch_mode"] = switchMode };
        if (soleSimId is not null) p["sole_sim_id"] = soleSimId;
        return PostAsync(RouterEndpoints.MssSupportSet, p, ct);
    }

    public Task SetCustomFwAsync(JsonArray rules, CancellationToken ct)
        => PostAsync(RouterEndpoints.CustomFwSet, new JsonObject { ["custom_rules_list"] = rules }, ct);

    /// <summary>重启设备（高危，需二次确认）。</summary>
    public Task RestartAsync(CancellationToken ct)
        => PostAsync(RouterEndpoints.DeviceRestart, new JsonObject(), ct);

    /// <summary>恢复出厂设置（高危，需二次确认）。</summary>
    public Task RestoreFactoryAsync(CancellationToken ct)
        => PostAsync(RouterEndpoints.DeviceRestoreFactory, new JsonObject { ["reset"] = "1" }, ct);
}
