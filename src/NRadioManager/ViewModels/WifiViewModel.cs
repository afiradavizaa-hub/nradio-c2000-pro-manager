using System.Collections.ObjectModel;
using System.Windows.Input;
using NRadioManager.Core;
using NRadioManager.Infrastructure;

namespace NRadioManager.ViewModels;

/// <summary>
/// Wi-Fi 模块。
/// 已实现且真机验证范围：开关、802.11 模式、主信道、最大客户端数、AP 隔离、休眠、SSID/密码/加密方式/隐藏 SSID。
/// 只读展示（未做写入验证）：带宽、MAC 黑白名单、WPS —— 设备接口存在但条目结构与取值语义尚未在真机确认，
/// 为避免下发错误配置，本版本不提供写入入口，并在功能覆盖表中标注状态。
/// </summary>
public sealed class WifiViewModel : ModuleViewModel
{
    public WifiViewModel(MainViewModel host) : base(host, "Wi-Fi", "热点基础设置与安全")
    {
        ToggleWifiCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            await Host.Api!.SetWlanEnableAsync(WlanEnable, CancellationToken.None);
        }, WlanEnable ? "已开启 Wi-Fi" : "已关闭 Wi-Fi（当前连接会中断，请改用有线或稍后重连）"), () => Host.IsConnected);

        SaveBasicCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            await Host.Api!.SetWlanAsync(RfBand, SelectedNetMode?.Value ?? NetMode,
                SelectedChannel?.Value ?? Channel, Bandwidth,
                J.Int(MaxClientsText) ?? 10, ApIsolate, SecChannel, CancellationToken.None);
        }, "Wi-Fi 基础设置已下发"), () => Host.IsConnected);

        SaveSecurityCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            ValidateSecurity();
            await Host.Api!.SetWlanSecurityAsync(Ssid, BroadcastSsid, SelectedSecurityMode?.Value ?? "Mixed",
                Password, EncryptionMode, CancellationToken.None);
        }, "Wi-Fi 名称/密码已下发，终端需要重新连接"), () => Host.IsConnected);

        SaveSleepCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            var minutes = J.Int(SleepMinutesText);
            if (SleepEnabled && (minutes is null || minutes < 1 || minutes > 1440))
                throw new RouterException(RouterErrorKind.Device, "自动关闭时间请在 1–1440 分钟之间。");
            await Host.Api!.SetWifiSleepAsync(SleepEnabled, minutes ?? 0, CancellationToken.None);
        }, "Wi-Fi 休眠设置已下发"), () => Host.IsConnected);
    }

    // ---------------- 基础 ----------------

    private bool _wlanEnable = true;
    public bool WlanEnable { get => _wlanEnable; set => Set(ref _wlanEnable, value); }

    public string RfBand { get; private set; } = "1";
    public string NetMode { get; private set; } = "4";
    public string Channel { get; private set; } = "0";
    public string Bandwidth { get; private set; } = "0";
    public string BandwidthAcs { get; private set; } = "1";
    /// <summary>40MHz 的上下行扩展信道。设备只在写接口里有该字段、读接口不返回，故默认不下发。</summary>
    public string? SecChannel { get; private set; }
    public string Only20M { get; private set; } = "0";
    public string FirstChannel { get; private set; } = "1";
    public string LastChannel { get; private set; } = "13";
    public string InitialMaxClients { get; private set; } = "10";
    public string BeaconPeriod { get; private set; } = "—";
    public string DtimInterval { get; private set; } = "—";
    public string DualBandSupport { get; private set; } = "0";

    private bool _apIsolate;
    public bool ApIsolate { get => _apIsolate; set => Set(ref _apIsolate, value); }

    private string _maxClientsText = "10";
    public string MaxClientsText { get => _maxClientsText; set => Set(ref _maxClientsText, value); }

    public ObservableCollection<OptionItem> NetModeOptions { get; } = new()
    {
        new OptionItem("0", "802.11n"),
        new OptionItem("4", "802.11ax (Wi-Fi 6)"),
    };

    public ObservableCollection<OptionItem> ChannelOptions { get; } = new();

    private OptionItem? _selectedNetMode;
    public OptionItem? SelectedNetMode { get => _selectedNetMode; set => Set(ref _selectedNetMode, value); }

    private OptionItem? _selectedChannel;
    public OptionItem? SelectedChannel { get => _selectedChannel; set => Set(ref _selectedChannel, value); }

    /// <summary>带宽：原厂页面没有可见的带宽选项，此处仅展示设备真实返回值。</summary>
    public string BandwidthNote =>
        $"设备返回：bandwidth={Bandwidth}，bandwidth_acs={BandwidthAcs}。原厂界面未提供带宽选项，" +
        "其取值语义无法从设备侧确认，故本软件只读展示，不提供写入。";

    // ---------------- 安全 ----------------

    private string _ssid = "";
    public string Ssid { get => _ssid; set => Set(ref _ssid, value); }

    private string _password = "";
    public string Password { get => _password; set => Set(ref _password, value); }

    private bool _broadcastSsid = true;
    public bool BroadcastSsid { get => _broadcastSsid; set => Set(ref _broadcastSsid, value); }

    public string CurrentSecurityMode { get; private set; } = "—";
    public string CurrentKeyMasked { get; private set; } = "—";
    public string EncryptionMode { get; private set; } = "AES-CCMP";
    public string WapiSupport { get; private set; } = "0";
    public string LcdShowPwd { get; private set; } = "0";

    public ObservableCollection<OptionItem> SecurityModeOptions { get; } = new()
    {
        new OptionItem("Mixed", "WPA/WPA2 混合"),
        new OptionItem("WPA2-PSK", "WPA2-PSK"),
        new OptionItem("None", "不加密（开放网络）"),
    };

    private OptionItem? _selectedSecurityMode;
    public OptionItem? SelectedSecurityMode { get => _selectedSecurityMode; set => Set(ref _selectedSecurityMode, value); }

    // ---------------- 只读：休眠 / MAC 过滤 / WPS ----------------

    private bool _sleepEnabled;
    public bool SleepEnabled { get => _sleepEnabled; set => Set(ref _sleepEnabled, value); }

    private string _sleepMinutesText = "0";
    public string SleepMinutesText { get => _sleepMinutesText; set => Set(ref _sleepMinutesText, value); }

    public string MacFilterState { get; private set; } = "—";
    public string MacFilterMode { get; private set; } = "—";
    public string MacAllowText { get; private set; } = "（空）";
    public string MacDenyText { get; private set; } = "（空）";
    public string WpsState { get; private set; } = "—";

    public string MacFilterNote =>
        "设备提供了 MAC 黑白名单接口（wlan_mac_filters_info / _set），但当前设备返回的 allow_list / deny_list 均为空数组，" +
        "条目字段结构无法确认；原厂界面也没有该页面。写入行为未经真机验证，因此本版本仅做只读展示。";

    public string WpsNote =>
        "设备提供 wlan_wps_info 与 WPS 写入字段（connect_method 取值 1/2/3），但原厂前端未标注其语义，未经真机验证，" +
        "故本版本仅展示 WPS 状态，不提供启动按钮。";

    public ICommand ToggleWifiCommand { get; }
    public ICommand SaveBasicCommand { get; }
    public ICommand SaveSecurityCommand { get; }
    public ICommand SaveSleepCommand { get; }

    protected override async Task LoadAsync()
    {
        var api = Host.Api!;

        var w = await api.GetWlanInfoAsync(CancellationToken.None);
        WlanEnable = J.Bool(w.WlanEnable) ?? false;
        RfBand = w.RfBand ?? "1";
        NetMode = w.NetMode ?? "4";
        Channel = w.Channel ?? "0";
        Bandwidth = w.Bandwidth ?? "0";
        BandwidthAcs = w.BandwidthAcs ?? "0";
        Only20M = w.Only20M ?? "0";
        FirstChannel = w.FirstChannel ?? "1";
        LastChannel = w.LastChannel ?? "13";
        InitialMaxClients = w.InitialMaxClients ?? "10";
        BeaconPeriod = w.BeaconPeriod ?? "—";
        DtimInterval = w.DtimInterval ?? "—";
        DualBandSupport = w.DualBandSupport ?? "0";
        ApIsolate = J.Bool(w.ApIsolate) ?? false;
        MaxClientsText = w.MaxClients ?? InitialMaxClients;
        SleepEnabled = (J.Int(w.WifiSleepTime) ?? 0) > 0;
        SleepMinutesText = w.WifiSleepTime ?? "0";

        BuildChannelOptions();
        SelectedNetMode = NetModeOptions.FirstOrDefault(o => o.Value == NetMode)
                          ?? new OptionItem(NetMode, $"当前值 {NetMode}");

        var s = await api.GetWlanSecurityAsync(CancellationToken.None);
        Ssid = s.Ssid;
        Password = s.CurrentKey ?? "";
        BroadcastSsid = J.Bool(s.SsidBcast) ?? true;
        CurrentSecurityMode = s.Mode ?? "—";
        CurrentKeyMasked = string.IsNullOrEmpty(s.CurrentKey) ? "（设备未返回密钥）" : new string('•', Math.Min(12, s.CurrentKey.Length));
        EncryptionMode = s.Mixed?.Mode ?? s.Wpa2Psk?.Mode ?? "AES-CCMP";
        WapiSupport = s.WapiSupport ?? "0";
        LcdShowPwd = s.LcdShowWifiPwd ?? "0";
        SelectedSecurityMode = SecurityModeOptions.FirstOrDefault(o => o.Value == s.Mode)
                               ?? SecurityModeOptions[0];

        var f = await api.GetMacFiltersAsync(CancellationToken.None);
        MacFilterState = J.Bool(f.Enable) switch { true => "已启用", false => "未启用", null => "—" };
        MacFilterMode = f.Mode switch { "1" => "白名单", "2" => "黑名单", null => "—", var v => $"原始值 {v}" };
        MacAllowText = f.AllowList is { Count: > 0 } a ? string.Join("\n", a.Select(WlanMacFiltersInfo.Describe)) : "（空）";
        MacDenyText = f.DenyList is { Count: > 0 } d ? string.Join("\n", d.Select(WlanMacFiltersInfo.Describe)) : "（空）";

        var wps = await api.GetWpsAsync(CancellationToken.None);
        WpsState = J.Bool(wps.WpsStatus) switch { true => "已启用", false => "未启用", null => "—" };

        foreach (var n in new[]
                 {
                     nameof(RfBand), nameof(NetMode), nameof(Channel), nameof(Bandwidth), nameof(BandwidthAcs),
                     nameof(FirstChannel), nameof(LastChannel), nameof(InitialMaxClients), nameof(BeaconPeriod),
                     nameof(DtimInterval), nameof(DualBandSupport), nameof(Only20M), nameof(BandwidthNote), nameof(CurrentSecurityMode),
                     nameof(CurrentKeyMasked), nameof(EncryptionMode), nameof(WapiSupport), nameof(LcdShowPwd),
                     nameof(MacFilterState), nameof(MacFilterMode), nameof(MacAllowText), nameof(MacDenyText),
                     nameof(WpsState),
                 })
            OnPropertyChanged(n);
    }

    private void BuildChannelOptions()
    {
        ChannelOptions.Clear();
        ChannelOptions.Add(new OptionItem("0", "自动"));
        if (int.TryParse(FirstChannel, out var first) && int.TryParse(LastChannel, out var last) && last >= first)
            for (var c = first; c <= last; c++)
                ChannelOptions.Add(new OptionItem(c.ToString(), $"信道 {c}"));
        SelectedChannel = ChannelOptions.FirstOrDefault(o => o.Value == Channel) ?? ChannelOptions[0];
    }

    private void ValidateSecurity()
    {
        if (string.IsNullOrWhiteSpace(Ssid))
            throw new RouterException(RouterErrorKind.Device, "Wi-Fi 名称不能为空。");
        if (SelectedSecurityMode?.Value is "Mixed" or "WPA2-PSK")
        {
            if (string.IsNullOrEmpty(Password) || Password.Length < 8)
                throw new RouterException(RouterErrorKind.Device, "WPA/WPA2 密码长度至少 8 位。");
            if (Password.Length > 32)
                throw new RouterException(RouterErrorKind.Device, "WPA/WPA2 密码长度不能超过 32 位。");
        }
    }
}
