using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NRadioManager.Core;

// 说明：设备所有字段均为字符串形态，此处保持原样以忠实反映设备语义，
// 数值解析统一通过 J 帮助类完成，解析失败即返回 null（界面显示"—"，不编造数据）。

public sealed class LoginInfo
{
    [JsonPropertyName("login_status")] public string? LoginStatus { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
}

public sealed class StatusInfo
{
    [JsonPropertyName("wifi_clients_num")] public string? WifiClientsNum { get; set; }
    [JsonPropertyName("run_seconds")] public string? RunSeconds { get; set; }
    [JsonPropertyName("battery_connect")] public string? BatteryConnect { get; set; }
    [JsonPropertyName("battery_charging")] public string? BatteryCharging { get; set; }
    [JsonPropertyName("battery_charge")] public string? BatteryCharge { get; set; }
    [JsonPropertyName("battery_percent")] public string? BatteryPercent { get; set; }
    [JsonPropertyName("tx_byte_all")] public string? TxByteAll { get; set; }
    [JsonPropertyName("rx_byte_all")] public string? RxByteAll { get; set; }
    [JsonPropertyName("signal_quality")] public string? SignalQuality { get; set; }
    [JsonPropertyName("rssi")] public string? Rssi { get; set; }
    [JsonPropertyName("sys_mode")] public string? SysMode { get; set; }
    [JsonPropertyName("roaming")] public string? Roaming { get; set; }
    [JsonPropertyName("new_sms_num")] public string? NewSmsNum { get; set; }
    [JsonPropertyName("tx_speed")] public string? TxSpeed { get; set; }
    [JsonPropertyName("rx_speed")] public string? RxSpeed { get; set; }
    [JsonPropertyName("tx_max_speed")] public string? TxMaxSpeed { get; set; }
    [JsonPropertyName("rx_max_speed")] public string? RxMaxSpeed { get; set; }
    [JsonPropertyName("login_status")] public string? LoginStatus { get; set; }
    [JsonPropertyName("password_status")] public string? PasswordStatus { get; set; }
    /// <summary>CPU 占用百分比；仅当设备直接提供时填写。</summary>
    public string? CpuPercent { get; set; }
    /// <summary>内存占用百分比；仅当设备直接提供时填写。</summary>
    public string? MemoryPercent { get; set; }
    /// <summary>设备/模块温度（摄氏度）；仅当设备直接提供时填写。</summary>
    public string? DeviceTemperatureC { get; set; }
}

public sealed class HomepageInfo
{
    [JsonPropertyName("wlan_enable")] public string? WlanEnable { get; set; }
    [JsonPropertyName("network_name")] public string? NetworkName { get; set; }
    [JsonPropertyName("connect_disconnect")] public string? ConnectDisconnect { get; set; }
    [JsonPropertyName("sim_status")] public string? SimStatus { get; set; }
    [JsonPropertyName("pin_status")] public string? PinStatus { get; set; }
    [JsonPropertyName("mac")] public string? Mac { get; set; }
    [JsonPropertyName("imei")] public string? Imei { get; set; }
    [JsonPropertyName("imsi")] public string? Imsi { get; set; }
    [JsonPropertyName("iccid")] public string? Iccid { get; set; }
    [JsonPropertyName("virtual_iccid")] public string? VirtualIccid { get; set; }
    [JsonPropertyName("sw_version")] public string? SwVersion { get; set; }
    [JsonPropertyName("hw_version")] public string? HwVersion { get; set; }
    [JsonPropertyName("device_name")] public string? DeviceName { get; set; }
    [JsonPropertyName("msisdn")] public string? Msisdn { get; set; }
    [JsonPropertyName("lan_ip")] public string? LanIp { get; set; }
    [JsonPropertyName("wan_ip")] public string? WanIp { get; set; }
    [JsonPropertyName("ssid")] public string? SsidRaw { get; set; }
    [JsonPropertyName("max_access_number")] public string? MaxAccessNumber { get; set; }
    [JsonPropertyName("wifi_coverage")] public string? WifiCoverage { get; set; }
    [JsonPropertyName("lan_domain")] public string? LanDomain { get; set; }
    [JsonPropertyName("serial_number")] public string? SerialNumber { get; set; }
    [JsonPropertyName("auto_apn")] public string? AutoApn { get; set; }
    [JsonPropertyName("device_model")] public string? DeviceModel { get; set; }
    [JsonPropertyName("tpb_ver")] public string? TpbVer { get; set; }
    [JsonPropertyName("mainSIM")] public string? MainSim { get; set; }
    [JsonPropertyName("qs_complete")] public string? QsComplete { get; set; }

