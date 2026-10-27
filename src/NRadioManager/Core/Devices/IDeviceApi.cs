using System.Text.Json.Nodes;

namespace NRadioManager.Core;

/// <summary>
/// 页面依赖的统一设备业务接口。具体协议由设备适配器实现，ViewModel 不拼接 HTTP 请求。
/// </summary>
public interface IDeviceApi
{
    Task<LoginInfo> GetLoginInfoAsync(CancellationToken ct);
    Task<StatusInfo> GetStatusAsync(CancellationToken ct);
    Task<HomepageInfo> GetHomepageAsync(CancellationToken ct);
    Task<FirmwareInfo> GetFirmwareAsync(CancellationToken ct);
    Task<MssStatus> GetMssStatusAsync(CancellationToken ct);
    Task<MssSupportInfo> GetMssSupportAsync(CancellationToken ct);
    Task<EngineeringInfo> GetEngineeringAsync(CancellationToken ct);
    Task<WanInfo> GetWanInfoAsync(CancellationToken ct);
    Task<WanApnProfileInfo> GetApnProfilesAsync(CancellationToken ct);
    Task<WanPdpContextInfo> GetPdpContextAsync(CancellationToken ct);
    Task<WlanInfo> GetWlanInfoAsync(CancellationToken ct);
    Task<WlanSecurityInfo> GetWlanSecurityAsync(CancellationToken ct);
    Task<WlanWpsInfo> GetWpsAsync(CancellationToken ct);
    Task<WlanMacFiltersInfo> GetMacFiltersAsync(CancellationToken ct);
    Task<DhcpInfo> GetDhcpAsync(CancellationToken ct);
    Task<DeviceManagementInfo> GetDevicesAsync(CancellationToken ct);
    Task<AccountManagementInfo> GetAccountsAsync(CancellationToken ct);
    Task<CustomFwInfo> GetCustomFwAsync(CancellationToken ct);

    Task SetConnectionAsync(bool connect, CancellationToken ct);
    Task SetConnectModeAsync(string connectMode, bool disableAutoDialOnRoaming, CancellationToken ct);
    Task SetNetworkAsync(string proto, string nwMode, string preferMode, string preferLteType, int? mtu, CancellationToken ct);
    Task SetMtuAsync(int mtu, CancellationToken ct);
    Task SetAutoApnAsync(bool auto, CancellationToken ct);
    Task SetApnProfilesAsync(IEnumerable<ApnProfile> profiles, CancellationToken ct);
    Task SetWlanAsync(string rfBand, string netMode, string channel, string bandwidth,
        int maxClients, bool apIsolate, string? secChannel, CancellationToken ct);
    Task SetWlanEnableAsync(bool enable, CancellationToken ct);
    Task SetWifiSleepAsync(bool enable, int minutes, CancellationToken ct);
    Task SetWlanSecurityAsync(string ssid, bool broadcastSsid, string securityMode, string? password,
        string? encryption, CancellationToken ct);
    Task SetWpsAsync(string connectMethod, string? pin, CancellationToken ct);
    Task SetMacFiltersAsync(bool enable, string mode, IEnumerable<string>? allow, IEnumerable<string>? deny,
        IEnumerable<int>? deleteAllowIndexes, IEnumerable<int>? deleteDenyIndexes, CancellationToken ct);
    Task SetLanAsync(string? ip, string? start, string? end, int? leaseTime, bool? dnsEnable,
        string? dns1, string? dns2, bool? dhcpV6, bool? redirectEnable, string? redirectUrl, CancellationToken ct);
    Task AddStaticIpAsync(string mac, string ip, CancellationToken ct);
    Task DeleteStaticIpAsync(string mac, CancellationToken ct);
    Task DeviceActionAsync(string action, string? mac, CancellationToken ct);
    Task SetAccountsAsync(string accountAction, IEnumerable<UserAccount> users, CancellationToken ct);
    Task SwitchSimAsync(string switchMode, string? soleSimId, CancellationToken ct);
    Task SetCustomFwAsync(JsonArray rules, CancellationToken ct);
    Task RestartAsync(CancellationToken ct);
    Task RestoreFactoryAsync(CancellationToken ct);
}
