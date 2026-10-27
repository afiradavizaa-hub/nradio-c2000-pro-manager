using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using NRadioManager.Core;
using NRadioManager.Infrastructure;
using Microsoft.Win32;

namespace NRadioManager.ViewModels;

/// <summary>设置中心：外观、数据、连接、采样、通知、启动、备份、关于与诊断。</summary>
public sealed class SettingsViewModel : ModuleViewModel
{
    private bool _loading;

    public SettingsViewModel(MainViewModel host) : base(host, "设置", "外观、数据、连接与诊断")
    {
        ReconnectCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            Host.DisconnectForSettings();
            await Host.ConnectForSettingsAsync();
        }, "已按新配置重新连接"), () => !Host.IsConnected);

        ApplyConnectionCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            // 先按输入参数探测一次，成功才写回配置，避免把可用配置改坏
            var probe = new RouterClient(HostText, UserNameText, PasswordText, TimeoutSeconds);
            try
            {
                await probe.LoginAsync();
            }
            finally
            {
                probe.Dispose();
            }
            Host.ApplyConnectionSettings(HostText, UserNameText, PasswordText, RememberPassword);
            Message = "连接配置已验证并保存，将在下次连接时生效。";
            IsError = false;
            OnPropertyChanged(nameof(ConnectionStateText));
        }, "连接配置已验证并保存"), () => !Host.IsConnected);

        ClearHistoryCommand = new RelayCommand(() =>
        {
            var r = MessageBox.Show(
                "将删除本软件记录在电脑上的流量采样历史（当日/近七天/本月统计曲线都会清空）。\n\n" +
                "注意：这**不会**重置路由器内部的累计流量计数器，设备上的累计值保持不变。\n\n确定继续？",
                "清理本地流量历史", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            Host.History.ResetLocal();
            Host.Settings.TrafficThresholdNotifiedKey = null;
            Host.Settings.Save();
            Host.Dashboard.RefreshTrafficViews();
            Message = "本地流量历史已清空。设备端累计计数器未做任何改动。";
            IsError = false;
            OnPropertyChanged(nameof(DataSourceText));
        });

        ResetAppearanceCommand = new RelayCommand(() =>
        {
            var d = new AppSettings();
            ThemeDark = d.Theme == AppTheme.Dark;
            ThemeLight = d.Theme == AppTheme.Light;
            ThemeSystem = d.Theme == AppTheme.System;
            AccentColor = d.AccentColor;
            TileCornerRadius = d.TileCornerRadius;
            TileSpacing = d.TileSpacing;
            TilePadding = d.TilePadding;
            UiScale = d.UiScale;
            AnimationsEnabled = d.AnimationsEnabled;
            ApplyAppearance(showMessage: true);
        });

        ResetTilesCommand = new RelayCommand(() =>
        {
            Host.Settings.Tiles = TileLayout.CreateDefault();
            Host.PersistSettings();
            Host.Dashboard.ReloadTiles();
            Message = "概览磁贴已恢复默认布局。";
            IsError = false;
        });

        ExportSettingsCommand = new RelayCommand(ExportSettings);
        PickAccentCommand = new RelayCommand(p => { if (p is string c) AccentColor = c; });
        ImportSettingsCommand = new RelayCommand(ImportSettings);
        ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics);
        TestNotificationCommand = new RelayCommand(() =>
        {
            Host.Notices.Raise(NoticeLevel.Info, "test-" + DateTime.Now.Ticks, "测试通知",
                "这是一条测试通知，用于确认应用内提醒可以正常显示。", true);
            Message = "测试通知已发送。";
            IsError = false;
        });
        TestNetworkQualityCommand = new AsyncRelayCommand(ProbeNetworkQualityAsync);
        ExportTrafficCsvCommand = new RelayCommand(ExportTrafficCsv);
        ExportTerminalCsvCommand = new AsyncRelayCommand(ExportTerminalCsvAsync);

        ReloadFromSettings();
    }

    // ---------------- 外观 ----------------

    public bool ThemeDark { get => Host.Settings.Theme == AppTheme.Dark; set { if (value) SetTheme(AppTheme.Dark); } }
    public bool ThemeLight { get => Host.Settings.Theme == AppTheme.Light; set { if (value) SetTheme(AppTheme.Light); } }
    public bool ThemeSystem { get => Host.Settings.Theme == AppTheme.System; set { if (value) SetTheme(AppTheme.System); } }

    private void SetTheme(AppTheme t)
    {
        if (_loading) return;
        Host.Settings.Theme = t;
        ApplyAppearance(showMessage: false);
    }

    public ObservableCollection<string> AccentPresets { get; } = new()
    {
        "#3B82F6", "#0EA5E9", "#22C55E", "#F59E0B", "#EF4444", "#8B5CF6", "#EC4899", "#14B8A6",
    };

    private string _accentColor = "#3B82F6";
    public string AccentColor
    {
        get => _accentColor;
        set
        {
            if (!Set(ref _accentColor, value) || _loading) return;
            Host.Settings.AccentColor = value;
            ThemeManager.ApplyAccent(value, ThemeManager.EffectiveDark);
            Host.Settings.Save();
        }
    }

    private double _tileCornerRadius = 14;
    public double TileCornerRadius { get => _tileCornerRadius; set { if (Set(ref _tileCornerRadius, value) && !_loading) ApplyAppearance(false); } }

    private double _tileSpacing = 10;
    public double TileSpacing { get => _tileSpacing; set { if (Set(ref _tileSpacing, value) && !_loading) ApplyAppearance(false); } }

    private double _tilePadding = 12;
    public double TilePadding { get => _tilePadding; set { if (Set(ref _tilePadding, value) && !_loading) ApplyAppearance(false); } }

    private double _uiScale = 1.0;
    public double UiScale { get => _uiScale; set { if (Set(ref _uiScale, value) && !_loading) ApplyAppearance(false); } }

    private bool _animationsEnabled = true;
    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set { if (Set(ref _animationsEnabled, value) && !_loading) ApplyAppearance(false); }
    }

    private void ApplyAppearance(bool showMessage)
    {
        var s = Host.Settings;
        s.AccentColor = AccentColor;
        s.TileCornerRadius = TileCornerRadius;
        s.TileSpacing = TileSpacing;
        s.TilePadding = TilePadding;
        s.UiScale = UiScale;
        s.AnimationsEnabled = AnimationsEnabled;
        AppSettings.Normalize(s);

        ThemeManager.Apply(s);
        Host.RaiseSettingsChanged();
        s.Save();

        if (showMessage)
        {
            Message = "外观设置已恢复默认值。";
            IsError = false;
        }
        OnPropertyChanged(nameof(ThemeDark));
        OnPropertyChanged(nameof(ThemeLight));
        OnPropertyChanged(nameof(ThemeSystem));
    }

    // ---------------- 数据 ----------------

    public ObservableCollection<OptionItem> UnitOptions { get; } = new()
    {
        new("Auto", "自动（按大小切换 B/KB/MB/GB）"),
        new("MB", "固定 MB"),
        new("GB", "固定 GB"),
    };

    private OptionItem? _selectedUnit;
    public OptionItem? SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (!Set(ref _selectedUnit, value) || value is null || _loading) return;
            Host.Settings.Unit = Enum.Parse<TrafficUnit>(value.Value);
            J.Unit = Host.Settings.Unit;
            Host.Settings.Save();
            Host.Dashboard.RefreshTrafficViews();
        }
    }

    public ObservableCollection<OptionItem> SignalThresholdOptions { get; } = new()
    {
        new("0", "0 档（仅完全无信号）"),
        new("1", "1 档（很弱或更低）"),
        new("2", "2 档（较弱或更低）"),
        new("3", "3 档（一般或更低）"),
        new("4", "4 档（良好或更低）"),
    };

    private OptionItem? _selectedSignalThreshold;
    public OptionItem? SelectedSignalThreshold
    {
        get => _selectedSignalThreshold;
        set
        {
            if (!Set(ref _selectedSignalThreshold, value) || value is null || _loading) return;
            if (!int.TryParse(value.Value, out var level)) return;
            Host.Settings.WeakSignalThresholdLevel = Math.Clamp(level, 0, 4);
            Host.Settings.Save();
        }
    }

    public ObservableCollection<OptionItem> TrafficPeriodOptions { get; } = new()
    {
        new("Day", "每日流量"),
        new("Month", "每月流量"),
    };

    private OptionItem? _selectedTrafficPeriod;
    public OptionItem? SelectedTrafficPeriod
    {
        get => _selectedTrafficPeriod;
        set
        {
            if (!Set(ref _selectedTrafficPeriod, value) || value is null || _loading) return;
            Host.Settings.TrafficThresholdScope = value.Value == "Month"
                ? TrafficThresholdPeriod.Month
                : TrafficThresholdPeriod.Day;
            Host.Settings.Save();
        }
    }

    public ObservableCollection<OptionItem> PeriodOptions { get; } = new()
    {
        new("day", "当日流量"),
        new("week", "近七天"),
        new("month", "本月"),
    };

    private OptionItem? _selectedPeriod;
    public OptionItem? SelectedPeriod
    {
        get => _selectedPeriod;
        set
        {
            if (!Set(ref _selectedPeriod, value) || value is null || _loading) return;
            Host.Settings.DefaultStatsPeriod = value.Value;
            Host.Settings.Save();
            Host.Dashboard.ApplyDefaultPeriod(value.Value);
        }
    }

    private int _retentionDays = 90;
    public int HistoryRetentionDays
    {
        get => _retentionDays;
        set
        {
            if (!Set(ref _retentionDays, value) || _loading) return;
            Host.Settings.HistoryRetentionDays = Math.Clamp(value, 7, 365);
            Host.Settings.Save();
        }
    }

    public ObservableCollection<OptionItem> BatterySampleOptions { get; } = new()
    {
        new("15", "15 秒"),
        new("30", "30 秒"),
        new("60", "1 分钟（推荐）"),
        new("300", "5 分钟"),
        new("600", "10 分钟"),
    };

    private OptionItem? _selectedBatterySample;
    public OptionItem? SelectedBatterySample
    {
        get => _selectedBatterySample;
        set
        {
            if (!Set(ref _selectedBatterySample, value) || value is null || _loading) return;
            if (!int.TryParse(value.Value, out var seconds)) return;
            Host.Settings.BatterySampleSeconds = Math.Clamp(seconds, 15, 3600);
            Host.PersistSettings();
            Host.Battery.ApplySettings();
            Host.Log.Info(LogCategory.Configuration, "电池监测",
                $"电池采样间隔已改为 {Host.Settings.BatterySampleSeconds} 秒。");
        }
    }

    public ObservableCollection<OptionItem> BatteryRetentionOptions { get; } = new()
    {
        new("7", "7 天"),
        new("30", "30 天（推荐）"),
        new("90", "90 天"),
        new("180", "180 天"),
        new("365", "365 天"),
    };

    private OptionItem? _selectedBatteryRetention;
    public OptionItem? SelectedBatteryRetention
    {
        get => _selectedBatteryRetention;
        set
        {
            if (!Set(ref _selectedBatteryRetention, value) || value is null || _loading) return;
            if (!int.TryParse(value.Value, out var days)) return;
            Host.Settings.BatteryHistoryRetentionDays = Math.Clamp(days, 7, 365);
            Host.PersistSettings();
            Host.Battery.ApplySettings();
            Host.Log.Info(LogCategory.Configuration, "电池监测",
                $"电池历史保留期已改为 {Host.Settings.BatteryHistoryRetentionDays} 天；已按新期限裁剪本地历史。");
        }
    }

    public ICommand BatteryClearHistoryCommand => Host.Battery.ClearHistoryCommand;

    private int _chartWindow = 10;
    public int ChartWindowMinutes
    {
        get => _chartWindow;
        set
        {
            if (!Set(ref _chartWindow, value) || _loading) return;
            Host.Settings.ChartWindowMinutes = Math.Clamp(value, 2, 60);
            Host.Settings.Save();
        }
    }

    public string DataSourceText =>
        "设备侧仅提供各终端「累计」流量，以及「本月」（rxm/txm）与「近 3 日」（rx3/tx3）两个现成计数器；" +
        "没有任何小时级或日级历史记录，也没有 DNS 或连接日志。\n" +
        "因此：本月视图同时提供「设备口径」（直接读设备）与「本地口径」（按日累加）；" +
        "当日与近七天视图只能来自本地采样。\n" + Host.History.CoverageText +
        (Host.History.UncapturedBytes > 0
            ? $"\n软件未运行期间无法归属的流量：{J.Bytes(Host.History.UncapturedBytes)}（如实单列，未摊派到任何一天）"
            : "");

    public string StorageText => $"本地数据目录：{AppSettings.DataDir}";

    // ---------------- 连接 ----------------

    private string _hostText = "";
    public string HostText { get => _hostText; set => Set(ref _hostText, value); }

    private string _userNameText = "";
    public string UserNameText { get => _userNameText; set => Set(ref _userNameText, value); }

    private string _passwordText = "";
    public string PasswordText { get => _passwordText; set => Set(ref _passwordText, value); }

    private bool _rememberPassword;
    public bool RememberPassword { get => _rememberPassword; set => Set(ref _rememberPassword, value); }

    private int _timeoutSeconds = 10;
    public int TimeoutSeconds
    {
        get => _timeoutSeconds;
        set
        {
            if (!Set(ref _timeoutSeconds, value) || _loading) return;
            Host.Settings.TimeoutSeconds = Math.Clamp(value, 3, 30);
            Host.Settings.Save();
        }
    }

    public string ConnectionStateText => Host.IsConnected
        ? $"当前已连接：{Host.Settings.Host}（用户 {Host.Settings.Username}）"
        : "当前未连接。";

    public string AuthStateText => Host.AuthEnforced
        ? "设备已校验登录凭据。"
        : "⚠ 实测该设备未校验登录凭据：任何能访问该地址的人都能读取/修改配置，请勿暴露到公网。";

    // ---------------- 采样与性能 ----------------

    private int _pollInterval = 5;
    public int PollIntervalSeconds
    {
        get => _pollInterval;
        set
        {
            if (!Set(ref _pollInterval, value) || _loading) return;
            Host.PollIntervalSeconds = Math.Clamp(value, 2, 60);
            OnPropertyChanged(nameof(SampleStatusText));
        }
    }

    private int _speedSample = 2;
    public int SpeedSampleSeconds
    {
        get => _speedSample;
        set
        {
            if (!Set(ref _speedSample, value) || _loading) return;
            Host.Settings.SpeedSampleSeconds = Math.Clamp(value, 2, 10);
            Host.Settings.Save();
        }
    }

    private int _deviceRefresh = 15;
    public int DeviceRefreshSeconds
    {
        get => _deviceRefresh;
        set
        {
            if (!Set(ref _deviceRefresh, value) || _loading) return;
            Host.Settings.DeviceRefreshSeconds = Math.Clamp(value, 15, 120);
            Host.Settings.Save();
        }
    }

    private bool _pauseWhenMinimized = true;
    public bool PauseWhenMinimized
    {
        get => _pauseWhenMinimized;
        set { if (Set(ref _pauseWhenMinimized, value) && !_loading) { Host.Settings.PauseWhenMinimized = value; Host.Settings.Save(); } }
    }

    private bool _lowPerformance;
    public bool LowPerformanceMode
    {
        get => _lowPerformance;
        set
        {
            if (!Set(ref _lowPerformance, value) || _loading) return;
            Host.Settings.LowPerformanceMode = value;
            Host.Settings.AnimationsEnabled = !value;
            AnimationsEnabled = !value;
            Host.Settings.Save();
        }
    }

    public string SampleStatusText =>
        Host.IsConnected
            ? $"采样中：实时状态每 {Host.PollIntervalSeconds} 秒，终端流量每 {DeviceRefreshSeconds} 秒，最后更新 {DateTime.Now:HH:mm:ss}"
            : "未采样：设备未连接。已保存的历史数据仍可查看，但不会更新。";

    // ---------------- 通知 ----------------

    public bool NotifyDisconnect { get => Host.Settings.NotifyDisconnect; set { Host.Settings.NotifyDisconnect = value; OnPropertyChanged(); Host.Settings.Save(); } }
    public bool NotifyReconnect { get => Host.Settings.NotifyReconnect; set { Host.Settings.NotifyReconnect = value; OnPropertyChanged(); Host.Settings.Save(); } }
    public bool NotifyDeviceEvents { get => Host.Settings.NotifyDeviceEvents; set { Host.Settings.NotifyDeviceEvents = value; OnPropertyChanged(); Host.Settings.Save(); } }
    public bool NotifyWeakSignal { get => Host.Settings.NotifyWeakSignal; set { Host.Settings.NotifyWeakSignal = value; OnPropertyChanged(); Host.Settings.Save(); } }
    public bool NotifyTrafficThreshold { get => Host.Settings.NotifyTrafficThreshold; set { Host.Settings.NotifyTrafficThreshold = value; OnPropertyChanged(); Host.Settings.Save(); } }
    public bool NotifyRouting { get => Host.Settings.NotifyRouting; set { Host.Settings.NotifyRouting = value; OnPropertyChanged(); Host.Settings.Save(); } }

    public bool NetworkQualityAutoCheck
    {
        get => Host.Settings.NetworkQualityAutoCheck;
        set
        {
            if (Host.Settings.NetworkQualityAutoCheck == value) return;
            Host.Settings.NetworkQualityAutoCheck = value;
            OnPropertyChanged();
            Host.Settings.Save();
            if (value) Host.ScheduleNetworkQualityProbeNow();
        }
    }

    private string _networkQualityStateText = "尚未检测";
    public string NetworkQualityStateText { get => _networkQualityStateText; private set => Set(ref _networkQualityStateText, value); }

    private string _networkQualitySummary = "点击「立即检测」执行一次独立探测；结果不会因为单次超时而判定断网。";
    public string NetworkQualitySummary { get => _networkQualitySummary; private set => Set(ref _networkQualitySummary, value); }

    private string _networkQualityRouter = "路由器：—";
    public string NetworkQualityRouter { get => _networkQualityRouter; private set => Set(ref _networkQualityRouter, value); }

    private string _networkQualityInternet = "外网：—";
    public string NetworkQualityInternet { get => _networkQualityInternet; private set => Set(ref _networkQualityInternet, value); }

    private string _networkQualityLatency = "延迟：—";
    public string NetworkQualityLatency { get => _networkQualityLatency; private set => Set(ref _networkQualityLatency, value); }

    private string _networkQualityLoss = "丢包率：—";
    public string NetworkQualityLoss { get => _networkQualityLoss; private set => Set(ref _networkQualityLoss, value); }

    private string _networkQualityTime = "检测时间：—";
    public string NetworkQualityTime { get => _networkQualityTime; private set => Set(ref _networkQualityTime, value); }
    private NetworkQualityState _lastLoggedQualityState = NetworkQualityState.NotTested;

    private double _thresholdGb = 10;
    public double TrafficThresholdGb
    {
        get => _thresholdGb;
        set
        {
            if (!Set(ref _thresholdGb, value) || _loading) return;
            Host.Settings.TrafficThresholdGb = Math.Clamp(value, 0.1, 1000);
            Host.Settings.Save();
        }
    }

    // ---------------- 启动与窗口 ----------------

    public bool RememberWindowPlacement { get => Host.Settings.RememberWindowPlacement; set { Host.Settings.RememberWindowPlacement = value; OnPropertyChanged(); Host.Settings.Save(); } }
    public bool RememberLastPage { get => Host.Settings.RememberLastPage; set { Host.Settings.RememberLastPage = value; OnPropertyChanged(); Host.Settings.Save(); } }
    public bool MinimizeToTray { get => Host.Settings.MinimizeToTray; set { Host.Settings.MinimizeToTray = value; OnPropertyChanged(); Host.Settings.Save(); } }

    public bool StartWithWindows
    {
        get => Host.Settings.StartWithWindows;
        set
        {
            Host.Settings.StartWithWindows = value;
            StartupRegistry.SetEnabled(value);
            OnPropertyChanged();
            Host.Settings.Save();
        }
    }

    public string StartupNote =>
        "默认不常驻后台、不开机自启、不安装任何服务。流量历史只在软件运行时采样；" +
        "开机自启需要你在这里显式打开（写入当前用户的启动项，可随时关闭）。";

    // ---------------- 关于与诊断 ----------------

    public string AppVersion => "NRadio C2000 Pro 高级管理器 v4.3";
    public string DeviceModel => string.IsNullOrWhiteSpace(Host.Dashboard.DeviceName) ? "—（未连接）" : Host.Dashboard.DeviceName;
    public string RuntimeText => $".NET {Environment.Version} · {Environment.OSVersion.VersionString}";

    public ObservableCollection<string> NotImplemented { get; } = new()
    {
        "固件升级：需上传镜像且失败不可回滚，本机无固件包可验证，未实现。",
        "短信 / USSD / 电话本：该固件不含这些模块（原厂页面返回 404），不存在可对接的接口。",
        "访问去向分析（域名/服务类别）：设备不提供 DNS 查询记录、连接日志或流量分类接口，无法真实获取，未实现。",
        "账号新增/删除：account_action 取值语义未能从原厂前端完全确认，仅实现修改当前管理员密码。",
        "MAC 黑白名单 / WPS / 自定义防火墙：设备接口存在但条目结构与取值语义未经真机确认，仅只读展示。",
    };

    public ICommand ReconnectCommand { get; }
    public ICommand ApplyConnectionCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand ResetAppearanceCommand { get; }
    public ICommand ResetTilesCommand { get; }
    public ICommand ExportSettingsCommand { get; }
    public ICommand PickAccentCommand { get; }
    public ICommand ImportSettingsCommand { get; }
    public ICommand ExportDiagnosticsCommand { get; }
    public ICommand TestNotificationCommand { get; }
    public ICommand TestNetworkQualityCommand { get; }
    public ICommand ExportTrafficCsvCommand { get; }
    public ICommand ExportTerminalCsvCommand { get; }

    /// <summary>设置页重新读取当前设置（例如用户在窗口里切换了主题）。</summary>
    public void ReloadFromSettings()
    {
        _loading = true;
        var s = Host.Settings;

        _accentColor = s.AccentColor;
        _tileCornerRadius = s.TileCornerRadius;
        _tileSpacing = s.TileSpacing;
        _tilePadding = s.TilePadding;
        _uiScale = s.UiScale;
        _animationsEnabled = s.AnimationsEnabled;
        _lowPerformance = s.LowPerformanceMode;
        _hostText = s.Host;
        _userNameText = s.Username;
        _passwordText = Host.Password;
        _rememberPassword = s.RememberPassword;
        _timeoutSeconds = s.TimeoutSeconds;
        _pollInterval = s.PollIntervalSeconds;
        _speedSample = s.SpeedSampleSeconds;
        _deviceRefresh = s.DeviceRefreshSeconds;
        _pauseWhenMinimized = s.PauseWhenMinimized;
        _retentionDays = s.HistoryRetentionDays;
        _selectedBatterySample = BatterySampleOptions.FirstOrDefault(o => o.Value == s.BatterySampleSeconds.ToString())
                                 ?? BatterySampleOptions[2];
        _selectedBatteryRetention = BatteryRetentionOptions
                                        .FirstOrDefault(o => o.Value == s.BatteryHistoryRetentionDays.ToString())
                                    ?? BatteryRetentionOptions[1];
        _chartWindow = s.ChartWindowMinutes;
        _thresholdGb = s.TrafficThresholdGb;

        _selectedUnit = UnitOptions.FirstOrDefault(o => o.Value == s.Unit.ToString()) ?? UnitOptions[0];
        _selectedPeriod = PeriodOptions.FirstOrDefault(o => o.Value == s.DefaultStatsPeriod) ?? PeriodOptions[0];
        _selectedSignalThreshold = SignalThresholdOptions.FirstOrDefault(o => o.Value == s.WeakSignalThresholdLevel.ToString())
                                   ?? SignalThresholdOptions[1];
        _selectedTrafficPeriod = TrafficPeriodOptions.FirstOrDefault(o =>
                                     o.Value == (s.TrafficThresholdScope == TrafficThresholdPeriod.Month ? "Month" : "Day"))
                                 ?? TrafficPeriodOptions[0];

        foreach (var n in new[]
                 {
                     nameof(ThemeDark), nameof(ThemeLight), nameof(ThemeSystem), nameof(AccentColor),
                     nameof(TileCornerRadius), nameof(TileSpacing), nameof(TilePadding), nameof(UiScale),
                     nameof(AnimationsEnabled), nameof(SelectedUnit), nameof(SelectedPeriod), nameof(HostText),
                     nameof(UserNameText), nameof(PasswordText), nameof(RememberPassword), nameof(TimeoutSeconds),
                     nameof(PollIntervalSeconds), nameof(SpeedSampleSeconds), nameof(DeviceRefreshSeconds),
                     nameof(PauseWhenMinimized), nameof(LowPerformanceMode), nameof(HistoryRetentionDays),
                     nameof(SelectedBatterySample), nameof(SelectedBatteryRetention),
                     nameof(ChartWindowMinutes), nameof(TrafficThresholdGb), nameof(StartWithWindows),
                     nameof(MinimizeToTray), nameof(RememberWindowPlacement), nameof(RememberLastPage),
                     nameof(SelectedSignalThreshold), nameof(SelectedTrafficPeriod), nameof(NotifyWeakSignal),
                     nameof(NotifyDisconnect), nameof(NotifyReconnect), nameof(NotifyDeviceEvents), nameof(NotifyTrafficThreshold),
                     nameof(NotifyRouting), nameof(NetworkQualityAutoCheck),
                 })
            OnPropertyChanged(n);
        _loading = false;
    }

    protected override async Task LoadAsync()
    {
        ReloadFromSettings();
        OnPropertyChanged(nameof(ConnectionStateText));
        OnPropertyChanged(nameof(AuthStateText));
        OnPropertyChanged(nameof(DataSourceText));
        OnPropertyChanged(nameof(StorageText));
        OnPropertyChanged(nameof(SampleStatusText));
        OnPropertyChanged(nameof(DeviceModel));
        await Task.CompletedTask;
    }

    // ---------------- 网络质量 ----------------

    public void ApplyNetworkQualityResult(NetworkQualityResult result)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ApplyNetworkQualityResult(result));
            return;
        }

        NetworkQualityStateText = result.State switch
        {
            NetworkQualityState.Healthy => "正常",
            NetworkQualityState.LocalNetworkUnavailable => "本机网络未连接",
            NetworkQualityState.RouterUnreachable => "路由器不可达",
            NetworkQualityState.InternetUnreachable => "外网连接失败",
            NetworkQualityState.DetectionFailed => "检测失败（无法判定）",
            _ => "尚未检测",
        };
        NetworkQualitySummary = result.Summary;
        NetworkQualityRouter = result.RouterText;
        NetworkQualityInternet = result.InternetText;
        NetworkQualityLatency = result.LatencyText;
        NetworkQualityLoss = result.LossText;
        NetworkQualityTime = $"检测时间：{result.CompletedAt:yyyy-MM-dd HH:mm:ss}";
    }

    public void ApplyNetworkQualityFailure(string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ApplyNetworkQualityFailure(message));
            return;
        }

        NetworkQualityStateText = "检测失败（无法判定）";
        NetworkQualitySummary = "检测过程异常，不能据此判定断网：" + message;
        NetworkQualityTime = $"检测时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
    }

    private async Task ProbeNetworkQualityAsync()
    {
        NetworkQualityStateText = "检测中…";
        NetworkQualitySummary = "正在探测路由器管理端口、ICMP 延迟与外网 TCP 连通性…";
        Host.Log.Info(LogCategory.Network, "网络质量", "开始手动网络质量检测。");
        try
        {
            var result = await NetworkQualityMonitor.ProbeAsync(Host.Settings.Host);
            ApplyNetworkQualityResult(result);
            var level = result.State == NetworkQualityState.Healthy ? LogLevel.Success : LogLevel.Warning;
            var changed = result.State != _lastLoggedQualityState;
            _lastLoggedQualityState = result.State;
            Host.Log.Add(level, LogCategory.Network, "网络质量",
                (changed ? "手动检测结果（状态变化）：" : "手动检测结果：") + result.Summary);
        }
        catch (Exception ex)
        {
            ApplyNetworkQualityFailure(ex.Message);
            Host.Log.Warn(LogCategory.Network, "网络质量",
                "手动检测失败，不能据此判定断网：" + ex.Message);
        }
    }

    // ---------------- CSV 导出 ----------------

    private void ExportTrafficCsv()
    {
        var bars = Host.Dashboard.Bars.ToList();
        if (bars.Count == 0)
        {
            Message = "当前流量视图没有可导出的采样数据。";
            IsError = true;
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "导出流量统计 CSV",
            Filter = "CSV 表格 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            FileName = $"nradio-traffic-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dlg.ShowDialog() != true) return;

        var rows = bars.Select(b => new TrafficCsvRow(b.Key, b.Rx, b.Tx, b.FromLocalSampling));
        var scope = Host.Dashboard.SelectedPeriod switch
        {
            "week" => "近七天（本机采样）",
            "month" => "本月（本机采样）",
            _ => "当日（本机采样）",
        };

        try
        {
            CsvExport.WriteUtf8Bom(dlg.FileName, CsvExport.BuildTrafficCsv(rows, DateTime.Now, scope));
            Message = "流量统计已导出（UTF-8 CSV）：" + dlg.FileName;
            IsError = false;
        }
        catch (Exception ex)
        {
            Message = "导出失败：" + ex.Message;
            IsError = true;
        }
    }

    private async Task ExportTerminalCsvAsync()
    {
        if (Host.Api is null)
        {
            Message = "请先连接设备，再导出在线终端清单。";
            IsError = true;
            return;
        }

        DeviceManagementInfo info;
        try
        {
            info = await Host.Api.GetDevicesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Message = "读取终端列表失败，未生成文件：" + ex.Message;
            IsError = true;
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "导出在线终端清单（脱敏）",
            Filter = "CSV 表格 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            FileName = $"nradio-clients-masked-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dlg.ShowDialog() != true) return;

        var rows = (info.ClientList ?? new List<ClientEntry>()).Select(c => new TerminalCsvRow(
            Host.DisplayName(c.Mac, c.Name), c.Name, c.Mac ?? "", c.Ip ?? "",
            c.ConnType == "WIFI" ? "无线" : c.ConnType ?? "—",
            c.Status == "2" ? "已禁止上网" : "正常",
            J.Long(c.Rx), J.Long(c.Tx), J.Long(c.RxMonth), J.Long(c.TxMonth),
            J.Long(c.Rx3Day), J.Long(c.Tx3Day))).ToList();

        try
        {
            CsvExport.WriteUtf8Bom(dlg.FileName, CsvExport.BuildTerminalCsv(rows, DateTime.Now));
            Message = $"终端清单已导出（{rows.Count} 条，名称/MAC/IP 已脱敏）：{dlg.FileName}";
            IsError = false;
        }
        catch (Exception ex)
        {
            Message = "导出失败：" + ex.Message;
            IsError = true;
        }
    }

    // ---------------- 备份与恢复 ----------------

    private void ExportSettings()
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出管理器设置",
            Filter = "管理器配置包 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = $"nradio-settings-{DateTime.Now:yyyyMMdd-HHmm}.json",
        };
        if (dlg.ShowDialog() != true) return;

        var package = new SettingsPackage
        {
            Version = 1,
            ExportedAt = DateTime.Now,
            Settings = Host.Settings.Clone(),
            DeviceAliases = Host.Aliases.Export(),
        };
        // 备份中不包含密码
        package.Settings.PasswordCipher = null;
        package.Settings.RememberPassword = false;

        try
        {
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(package,
                new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
            Message = "设置已导出（不含密码）：" + dlg.FileName;
            IsError = false;
        }
        catch (Exception ex)
        {
            Message = "导出失败：" + ex.Message;
            IsError = true;
        }
    }

    private void ImportSettings()
    {
        var dlg = new OpenFileDialog { Title = "导入管理器设置", Filter = "管理器配置包 (*.json)|*.json|所有文件 (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var package = JsonSerializer.Deserialize<SettingsPackage>(File.ReadAllText(dlg.FileName),
                new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
            if (package?.Settings is null)
            {
                Message = "导入失败：文件格式不正确。";
                IsError = true;
                return;
            }
            if (package.Version > 1)
            {
                Message = $"导入失败：该备份由更高版本生成（版本 {package.Version}），当前软件无法识别。";
                IsError = true;
                return;
            }

            var r = MessageBox.Show(
                "导入将覆盖当前的界面外观、采样、通知等设置与设备自定义名称（连接地址与账号也会被覆盖）。是否继续？",
                "导入设置", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            package.Settings.SetPassword(Host.Password);
            Host.ReplaceSettings(package.Settings);
            Host.Aliases.Import(package.DeviceAliases);
            if (!string.IsNullOrWhiteSpace(Host.Aliases.LastSaveError))
            {
                Host.Log.Error(LogCategory.Persistence, "设备名称",
                    "设置导入时设备名称保存失败：" + Host.Aliases.LastSaveError);
            }
            Host.Battery.ApplySettings();
            ReloadFromSettings();
            Host.RaiseSettingsChanged();
            Host.Dashboard.ReloadTiles();
            Message = "设置已导入并生效。";
            IsError = false;
        }
        catch (Exception ex)
        {
            Message = "导入失败：" + ex.Message;
            IsError = true;
        }
    }

    private void ExportDiagnostics()
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出脱敏诊断日志",
            Filter = "文本文件 (*.txt)|*.txt",
            FileName = $"nradio-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt",
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("NRadio C2000 Pro 高级管理器 · 诊断报告（已脱敏，不含密码 / Cookie / 摘要）");
        sb.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"软件版本：{AppVersion}");
        sb.AppendLine($"运行环境：{RuntimeText}");
        sb.AppendLine($"设备地址：{CsvExport.MaskEndpoint(Host.Settings.Host)}（用户 {CsvExport.MaskText(Host.Settings.Username)}）");
        sb.AppendLine($"连接状态：{Host.ConnectionStateText}，认证强制：{Host.AuthEnforced}");
        if (Host.IsConnected)
        {
            sb.AppendLine($"设备：{CsvExport.MaskText(Host.Dashboard.DeviceName)} / 固件 {Host.Dashboard.FirmwareVersion}");
            sb.AppendLine($"运营商：{Host.Dashboard.Operator}，WAN {CsvExport.MaskIp(Host.Dashboard.WanIp)}，信号 {Host.Dashboard.SignalLevelText}({Host.Dashboard.SignalDetail})");
        }
        sb.AppendLine($"采样设置：状态 {Host.PollIntervalSeconds}s / 速率 {Host.Settings.SpeedSampleSeconds}s / 终端 {Host.Settings.DeviceRefreshSeconds}s");
        sb.AppendLine($"本地历史：{Host.History.CoverageText}，未采集量 {J.Bytes(Host.History.UncapturedBytes)}");
        sb.AppendLine($"外部依赖：无（未使用任何第三方库或后台服务）");
        sb.AppendLine();
        sb.AppendLine("---- 最近通信日志（已自动脱敏）----");
        foreach (var item in Host.Log.Items.Take(300))
            sb.AppendLine($"{item.Time:yyyy-MM-dd HH:mm:ss} [{item.LevelText}] {CsvExport.MaskSensitiveText(item.Message)}");

        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString());
            Message = "诊断日志已导出：" + dlg.FileName;
            IsError = false;
        }
        catch (Exception ex)
        {
            Message = "导出失败：" + ex.Message;
            IsError = true;
        }
    }

    private sealed class SettingsPackage
    {
        public int Version { get; set; }
        public DateTime ExportedAt { get; set; }
        public AppSettings? Settings { get; set; }
        public Dictionary<string, string>? DeviceAliases { get; set; }
    }
}

/// <summary>开机自启：只写当前用户的 Run 键，随开随关，不装服务。</summary>
internal static class StartupRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NRadioManager";

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 无权限时静默失败，设置项会随下次读取回到真实状态
        }
    }
}
