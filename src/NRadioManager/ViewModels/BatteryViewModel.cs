using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using NRadioManager.Core;
using NRadioManager.Infrastructure;

namespace NRadioManager.ViewModels;

/// <summary>
/// 电池监测：实时状态 + 本地历史趋势 + 可信的区间分析。
/// 只使用 status_info 已确认的字段；电压、电流、温度、剩余时间、Wh 等设备不提供，界面明确标注。
/// </summary>
public sealed class BatteryViewModel : ModuleViewModel
{
    private readonly BatteryHistory _history;
    private IReadOnlyList<BatterySample> _samples = Array.Empty<BatterySample>();
    private BatteryAnalysis _analysis = EmptyAnalysis();

    private DateTime _viewStartUtc = DateTime.MinValue;
    private DateTime _viewEndUtc = DateTime.MinValue;
    private int _rangeSeconds = 3600;
    private OptionItem? _selectedRange;

    private int _percentValue = -1;
    private int? _chargeRaw;
    private bool? _charging;
    private int? _batteryConnectRaw;
    private BatteryChargeState _chargeState = BatteryChargeState.Unknown;
    private DateTime _lastStatusUtc = DateTime.MinValue;
    private string _dataWarning = "";
    private bool _samplingSessionActive;
    private bool _firstSampleLogged;
    private bool _lastSampleAvailable = true;
    private BatteryChargeState _lastLoggedState = BatteryChargeState.Unknown;
    private string? _lastLoggedSaveError;
    private DateTime _lastTimeWarningLogUtc = DateTime.MinValue;

    public BatteryViewModel(MainViewModel host) : base(host, "电池监测", "设备电量、充电状态与本地历史趋势")
    {
        _history = BatteryHistory.Load(host.Settings.BatteryHistoryRetentionDays);
        RangeOptions = new ObservableCollection<OptionItem>
        {
            new("3600", "最近 1 小时"),
            new("21600", "最近 6 小时"),
            new("86400", "最近 24 小时"),
            new("604800", "最近 7 天"),
        };
        _selectedRange = RangeOptions[0];

        ClearHistoryCommand = new RelayCommand(ClearHistory);

        var now = DateTime.UtcNow;
        _viewStartUtc = now.AddSeconds(-_rangeSeconds);
        _viewEndUtc = now;
        _samples = _history.AllSnapshot();
        UpdateHistorySummary();
        RecomputeAnalysis();
        if (HasHistoryWarning)
        {
            Log.Warn(LogCategory.Persistence, "电池历史",
                "电池历史加载异常：" + HistoryWarningText);
        }
        CheckSaveHealth();
    }

    // ---------------- 时间范围 ----------------

    public ObservableCollection<OptionItem> RangeOptions { get; }