    [JsonIgnore] public string Ssid => HexText.DecodeUtf16(SsidRaw);
}

public sealed class FirmwareInfo
{
    [JsonPropertyName("version_num")] public string? VersionNum { get; set; }
    [JsonPropertyName("version_date")] public string? VersionDate { get; set; }
    [JsonPropertyName("hardware_version")] public string? HardwareVersion { get; set; }
    [JsonPropertyName("two_partition")] public string? TwoPartition { get; set; }
}

public sealed class MssStatus
{
    [JsonPropertyName("total_sim_count")] public string? TotalSimCount { get; set; }
    [JsonPropertyName("virtual_iccid")] public string? VirtualIccid { get; set; }
}

public sealed class MssSupportInfo
{
    [JsonPropertyName("switch_mode")] public string? SwitchMode { get; set; }
    [JsonPropertyName("current_sim_id")] public string? CurrentSimId { get; set; }
    [JsonPropertyName("current_sim_banned")] public string? CurrentSimBanned { get; set; }
    [JsonPropertyName("master_sim_id")] public string? MasterSimId { get; set; }
    [JsonPropertyName("sole_sim_id")] public string? SoleSimId { get; set; }
    [JsonPropertyName("virtual_iccid")] public string? VirtualIccid { get; set; }
    [JsonPropertyName("sim_list")] public List<SimCard>? SimList { get; set; }
}

public sealed class SimCard
{
    [JsonPropertyName("sim_id")] public string? SimId { get; set; }
    [JsonPropertyName("sim_name")] public string? SimName { get; set; }
    [JsonPropertyName("sim_type")] public string? SimType { get; set; }
    [JsonPropertyName("sim_present")] public string? SimPresent { get; set; }
    [JsonPropertyName("sim_banned")] public string? SimBanned { get; set; }
    [JsonPropertyName("sim_iccid")] public string? SimIccid { get; set; }
    [JsonPropertyName("sim_imsi")] public string? SimImsi { get; set; }
}

public sealed class EngineeringInfo
{
    [JsonPropertyName("Engineering_mode")] public string? EngineeringMode { get; set; }
    [JsonPropertyName("sys_mode")] public string? SysMode { get; set; }
    [JsonPropertyName("lte")] public LteCell? Lte { get; set; }
    [JsonPropertyName("umts")] public LteCell? Umts { get; set; }
}

/// <summary>工程模式小区参数。注意：rsrp/rsrq/rssi/sinr/cqi 是基带原始编码值，不是 dBm。</summary>
public sealed class LteCell
{
    [JsonPropertyName("mcc")] public string? Mcc { get; set; }
    [JsonPropertyName("mnc")] public string? Mnc { get; set; }
    [JsonPropertyName("lenOfMnc")] public string? LenOfMnc { get; set; }
    [JsonPropertyName("tac")] public string? Tac { get; set; }
    [JsonPropertyName("phyCellId")] public string? PhyCellId { get; set; }
    [JsonPropertyName("dlEuArfcn")] public string? DlEuArfcn { get; set; }
    [JsonPropertyName("ulEuArfcn")] public string? UlEuArfcn { get; set; }
    [JsonPropertyName("band")] public string? Band { get; set; }
    [JsonPropertyName("dlBandwidth")] public string? DlBandwidth { get; set; }
    [JsonPropertyName("ECGI")] public string? Ecgi { get; set; }
    [JsonPropertyName("eNB_ID")] public string? EnbId { get; set; }
    [JsonPropertyName("Cell_ID")] public string? CellId { get; set; }
    [JsonPropertyName("rsrp")] public string? Rsrp { get; set; }
    [JsonPropertyName("rsrq")] public string? Rsrq { get; set; }
    [JsonPropertyName("sinr")] public string? Sinr { get; set; }
    [JsonPropertyName("rssi")] public string? Rssi { get; set; }
    [JsonPropertyName("cqi")] public string? Cqi { get; set; }
    [JsonPropertyName("mainRsrp")] public string? MainRsrp { get; set; }
    [JsonPropertyName("diversityRsrp")] public string? DiversityRsrp { get; set; }
    [JsonPropertyName("mainRsrq")] public string? MainRsrq { get; set; }
    [JsonPropertyName("diversityRsrq")] public string? DiversityRsrq { get; set; }
    [JsonPropertyName("dlThroughPut")] public string? DlThroughPut { get; set; }
    [JsonPropertyName("dlPeakThroughPut")] public string? DlPeakThroughPut { get; set; }
    [JsonPropertyName("ulThroughPut")] public string? UlThroughPut { get; set; }
    [JsonPropertyName("ulPeakThroughPut")] public string? UlPeakThroughPut { get; set; }
    [JsonPropertyName("txPower")] public string? TxPower { get; set; }
}

public sealed class WanInfo
{
    [JsonPropertyName("proto")] public string? Proto { get; set; }
    [JsonPropertyName("NW_mode_default")] public string? NwModeDefault { get; set; }
    [JsonPropertyName("NW_mode")] public string? NwMode { get; set; }
    [JsonPropertyName("prefer_mode")] public string? PreferMode { get; set; }
    [JsonPropertyName("prefer_lte_type")] public string? PreferLteType { get; set; }
    [JsonPropertyName("connect_mode")] public string? ConnectMode { get; set; }
    [JsonPropertyName("Roaming_disable_auto_dial")] public string? RoamingDisableAutoDial { get; set; }
    [JsonPropertyName("mtu")] public string? Mtu { get; set; }
}

public sealed class WanApnProfileInfo
{
    [JsonPropertyName("auto_apn")] public string? AutoApn { get; set; }
    [JsonPropertyName("apn")] public string? Apn { get; set; }
    [JsonPropertyName("profile_list")] public List<ApnProfile>? ProfileList { get; set; }
}

public sealed class ApnProfile
{
    [JsonPropertyName("profile_name")] public string? ProfileName { get; set; }
    [JsonPropertyName("enable")] public string? Enable { get; set; }
    [JsonPropertyName("default")] public string? IsDefault { get; set; }
    [JsonPropertyName("apn")] public string? Apn { get; set; }
    [JsonPropertyName("lte_apn")] public string? LteApn { get; set; }
    [JsonPropertyName("iptype")] public string? IpType { get; set; }
    [JsonPropertyName("qci")] public string? Qci { get; set; }
    [JsonPropertyName("authtype4g")] public string? AuthType4G { get; set; }
    [JsonPropertyName("usr4g")] public string? User4G { get; set; }
    [JsonPropertyName("paswd4g")] public string? Password4G { get; set; }
    [JsonPropertyName("authtype2g3")] public string? AuthType2G3 { get; set; }
    [JsonPropertyName("usr2g3")] public string? User2G3 { get; set; }
    [JsonPropertyName("paswd2g3")] public string? Password2G3 { get; set; }
}

public sealed class WanPdpContextInfo
{
    [JsonPropertyName("network_name")] public string? NetworkName { get; set; }
    [JsonPropertyName("pdp_activated_apn")] public string? PdpActivatedApn { get; set; }
    [JsonPropertyName("pdp_context_list")] public List<PdpContext>? PdpContextList { get; set; }
}

public sealed class PdpContext
{
    [JsonPropertyName("rulename")] public string? RuleName { get; set; }
    [JsonPropertyName("connnum")] public string? ConnNum { get; set; }
    [JsonPropertyName("pconnnum")] public string? PConnNum { get; set; }
    [JsonPropertyName("success")] public string? Success { get; set; }
    [JsonPropertyName("default")] public string? IsDefault { get; set; }
    [JsonPropertyName("secondary")] public string? Secondary { get; set; }
    [JsonPropertyName("ipv4")] public string? Ipv4 { get; set; }
    [JsonPropertyName("v4dns1")] public string? V4Dns1 { get; set; }
    [JsonPropertyName("v4dns2")] public string? V4Dns2 { get; set; }
    [JsonPropertyName("v4gateway")] public string? V4Gateway { get; set; }
    [JsonPropertyName("v4netmask")] public string? V4Netmask { get; set; }
    [JsonPropertyName("ipv6")] public string? Ipv6 { get; set; }
    [JsonPropertyName("g_ipv6")] public string? GlobalIpv6 { get; set; }
    [JsonPropertyName("v6dns1")] public string? V6Dns1 { get; set; }
    [JsonPropertyName("v6dns2")] public string? V6Dns2 { get; set; }
    [JsonPropertyName("v6gateway")] public string? V6Gateway { get; set; }
    [JsonPropertyName("v6netmask")] public string? V6Netmask { get; set; }
    [JsonPropertyName("curconntime")] public string? CurConnTime { get; set; }
    [JsonPropertyName("totalconntime")] public string? TotalConnTime { get; set; }
}

public sealed class WlanInfo
{
    [JsonPropertyName("dual_band_support")] public string? DualBandSupport { get; set; }
    [JsonPropertyName("rf_band")] public string? RfBand { get; set; }
    [JsonPropertyName("net_mode")] public string? NetMode { get; set; }
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("first_channel")] public string? FirstChannel { get; set; }
    [JsonPropertyName("last_channel")] public string? LastChannel { get; set; }
    [JsonPropertyName("bandwidth")] public string? Bandwidth { get; set; }
    [JsonPropertyName("bandwidth_acs")] public string? BandwidthAcs { get; set; }
    [JsonPropertyName("wlan_enable")] public string? WlanEnable { get; set; }
    [JsonPropertyName("max_clients")] public string? MaxClients { get; set; }
    [JsonPropertyName("initial_max_clients")] public string? InitialMaxClients { get; set; }
    [JsonPropertyName("wifi_sleep_time")] public string? WifiSleepTime { get; set; }
    [JsonPropertyName("wifi_sleep_action")] public string? WifiSleepAction { get; set; }
    [JsonPropertyName("band40_acs_enable")] public string? Band40AcsEnable { get; set; }
    [JsonPropertyName("beacon_period")] public string? BeaconPeriod { get; set; }
    [JsonPropertyName("dtim_interval")] public string? DtimInterval { get; set; }
    [JsonPropertyName("ap_isolate")] public string? ApIsolate { get; set; }
    [JsonPropertyName("only_20m")] public string? Only20M { get; set; }
}

