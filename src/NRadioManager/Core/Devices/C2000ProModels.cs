using System.Globalization;
using System.Text.Json.Serialization;

namespace NRadioManager.Core;

public sealed class C2000RuntimeEnvelope
{
    [JsonPropertyName("result")] public C2000Runtime? Result { get; set; }
}

public sealed class C2000Runtime
{
    [JsonPropertyName("global")] public C2000Global? Global { get; set; }
    [JsonPropertyName("terminal_all")] public C2000TerminalAll? TerminalAll { get; set; }
    [JsonPropertyName("lans")] public List<C2000Lan>? Lans { get; set; }
    [JsonPropertyName("cpe")] public List<C2000Cpe>? Cpe { get; set; }
    [JsonPropertyName("wans")] public List<C2000Wan>? Wans { get; set; }
    [JsonPropertyName("bat")] public C2000Battery? Battery { get; set; }
    [JsonPropertyName("wlan")] public C2000Wlan? Wlan { get; set; }
}

public sealed class C2000Global
{
    [JsonPropertyName("net_kind")] public string? NetKind { get; set; }
    [JsonPropertyName("uptime")] public long? Uptime { get; set; }
    [JsonPropertyName("wifi_temp")] public string? WifiTemp { get; set; }
    [JsonPropertyName("device_temp")] public string? DeviceTemp { get; set; }
    [JsonPropertyName("cpu_percent")] public string? CpuPercent { get; set; }
    [JsonPropertyName("net_type")] public string? NetType { get; set; }
    [JsonPropertyName("mem_percent")] public string? MemPercent { get; set; }
}

public sealed class C2000TerminalAll
{
    [JsonPropertyName("auth_on")] public int? AuthOn { get; set; }
    [JsonPropertyName("count")] public int? Count { get; set; }
    [JsonPropertyName("client")] public Dictionary<string, C2000Terminal>? Clients { get; set; }
}

public sealed class C2000Terminal
{
    [JsonPropertyName("txbytes")] public string? TxBytes { get; set; }
    [JsonPropertyName("rxbytes")] public string? RxBytes { get; set; }
    [JsonPropertyName("rssi")] public string? Rssi { get; set; }
    [JsonPropertyName("hostname")] public string? Hostname { get; set; }
    [JsonPropertyName("ipaddr")] public string? IpAddress { get; set; }
    [JsonPropertyName("mac")] public string? Mac { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("ssid")] public string? Ssid { get; set; }
    [JsonPropertyName("frequency")] public string? Frequency { get; set; }
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("txrate")] public string? TxRate { get; set; }
    [JsonPropertyName("rxrate")] public string? RxRate { get; set; }
    [JsonPropertyName("assoctime")] public long? AssocTime { get; set; }
    [JsonPropertyName("switch")] public int? Switch { get; set; }
    [JsonPropertyName("switch_off")] public int? SwitchOff { get; set; }
    [JsonPropertyName("authed")] public int? Authed { get; set; }
    [JsonPropertyName("exsit")] public int? Exist { get; set; }
    [JsonPropertyName("qos")] public int? Qos { get; set; }
}

public sealed class C2000Lan
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("proto")] public string? Proto { get; set; }
    [JsonPropertyName("status")] public int? Status { get; set; }
    [JsonPropertyName("ipaddrs")] public List<C2000Address>? Addresses { get; set; }
    [JsonPropertyName("dns")] public List<string>? Dns { get; set; }
    [JsonPropertyName("download")] public string? Download { get; set; }
    [JsonPropertyName("upload")] public string? Upload { get; set; }
}

public sealed class C2000Address
{
    [JsonPropertyName("ipaddr")] public string? IpAddress { get; set; }
    [JsonPropertyName("netmask")] public string? NetMask { get; set; }
}

