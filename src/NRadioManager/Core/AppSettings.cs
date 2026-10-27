using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NRadioManager.Core;

public enum AppTheme { System, Light, Dark }
public enum TrafficUnit { Auto, MB, GB }

/// <summary>
/// 应用配置。保存在 %APPDATA%\NRadioManager\settings.json。
/// 带 Version 字段用于后续格式迁移；解析失败一律回退默认值，不影响启动。
/// </summary>
public sealed class AppSettings
{
    /// <summary>配置格式版本，用于迁移。</summary>
    public int Version { get; set; } = 4;

    // ---- 连接 ----
    public string Host { get; set; } = "http://192.168.66.1";
    public string Username { get; set; } = "root";
    public bool RememberPassword { get; set; }
    /// <summary>DPAPI 加密后的密码（仅当用户选择记住时才写入）。</summary>
    public string? PasswordCipher { get; set; }
    public int TimeoutSeconds { get; set; } = 10;

    // ---- 采样与性能 ----
    public int PollIntervalSeconds { get; set; } = 5;
    public int SpeedSampleSeconds { get; set; } = 2;
    public int DeviceRefreshSeconds { get; set; } = 15;
    public bool PauseWhenMinimized { get; set; } = true;
    public bool LowPerformanceMode { get; set; }
    public bool AnimationsEnabled { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 90;
    /// <summary>电池历史采样间隔（秒）。电量变化慢，默认 60 秒，避免把 5 秒轮询全部落盘。</summary>
    public int BatterySampleSeconds { get; set; } = 60;
    /// <summary>电池历史保留天数。</summary>
    public int BatteryHistoryRetentionDays { get; set; } = 30;

    // ---- 外观 ----
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public string AccentColor { get; set; } = "#3B82F6";
    public double TileCornerRadius { get; set; } = 14;
    public double TileSpacing { get; set; } = 10;
    public double TilePadding { get; set; } = 12;
    public double UiScale { get; set; } = 1.0;

    // ---- 数据展示 ----
    public TrafficUnit Unit { get; set; } = TrafficUnit.Auto;
    public string DefaultStatsPeriod { get; set; } = "day";
    public int ChartWindowMinutes { get; set; } = 10;
    public bool ShowTileTitles { get; set; } = true;
    public bool CompactTiles { get; set; }

    // ---- 流量套餐与用户基线 ----
    /// <summary>用户输入的月套餐总量（GB）；0 表示未设置。</summary>
    public double MonthlyDataPlanGb { get; set; }
    /// <summary>用户手动覆盖的本月已用流量（GB）；null 表示使用设备上报值。</summary>
    public double? MonthlyDataManualUsedGb { get; set; }
    /// <summary>整机下行计数器的本机重置基线。</summary>
    public long DeviceTrafficBaselineRxBytes { get; set; }
    /// <summary>整机上行计数器的本机重置基线。</summary>
    public long DeviceTrafficBaselineTxBytes { get; set; }
    /// <summary>按 MAC 保存的单终端计数器本机重置基线。</summary>
    public Dictionary<string, long> TerminalTrafficBaselinesBytes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // ---- 通知 ----
    public bool NotifyDisconnect { get; set; } = true;
    public bool NotifyReconnect { get; set; } = true;
    public bool NotifyDeviceEvents { get; set; } = true;
    public bool NotifyWeakSignal { get; set; } = true;
    /// <summary>弱信号阈值，使用设备原始 signal_quality 档位（0–5），不做任何换算。</summary>
    public int WeakSignalThresholdLevel { get; set; } = 1;
    public bool NotifyTrafficThreshold { get; set; }
    public double TrafficThresholdGb { get; set; } = 10;
    public TrafficThresholdPeriod TrafficThresholdScope { get; set; } = TrafficThresholdPeriod.Day;
    /// <summary>同一统计周期/阈值配置的已提醒键，用于跨软件重启去重。</summary>
    public string? TrafficThresholdNotifiedKey { get; set; }
    public bool NotifyRouting { get; set; } = true; // 应用内提示条
    /// <summary>网络质量自动检测默认关闭；开启后固定每 5 分钟一次，不参与高频轮询。</summary>
    public bool NetworkQualityAutoCheck { get; set; }

    // ---- 启动与窗口 ----
    public bool AutoConnect { get; set; } = true;
    public bool RememberWindowPlacement { get; set; } = true;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool Maximized { get; set; }
    public bool RememberLastPage { get; set; }
    public string LastPage { get; set; } = "概览";
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; }

    // ---- 磁贴布局 ----
    public List<TileState> Tiles { get; set; } = new();