public sealed class WlanKeySet
{
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("key")] public string? Key { get; set; }
}

public sealed class WlanSecurityInfo
{
    [JsonPropertyName("net_mode")] public string? NetMode { get; set; }
    [JsonPropertyName("wapi_support")] public string? WapiSupport { get; set; }
    [JsonPropertyName("ssid")] public string? SsidRaw { get; set; }
    [JsonPropertyName("ssid_bcast")] public string? SsidBcast { get; set; }
    [JsonPropertyName("lcd_show_wifi_pwd")] public string? LcdShowWifiPwd { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("WPA2-PSK")] public WlanKeySet? Wpa2Psk { get; set; }
    [JsonPropertyName("WPA-PSK")] public WlanKeySet? WpaPsk { get; set; }
    [JsonPropertyName("Mixed")] public WlanKeySet? Mixed { get; set; }
    [JsonPropertyName("WPA3-SAE")] public WlanKeySet? Wpa3Sae { get; set; }
    [JsonPropertyName("WPA2-WPA3")] public WlanKeySet? Wpa2Wpa3 { get; set; }
    [JsonPropertyName("WEP")] public WlanKeySet? Wep { get; set; }
    [JsonPropertyName("WAPI-PSK")] public WlanKeySet? WapiPsk { get; set; }