public sealed class C2000Cpe
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("revision")] public string? Revision { get; set; }
    [JsonPropertyName("status")] public int? Status { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("band")] public string? Band { get; set; }
    [JsonPropertyName("band_count")] public int? BandCount { get; set; }
    [JsonPropertyName("pci")] public string? Pci { get; set; }
    [JsonPropertyName("cell")] public string? Cell { get; set; }
    [JsonPropertyName("earfcn")] public string? Earfcn { get; set; }
    [JsonPropertyName("tac")] public string? Tac { get; set; }
    [JsonPropertyName("lac")] public string? Lac { get; set; }
    [JsonPropertyName("rsrp")] public string? Rsrp { get; set; }
    [JsonPropertyName("rsrq")] public string? Rsrq { get; set; }
    [JsonPropertyName("sinr")] public string? Sinr { get; set; }
    [JsonPropertyName("rssi")] public string? Rssi { get; set; }
    [JsonPropertyName("CQI")] public string? Cqi { get; set; }
    [JsonPropertyName("DLBW")] public string? DownlinkBandwidthKhz { get; set; }
    [JsonPropertyName("imei")] public string? Imei { get; set; }
    [JsonPropertyName("mac")] public string? Mac { get; set; }
    [JsonPropertyName("imsi")] public string? Imsi { get; set; }
    [JsonPropertyName("iccid")] public string? Iccid { get; set; }
    [JsonPropertyName("simcount")] public int? SimCount { get; set; }
    [JsonPropertyName("simno")] public string? SimNo { get; set; }
    [JsonPropertyName("simtype")] public int? SimType { get; set; }
    [JsonPropertyName("sim_isp")] public string? SimIsp { get; set; }
    [JsonPropertyName("isp")] public string? Isp { get; set; }
    [JsonPropertyName("isp_ori")] public string? IspOriginal { get; set; }
    [JsonPropertyName("apn")] public string? Apn { get; set; }
    [JsonPropertyName("traffic")] public string? Traffic { get; set; }
    [JsonPropertyName("device_traffic")] public string? DeviceTraffic { get; set; }
    [JsonPropertyName("sim_flow")] public string? SimFlow { get; set; }
    [JsonPropertyName("download")] public string? Download { get; set; }
    [JsonPropertyName("upload")] public string? Upload { get; set; }
    [JsonPropertyName("cpu_percent")] public string? CpuPercent { get; set; }
    [JsonPropertyName("mem_percent")] public string? MemPercent { get; set; }
    [JsonPropertyName("model_temp")] public string? ModelTemp { get; set; }
    [JsonPropertyName("CPIN")] public string? Cpin { get; set; }
    [JsonPropertyName("V4")] public C2000IpFamily? V4 { get; set; }
    [JsonPropertyName("V6")] public C2000IpFamily? V6 { get; set; }
}

public sealed class C2000IpFamily
{
    [JsonPropertyName("ipaddrs")] public List<string>? Addresses { get; set; }
    [JsonPropertyName("dns")] public List<string>? Dns { get; set; }
}

public sealed class C2000Wan
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("classname")] public string? ClassName { get; set; }
    [JsonPropertyName("proto")] public string? Proto { get; set; }
    [JsonPropertyName("status")] public int? Status { get; set; }
    [JsonPropertyName("uptime")] public long? Uptime { get; set; }
    [JsonPropertyName("device")] public string? Device { get; set; }
    [JsonPropertyName("gateway")] public string? Gateway { get; set; }
    [JsonPropertyName("dns")] public List<string>? Dns { get; set; }
    [JsonPropertyName("ipaddrs")] public List<C2000Address>? Addresses { get; set; }
    [JsonPropertyName("download")] public string? Download { get; set; }
    [JsonPropertyName("upload")] public string? Upload { get; set; }
}

public sealed class C2000Battery
{
    [JsonPropertyName("charging")] public int? Charging { get; set; }
    [JsonPropertyName("percent")] public int? Percent { get; set; }
}

public sealed class C2000Wlan
{
    [JsonPropertyName("radio5_disabled")] public int? Radio5Disabled { get; set; }
    [JsonPropertyName("radio2_disabled")] public int? Radio2Disabled { get; set; }
    [JsonPropertyName("radio")] public List<C2000Radio>? Radios { get; set; }
}

