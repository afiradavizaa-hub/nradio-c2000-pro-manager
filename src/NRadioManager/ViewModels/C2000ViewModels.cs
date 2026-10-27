using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using NRadioManager.Controls;
using NRadioManager.Core;
using NRadioManager.Infrastructure;

namespace NRadioManager.ViewModels;

public abstract class C2000ModuleViewModel : ModuleViewModel
{
    protected C2000ModuleViewModel(MainViewModel host, string title, string subtitle)
        : base(host, title, subtitle)
    {
    }

    protected C2000ProAdapter Adapter => Host.C2000Adapter
        ?? throw new RouterException(RouterErrorKind.NotSupported, "当前连接不是 C2000 Pro。", "adapter");

    public bool IsC2000 => Host.Device?.Kind == DeviceKind.C2000Pro;
}

public sealed class C2000CellularViewModel : C2000ModuleViewModel
{
    public C2000CellularViewModel(MainViewModel host)
        : base(host, "蜂窝网络", "5G/4G 注册、SIM 与连接状态")
    {
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync());
        SaveNetworkModeCommand = new AsyncRelayCommand(() => ConfirmNetworkModeAsync(),
            () => Host.IsConnected && SelectedNetworkMode is not null);
    }

    public ICommand RefreshCommand { get; }
    public ICommand SaveNetworkModeCommand { get; }
    public ObservableCollection<OptionItem> NetworkModeOptions { get; } = new()
    {
        new OptionItem("auto", "自动（推荐）"),
        new OptionItem("sa_only", "仅 5G SA"),
        new OptionItem("lte", "仅 4G"),
    };
    private OptionItem? _selectedNetworkMode;
    public OptionItem? SelectedNetworkMode
    {
        get => _selectedNetworkMode;
        set
        {
            if (Set(ref _selectedNetworkMode, value))
                (SaveNetworkModeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }
    public string NetworkModeOperation { get; private set; } = "尚未修改网络模式。";
    public string Model { get; private set; } = "—";
    public string Firmware { get; private set; } = "—";
    public string Module { get; private set; } = "—";
    public string ModuleFirmware { get; private set; } = "—";
    public string Mode { get; private set; } = "—";
    public string Operator { get; private set; } = "—";
    public string SimOperator { get; private set; } = "—";
    public string Registration { get; private set; } = "—";
    public string Apn { get; private set; } = "—";
    public string Ipv4 { get; private set; } = "—";
    public string Ipv6 { get; private set; } = "—";
    public string Dns { get; private set; } = "—";
    public string Imei { get; private set; } = "—";
    public string Imsi { get; private set; } = "—";
    public string Iccid { get; private set; } = "—";
    public string CurrentSlot { get; private set; } = "—";
    public string Uptime { get; private set; } = "—";
    public string WanUptime { get; private set; } = "—";
    public string CpuText { get; private set; } = "—";
    public string MemoryText { get; private set; } = "—";
    public string TemperatureText { get; private set; } = "—";
    public string UpdatedText { get; private set; } = "—";
    public string SourceNote =>
        "数据来源：/nradio/status/runtime/wan,cpe_4 与 /nradio/cellular/cpestatus/cellinfo。" +
        "RSRP/RSRQ/SINR 是设备直接返回的 dBm/dB 值；未返回的字段保持“—”。";

    protected override async Task LoadAsync()
    {
        var snapshot = await Adapter.GetSnapshotAsync(CancellationToken.None);
        var runtime = snapshot.Runtime;
        var cpe = snapshot.Cell.Status ?? runtime.Cpe?.FirstOrDefault();
        var wan = runtime.Wans?.FirstOrDefault(x => string.Equals(x.ClassName, cpe?.Name, StringComparison.OrdinalIgnoreCase))
                  ?? runtime.Wans?.FirstOrDefault();

        Model = snapshot.Identity.Model;
        Firmware = snapshot.Identity.FirmwareVersion;
        Module = cpe?.Model is { Length: > 0 } m && m != "none" ? m : "—";
        ModuleFirmware = cpe?.Revision is { Length: > 0 } r && r != "none" ? r : "—";
        Mode = cpe?.Mode is { Length: > 0 } mode && mode != "none" ? mode : "—";
        Operator = cpe?.Isp is { Length: > 0 } isp && isp != "none" ? isp : "未注册";
        SimOperator = cpe?.SimIsp is { Length: > 0 } sim && sim != "none" ? sim : "—";
        Registration = cpe?.Status == 0 && cpe.Isp is { Length: > 0 } && cpe.Isp != "none"
            ? "已注册 / 已连接"
            : "未确认或未连接";
        Apn = cpe?.Apn ?? "—";
        Ipv4 = cpe?.V4?.Addresses?.FirstOrDefault() ?? "—";
        Ipv6 = cpe?.V6?.Addresses?.FirstOrDefault() ?? "—";
        Dns = Join(cpe?.V4?.Dns, cpe?.V6?.Dns);
        Imei = cpe?.Imei ?? "—";
        Imsi = cpe?.Imsi ?? "—";
        Iccid = cpe?.Iccid ?? "—";
        CurrentSlot = cpe?.SimNo switch { "1" => "Embedded SIM1", "2" => "Embedded SIM2", _ => "—" };
        Uptime = J.Duration(runtime.Global?.Uptime);
        WanUptime = J.Duration(wan?.Uptime ?? snapshot.Cell.Uptime);
        CpuText = Percent(runtime.Global?.CpuPercent ?? cpe?.CpuPercent);
        MemoryText = Percent(runtime.Global?.MemPercent ?? cpe?.MemPercent);
        TemperatureText = string.IsNullOrWhiteSpace(cpe?.ModelTemp) ? "—" : $"{cpe.ModelTemp} ℃";
        UpdatedText = snapshot.SampledAt.ToString("yyyy-MM-dd HH:mm:ss");
        try
        {
            var selectedMode = await Adapter.GetNetworkModeSelectionAsync("wan0", CancellationToken.None);
            SelectedNetworkMode = NetworkModeOptions.FirstOrDefault(x => x.Value == selectedMode);
        }
        catch (RouterException)
        {
            SelectedNetworkMode = null;
        }

        foreach (var name in new[]
                 {
                     nameof(Model), nameof(Firmware), nameof(Module), nameof(ModuleFirmware), nameof(Mode),
                     nameof(Operator), nameof(SimOperator), nameof(Registration), nameof(Apn), nameof(Ipv4),
                     nameof(Ipv6), nameof(Dns), nameof(Imei), nameof(Imsi), nameof(Iccid), nameof(CurrentSlot),
                     nameof(Uptime), nameof(WanUptime), nameof(CpuText), nameof(MemoryText),
                     nameof(TemperatureText), nameof(UpdatedText),
                 })
            OnPropertyChanged(name);
    }

    private async Task ConfirmNetworkModeAsync()
    {
        if (SelectedNetworkMode is null) return;
        if (MessageBox.Show(
                $"将网络模式改为「{SelectedNetworkMode.Text}」？\n\n设备会重新注册网络，当前连接可能短暂中断。",
                "修改网络模式", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (MessageBox.Show(
                "最终确认：仅 5G/仅 4G 在无对应覆盖时可能导致无法联网。是否继续？",
                "最终确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await RunAsync(async () =>
        {
            Log.Info(LogCategory.Configuration, "网络制式",
                $"用户经二次确认请求应用网络制式：{SelectedNetworkMode.Value}。");
            var result = await Adapter.SetNetworkModeAsync(SelectedNetworkMode.Value, CancellationToken.None);
            NetworkModeOperation = result.Message + $"（{result.Before} → {result.After}）";
            OnPropertyChanged(nameof(NetworkModeOperation));
            if (!result.Confirmed)
                throw new RouterException(RouterErrorKind.Device, result.Message, "network-mode");
            Log.Success(LogCategory.Configuration, "网络制式",
                $"网络制式写入并回读：{result.Before} → {result.After}");
        }, "网络模式已回读确认");
    }

    private static string Join(params List<string>?[] parts)
        => parts.Where(x => x is { Count: > 0 }).SelectMany(x => x!)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().DefaultIfEmpty("—").Aggregate((a, b) => a + " / " + b);

    private static string Percent(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? "—" : raw + " %";
}

public sealed class C2000SignalViewModel : C2000ModuleViewModel
{
    private string _range = "1h";

    public C2000SignalViewModel(MainViewModel host)
        : base(host, "信号分析", "RSRP / RSRQ / SINR 实时值与本地趋势")
    {
        SelectRangeCommand = new RelayCommand(p =>
        {
            if (p is string value) SelectedRange = value;
        });
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync());
        ReloadChart();
    }

    public ICommand SelectRangeCommand { get; }
    public ICommand RefreshCommand { get; }
    public ObservableCollection<SamplePoint> SignalPoints { get; } = new();
    public ObservableCollection<SignalMetricPoint> SignalMetrics { get; } = new();
    public string CurrentRsrp { get; private set; } = "—";
    public string CurrentRsrq { get; private set; } = "—";
    public string CurrentSinr { get; private set; } = "—";
    public string CurrentCqi { get; private set; } = "—";
    public double? RsrpValue { get; private set; }
    public double? RsrqValue { get; private set; }
    public double? SinrValue { get; private set; }
    public double? CqiValue { get; private set; }
    public string Band { get; private set; } = "—";
    public string Pci { get; private set; } = "—";
    public string Earfcn { get; private set; } = "—";
    public string Cell { get; private set; } = "—";
    public string Bandwidth { get; private set; } = "—";
    public string ConnectionText { get; private set; } = "—";
    public string UpdateText { get; private set; } = "—";
    public string SampleCountText { get; private set; } = "0 个有效采样";
    public string RangeTitle => _range switch { "6h" => "最近 6 小时", "24h" => "最近 24 小时", "7d" => "最近 7 天", _ => "最近 1 小时" };
    public string CurrentQualityText { get; private set; } = "等待信号数据";
    public string AnalysisRecommendation { get; private set; } =
        "建议结合 RSRP、RSRQ、SINR 三项观察：RSRP 看覆盖，RSRQ 看小区负载与干扰，SINR 看可用链路质量。";
    public string LastSampleText { get; private set; } = "最后采样：—";
    public bool Is1Hour => _range == "1h";
    public bool Is6Hours => _range == "6h";
    public bool Is24Hours => _range == "24h";
    public bool Is7Days => _range == "7d";
    public string SourceNote =>
        "趋势来自本机定时采样，保存于 %APPDATA%\\NRadioManager\\c2000-signal-history.json；" +
        "设备未返回或断连的区间保留缺口，不插值、不补零。";

    public string SelectedRange
    {
        get => _range;
        private set
        {
            if (!Set(ref _range, value)) return;
            OnPropertyChanged(nameof(RangeTitle));
            OnPropertyChanged(nameof(Is1Hour));
            OnPropertyChanged(nameof(Is6Hours));
            OnPropertyChanged(nameof(Is24Hours));
            OnPropertyChanged(nameof(Is7Days));
            ReloadChart();
        }
    }

    protected override async Task LoadAsync()
    {
        var snapshot = await Adapter.GetSnapshotAsync(CancellationToken.None);
        Host.SignalHistory.Observe(snapshot, DateTime.Now);
        ApplySnapshot(snapshot);
    }

    public void ApplySnapshot(C2000ProRuntimeSnapshot? snapshot)
    {
        if (snapshot is null) return;
        var c = snapshot.Cell.Status;
        RsrpValue = Number(c?.Rsrp);
        RsrqValue = Number(c?.Rsrq);
        SinrValue = Number(c?.Sinr);
        CqiValue = Number(c?.Cqi);
        CurrentRsrp = Unit(c?.Rsrp, "dBm");
        CurrentRsrq = Unit(c?.Rsrq, "dB");
        CurrentSinr = Unit(c?.Sinr, "dB");
        CurrentCqi = Text(c?.Cqi);
        Band = Text(c?.Band);
        Pci = Text(c?.Pci);
        Earfcn = Text(c?.Earfcn);
        Cell = Text(c?.Cell);
        Bandwidth = FormatBandwidth(c?.DownlinkBandwidthKhz);
        ConnectionText = c?.Status == 0 && c.Isp is { Length: > 0 } && c.Isp != "none" ? "已注册 / 已连接" : "未连接或状态未知";
        UpdateText = snapshot.SampledAt.ToString("yyyy-MM-dd HH:mm:ss");
        CurrentQualityText = RsrpValue is { } rsrp
            ? $"{QualityText(rsrp)} · RSRP {rsrp:0} dBm"
            : "设备未返回有效 RSRP";
        AnalysisRecommendation = BuildRecommendation(RsrpValue, RsrqValue, SinrValue);
        LastSampleText = $"最后采样：{snapshot.SampledAt:yyyy-MM-dd HH:mm:ss} · 数据由设备实时上报";
        foreach (var name in new[]
                 {
                     nameof(CurrentRsrp), nameof(CurrentRsrq), nameof(CurrentSinr), nameof(CurrentCqi),
                     nameof(RsrpValue), nameof(RsrqValue), nameof(SinrValue), nameof(CqiValue),
                     nameof(Band), nameof(Pci), nameof(Earfcn), nameof(Cell), nameof(Bandwidth),
                     nameof(ConnectionText), nameof(UpdateText), nameof(CurrentQualityText),
                     nameof(AnalysisRecommendation), nameof(LastSampleText),
                 })
            OnPropertyChanged(name);
        ReloadChart();
    }

    public void ReloadChart()
    {
        var now = DateTime.Now;
        var from = _range switch
        {
            "6h" => now.AddHours(-6),
            "24h" => now.AddHours(-24),
            "7d" => now.AddDays(-7),
            _ => now.AddHours(-1),
        };
        var samples = Host.SignalHistory.InRange(from, now);
        SignalPoints.Clear();
        SignalMetrics.Clear();
        foreach (var sample in samples)
        {
            SignalPoints.Add(new SamplePoint(sample.Time, 0, 0,
                sample.RsrpDbm is { } v ? (int)Math.Round(v) : null, null, HasSpeed: false));
            SignalMetrics.Add(new SignalMetricPoint(
                sample.Time, sample.RsrpDbm, sample.RsrqDb, sample.SinrDb, sample.Connected,
                sample.CellId, sample.Band, sample.Earfcn, sample.Pci));
        }
        while (SignalPoints.Count > 600) SignalPoints.RemoveAt(0);
        while (SignalMetrics.Count > 600) SignalMetrics.RemoveAt(0);
        var valid = SignalPoints.Count(x => x.RssiDbm is not null);
        SampleCountText = $"{valid} 个有效 RSRP 采样 / 共 {SignalPoints.Count} 个时间点";
        OnPropertyChanged(nameof(SampleCountText));
    }

    private static string Unit(string? value, string unit)
        => string.IsNullOrWhiteSpace(value) ? "—" : $"{value} {unit}";
    private static string Text(string? value)
        => string.IsNullOrWhiteSpace(value) || value == "none" ? "—" : value;
    private static string FormatBandwidth(string? khz)
        => C2000ProAdapter.TryLong(khz) is { } n ? $"{n / 1000d:0.###} MHz" : "—";
    private static double? Number(string? raw)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static string QualityText(double rsrp)
        => rsrp >= -80 ? "信号优秀" : rsrp >= -90 ? "信号良好" : rsrp >= -100 ? "信号一般" : "信号较差";

    private static string BuildRecommendation(double? rsrp, double? rsrq, double? sinr)
    {
        if (rsrp is null && rsrq is null && sinr is null)
            return "设备当前未返回可解析的信号质量字段，页面不会推算或补造数值。";

        var points = new List<string>();
        if (rsrp < -100) points.Add("覆盖偏弱，优先调整设备位置或朝向");
        else if (rsrp >= -80) points.Add("覆盖优秀，保持当前方向通常即可");
        else points.Add("覆盖可用，无需仅凭 RSRP 调整天线");

        if (rsrq is < -15) points.Add("RSRQ 偏低，可能存在邻区干扰或小区负载");
        if (sinr is < 5) points.Add("SINR 偏低，应重点排查干扰源并比较邻区");
        else if (sinr >= 13) points.Add("SINR 良好，链路抗干扰能力稳定");
        if (points.Count == 1) points.Add("RSRQ 与 SINR 未见明显短板");
        return string.Join("；", points) + "。";
    }
}

public sealed class C2000LockViewModel : C2000ModuleViewModel
{
    public C2000LockViewModel(MainViewModel host)
        : base(host, "锁频与小区", "专业版 Band & Cell Lock 实机能力")
    {
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync());
        ToggleCellLockCommand = new AsyncRelayCommand(p => ConfirmToggleAsync(p), _ => Host.IsConnected);
    }

    public ICommand RefreshCommand { get; }
    public ICommand ToggleCellLockCommand { get; }
    public ObservableCollection<C2000CellEntry> Cells { get; } = new();
    public string LockMode { get; private set; } = "—";
    public string CurrentCell { get; private set; } = "—";
    public string Band { get; private set; } = "—";
    public string Pci { get; private set; } = "—";
    public string Earfcn { get; private set; } = "—";
    public string Tac { get; private set; } = "—";
    public string Rsrp { get; private set; } = "—";
    public string Rsrq { get; private set; } = "—";
    public string Sinr { get; private set; } = "—";
    public string Cqi { get; private set; } = "—";
    public string Bandwidth { get; private set; } = "—";
    public string OperationText { get; private set; } = "尚未执行写操作。";
    public string RecommendationTitle { get; private set; } = "等待小区数据";
    public string RecommendationReason { get; private set; } = "刷新后将根据 RSRP、SINR 和当前服务小区给出建议。";
    public string RecommendationDisclaimer { get; } =
        "建议依据 3GPP TS 36.133/38.133 的 RSRP/SINR 测量定义与固定质量区间做本地排序，" +
        "不是运营商或厂商官方覆盖评级，也不能保证锁频后网速一定提升。";
    public string Warning =>
        "锁频写入会触发重新选网，可能导致管理连接中断。仅对下面设备实时扫描到的邻居小区开放操作；" +
        "执行前会二次确认，写入后必须回读 cfg 才能报告成功，HTTP 200 和 20 秒内无回读一致都会标记为待确认。";

    protected override async Task LoadAsync()
    {
        var lockInfo = await Adapter.GetLockSnapshotAsync("wan0", CancellationToken.None);
        LockMode = lockInfo.LockMode;
        CurrentCell = lockInfo.NciEci;
        Band = lockInfo.Band;
        Pci = lockInfo.Pci;
        Earfcn = lockInfo.Earfcn;
        Tac = lockInfo.Tac;
        Rsrp = lockInfo.RsrpDbm == "—" ? "—" : lockInfo.RsrpDbm + " dBm";
        Rsrq = lockInfo.RsrqDb == "—" ? "—" : lockInfo.RsrqDb + " dB";
        Sinr = lockInfo.SinrDb == "—" ? "—" : lockInfo.SinrDb + " dB";
        Cqi = lockInfo.Cqi;
        Bandwidth = lockInfo.Bandwidth;
        Cells.Clear();
        var entries = lockInfo.Neighbours.ToList();
        if (!entries.Any(x => x.Current) && lockInfo.Pci != "—" && lockInfo.Earfcn != "—")
            entries.Insert(0, new C2000CellEntry(
                string.IsNullOrWhiteSpace(lockInfo.Mode) ? "NR" : lockInfo.Mode,
                lockInfo.Band, lockInfo.Pci, lockInfo.Earfcn, lockInfo.RsrpDbm,
                lockInfo.RsrqDb, lockInfo.SinrDb, false, true));
        var candidates = entries
            .Where(x => x.RsrpValue is not null)
            .OrderByDescending(RecommendationScore)
            .ToList();
        var current = candidates.FirstOrDefault(x => x.Current);
        var best = candidates.FirstOrDefault();
        var recommended = current ?? best;
        if (current is not null && best is not null &&
            RecommendationScore(best) > RecommendationScore(current) + 3)
            recommended = best;

        foreach (var cell in entries.OrderByDescending(RecommendationScore))
            Cells.Add(cell with { Recommended = recommended is not null && SameCell(cell, recommended) });

        if (recommended is null)
        {
            RecommendationTitle = "暂无足够数据";
            RecommendationReason = "设备没有返回可比较的 RSRP/SINR 小区列表，暂不建议修改锁频。";
        }
        else if (recommended.Current)
        {
            RecommendationTitle = $"建议保持当前小区 · {recommended.BandText} / PCI {recommended.Pci}";
            RecommendationReason =
                $"当前小区 RSRP {recommended.SignalText}、SINR {recommended.SinrText}，质量等级“{recommended.Quality}”；" +
                "未发现明显更强且更稳定的邻区，保持现状可避免重新注册风险。";
        }
        else
        {
            RecommendationTitle = $"候选小区 · {recommended.BandText} / PCI {recommended.Pci}";
            RecommendationReason =
                $"该邻区 RSRP {recommended.SignalText}、SINR {recommended.SinrText}，综合评分高于当前小区。" +
                "建议先观察多次采样，再决定是否锁定；单次小区扫描不能保证持续稳定。";
        }
        foreach (var name in new[]
                 {
                     nameof(LockMode), nameof(CurrentCell), nameof(Band), nameof(Pci), nameof(Earfcn),
                     nameof(Tac), nameof(Rsrp), nameof(Rsrq), nameof(Sinr), nameof(Cqi), nameof(Bandwidth),
                     nameof(RecommendationTitle), nameof(RecommendationReason),
                 })
            OnPropertyChanged(name);
    }

    private static double RecommendationScore(C2000CellEntry cell)
    {
        var rsrp = cell.RsrpValue ?? -140;
        var sinr = cell.SinrValue ?? -10;
        return Math.Clamp((rsrp + 140) / 60 * 70, 0, 70) +
               Math.Clamp((sinr + 10) / 40 * 30, 0, 30);
    }

    private static bool SameCell(C2000CellEntry a, C2000CellEntry b)
        => string.Equals(a.Earfcn, b.Earfcn, StringComparison.Ordinal) &&
           string.Equals(a.Pci, b.Pci, StringComparison.Ordinal) &&
           string.Equals(a.Mode, b.Mode, StringComparison.Ordinal);

    private async Task ConfirmToggleAsync(object? parameter)
    {
        if (parameter is not C2000CellEntry cell) return;
        var enable = !cell.Locked;
        var modelType = cell.Mode.Contains("NR", StringComparison.OrdinalIgnoreCase) ? 5 : 4;
        var action = enable ? "锁定" : "解锁";
        var first = MessageBox.Show(
            $"{action}小区？\n\n模式 {cell.Mode}\nBand {cell.Band}\nPCI {cell.Pci}\nEARFCN {cell.Earfcn}\n\n" +
            "此操作会重新选网，当前网速和管理连接可能暂时中断。",
            $"{action}小区", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (first != MessageBoxResult.Yes) return;
        var second = MessageBox.Show(
            $"最终确认：{action} PCI={cell.Pci} / EARFCN={cell.Earfcn}。\n若无法注册，需要使用设备恢复自动选频。",
            "最终确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (second != MessageBoxResult.Yes) return;

        await RunAsync(async () =>
        {
            Log.Warn(LogCategory.Configuration, "锁频操作",
                $"用户经二次确认请求{(enable ? "锁定" : "解锁")}小区：Mode={cell.Mode}, Band={cell.Band}, " +
                $"PCI={cell.Pci}, EARFCN={cell.Earfcn}。");
            var result = await Adapter.SetCellLockAsync("wan0", modelType, cell.Earfcn, cell.Pci, cell.Band,
                enable, CancellationToken.None);
            OperationText = result.Message + "\n写入前：" + result.Before + "\n写入后：" + result.After;
            OnPropertyChanged(nameof(OperationText));
            if (!result.Confirmed)
                throw new RouterException(RouterErrorKind.Device, result.Message, "earfcn-lock");
            Log.Success(LogCategory.Configuration, "锁频操作",
                $"锁频结果已回读：{result.Before} → {result.After}");
        }, enable ? "小区锁已回读确认" : "自动选频已回读确认");
    }
}

public sealed class C2000SimViewModel : C2000ModuleViewModel
{
    public C2000SimViewModel(MainViewModel host)
        : base(host, "SIM 卡管理", "双内嵌 SIM 卡位与手动/自动切换")
    {
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync());
        SwitchToSlotCommand = new AsyncRelayCommand(p => ConfirmSwitchAsync(p), _ => Host.IsConnected && CanSwitchSim);
        RestoreAutoCommand = new AsyncRelayCommand(() => ConfirmAutoAsync(), () => Host.IsConnected && CanSwitchSim);
    }

    public ICommand RefreshCommand { get; }
    public ICommand SwitchToSlotCommand { get; }
    public ICommand RestoreAutoCommand { get; }
    public string SlotCount { get; private set; } = "—";
    public string CurrentSlot { get; private set; } = "—";
    public string SwitchMode { get; private set; } = "—";
    public string Registration { get; private set; } = "—";
    public string PinState { get; private set; } = "—";
    public string SimOperator { get; private set; } = "—";
    public string Imsi { get; private set; } = "—";
    public string Iccid { get; private set; } = "—";
    public string OperationText { get; private set; } = "尚未执行切卡写操作。";
    public bool CanSwitchSim { get; private set; }
    public string Note =>
        "当前固件提供 Manual/Auto 切换与 Embedded SIM1/SIM2，但切换会重新注册网络并可能中断管理连接。" +
        "按钮使用原厂完整 CBI 表单并保留未修改字段；每次操作需二次确认，写入后最多等待 90 秒回读注册状态。";

    protected override async Task LoadAsync()
    {
        var sim = await Adapter.GetSimSnapshotAsync("wan0", CancellationToken.None);
        CanSwitchSim = sim.Supported && sim.SlotCount > 0;
        SlotCount = sim.SlotCount == 0 ? "—" : sim.SlotCount.ToString(CultureInfo.InvariantCulture) + " 个内嵌卡位";
        CurrentSlot = sim.CurrentSlot;
        SwitchMode = sim.SwitchMode;
        Registration = sim.Registration;
        PinState = sim.PinState;
        SimOperator = sim.SimOperator;
        Imsi = sim.Imsi;
        Iccid = sim.Iccid;
        foreach (var name in new[]
                 {
                     nameof(SlotCount), nameof(CurrentSlot), nameof(SwitchMode), nameof(Registration),
                     nameof(PinState), nameof(SimOperator), nameof(Imsi), nameof(Iccid), nameof(CanSwitchSim),
                 })
            OnPropertyChanged(name);
        (SwitchToSlotCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RestoreAutoCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task ConfirmSwitchAsync(object? parameter)
    {
        if (parameter is not string raw || !int.TryParse(raw, out var slot) || slot is not (1 or 2))
            return;
        if (MessageBox.Show(
                $"切换到 Embedded SIM{slot}？\n\n设备会重新注册网络，可能短暂断网。",
                "切换 SIM 卡", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (MessageBox.Show(
                $"最终确认：将 SIM 切换模式设为 Manual，并选择 Embedded SIM{slot}。",
                "最终确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await RunAsync(async () =>
        {
            Log.Warn(LogCategory.Configuration, "SIM 切换",
                $"用户经二次确认请求切换到 Embedded SIM{slot}。");
            var result = await Adapter.SetSimModeAsync(slot, CancellationToken.None);
            OperationText = result.Message + "\n切换前：" + result.Before + "\n切换后：" + result.After;
            OnPropertyChanged(nameof(OperationText));
            if (!result.Confirmed)
                throw new RouterException(RouterErrorKind.Device, result.Message, "sim-switch");
            Log.Success(LogCategory.Configuration, "SIM 切换", result.Message + "；" + result.After);
        }, $"已切换到 Embedded SIM{slot}");
    }

    private async Task ConfirmAutoAsync()
    {
        if (MessageBox.Show(
                "切回自动选卡？\n\n设备将根据连接、流量和信号自动选择 SIM 卡，网络可能重新注册。",
                "恢复自动选卡", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await RunAsync(async () =>
        {
            Log.Warn(LogCategory.Configuration, "SIM 切换", "用户请求恢复自动选卡模式。");
            var result = await Adapter.SetSimModeAsync(null, CancellationToken.None);
            OperationText = result.Message + "\n切换前：" + result.Before + "\n切换后：" + result.After;
            OnPropertyChanged(nameof(OperationText));
            if (!result.Confirmed)
                throw new RouterException(RouterErrorKind.Device, result.Message, "sim-switch");
            Log.Success(LogCategory.Configuration, "SIM 切换", result.Message + "；" + result.After);
        }, "已恢复自动选卡模式");
    }
}

public sealed class C2000WifiViewModel : C2000ModuleViewModel
{
    public C2000WifiViewModel(MainViewModel host)
        : base(host, "Wi-Fi 与局域网", "双频热点、LAN 与接入状态")
    {
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync());
        Save24Command = new AsyncRelayCommand(() => ConfirmSaveWifiAsync(0), () => Host.IsConnected);
        Save5Command = new AsyncRelayCommand(() => ConfirmSaveWifiAsync(1), () => Host.IsConnected);
    }

    public ICommand RefreshCommand { get; }
    public ICommand Save24Command { get; }
    public ICommand Save5Command { get; }
    public ObservableCollection<C2000Radio> Radios { get; } = new();
    public ObservableCollection<OptionItem> Channels24 { get; } =
        new(new[] { new OptionItem("auto", "自动") }
            .Concat(Enumerable.Range(1, 13).Select(i => new OptionItem(i.ToString(), i.ToString()))));
    public ObservableCollection<OptionItem> Channels5 { get; } = new(
        new[] { ("auto", "自动"), ("36", "36"), ("40", "40"), ("44", "44"), ("48", "48"),
                ("149", "149"), ("153", "153"), ("157", "157"), ("161", "161"), ("165", "165") }
            .Select(x => new OptionItem(x.Item1, x.Item2)));
    public ObservableCollection<OptionItem> Bandwidth24 { get; } = new()
    {
        new("20", "20 MHz"), new("40", "40 MHz"),
    };
    public ObservableCollection<OptionItem> Bandwidth5 { get; } = new()
    {
        new("20", "20 MHz"), new("40", "40 MHz"), new("80", "80 MHz"),
    };
    public string LanAddress { get; private set; } = "—";
    public string DhcpState { get; private set; } = "—";
    public string ClientCount { get; private set; } = "—";
    public string Note =>
        "2.4 GHz 与 5 GHz 均可独立修改。保存会重新加载对应射频，若本机正连接该频段会短暂断网。" +
        "密码留空表示保持设备原密码。";
    public string PasswordNote => "密码通过 PasswordBox 输入且不写入普通日志；保存后等待配置回读。";
    public string OperationText { get; private set; } = "尚未执行 Wi-Fi 写操作。";
    public string Ssid24Input { get; set; } = "";
    public string Ssid5Input { get; set; } = "";
    private string _password24Input = "";
    public string Password24Input { get => _password24Input; set => Set(ref _password24Input, value); }
    private string _password5Input = "";
    public string Password5Input { get => _password5Input; set => Set(ref _password5Input, value); }
    public string Channel24Input { get; set; } = "auto";
    public string Channel5Input { get; set; } = "auto";
    public string Bandwidth24Input { get; set; } = "40";
    public string Bandwidth5Input { get; set; } = "80";
    public bool Wifi24Enabled { get; set; } = true;
    public bool Wifi5Enabled { get; set; } = true;
    public string Wifi24Summary { get; private set; } = "—";
    public string Wifi5Summary { get; private set; } = "—";

    protected override async Task LoadAsync()
    {
        var snapshot = await Adapter.GetSnapshotAsync(CancellationToken.None);
        Radios.Clear();
        foreach (var radio in snapshot.Runtime.Wlan?.Radios ?? new List<C2000Radio>())
            Radios.Add(radio);
        var wifi24 = FindRadio(0);
        var wifi5 = FindRadio(1);
        Ssid24Input = wifi24?.Ssid ?? "";
        Ssid5Input = wifi5?.Ssid ?? "";
        Channel24Input = wifi24?.Channel ?? "auto";
        Channel5Input = wifi5?.Channel ?? "auto";
        Wifi24Enabled = snapshot.Runtime.Wlan?.Radio2Disabled != 1;
        Wifi5Enabled = snapshot.Runtime.Wlan?.Radio5Disabled != 1;
        var bandwidths = await Adapter.GetWifiBandwidthsAsync(CancellationToken.None);
        Bandwidth24Input = Bandwidth24.Any(x => x.Value == bandwidths.Bandwidth24)
            ? bandwidths.Bandwidth24! : "40";
        Bandwidth5Input = Bandwidth5.Any(x => x.Value == bandwidths.Bandwidth5)
            ? bandwidths.Bandwidth5! : "80";
        Wifi24Summary = wifi24 is null ? "设备未返回 2.4 GHz 射频"
            : $"{wifi24.Ssid} · 信道 {wifi24.Channel} · {wifi24.StationCount ?? 0} 台终端";
        Wifi5Summary = wifi5 is null ? "设备未返回 5 GHz 射频"
            : $"{wifi5.Ssid} · 信道 {wifi5.Channel} · {wifi5.StationCount ?? 0} 台终端";
        var lan = snapshot.Runtime.Lans?.FirstOrDefault();
        LanAddress = lan?.Addresses?.FirstOrDefault() is { } address
            ? $"{address.IpAddress} / {address.NetMask}"
            : "—";
        DhcpState = lan?.Proto is { Length: > 0 } p ? $"接口协议 {p}（DHCP 池需读取专业版页面）" : "—";
        ClientCount = snapshot.Runtime.TerminalAll?.Count is { } count ? $"{count} 台在线" : "—";
        foreach (var name in new[]
                 {
                     nameof(LanAddress), nameof(DhcpState), nameof(ClientCount), nameof(Ssid24Input),
                     nameof(Ssid5Input), nameof(Channel24Input), nameof(Channel5Input), nameof(Wifi24Enabled),
                     nameof(Wifi5Enabled), nameof(Wifi24Summary), nameof(Wifi5Summary),
                     nameof(Bandwidth24Input), nameof(Bandwidth5Input),
                 })
            OnPropertyChanged(name);
    }

    private C2000Radio? FindRadio(int index)
        => Radios.FirstOrDefault(x => index == 0
            ? x.InterfaceName?.EndsWith('0') == true
            : x.InterfaceName?.EndsWith('1') == true);

    private async Task ConfirmSaveWifiAsync(int index)
    {
        var radio = FindRadio(index);
        if (radio is null) return;
        var ssid = index == 0 ? Ssid24Input : Ssid5Input;
        var password = index == 0 ? Password24Input : Password5Input;
        var channel = index == 0 ? Channel24Input : Channel5Input;
        var bandwidth = index == 0 ? Bandwidth24Input : Bandwidth5Input;
        var enabled = index == 0 ? Wifi24Enabled : Wifi5Enabled;
        if (MessageBox.Show(
                $"修改 {radio.InterfaceName}（{(index == 0 ? "2.4 GHz" : "5 GHz")}）？\n\n" +
                "会重新加载射频。若本机正通过该频段连接，管理连接会中断。密码留空表示保持原密码。",
                "修改 Wi-Fi", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (MessageBox.Show(
                $"最终确认：SSID「{ssid}」，信道「{channel}」，带宽「{bandwidth} MHz」，启用={enabled}。\n" +
                "保存后将等待最多 40 秒回读，无法回读时只显示“结果待确认”。",
                "最终确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            await RunAsync(async () =>
            {
                Log.Warn(LogCategory.Configuration, "Wi-Fi 配置",
                    $"用户经二次确认修改 {radio.InterfaceName}：SSID={ssid}, channel={channel}, " +
                    $"bandwidth={bandwidth}, enabled={enabled}，密码{(string.IsNullOrEmpty(password) ? "保持不变" : "已提供但不记录")}。");
                var result = await Adapter.SetWifiProfileAsync(index, ssid,
                    string.IsNullOrEmpty(password) ? null : password, channel, bandwidth, enabled,
                    CancellationToken.None);
                OperationText = result.Message + "\n修改前：" + result.Before + "\n修改后：" + result.After;
                OnPropertyChanged(nameof(OperationText));
                if (!result.Confirmed)
                    throw new RouterException(RouterErrorKind.Device, result.Message, "wifi-save");
                Log.Success(LogCategory.Configuration, "Wi-Fi 配置", result.Message + "；" + result.After);
            }, "Wi-Fi 配置已回读确认");
        }
        finally
        {
            if (index == 0) Password24Input = "";
            else Password5Input = "";
        }
    }
}

public sealed class C2000SmsViewModel : C2000ModuleViewModel
{
    public C2000SmsViewModel(MainViewModel host)
        : base(host, "短信", "SIM 短信服务、容量与消息")
    {
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync());
        SendCommand = new AsyncRelayCommand(() => ConfirmSendAsync(), () => Host.IsConnected && CanSend);
    }

    public ICommand RefreshCommand { get; }
    public ICommand SendCommand { get; }
    public ObservableCollection<C2000SmsMessage> Messages { get; } = new();
    public string ServiceText { get; private set; } = "—";
    public string CapacityText { get; private set; } = "—";
    public string UnreadText { get; private set; } = "—";
    public bool CanSend { get; private set; }
    public bool HasMessages => Messages.Count > 0;
    public string MessageCountText { get; private set; } = "0 条消息";
    private string _recipient = "";
    public string Recipient { get => _recipient; set => Set(ref _recipient, value); }
    private string _smsBody = "";
    public string SmsBody { get => _smsBody; set => Set(ref _smsBody, value); }
    public string OperationText { get; private set; } = "尚未发送短信。";
    public string SendNote =>
        "专业版存在 /sms/send 接口；只有设备返回服务启用且 ready=1 时发送按钮才可用。" +
        "正文和号码不会写入普通运行日志。";

    protected override async Task LoadAsync()
    {
        var sms = await Adapter.GetSmsSnapshotAsync("wan0", CancellationToken.None);
        ServiceText = sms.StatusText;
        CanSend = sms.ServiceEnabled && sms.Ready;
        CapacityText = sms.Capacity == 0 ? "设备未报告容量" : $"{sms.Used} / {sms.Capacity}";
        UnreadText = sms.Unread.ToString(CultureInfo.InvariantCulture);
        Messages.Clear();
        foreach (var item in sms.Messages.OrderByDescending(x => x.Date)) Messages.Add(item);
        MessageCountText = $"{Messages.Count} 条消息";
        foreach (var name in new[]
                 {
                     nameof(ServiceText), nameof(CapacityText), nameof(UnreadText), nameof(CanSend),
                     nameof(HasMessages), nameof(MessageCountText),
                 })
            OnPropertyChanged(name);
        (SendCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task ConfirmSendAsync()
    {
        if (!CanSend) return;
        if (string.IsNullOrWhiteSpace(Recipient) || string.IsNullOrWhiteSpace(SmsBody))
        {
            MessageBox.Show("请填写收件号码和短信正文。", "短信", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var preview = SmsBody.Length > 40 ? SmsBody[..40] + "…" : SmsBody;
        if (MessageBox.Show(
                $"确认发送短信？\n\n收件号码：{Recipient}\n正文预览：{preview}\n\n短信可能产生运营商费用。",
                "确认发送短信", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await RunAsync(async () =>
        {
            var result = await Adapter.SendSmsAsync("wan0", Recipient, SmsBody, CancellationToken.None);
            OperationText = result.Message;
            SmsBody = "";
            OnPropertyChanged(nameof(OperationText));
            if (!result.Confirmed)
                throw new RouterException(RouterErrorKind.Device, result.Message, "sms-send");
            Log.Success(LogCategory.Communication, "短信", "短信发送接口返回成功；号码和正文未写入日志。");
        }, "短信发送成功");
    }
}

public sealed class C2000MaintenanceViewModel : C2000ModuleViewModel
{
    public C2000MaintenanceViewModel(MainViewModel host)
        : base(host, "设备维护", "固件、硬件、运行状态、重启与网络质量")
    {
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync());
        RebootCommand = new AsyncRelayCommand(() => ConfirmRebootAsync(), () => Host.IsConnected);
        DiagnoseCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
                var result = await NetworkQualityMonitor.ProbeAsync(Host.Settings.Host);
                DiagnosisText = result.Summary;
                OnPropertyChanged(nameof(DiagnosisText));
        }, reloadAfter: false));
    }

    public ICommand RefreshCommand { get; }
    public ICommand DiagnoseCommand { get; }
    public ICommand RebootCommand { get; }
    public string Model { get; private set; } = "—";
    public string Firmware { get; private set; } = "—";
    public string Hardware { get; private set; } = "—";
    public string Board { get; private set; } = "—";
    public string MacAddress { get; private set; } = "—";
    public string CpuText { get; private set; } = "—";
    public string MemoryText { get; private set; } = "—";
    public string TemperatureText { get; private set; } = "—";
    public string UptimeText { get; private set; } = "—";
    public string DiagnosisText { get; private set; } = "尚未检测。";
    public string OperationText { get; private set; } = "尚未执行维护写操作。";
    public string WriteNote =>
        "已确认专业版包含重启、升级、配置备份和诊断入口；重启与固件升级属于高风险写入，" +
        "重启使用两次确认并在执行后等待设备恢复；固件升级仍不开放。";

    protected override async Task LoadAsync()
    {
        var snapshot = await Adapter.GetSnapshotAsync(CancellationToken.None);
        Model = snapshot.Identity.Model;
        Firmware = snapshot.Identity.FirmwareVersion;
        Hardware = snapshot.Identity.Hardware;
        Board = snapshot.Identity.Board;
        MacAddress = snapshot.Identity.MacAddress;
        CpuText = Percent(snapshot.Runtime.Global?.CpuPercent);
        MemoryText = Percent(snapshot.Runtime.Global?.MemPercent);
        TemperatureText = string.IsNullOrWhiteSpace(snapshot.Cell.Status?.ModelTemp)
            ? "—"
            : snapshot.Cell.Status.ModelTemp + " ℃";
        UptimeText = J.Duration(snapshot.Runtime.Global?.Uptime);
        foreach (var name in new[]
                 {
                     nameof(Model), nameof(Firmware), nameof(Hardware), nameof(Board), nameof(MacAddress),
                     nameof(CpuText), nameof(MemoryText), nameof(TemperatureText), nameof(UptimeText),
                 })
            OnPropertyChanged(name);
    }

    private async Task ConfirmRebootAsync()
    {
        if (MessageBox.Show(
                "立即重启 C2000 Pro？\n\n所有有线和无线终端会断开，约需 30–180 秒恢复。",
                "重启设备", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (MessageBox.Show(
                "最终确认：设备将断电重启，当前管理连接会中断。是否继续？",
                "最终确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await RunAsync(async () =>
        {
            Log.Warn(LogCategory.Configuration, "设备重启", "用户经二次确认请求重启 C2000 Pro。");
            var result = await Adapter.RebootAsync(CancellationToken.None);
            OperationText = result.Message;
            OnPropertyChanged(nameof(OperationText));
            if (!result.Confirmed)
                throw new RouterException(RouterErrorKind.Device, result.Message, "reboot");
            Log.Success(LogCategory.Configuration, "设备重启", result.Message);
        }, "设备已重启并恢复");
    }

    private static string Percent(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value + " %";
}