    [JsonIgnore] public string Ssid => HexText.DecodeUtf16(SsidRaw);

    /// <summary>当前安全模式对应的密钥（优先取与 mode 同名的分组，其次取任一非空分组）。</summary>
    [JsonIgnore]
    public string? CurrentKey
    {
        get
        {
            var byMode = Mode switch
            {
                "WPA2-PSK" => Wpa2Psk,
                "WPA-PSK" => WpaPsk,
                "Mixed" => Mixed,
                "WPA3-SAE" => Wpa3Sae,
                "WPA2-WPA3" => Wpa2Wpa3,
                "WEP" => Wep,
                "WAPI-PSK" => WapiPsk,
                _ => null,
            };
            return byMode?.Key
                   ?? new[] { Wpa2Psk, WpaPsk, Mixed, Wpa3Sae, Wpa2Wpa3, Wep, WapiPsk }
                       .FirstOrDefault(k => !string.IsNullOrEmpty(k?.Key))?.Key;
        }
    }
}

public sealed class WlanWpsInfo
{
    [JsonPropertyName("wps_status")] public string? WpsStatus { get; set; }
}

public sealed class WlanMacFiltersInfo
{
    [JsonPropertyName("enable")] public string? Enable { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("currnet_device_mac")] public string? CurrentDeviceMac { get; set; }
    // 设备未启用时返回空数组，元素结构无法从当前样本确定，故保留原始节点避免反序列化失败。
    [JsonPropertyName("allow_list")] public List<JsonNode>? AllowList { get; set; }
    [JsonPropertyName("deny_list")] public List<JsonNode>? DenyList { get; set; }