public sealed class C2000Radio
{
    [JsonPropertyName("mac")] public string? Mac { get; set; }
    [JsonPropertyName("ifname")] public string? InterfaceName { get; set; }
    [JsonPropertyName("ssid")] public string? Ssid { get; set; }
    [JsonPropertyName("encryption")] public string? Encryption { get; set; }
    [JsonPropertyName("rxbytes")] public string? RxBytes { get; set; }
    [JsonPropertyName("txbytes")] public string? TxBytes { get; set; }
    [JsonPropertyName("hidden")] public int? Hidden { get; set; }
    [JsonPropertyName("disabled")] public int? Disabled { get; set; }
    [JsonPropertyName("sta")] public int? StationCount { get; set; }
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("frequency")] public string? FrequencyGhz { get; set; }
    [JsonPropertyName("phyname")] public string? PhyName { get; set; }
    [JsonPropertyName("assoclist")] public List<C2000Terminal>? AssociatedClients { get; set; }
}

public sealed class C2000CellInfoEnvelope
{
    [JsonPropertyName("result")] public List<C2000CellInfoItem>? Result { get; set; }
}

public sealed class C2000CellInfoItem
{
    [JsonPropertyName("status")] public C2000Cpe? Status { get; set; }
    [JsonPropertyName("uptime")] public long? Uptime { get; set; }
    [JsonPropertyName("up")] public int? Up { get; set; }
}

public sealed class C2000SmsEnvelope
{
    [JsonPropertyName("result")] public C2000SmsList? Result { get; set; }
}

public sealed class C2000SmsList
{
    [JsonPropertyName("on")] public string? On { get; set; }
    [JsonPropertyName("code")] public int? Code { get; set; }
    [JsonPropertyName("ready")] public int? Ready { get; set; }
    [JsonPropertyName("smslist")] public List<C2000SmsMessage>? Messages { get; set; }
}

public sealed class C2000SmsMessage
{
    [JsonPropertyName("contact")] public string? Contact { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("undeal")] public int? Unread { get; set; }
    [JsonPropertyName("index")] public string? Index { get; set; }
}

public sealed class C2000SmsUnreadEnvelope
{
    [JsonPropertyName("result")] public List<C2000SmsUnread>? Result { get; set; }
}

public sealed class C2000SmsUnread
{
    [JsonPropertyName("iface")] public string? InterfaceName { get; set; }
    [JsonPropertyName("count")] public int? Count { get; set; }
    [JsonPropertyName("max")] public int? Max { get; set; }
    [JsonPropertyName("used")] public int? Used { get; set; }
}

