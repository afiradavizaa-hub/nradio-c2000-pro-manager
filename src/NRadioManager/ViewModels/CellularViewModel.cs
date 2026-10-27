using System.Collections.ObjectModel;
using System.Windows.Input;
using NRadioManager.Core;
using NRadioManager.Infrastructure;

namespace NRadioManager.ViewModels;

/// <summary>
/// 移动网络模块：拨号控制、网络模式、漫游、MTU、APN 配置、PDP 连接信息与 LTE 工程参数。
/// 网络模式取值的可用范围由设备返回的 NW_mode_default 决定（与原厂前端一致）。
/// </summary>
public sealed class CellularViewModel : ModuleViewModel
{
    public CellularViewModel(MainViewModel host) : base(host, "移动网络", "LTE 连接、APN 与小区信息")
    {
        ConnectDataCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            await Host.Api!.SetConnectionAsync(true, CancellationToken.None);
        }, "已发送拨号命令"), () => IsConnected);

        DisconnectDataCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            await Host.Api!.SetConnectionAsync(false, CancellationToken.None);
        }, "已发送断开命令"), () => IsConnected);

        SaveDialCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            await Host.Api!.SetConnectModeAsync(ConnectMode, DisableAutoDialOnRoaming, CancellationToken.None);
        }, "连接模式已下发，设备会按新设置重新拨号"), () => IsConnected);

        SaveNetworkCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            var mtu = J.Int(MtuText);
            if (mtu is not null && (mtu < 1000 || mtu > 1500))
                throw new RouterException(RouterErrorKind.Device, "MTU 必须在 1000–1500 之间。");
            var mode = SelectedNetworkMode?.Value ?? NwMode;
            await Host.Api!.SetNetworkAsync(Proto, mode, mode, SelectedPreferLteType?.Value ?? PreferLteType,
                mtu, CancellationToken.None);
        }, "网络模式已下发"), () => IsConnected);

        SaveAutoApnCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            await Host.Api!.SetAutoApnAsync(AutoApn, CancellationToken.None);
        }, "自动 APN 设置已下发"), () => IsConnected);

        SaveProfilesCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            await Host.Api!.SetApnProfilesAsync(Profiles, CancellationToken.None);
        }, "APN 配置已下发"), () => IsConnected);

        AddProfileCommand = new RelayCommand(AddProfile);
        RemoveProfileCommand = new RelayCommand(RemoveSelectedProfile, () => SelectedProfile is not null);
    }

    private bool IsConnected => Host.IsConnected;

    // ---------------- 连接 ----------------

    public string ConnectionState { get; private set; } = "—";
    public string Operator { get; private set; } = "—";
    public string WanIp { get; private set; } = "—";
    public string SimState { get; private set; } = "—";

    private string _proto = "cellular";
    public string Proto { get => _proto; set => Set(ref _proto, value); }

    private string _nwMode = "2";
    public string NwMode { get => _nwMode; private set => Set(ref _nwMode, value); }

    private string _nwModeDefault = "1";
    public string NwModeDefault { get => _nwModeDefault; private set => Set(ref _nwModeDefault, value); }

    private string _preferLteType = "1";
    public string PreferLteType { get => _preferLteType; private set => Set(ref _preferLteType, value); }

    public string PreferMode { get; private set; } = "—";

    private string _connectMode = "0";
    public string ConnectMode
    {
        get => _connectMode;
        set
        {
            if (!Set(ref _connectMode, value)) return;
            OnPropertyChanged(nameof(IsAutoConnect));
            OnPropertyChanged(nameof(IsManualConnect));
        }
    }

    public bool IsAutoConnect
    {
        get => _connectMode == "0";
        set => ConnectMode = value ? "0" : "1";
    }

    public bool IsManualConnect
    {
        get => _connectMode == "1";
        set => ConnectMode = value ? "1" : "0";
    }

    private bool _disableAutoDialOnRoaming;
    public bool DisableAutoDialOnRoaming { get => _disableAutoDialOnRoaming; set => Set(ref _disableAutoDialOnRoaming, value); }

    private string _mtuText = "";
    public string MtuText { get => _mtuText; set => Set(ref _mtuText, value); }

    public ObservableCollection<OptionItem> NetworkModeOptions { get; } = new();
    public ObservableCollection<OptionItem> PreferLteOptions { get; } = new()
    {
        new OptionItem("0", "TD-LTE 优先"),
        new OptionItem("1", "FDD-LTE 优先"),
    };

    private OptionItem? _selectedNetworkMode;
    public OptionItem? SelectedNetworkMode { get => _selectedNetworkMode; set => Set(ref _selectedNetworkMode, value); }

    private OptionItem? _selectedPreferLteType;
    public OptionItem? SelectedPreferLteType { get => _selectedPreferLteType; set => Set(ref _selectedPreferLteType, value); }

    // ---------------- APN ----------------

    private bool _autoApn;
    public bool AutoApn { get => _autoApn; set => Set(ref _autoApn, value); }

    public ObservableCollection<ApnProfile> Profiles { get; } = new();

    private ApnProfile? _selectedProfile;
    public ApnProfile? SelectedProfile
    {
        get => _selectedProfile;
        set { if (Set(ref _selectedProfile, value)) (RemoveProfileCommand as RelayCommand)?.RaiseCanExecuteChanged(); }
    }

    public ObservableCollection<OptionItem> IpTypeOptions { get; } = new()
    {
        new OptionItem("1", "IPv4"),
        new OptionItem("2", "IPv6"),
        new OptionItem("3", "IPv4/IPv6"),
    };

    public ObservableCollection<OptionItem> AuthTypeOptions { get; } = new()
    {
        new OptionItem("NONE", "无"),
        new OptionItem("PAP", "PAP"),
        new OptionItem("CHAP", "CHAP"),
        new OptionItem("PAP_CHAP", "PAP 或 CHAP"),
    };

    public ObservableCollection<OptionItem> ApnEncryptionHints { get; } = new();

    // ---------------- 连接信息 / 工程模式 ----------------

    public ObservableCollection<PdpContext> PdpContexts { get; } = new();
    public string PdpNetworkName { get; private set; } = "—";

    public string CellInfo { get; private set; } = "—";
    public string SignalRaw { get; private set; } = "—";
    public string ThroughputRaw { get; private set; } = "—";
    public string EngineeringNote { get; } =
        "说明：工程模式下的 RSRP/RSRQ/SINR/CQI 为基带原始编码值，原厂后台同样原样显示，" +
        "并非 dBm，本软件不做主观换算以免误导；概览页的 RSSI 为设备直接给出的 dBm 值。";

    public ICommand ConnectDataCommand { get; }
    public ICommand DisconnectDataCommand { get; }
    public ICommand SaveDialCommand { get; }
    public ICommand SaveNetworkCommand { get; }
    public ICommand SaveAutoApnCommand { get; }
    public ICommand SaveProfilesCommand { get; }
    public ICommand AddProfileCommand { get; }
    public ICommand RemoveProfileCommand { get; }

    protected override async Task LoadAsync()
    {
        var api = Host.Api!;

        var wan = await api.GetWanInfoAsync(CancellationToken.None);
        Proto = wan.Proto ?? "cellular";
        NwMode = wan.NwMode ?? "2";
        NwModeDefault = wan.NwModeDefault ?? "1";
        PreferLteType = wan.PreferLteType ?? "1";
        PreferMode = wan.PreferMode ?? "—";
        ConnectMode = wan.ConnectMode ?? "0";
        DisableAutoDialOnRoaming = J.Bool(wan.RoamingDisableAutoDial) ?? false;
        MtuText = wan.Mtu ?? "";
        BuildNetworkModeOptions();

        var apn = await api.GetApnProfilesAsync(CancellationToken.None);
        AutoApn = J.Bool(apn.AutoApn) ?? false;
        Profiles.Clear();
        foreach (var p in apn.ProfileList ?? new List<ApnProfile>()) Profiles.Add(p);
        SelectedProfile ??= Profiles.FirstOrDefault();

        var pdp = await api.GetPdpContextAsync(CancellationToken.None);
        PdpNetworkName = string.IsNullOrWhiteSpace(pdp.NetworkName) ? "未注册" : pdp.NetworkName;
        PdpContexts.Clear();
        foreach (var c in pdp.PdpContextList ?? new List<PdpContext>()) PdpContexts.Add(c);

        var eng = await api.GetEngineeringAsync(CancellationToken.None);
        ApplyEngineering(eng);

        var home = await api.GetHomepageAsync(CancellationToken.None);
        ConnectionState = home.ConnectDisconnect == "cellular" ? "已拨号连接"
            : home.ConnectDisconnect == "disabled" ? "已断开" : home.ConnectDisconnect ?? "—";
        Operator = string.IsNullOrWhiteSpace(home.NetworkName) ? "未注册" : home.NetworkName;
        WanIp = string.IsNullOrWhiteSpace(home.WanIp) ? "—" : home.WanIp;
        SimState = J.Int(home.SimStatus) switch
        {
            0 => "SIM 正常",
            1 => "未检测到 SIM 卡",
            null => "—",
            var v => $"设备状态码 {v}",
        };

        foreach (var n in new[] { nameof(ConnectionState), nameof(Operator), nameof(WanIp), nameof(SimState), nameof(PreferMode) })
            OnPropertyChanged(n);
    }

    private void ApplyEngineering(EngineeringInfo eng)
    {
        var lte = eng.Lte;
        if (lte is null || string.IsNullOrEmpty(lte.Band))
        {
            CellInfo = "设备未上报 LTE 小区参数。";
            SignalRaw = "—";
            ThroughputRaw = "—";
            return;
        }

        CellInfo = $"MCC {lte.Mcc} / MNC {lte.Mnc} · TAC {lte.Tac} · PCI {lte.PhyCellId} · " +
                   $"Band {lte.Band} · 下行带宽 {lte.DlBandwidth} · DL EARFCN {lte.DlEuArfcn} / UL EARFCN {lte.UlEuArfcn}\n" +
                   $"ECGI {lte.Ecgi} · eNB {lte.EnbId} · Cell {lte.CellId}";
        SignalRaw = $"RSRP {lte.Rsrp}（主集 {lte.MainRsrp} / 分集 {lte.DiversityRsrp}） · " +
                    $"RSRQ {lte.Rsrq}（主集 {lte.MainRsrq} / 分集 {lte.DiversityRsrq}） · " +
                    $"SINR {lte.Sinr} · RSSI {lte.Rssi} · CQI {lte.Cqi} · 发射功率 {lte.TxPower}";
        ThroughputRaw = $"下行 {lte.DlThroughPut}（峰值 {lte.DlPeakThroughPut}） · " +
                        $"上行 {lte.UlThroughPut}（峰值 {lte.UlPeakThroughPut}）";
        foreach (var n in new[] { nameof(CellInfo), nameof(SignalRaw), nameof(ThroughputRaw) }) OnPropertyChanged(n);
    }

    /// <summary>按设备 NW_mode_default 生成与原厂一致的可用网络模式列表。</summary>
    private void BuildNetworkModeOptions()
    {
        var all = new Dictionary<string, string>
        {
            ["1"] = "多模（自动）",
            ["2"] = "仅 4G",
            ["3"] = "4G/3G",
            ["8"] = "4G/2G",
            ["4"] = "3G/2G",
            ["5"] = "仅 3G",
            ["6"] = "仅 2G",
            ["7"] = "关闭网络",
        };

        var allowed = NwModeDefault switch
        {
            "1" => new[] { "1", "2", "3", "8", "4", "5", "6", "7" },
            "2" => new[] { "2" },
            "3" => new[] { "2", "3", "5" },
            "8" => new[] { "2", "8", "6" },
            "4" => new[] { "4", "5", "6" },
            _ => new[] { NwMode },
        };

        NetworkModeOptions.Clear();
        foreach (var v in allowed)
            if (all.TryGetValue(v, out var text)) NetworkModeOptions.Add(new OptionItem(v, text));
        if (!NetworkModeOptions.Any(o => o.Value == NwMode))
            NetworkModeOptions.Add(new OptionItem(NwMode, $"当前值 {NwMode}（设备未提供该选项的名称）"));

        SelectedNetworkMode = NetworkModeOptions.FirstOrDefault(o => o.Value == NwMode);
        SelectedPreferLteType = PreferLteOptions.FirstOrDefault(o => o.Value == PreferLteType);
    }

    private void AddProfile()
    {
        var index = Profiles.Count + 1;
        var profile = new ApnProfile
        {
            ProfileName = $"PDN{index}",
            Enable = "1",
            IsDefault = "0",
            Apn = "",
            LteApn = "",
            IpType = "1",
            Qci = "0",
            AuthType2G3 = "NONE",
            AuthType4G = "NONE",
        };
        Profiles.Add(profile);
        SelectedProfile = profile;
        ShowMessage("已新增一条空白 APN 配置，填写完整后请点击「保存 APN 配置」下发到设备。", isError: false);
    }

    private void RemoveSelectedProfile()
    {
        if (SelectedProfile is null) return;
        var name = SelectedProfile.ProfileName;
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.FirstOrDefault();
        ShowMessage($"已从列表移除「{name}」，点击「保存 APN 配置」后设备才会真正生效。", isError: false);
    }
}