    private static string Dir
    {
        get
        {
            // 测试/便携运行可用环境变量隔离数据目录；正常启动仍使用 %APPDATA%\NRadioManager。
            var overridden = Environment.GetEnvironmentVariable("NRADIO_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                try { return Path.GetFullPath(overridden); }
                catch { /* 非法覆盖值回退到标准目录 */ }
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NRadioManager");
        }
    }

    public static string FilePath => Path.Combine(Dir, "settings.json");
    public static string DataDir => Dir;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts);
                if (s is not null) return Normalize(s);
            }
        }
        catch (Exception ex)
        {
            return new AppSettings
            {
                LastLoadError = $"{ex.GetType().Name}: {ex.Message}",
            };
        }
        return new AppSettings();
    }

    public string? LastLoadError { get; private set; }

    /// <summary>范围收敛：避免用户把轮询设置成不受控的高频请求。</summary>
    public static AppSettings Normalize(AppSettings s)
    {
        if (s.Version < 3)
        {
            // 仅迁移仍处于旧版出厂默认值的配置；用户已改过的地址/用户名原样保留。
            if (string.Equals(s.Host, "http://192.168.1.1", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Username, "admin", StringComparison.OrdinalIgnoreCase))
            {
                s.Host = "http://192.168.66.1";
                s.Username = "root";
            }
            s.Version = 3;
        }
        if (s.Version < 4)
            s.Version = 4;
        s.PollIntervalSeconds = Math.Clamp(s.PollIntervalSeconds, 2, 60);
        s.SpeedSampleSeconds = Math.Clamp(s.SpeedSampleSeconds, 2, 10);
        s.DeviceRefreshSeconds = Math.Clamp(s.DeviceRefreshSeconds, 15, 120);
        s.TimeoutSeconds = Math.Clamp(s.TimeoutSeconds, 3, 30);
        s.HistoryRetentionDays = Math.Clamp(s.HistoryRetentionDays, 7, 365);
        s.BatterySampleSeconds = Math.Clamp(s.BatterySampleSeconds, 15, 3600);
        s.BatteryHistoryRetentionDays = Math.Clamp(s.BatteryHistoryRetentionDays, 7, 365);
        s.TileCornerRadius = Math.Clamp(s.TileCornerRadius, 0, 28);
        s.TileSpacing = Math.Clamp(s.TileSpacing, 4, 24);
        s.TilePadding = Math.Clamp(s.TilePadding, 8, 28);
        s.UiScale = Math.Clamp(s.UiScale, 0.85, 1.3);
        s.ChartWindowMinutes = Math.Clamp(s.ChartWindowMinutes, 2, 60);
        s.TrafficThresholdGb = Math.Clamp(s.TrafficThresholdGb, 0.1, 1000);
        s.WeakSignalThresholdLevel = Math.Clamp(s.WeakSignalThresholdLevel, 0, 4);
        s.MonthlyDataPlanGb = Math.Clamp(s.MonthlyDataPlanGb, 0, 1_000_000);
        if (s.MonthlyDataManualUsedGb is { } used)
            s.MonthlyDataManualUsedGb = Math.Clamp(used, 0, 1_000_000);
        s.DeviceTrafficBaselineRxBytes = Math.Max(0, s.DeviceTrafficBaselineRxBytes);
        s.DeviceTrafficBaselineTxBytes = Math.Max(0, s.DeviceTrafficBaselineTxBytes);
        s.TerminalTrafficBaselinesBytes = new Dictionary<string, long>(
            s.TerminalTrafficBaselinesBytes ?? new Dictionary<string, long>(),
            StringComparer.OrdinalIgnoreCase);
        return s;
    }

    /// <summary>最近一次保存失败的原因（null 表示成功）。用于把静默失败暴露到界面。</summary>
    public string? LastSaveError { get; private set; }

    /// <summary>记录一次保存失败（供调用方在 catch 中使用）。</summary>
    public void ReportSaveError(Exception ex) => LastSaveError = $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>保存配置。返回 false 时 LastSaveError 说明原因——不允许静默失败。</summary>
    public bool Save()
    {
        try
        {
            SanitizeForSave();
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
            LastSaveError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastSaveError = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 防止界面滑块或窗口计算产生的 NaN/Infinity 让整个 settings.json 无法序列化。
    /// 非有限值回落到默认值，其他配置和磁贴顺序继续正常保存。
    /// </summary>
    private void SanitizeForSave()
    {
        TileCornerRadius = Finite(TileCornerRadius, 14);
        TileSpacing = Finite(TileSpacing, 10);
        TilePadding = Finite(TilePadding, 12);
        UiScale = Finite(UiScale, 1);
        MonthlyDataPlanGb = Finite(MonthlyDataPlanGb, 0);
        if (MonthlyDataManualUsedGb is { } manual)
            MonthlyDataManualUsedGb = double.IsFinite(manual) ? manual : null;
        TrafficThresholdGb = Finite(TrafficThresholdGb, 10);
        WindowLeft = FiniteOrNull(WindowLeft);
        WindowTop = FiniteOrNull(WindowTop);
        WindowWidth = FiniteOrNull(WindowWidth);
        WindowHeight = FiniteOrNull(WindowHeight);
        Normalize(this);
    }

    private static double Finite(double value, double fallback)
        => double.IsFinite(value) ? value : fallback;

    private static double? FiniteOrNull(double? value)
        => value is { } number && double.IsFinite(number) ? number : null;

    public void SetPassword(string password)
        => PasswordCipher = RememberPassword ? DpapiHelper.Protect(password) : null;

    public string GetPassword()
        => string.IsNullOrEmpty(PasswordCipher) ? string.Empty : DpapiHelper.Unprotect(PasswordCipher) ?? string.Empty;

    public AppSettings Clone()
        => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts) ?? new AppSettings();

    /// <summary>把另一份配置的内容复制到当前实例（导入设置时使用，保持实例引用不变）。</summary>
    public void CopyFrom(AppSettings other)
    {
        Version = other.Version;
        Host = other.Host;
        Username = other.Username;
        RememberPassword = other.RememberPassword;
        PasswordCipher = other.PasswordCipher;
        TimeoutSeconds = other.TimeoutSeconds;
        PollIntervalSeconds = other.PollIntervalSeconds;
        SpeedSampleSeconds = other.SpeedSampleSeconds;
        DeviceRefreshSeconds = other.DeviceRefreshSeconds;
        PauseWhenMinimized = other.PauseWhenMinimized;
        LowPerformanceMode = other.LowPerformanceMode;
        AnimationsEnabled = other.AnimationsEnabled;
        HistoryRetentionDays = other.HistoryRetentionDays;
        BatterySampleSeconds = other.BatterySampleSeconds;
        BatteryHistoryRetentionDays = other.BatteryHistoryRetentionDays;
        Theme = other.Theme;
        AccentColor = other.AccentColor;
        TileCornerRadius = other.TileCornerRadius;
        TileSpacing = other.TileSpacing;
        TilePadding = other.TilePadding;
        UiScale = other.UiScale;
        Unit = other.Unit;
        DefaultStatsPeriod = other.DefaultStatsPeriod;
        ChartWindowMinutes = other.ChartWindowMinutes;
        ShowTileTitles = other.ShowTileTitles;
        CompactTiles = other.CompactTiles;
        MonthlyDataPlanGb = other.MonthlyDataPlanGb;
        MonthlyDataManualUsedGb = other.MonthlyDataManualUsedGb;
        DeviceTrafficBaselineRxBytes = other.DeviceTrafficBaselineRxBytes;
        DeviceTrafficBaselineTxBytes = other.DeviceTrafficBaselineTxBytes;
        TerminalTrafficBaselinesBytes = new Dictionary<string, long>(
            other.TerminalTrafficBaselinesBytes, StringComparer.OrdinalIgnoreCase);
        NotifyDisconnect = other.NotifyDisconnect;
        NotifyReconnect = other.NotifyReconnect;
        NotifyDeviceEvents = other.NotifyDeviceEvents;
        NotifyWeakSignal = other.NotifyWeakSignal;
        WeakSignalThresholdLevel = other.WeakSignalThresholdLevel;
        NotifyTrafficThreshold = other.NotifyTrafficThreshold;
        TrafficThresholdGb = other.TrafficThresholdGb;
        TrafficThresholdScope = other.TrafficThresholdScope;
        TrafficThresholdNotifiedKey = other.TrafficThresholdNotifiedKey;
        NotifyRouting = other.NotifyRouting;
        NetworkQualityAutoCheck = other.NetworkQualityAutoCheck;
        AutoConnect = other.AutoConnect;
        RememberWindowPlacement = other.RememberWindowPlacement;
        WindowLeft = other.WindowLeft;
        WindowTop = other.WindowTop;
        WindowWidth = other.WindowWidth;
        WindowHeight = other.WindowHeight;
        Maximized = other.Maximized;
        RememberLastPage = other.RememberLastPage;
        LastPage = other.LastPage;
        StartWithWindows = other.StartWithWindows;
        MinimizeToTray = other.MinimizeToTray;
        Tiles = other.Tiles;
    }
}

/// <summary>单个磁贴的布局与外观状态。</summary>
public sealed class TileState
{
    public string Id { get; set; } = "";
    public int Order { get; set; }
    public int ColumnSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;
    public bool Visible { get; set; } = true;
    public bool ShowTitle { get; set; } = true;
}