public sealed record C2000CellEntry(
    string Mode,
    string Band,
    string Pci,
    string Earfcn,
    string Rsrp,
    string Rsrq,
    string Sinr,
    bool Locked,
    bool Current,
    bool Recommended = false)
{
    public double? RsrpValue => double.TryParse(Rsrp, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    public double? SinrValue => double.TryParse(Sinr, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    public double SignalPercent => RsrpValue is { } v ? Math.Clamp((v + 140) / 100 * 100, 0, 100) : 0;
    public string Quality => RsrpValue switch
    {
        null => "未知",
        >= -80 => "优秀",
        >= -90 => "良好",
        >= -100 => "一般",
        _ => "较差",
    };
    public string SignalText => RsrpValue is { } v ? $"{v:0} dBm" : "—";
    public string SinrText => SinrValue is { } v ? $"{v:0.#} dB" : "—";
    public string RsrqText => double.TryParse(Rsrq, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
        ? $"{v:0.#} dB" : "—";
    public string BandText => string.IsNullOrWhiteSpace(Band) || Band == "—"
        ? "—"
        : Mode.Contains("NR", StringComparison.OrdinalIgnoreCase)
            ? "n" + Band.TrimStart('n', 'N')
            : "B" + Band.TrimStart('b', 'B');
}

public sealed record C2000LockSnapshot(
    string LockMode,
    string Mode,
    string NciEci,
    string Band,
    string Pci,
    string Earfcn,
    string Tac,
    string RsrpDbm,
    string RsrqDb,
    string SinrDb,
    string Cqi,
    string Bandwidth,
    IReadOnlyList<C2000CellEntry> Neighbours);

public sealed record C2000SimSnapshot(
    bool Supported,
    int SlotCount,
    string CurrentSlot,
    string SwitchMode,
    string Registration,
    string PinState,
    string SimOperator,
    string Imsi,
    string Iccid);

public sealed record C2000SmsSnapshot(
    bool ServiceEnabled,
    bool Ready,
    int Unread,
    int Used,
    int Capacity,
    IReadOnlyList<C2000SmsMessage> Messages,
    string StatusText);

public sealed record C2000WriteResult(
    bool Confirmed,
    string Message,
    string Before,
    string After);

public sealed class C2000DataUsageEnvelope
{
    [JsonPropertyName("result")] public C2000DataUsageResult? Result { get; set; }
}

public sealed class C2000DataUsageResult
{
    [JsonPropertyName("data")] public List<List<C2000SimDataUsage>>? Data { get; set; }
}

public sealed class C2000SimDataUsage
{
    [JsonPropertyName("enabled")] public int Enabled { get; set; }
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("modelname")] public string? ModelName { get; set; }
    [JsonPropertyName("alias")] public string? Alias { get; set; }
    [JsonPropertyName("threshold_smds")] public double MonthlyUsedGb { get; set; }
    [JsonPropertyName("threshold_cds")] public double TotalUsedGb { get; set; }
    [JsonPropertyName("threshold_data")] public double ThresholdGb { get; set; }
    [JsonPropertyName("threshold_enabled")] public int ThresholdEnabled { get; set; }
    [JsonPropertyName("threshold_percent")] public double ThresholdPercent { get; set; }
    [JsonPropertyName("cross_flow")] public int CrossFlow { get; set; }
}

public sealed record C2000DataUsageSnapshot(
    IReadOnlyList<C2000SimDataUsage> Sims,
    DateTime UpdatedAt)
{
    public C2000SimDataUsage? Current(int slot) => Sims.FirstOrDefault(x => x.Index == slot);
}

public sealed class C2000CpestatusEnvelope
{
    [JsonPropertyName("result")] public C2000CpestatusResult? Result { get; set; }
}

public sealed class C2000CpestatusResult
{
    [JsonPropertyName("cfg")] public C2000LockConfig? Cfg { get; set; }
    [JsonPropertyName("status")] public C2000Cpe? Status { get; set; }
    [JsonPropertyName("neighbour")] public List<List<C2000Neighbour>>? Neighbour { get; set; }
    [JsonPropertyName("sim_company")] public string? SimCompany { get; set; }
}

public sealed class C2000LockConfig
{
    [JsonPropertyName("nr")] public C2000ConfiguredCell? Nr { get; set; }
    [JsonPropertyName("lte")] public C2000ConfiguredCell? Lte { get; set; }
}

public sealed class C2000ConfiguredCell
{
    [JsonPropertyName("BAND")] public string? Band { get; set; }
    [JsonPropertyName("PCI")] public string? Pci { get; set; }
    [JsonPropertyName("EARFCN")] public string? Earfcn { get; set; }
}

public sealed class C2000Neighbour
{
    [JsonPropertyName("MODE")] public string? Mode { get; set; }
    [JsonPropertyName("BAND")] public string? Band { get; set; }
    [JsonPropertyName("PCI")] public string? Pci { get; set; }
    [JsonPropertyName("EARFCN")] public string? Earfcn { get; set; }
    [JsonPropertyName("RSRP")] public string? Rsrp { get; set; }
    [JsonPropertyName("RSRQ")] public string? Rsrq { get; set; }
    [JsonPropertyName("SINR")] public string? Sinr { get; set; }
}

public sealed record C2000DeviceIdentity(
    string Model,
    string FirmwareVersion,
    string FirmwareDate,
    string Hardware,
    string Board,
    string MacAddress);

public sealed class C2000ProRuntimeSnapshot
{
    public required C2000Runtime Runtime { get; init; }
    public required C2000CellInfoItem Cell { get; init; }
    public required C2000DeviceIdentity Identity { get; init; }
    public DateTime SampledAt { get; init; } = DateTime.Now;
}
