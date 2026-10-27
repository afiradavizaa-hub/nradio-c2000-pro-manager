namespace NRadioManager.Core;

/// <summary>
/// 原厂接口名常量。全部来自真机实测（详见 docs/01-原厂接口逆向分析.md）。
/// 命名规律：*_info 只读（GET），*_set 只写（POST）。
/// </summary>
public static class RouterEndpoints
{
    // 只读
    public const string LoginInfo = "login_info";
    public const string StatusInfo = "status_info";
    public const string HomepageInfo = "homepage_info";
    public const string FirmwareInfo = "firmware_info";
    public const string MssStatus = "mss_status";
    public const string MssSupportInfo = "mss_support_info";
    public const string EngineeringInfo = "engineering_info";
    public const string WanInfo = "wan_info";
    public const string WanApnProfileInfo = "wan_apn_profile_info";
    public const string WanPdpContextInfo = "wan_pdp_context_info";
    public const string WlanInfo = "wlan_info";
    public const string WlanSecurityInfo = "wlan_security_info";
    public const string WlanWpsInfo = "wlan_wps_info";
    public const string WlanMacFiltersInfo = "wlan_mac_filters_info";
    public const string DhcpInfo = "dhcp_info";
    public const string DeviceManagementInfo = "device_management_info";
    public const string AccountManagementInfo = "account_management_info";
    public const string CustomFwInfo = "custom_fw_info";

    // 写入
    public const string WanSet = "wan_set";
    public const string WlanSet = "wlan_set";
    public const string WlanSecuritySet = "wlan_security_set";
    public const string WlanMacFiltersSet = "wlan_mac_filters_set";
    public const string LanSet = "lan_set";
    public const string DeviceManagementSet = "device_management_set";
    public const string AccountManagementSet = "account_management_set";
    public const string MssSupportSet = "mss_support_set";
    public const string CustomFwSet = "custom_fw_set";
    public const string DeviceRestart = "device_restart";
    public const string DeviceRestoreFactory = "device_restore_factory";
}