    public OptionItem? SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (!Set(ref _selectedRange, value) || value is null) return;
            if (int.TryParse(value.Value, out var seconds)) RangeSeconds = seconds;
            OnPropertyChanged(nameof(RangeLabel));
        }
    }

    public int RangeSeconds
    {
        get => _rangeSeconds;
        set
        {
            if (!Set(ref _rangeSeconds, Math.Clamp(value, 300, 604800))) return;
            var now = DateTime.UtcNow;
            SetView(now.AddSeconds(-_rangeSeconds), now);
            OnPropertyChanged(nameof(RangeLabel));
        }
    }

    public string RangeLabel => _selectedRange?.Text ?? "最近 1 小时";

    public int GapThresholdSeconds => Math.Max(180, Host.Settings.BatterySampleSeconds * 3);

    public DateTime ViewStartUtc
    {
        get => _viewStartUtc;
        set
        {
            if (!Set(ref _viewStartUtc, value)) return;
            RecomputeAnalysis();
        }
    }

    public DateTime ViewEndUtc
    {
        get => _viewEndUtc;
        set
        {
            if (!Set(ref _viewEndUtc, value)) return;
            RecomputeAnalysis();
        }
    }

    private void SetView(DateTime start, DateTime end)
    {
        var changed = false;
        if (_viewStartUtc != start)
        {
            _viewStartUtc = start;
            OnPropertyChanged(nameof(ViewStartUtc));
            changed = true;
        }
        if (_viewEndUtc != end)
        {
            _viewEndUtc = end;
            OnPropertyChanged(nameof(ViewEndUtc));
            changed = true;
        }
        if (changed) RecomputeAnalysis();
    }

    // ---------------- 实时状态 ----------------

    public int PercentValue
    {
        get => _percentValue;
        private set
        {
            if (!Set(ref _percentValue, value)) return;
            OnPropertyChanged(nameof(HasPercent));
            OnPropertyChanged(nameof(PercentText));
            OnPropertyChanged(nameof(SamplingStatusText));
            OnPropertyChanged(nameof(HasSamplingProblem));
        }
    }

    public bool HasPercent => _percentValue >= 0;
    public string PercentText => HasPercent ? $"{_percentValue}%" : "—";
    public bool IsCharging => _chargeState == BatteryChargeState.Charging;
    public bool IsFull => _chargeState == BatteryChargeState.Full;
    public bool BatteryAvailable => HasPercent;
    public string SamplingStatusText => !Host.IsConnected
        ? "设备已断开，无法采样"
        : !HasPercent
            ? "设备未上报有效电量字段"
            : "采样正常";
    public bool HasSamplingProblem => !Host.IsConnected || !HasPercent;
    public string ChargeStateText => BatteryState.Text(_chargeState);
    public string ChargeDetailText => _chargeRaw is null && _charging is null
        ? "设备未上报充电状态"
        : BatteryState.Describe(_chargeRaw, _charging);
    public string BatteryConnectText => _batteryConnectRaw is null
        ? "battery_connect：—"
        : $"battery_connect={_batteryConnectRaw}（原厂页面未使用，语义未确认，不参与分析）";
    public string LiveUpdatedText => _lastStatusUtc == DateTime.MinValue
        ? "等待设备状态…"
        : $"设备状态更新于 {_lastStatusUtc.ToLocalTime():HH:mm:ss}";
    public string ConnectionText { get; private set; } = "尚未读取";
    public string LastSampleText { get; private set; } = "最后采样：—";
    public string HistoryCoverageText { get; private set; } = "";
    public string DataWarning => _dataWarning;
    public bool HasDataWarning => _dataWarning.Length > 0;
    public string HistoryWarningText => _history.LoadWarning ?? "";
    public bool HasHistoryWarning => !string.IsNullOrEmpty(_history.LoadWarning);
    public string SaveWarningText => _history.LastSaveError is null
        ? ""
        : $"电池历史保存失败：{_history.LastSaveError}";
    public bool HasSaveWarning => _history.LastSaveError is not null;

    public string DataSourceText =>
        "数据来源：设备只读接口 status_info 的 battery_percent（0–100）、" +
        "battery_charge（0 未充电 / 1 充电中 / 2、3 已充满）、battery_charging。" +
        "字段含义已按原厂 js/base.js 与 dashboard.html 核对；两个充电字段互相冲突时显示“未知”，不做猜测。" +
        "battery_connect 虽可读，但原厂页面未使用，语义未确认。";

    public string UnsupportedText =>
        "设备未提供：电池电压、电流、温度、剩余时间、累计耗电（Wh）、电池健康度、充放电功率。" +
        "原厂接口清单与页面均无这些字段，本页不做估算或推算，以免把猜测当成真实数据。";

    public string RateDisclaimer =>
        "平均下降速度只根据连续、有效的采样区间计算；缺失、断连、设备重启区间不参与。" +
        "不根据两个百分比点推算剩余续航。";

    public void ApplyLive(StatusInfo status, DateTime utcNow)
    {
        var percent = J.Int(status.BatteryPercent);
        PercentValue = percent is >= 0 and <= 100 ? percent.Value : -1;
        _chargeRaw = J.Int(status.BatteryCharge);
        _charging = J.Bool(status.BatteryCharging);
        _chargeState = BatteryState.Of(_chargeRaw, _charging);
        _batteryConnectRaw = J.Int(status.BatteryConnect);
        _lastStatusUtc = utcNow;
        ConnectionText = Host.IsConnected ? "已连接" : "设备已断开";

        foreach (var n in new[]
                 {
                     nameof(ChargeStateText), nameof(ChargeDetailText), nameof(IsCharging), nameof(IsFull),
                     nameof(BatteryAvailable), nameof(BatteryConnectText), nameof(LiveUpdatedText),
                     nameof(ConnectionText), nameof(SamplingStatusText), nameof(HasSamplingProblem),
                 })
            OnPropertyChanged(n);
        UpdateHistorySummary();
    }

    /// <summary>由主轮询调用：更新实时状态，并按配置间隔写入历史。不额外发 HTTP 请求。</summary>
    public void Observe(StatusInfo status, DateTime utcNow)
    {
        if (!_samplingSessionActive)
        {
            _samplingSessionActive = true;
            Log.Info(LogCategory.Sampling, "电池采样",
                $"电池采样开始：按配置间隔 {Math.Max(15, Host.Settings.BatterySampleSeconds)} 秒记录，" +
                $"历史保留 {Host.Settings.BatteryHistoryRetentionDays} 天。");
        }

        var previousAvailable = _lastSampleAvailable;
        var previousState = _lastLoggedState;
        ApplyLive(status, utcNow);
        var result = _history.MaybeSample(status, utcNow,
            Host.Settings.BatterySampleSeconds, Host.Settings.BatteryHistoryRetentionDays, out _);

        if (result is BatterySampleResult.TimeRollback or BatterySampleResult.TimeInvalid)
        {
            SetWarning("检测到系统时间回退或不前进，本次电池采样已忽略（避免把历史写乱）。");
            if (DateTime.UtcNow - _lastTimeWarningLogUtc >= TimeSpan.FromMinutes(5))
            {
                _lastTimeWarningLogUtc = DateTime.UtcNow;
                Log.Warn(LogCategory.Sampling, "电池采样",
                    result == BatterySampleResult.TimeInvalid
                        ? "电池采样时间明显超前于本机当前时间，本次采样已拒绝（5 分钟内不重复记录）。"
                        : "电池采样时间未前进，本次采样已拒绝（5 分钟内不重复记录）。");
            }
        }
        else if (result == BatterySampleResult.Recorded)
        {
            SetWarning("");
            var sample = _history.Latest;
            var available = sample?.Available == true;
            if (!available && previousAvailable)
            {
                Log.Warn(LogCategory.Sampling, "电池采样",
                    "设备未上报有效 battery_percent；已记录“数据缺失”标记，不补默认电量。");
            }
            else if (available && !previousAvailable)
            {
                Log.Success(LogCategory.Sampling, "电池采样",
                    $"设备恢复上报有效电量：{sample!.Percent}% · {BatteryState.Text(sample.State)}。");
            }

            if (sample is not null && available)
            {
                if (!_firstSampleLogged)
                {
                    _firstSampleLogged = true;
                    Log.Info(LogCategory.Sampling, "电池采样",
                        $"首个有效电池样本：{sample.Percent}% · {BatteryState.Text(sample.State)}；" +
                        $"battery_charge={sample.ChargeRaw?.ToString() ?? "—"}，" +
                        $"battery_charging={sample.Charging?.ToString() ?? "—"}，" +
                        $"battery_connect={sample.BatteryConnectRaw?.ToString() ?? "—"}。");
                }
                else if (sample.State != previousState)
                {
                    Log.Info(LogCategory.Sampling, "电池采样",
                        $"充电状态变化：{BatteryState.Text(previousState)} → {BatteryState.Text(sample.State)}；" +
                        $"电量 {sample.Percent}%，原始 battery_charge=" +
                        $"{sample.ChargeRaw?.ToString() ?? "—"}、battery_charging=" +
                        $"{sample.Charging?.ToString() ?? "—"}。");
                }
            }

            _lastSampleAvailable = available;
            _lastLoggedState = sample?.State ?? BatteryChargeState.Unknown;

            // 视图原本跟随最新时，把窗口推进到本次采样；用户已拖到历史区间时不打断。
            if (_viewEndUtc == DateTime.MinValue || _viewEndUtc >= utcNow.AddMinutes(-2))
            {
                var span = _viewEndUtc - _viewStartUtc;
                if (span < TimeSpan.FromMinutes(5) || span > TimeSpan.FromDays(31))
                    span = TimeSpan.FromSeconds(_rangeSeconds);
                SetView(utcNow - span, utcNow);
            }
            _samples = _history.AllSnapshot();
            OnPropertyChanged(nameof(Samples));
            UpdateHistorySummary();
            RecomputeAnalysis();
        }

        _history.SaveIfDue(utcNow, Host.Settings.BatteryHistoryRetentionDays);
        RaiseStorageWarnings();
    }

    public void MarkDisconnected(DateTime utcNow)
    {
        if (_samplingSessionActive)
        {
            _samplingSessionActive = false;
            Log.Warn(LogCategory.Sampling, "电池采样",
                "电池采样暂停：设备已断开；断连区间会作为缺口显示，不计入耗电分析。");
        }
        _history.MarkDisconnected(utcNow);
        ConnectionText = "设备已断开";
        OnPropertyChanged(nameof(SamplingStatusText));
        OnPropertyChanged(nameof(HasSamplingProblem));
        SetWarning("设备已断开。断连区间会显示在图表中，且不会计入耗电分析。");
        _samples = _history.AllSnapshot();
        OnPropertyChanged(nameof(Samples));
        UpdateHistorySummary();
        RecomputeAnalysis();
        _history.SaveNow(utcNow, Host.Settings.BatteryHistoryRetentionDays);
        RaiseStorageWarnings();
        OnPropertyChanged(nameof(ConnectionText));
    }

    public void SaveNow()
    {
        _history.SaveNow(DateTime.UtcNow, Host.Settings.BatteryHistoryRetentionDays);
        RaiseStorageWarnings();
    }

    public void StopSampling()
    {
        if (!_samplingSessionActive) return;
        _samplingSessionActive = false;
        Log.Info(LogCategory.Sampling, "电池采样", "电池采样随软件退出停止；已记录的历史保留在本地。");
    }

    public void ApplySettings()
    {
        OnPropertyChanged(nameof(GapThresholdSeconds));
        _history.Prune(DateTime.UtcNow, Host.Settings.BatteryHistoryRetentionDays);
        _samples = _history.AllSnapshot();
        OnPropertyChanged(nameof(Samples));
        UpdateHistorySummary();
        RecomputeAnalysis();
        _history.SaveNow(DateTime.UtcNow, Host.Settings.BatteryHistoryRetentionDays);
        RaiseStorageWarnings();
    }

    public IReadOnlyList<BatterySample> Samples => _samples;

    public ICommand ClearHistoryCommand { get; }

    private void ClearHistory()
    {
        var r = MessageBox.Show(
            "将删除本软件记录的电池采样历史（电量趋势与分析结果都会清空）。\n\n" +
            "这不会改变设备电量，也不会重启或重置路由器。\n\n确定继续？",
            "清理本地电池历史", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;

        _history.Reset(Host.Settings.BatteryHistoryRetentionDays);
        _samples = Array.Empty<BatterySample>();
        OnPropertyChanged(nameof(Samples));
        _dataWarning = "";
        OnPropertyChanged(nameof(DataWarning));
        OnPropertyChanged(nameof(HasDataWarning));
        UpdateHistorySummary();
        RecomputeAnalysis();
        RaiseStorageWarnings();
        Log.Info(LogCategory.Data, "电池历史",
            "用户清空了本地电池采样历史；设备端数据未修改；已显示结果通知。");
        Host.Notices.Raise(NoticeLevel.Info, Title,
            "本地电池历史已清空；设备端电量与路由器状态未做任何改动。",
            logCategory: LogCategory.Data, logSource: "电池历史", logEvent: false);
    }

    protected override async Task LoadAsync()
    {
        var status = Host.Dashboard.Status;
        if (status is null && Host.Api is not null)
            status = await Host.Api.GetStatusAsync(CancellationToken.None);

        if (status is not null)
        {
            ApplyLive(status, DateTime.UtcNow);
            if (HasPercent && Host.IsConnected) SetWarning("");
        }
        else ConnectionText = Host.IsConnected ? "已连接，等待首次状态" : "设备已断开";
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(SamplingStatusText));
        OnPropertyChanged(nameof(HasSamplingProblem));

        _samples = _history.AllSnapshot();
        OnPropertyChanged(nameof(Samples));
        UpdateHistorySummary();
        RecomputeAnalysis();
    }

    // ---------------- 历史与分析 ----------------

    public BatteryAnalysis Analysis => _analysis;

    public string NetChangeText => !_analysis.HasData || _analysis.NetChange is null
        ? "—"
        : $"{_analysis.StartPercent}% → {_analysis.EndPercent}%（{_analysis.NetChange:+0;-0;0}%）";

    public string TrendText => _analysis.Trend switch
    {
        BatteryTrend.Falling => "总体下降",
        BatteryTrend.Rising => "总体上升",
        BatteryTrend.Stable => "基本持平",
        BatteryTrend.Charging => "以充电为主",
        _ => "数据不足",
    };

    public string AverageRateText => _analysis.AverageDropPerHour is { } rate
        ? $"{rate:0.##} %/小时"
        : "—";

    public string CoverageText => _analysis.HasData
        ? $"{_analysis.CoverageRatio * 100:0.#}% 有效覆盖"
        : "—";

    public string MissingText => _analysis.HasData ? FormatDuration(_analysis.MissingDuration) : "—";
    public string MissingSummaryText => $"缺失 {MissingText}";
    public string ChargingText => _analysis.HasData
        ? $"充电 {FormatDuration(_analysis.ChargeDuration)} · 放电 {FormatDuration(_analysis.DischargeDuration)} · 已充满 {FormatDuration(_analysis.FullDuration)}"
        : "—";
    public string GapsText => _analysis.HasData
        ? $"{_analysis.GapCount} 段缺口 · 最长 {FormatDuration(_analysis.LongestGap)} · 断连 {_analysis.DisconnectCount} 次 · 重启 {_analysis.RestartCount} 次"
        : "—";
    public string SampleSummaryText => _analysis.HasData
        ? $"范围内 {_analysis.SampleCount} 条记录，{_analysis.UsableSamples} 条有效"
        : "范围内没有足够的有效采样";

    public string RangeWindowText
    {
        get
        {
            var start = _viewStartUtc == DateTime.MinValue ? DateTime.UtcNow.AddSeconds(-_rangeSeconds) : _viewStartUtc;
            var end = _viewEndUtc == DateTime.MinValue ? DateTime.UtcNow : _viewEndUtc;
            return $"{start.ToLocalTime():MM-dd HH:mm} — {end.ToLocalTime():MM-dd HH:mm}";
        }
    }

    public string AnalysisStatusText
    {
        get
        {
            if (_samples.Count == 0) return "尚无本地电池采样。软件运行并连接设备后会按设置间隔记录；设备断连时不会产生数据。";
            if (_analysis.SampleCount == 0) return "当前时间范围没有采样记录。可切换到更长范围，或等待新的采样。";
            if (_analysis.UsableSamples < 2) return "当前范围只有少于 2 个有效电量采样，无法计算变化与速度。";
            if (_analysis.CoverageRatio < 0.5)
                return "有效覆盖不足 50%：缺口、断连或软件未运行时段较多，变化量与平均速度仅供参考。";
            return "";
        }
    }

    public bool HasAnalysisWarning => AnalysisStatusText.Length > 0;

    private void RecomputeAnalysis()
    {
        var start = _viewStartUtc == DateTime.MinValue ? DateTime.UtcNow.AddSeconds(-_rangeSeconds) : _viewStartUtc;
        var end = _viewEndUtc == DateTime.MinValue ? DateTime.UtcNow : _viewEndUtc;
        _analysis = BatteryAnalyzer.Analyze(_samples, start, end, TimeSpan.FromSeconds(GapThresholdSeconds));

        foreach (var n in new[]
                 {
                     nameof(Analysis), nameof(NetChangeText), nameof(TrendText), nameof(AverageRateText),
                     nameof(CoverageText), nameof(MissingText), nameof(MissingSummaryText),
                     nameof(ChargingText), nameof(GapsText),
                     nameof(SampleSummaryText), nameof(RangeWindowText), nameof(AnalysisStatusText),
                     nameof(HasAnalysisWarning),
                 })
            OnPropertyChanged(n);
    }

    private void UpdateHistorySummary()
    {
        LastSampleText = _history.Latest switch
        {
            null => "最后采样：—",
            { Connected: false } s => $"最后记录：{s.TimeLocal:HH:mm:ss}（设备断连）",
            { Available: false } s => $"最后记录：{s.TimeLocal:HH:mm:ss}（设备未上报电量）",
            var s => $"最后采样：{s.TimeLocal:HH:mm:ss} · {s.Percent}% · {BatteryState.Text(s.State)}",
        };

        HistoryCoverageText = _history.Count == 0
            ? "尚未记录电池采样。"
            : $"本地记录：{_history.FirstTimeUtc!.Value.ToLocalTime():yyyy-MM-dd HH:mm} 起 · {_history.Count} 条 · 最后 {_history.LastTimeUtc!.Value.ToLocalTime():HH:mm:ss}";
        OnPropertyChanged(nameof(LastSampleText));
        OnPropertyChanged(nameof(HistoryCoverageText));
    }

    private void SetWarning(string text)
    {
        _dataWarning = text;
        OnPropertyChanged(nameof(DataWarning));
        OnPropertyChanged(nameof(HasDataWarning));
    }

    private void RaiseStorageWarnings()
    {
        foreach (var n in new[]
                 {
                     nameof(HistoryWarningText), nameof(HasHistoryWarning),
                     nameof(SaveWarningText), nameof(HasSaveWarning),
                 })
            OnPropertyChanged(n);
        CheckSaveHealth();
    }

    private void CheckSaveHealth()
    {
        var error = _history.LastSaveError;
        if (!string.IsNullOrWhiteSpace(error) &&
            !string.Equals(error, _lastLoggedSaveError, StringComparison.Ordinal))
        {
            _lastLoggedSaveError = error;
            Log.Error(LogCategory.Persistence, "电池历史",
                "电池历史保存失败：" + error);
        }
        else if (string.IsNullOrWhiteSpace(error) && _lastLoggedSaveError is not null)
        {
            _lastLoggedSaveError = null;
            Log.Success(LogCategory.Persistence, "电池历史", "电池历史保存已恢复正常。");
        }
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero) return "0 分钟";
        if (value.TotalMinutes < 1) return "不足 1 分钟";
        if (value.TotalHours < 1) return $"{(int)value.TotalMinutes} 分钟";
        if (value.TotalDays < 1) return $"{(int)value.TotalHours} 小时 {value.Minutes} 分";
        return $"{(int)value.TotalDays} 天 {value.Hours} 小时";
    }

    private static BatteryAnalysis EmptyAnalysis()
        => BatteryAnalyzer.Analyze(Array.Empty<BatterySample>(), DateTime.UtcNow.AddHours(-1), DateTime.UtcNow,
            TimeSpan.FromMinutes(5));
}