    public static string Describe(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (node is JsonObject o)
        {
            var mac = o["mac"]?.ToString();
            var name = o["name"]?.ToString();
            return string.Join("  ", new[] { mac, name }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        return node.ToJsonString();
    }
}

public sealed class DhcpInfo
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("start")] public string? Start { get; set; }
    [JsonPropertyName("end")] public string? End { get; set; }
    [JsonPropertyName("lease_time")] public string? LeaseTime { get; set; }
    [JsonPropertyName("redirect_enable")] public string? RedirectEnable { get; set; }
    [JsonPropertyName("redirect_url")] public string? RedirectUrl { get; set; }
    [JsonPropertyName("dhcpv6server")] public string? DhcpV6Server { get; set; }
    [JsonPropertyName("ip")] public string? Ip { get; set; }
    [JsonPropertyName("mask")] public string? Mask { get; set; }
    [JsonPropertyName("dns_enable")] public string? DnsEnable { get; set; }
    [JsonPropertyName("dns1")] public string? Dns1 { get; set; }
    [JsonPropertyName("dns2")] public string? Dns2 { get; set; }
    [JsonPropertyName("mac")] public string? Mac { get; set; }
    [JsonPropertyName("Fixed_IP_list")] public List<JsonNode>? FixedIpList { get; set; }
}

public sealed class KnownDevice
{
    [JsonPropertyName("index")] public string? Index { get; set; }
    [JsonPropertyName("mac")] public string? Mac { get; set; }
    [JsonPropertyName("name")] public string? NameRaw { get; set; }
    [JsonPropertyName("conn_type")] public string? ConnType { get; set; }
    [JsonPropertyName("ip_address")] public string? IpAddress { get; set; }
    [JsonPropertyName("conn_time")] public string? ConnTime { get; set; }
    [JsonIgnore] public string Name => HexText.DecodeName(NameRaw);
}

public sealed class ClientEntry
{
    [JsonPropertyName("i")] public string? Index { get; set; }
    [JsonPropertyName("m")] public string? Mac { get; set; }
    [JsonPropertyName("n")] public string? NameRaw { get; set; }
    [JsonIgnore] public string? NameText { get; set; }
    [JsonPropertyName("nt")] public string? NameType { get; set; }
    [JsonPropertyName("s")] public string? Status { get; set; }   // 1 正常 / 2 已拉黑
    [JsonPropertyName("ct")] public string? ConnType { get; set; }
    [JsonPropertyName("ip")] public string? Ip { get; set; }
    [JsonPropertyName("ta")] public string? AccessTime { get; set; }
    [JsonPropertyName("tf")] public string? TotalFlow { get; set; }
    [JsonPropertyName("rx")] public string? Rx { get; set; }
    [JsonPropertyName("tx")] public string? Tx { get; set; }
    [JsonPropertyName("rxm")] public string? RxMonth { get; set; }
    [JsonPropertyName("txm")] public string? TxMonth { get; set; }
    [JsonPropertyName("rx3")] public string? Rx3Day { get; set; }
    [JsonPropertyName("tx3")] public string? Tx3Day { get; set; }
    [JsonPropertyName("rssi")] public string? Rssi { get; set; }
    [JsonIgnore] public string Name => NameText ?? HexText.DecodeName(NameRaw);
}

public sealed class DeviceManagementInfo
{
    [JsonPropertyName("known_devices_list")] public List<KnownDevice>? KnownDevices { get; set; }
    [JsonPropertyName("client_list")] public List<ClientEntry>? ClientList { get; set; }
}

public sealed class AccountManagementInfo
{
    [JsonPropertyName("multi_account")] public string? MultiAccount { get; set; }
    [JsonPropertyName("router_username")] public string? RouterUsername { get; set; }
    [JsonPropertyName("router_user_list")] public List<UserAccount>? UserList { get; set; }
}

public sealed class UserAccount
{
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("authority")] public string? Authority { get; set; }
}

public sealed class CustomFwInfo
{
    [JsonPropertyName("custom_rules_mode")] public string? RulesMode { get; set; }
    /// <summary>字段结构在设备侧为空数组时不可推断，保留原始节点。</summary>
    [JsonPropertyName("custom_rules_list")] public List<JsonNode>? Rules { get; set; }
}
