using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using NRadioManager.Core;
using NRadioManager.Infrastructure;

namespace NRadioManager.ViewModels;

/// <summary>
/// 应用主上下文：连接管理、全局轮询、导航、共享数据（流量历史 / 设备别名 / 通知）与各模块生命周期。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private int _pollCounter;
    private int _ticksSinceHistorySave;
    private int _consecutivePollFailures;
    private bool _wasOffline;
    private bool _shutdownLogged;
    private long? _lastRunSeconds;
    private DateTime _lastRoutineSuccessLogUtc = DateTime.MinValue;
    private DateTime _lastRequestFailureUtc = DateTime.MinValue;
    private string? _lastTrafficSaveError;
    private NetworkQualityState _lastNetworkQualityState = NetworkQualityState.NotTested;
    private readonly SignalAlertMonitor _signalAlerts = new();
    private bool _qualityProbeRunning;
    private DateTime _nextQualityProbeUtc = DateTime.MinValue;
    private bool _terminalBaselineReady;
    private readonly HashSet<string> _knownOnlineMacs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _terminalMissingCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _terminalNames = new(StringComparer.OrdinalIgnoreCase);

    public MainViewModel(ActivityLog? activityLog = null)
    {
        Settings = AppSettings.Load();
        History = TrafficHistory.Load(Settings.HistoryRetentionDays);
        SignalHistory = new C2000SignalHistory(Settings.HistoryRetentionDays);
        Aliases = DeviceAliases.Load();
        Log = activityLog ?? new ActivityLog();
        Notices = new NoticeCenter(Log);
        Log.WriteFailed += OnLogWriteFailed;
        if (!string.IsNullOrWhiteSpace(Settings.LastLoadError))
        {
            Log.Warn(LogCategory.Persistence, "配置",
                "settings.json 读取失败，已使用默认配置：" + Settings.LastLoadError);
        }
        if (!string.IsNullOrWhiteSpace(History.LoadWarning))
        {
            Log.Warn(LogCategory.Persistence, "流量历史",
                "流量历史读取失败，已从空历史开始：" + History.LoadWarning);
        }
        if (!string.IsNullOrWhiteSpace(Aliases.LastLoadError))
        {
            Log.Warn(LogCategory.Persistence, "设备名称",
                "设备自定义名称读取失败，已从空映射开始：" + Aliases.LastLoadError);
        }

        _host = Settings.Host;
        _userName = Settings.Username;
        _password = Settings.GetPassword();
        _rememberPassword = Settings.RememberPassword;
        _pollIntervalSeconds = Settings.PollIntervalSeconds;

        ThemeManager.Apply(Settings);
        J.Unit = Settings.Unit;

        Dashboard = new DashboardViewModel(this);
        Cellular = new CellularViewModel(this);
        Wifi = new WifiViewModel(this);
        Lan = new LanViewModel(this);
        Devices = new DevicesViewModel(this);
        Battery = new BatteryViewModel(this);
        Cellular2000 = new C2000CellularViewModel(this);
        Signal2000 = new C2000SignalViewModel(this);
        Lock2000 = new C2000LockViewModel(this);
        Sim2000 = new C2000SimViewModel(this);
        Wifi2000 = new C2000WifiViewModel(this);
        Sms2000 = new C2000SmsViewModel(this);
        Maintenance2000 = new C2000MaintenanceViewModel(this);
        System = new SystemViewModel(this);
        Logs = new LogViewModel(this);
        SettingsPage = new SettingsViewModel(this);

        NavItems = new ObservableCollection<NavItem>
        {
            new("概览", "\uE80F", Dashboard),
            new("移动网络", "\uE704", Cellular),
            new("Wi-Fi", "\uE701", Wifi),
            new("局域网", "\uE968", Lan),
            new("设备与流量", "\uE7F4", Devices),
            new("电池监测", "\uE83F", Battery),
            new("系统与维护", "\uE713", System),
            new("通信日志", "\uE9D5", Logs),
            new("设置", "\uE713", SettingsPage, isSettings: true),
        };

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected && !IsConnecting);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
        RefreshAllCommand = new AsyncRelayCommand(async () => await RefreshAllAsync(true), () => IsConnected);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        SelectNavCommand = new RelayCommand(p =>
        {
            if (p is NavItem item) SelectedNav = item;
        });

        _timer.Tick += async (_, _) => await PollAsync();
        SelectedNav = NavItems.FirstOrDefault(n => n.Title == Settings.LastPage && Settings.RememberLastPage) ?? NavItems[0];

        var version = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        Log.Info(LogCategory.Application, "软件",
            $"软件启动，版本 {version}；已加载历史日志 {Log.LoadedCount} 条；" +
            $"日志文件 {Log.CurrentFilePath}");
    }

    // ---------------- 共享状态 ----------------

    public AppSettings Settings { get; }
    public TrafficHistory History { get; }
    public C2000SignalHistory SignalHistory { get; }
    public DeviceAliases Aliases { get; }
    public ActivityLog Log { get; }
    public NoticeCenter Notices { get; }

    public IDeviceAdapter? Device { get; private set; }
    public IDeviceApi? Api { get; private set; }

    public DashboardViewModel Dashboard { get; }
    public CellularViewModel Cellular { get; }
    public WifiViewModel Wifi { get; }
    public LanViewModel Lan { get; }
    public DevicesViewModel Devices { get; }
    public BatteryViewModel Battery { get; }
    public C2000CellularViewModel Cellular2000 { get; }
    public C2000SignalViewModel Signal2000 { get; }
    public C2000LockViewModel Lock2000 { get; }
    public C2000SimViewModel Sim2000 { get; }
    public C2000WifiViewModel Wifi2000 { get; }
    public C2000SmsViewModel Sms2000 { get; }
    public C2000MaintenanceViewModel Maintenance2000 { get; }
    public SystemViewModel System { get; }
    public LogViewModel Logs { get; }
    public SettingsViewModel SettingsPage { get; }

    public ObservableCollection<NavItem> NavItems { get; }

    public C2000ProAdapter? C2000Adapter => Device as C2000ProAdapter;

    /// <summary>外观类设置变化时通知窗口（用于界面缩放等需要窗口参与的调整）。</summary>
    public event Action? SettingsChanged;

    public void RaiseSettingsChanged() => SettingsChanged?.Invoke();

    public bool PersistSettings()
    {
        var ok = Settings.Save();
        if (!ok)
        {
            Log.Error(LogCategory.Persistence, "配置",
                "设置保存失败：" + (Settings.LastSaveError ?? "未知错误"));
        }
        return ok;
    }

    /// <summary>
    /// 把异常写到磁盘（%APPDATA%\NRadioManager\last-error.txt）并记入通信日志。
    /// 界面上无法复现的问题，靠这份现场记录定位原因。
    /// </summary>
    public void ReportFailure(string context, Exception ex)
    {
        try
        {
            Log.Error(LogCategory.Application, "软件",
                $"{context} 失败：{ex.GetType().Name}: {ex.Message}",
                ActivityLog.Sanitize(ex.ToString()));
        }
        catch { }

        try
        {
            global::System.IO.Directory.CreateDirectory(AppSettings.DataDir);
            var safe = ActivityLog.Sanitize(ex.ToString());
            global::System.IO.File.WriteAllText(
                global::System.IO.Path.Combine(AppSettings.DataDir, "last-error.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}\r\n{safe}\r\n");
        }
        catch { }
    }

    /// <summary>窗口关闭时调用：停止采样说明、保存历史和配置，并同步冲刷日志队列。</summary>
    public void Shutdown()
    {
        if (_shutdownLogged) return;
        _shutdownLogged = true;

        _timer.Stop();
        try { Battery.StopSampling(); } catch { }
        try { Battery.SaveNow(); } catch { }
        try { SignalHistory.Save(force: true); } catch { }
        try { Dashboard.PersistTiles(); } catch (Exception ex) { ReportFailure("退出时保存磁贴布局", ex); }
        PersistSettings();

        try
        {
            Log.Info(LogCategory.Application, "软件", "软件正常退出。");
        }
        finally
        {
            Log.Flush(TimeSpan.FromSeconds(3));
        }

        try { Device?.Dispose(); } catch { }
        Device = null;
        Api = null;
    }

    private void OnLogWriteFailed(string message)
    {
        // 不能用 ActivityLog 记录“ActivityLog 写入失败”：这会形成递归。
        // 仅在界面上给出一次通知（错误变化时才触发），详情留在日志页与 activity-write-error.txt。
        try
        {
            Notices.Raise(NoticeLevel.Warning, "日志写入失败",
                $"运行日志无法写入磁盘：{message}。事件仍保留在内存队列，稍后会重试；可在「通信日志」页查看状态。",
                logCategory: LogCategory.Persistence, logSource: "通信日志", logEvent: false);
        }
        catch { }
    }

    /// <summary>轻量执行轨迹（%APPDATA%\NRadioManager\ui-trace.txt），用于确认按钮动作是否真的执行到。</summary>
    public void Trace(string message)
    {
        try
        {
            global::System.IO.Directory.CreateDirectory(AppSettings.DataDir);
            global::System.IO.File.AppendAllText(
                global::System.IO.Path.Combine(AppSettings.DataDir, "ui-trace.txt"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
        }
        catch { }
    }

    /// <summary>设置中心用：断开当前连接（用于应用新的连接配置）。</summary>
    internal void DisconnectForSettings() => Disconnect();

    /// <summary>设置中心用：按当前配置重新连接。</summary>
    internal Task ConnectForSettingsAsync() => ConnectAsync();

    /// <summary>用户刚开启自动检测时，让下一轮轮询立即执行一次，而不是等满 5 分钟。</summary>
    internal void ScheduleNetworkQualityProbeNow() => _nextQualityProbeUtc = DateTime.MinValue;

    /// <summary>连接配置在验证成功后写回。</summary>
    internal void ApplyConnectionSettings(string host, string user, string password, bool remember)
    {
        Host = host;
        UserName = user;
        Password = password;
        RememberPassword = remember;
        Settings.Host = host;
        Settings.Username = user;
        Settings.RememberPassword = remember;
        Settings.SetPassword(remember ? password : string.Empty);
        PersistSettings();
        Log.Info(LogCategory.Configuration, "连接设置",
            $"连接配置已更新：地址 {RouterClient.NormalizeHost(host)}，用户 {user}，" +
            $"保存密码={remember}（密码内容未写入日志）。");
    }

    /// <summary>导入设置包时整体替换（保留正在使用的密码）。</summary>
    internal void ReplaceSettings(AppSettings imported)
    {
        var merged = AppSettings.Normalize(imported);
        merged.Tiles = TileLayout.Merge(merged.Tiles);
        Settings.CopyFrom(merged);
        ThemeManager.Apply(Settings);
        J.Unit = Settings.Unit;
        RaiseSettingsChanged();
        PersistSettings();
        Log.Info(LogCategory.Configuration, "设置",
            "已导入并应用设置包；设备连接、主题、采样参数和界面布局已按新配置更新。");
        OnPropertyChanged(nameof(ThemeGlyph));
    }

    /// <summary>设备显示名：优先用户自定义名称，其次设备原始主机名。</summary>
    public string DisplayName(string? mac, string? hostname)
        => Aliases.Get(mac) ?? (string.IsNullOrWhiteSpace(hostname) ? "未知设备" : hostname!);

    private NavItem? _selectedNav;
    public NavItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (!Set(ref _selectedNav, value)) return;
            foreach (var n in NavItems) n.IsSelected = ReferenceEquals(n, value);
            if (value is not null) _ = value.Module.RefreshAsync();
        }
    }

    // ---------------- 连接参数 ----------------

    private string _host;
    public string Host { get => _host; set => Set(ref _host, value); }

    private string _userName;
    public string UserName { get => _userName; set => Set(ref _userName, value); }

    private string _password;
    public string Password { get => _password; set => Set(ref _password, value); }

    private bool _rememberPassword;
    public bool RememberPassword { get => _rememberPassword; set => Set(ref _rememberPassword, value); }

    private int _pollIntervalSeconds;
    public int PollIntervalSeconds
    {
        get => _pollIntervalSeconds;
        set
        {
            var clamped = Math.Clamp(value, 2, 60);
            if (!Set(ref _pollIntervalSeconds, clamped)) return;
            Settings.PollIntervalSeconds = clamped;
            _timer.Interval = TimeSpan.FromSeconds(clamped);
            PersistSettings();
            Log.Info(LogCategory.Configuration, "采样设置", $"轮询间隔已改为 {clamped} 秒。");
        }
    }

    // ---------------- 连接状态 ----------------

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!Set(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(IsDisconnected));
            OnPropertyChanged(nameof(ConnectionStateText));
            (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (DisconnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RefreshAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsDisconnected => !IsConnected;

    private bool _isConnecting;
    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (!Set(ref _isConnecting, value)) return;
            OnPropertyChanged(nameof(ConnectionStateText));
            (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string ConnectionStateText => IsConnecting ? "正在连接…" : IsConnected ? "已连接" : "未连接";

    private bool _authEnforced = true;
    /// <summary>设备是否真的强制校验登录凭据（由 RouterClient 实测探测得出）。</summary>
    public bool AuthEnforced
    {
        get => _authEnforced;
        private set
        {
            if (!Set(ref _authEnforced, value)) return;
            OnPropertyChanged(nameof(HasAuthWarning));
        }
    }

    public bool HasAuthWarning => !AuthEnforced;

    public string AuthWarning =>
        "实测发现：本设备的原厂接口未校验登录凭据 —— 在不携带任何认证信息的请求下，仍可读取全部配置数据。" +
        "这是设备固件自身的缺陷，第三方软件无法修复。请勿将该路由器的管理地址暴露到公网，也不要在不可信的局域网中使用。";

    private string? _lastError;
    public string? LastError
    {
        get => _lastError;
        private set { if (Set(ref _lastError, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_lastError);

    private string _summary = "尚未连接设备";
    public string Summary { get => _summary; internal set => Set(ref _summary, value); }

    private string _pollHint = "";
    public string PollHint { get => _pollHint; private set => Set(ref _pollHint, value); }

    public string ThemeGlyph => Infrastructure.ThemeManager.EffectiveDark ? "\uE706" : "\uE708";

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand RefreshAllCommand { get; }
    public ICommand SelectNavCommand { get; }
    public ICommand ToggleThemeCommand { get; }

    private void ToggleTheme()
    {
        Settings.Theme = Infrastructure.ThemeManager.EffectiveDark ? AppTheme.Light : AppTheme.Dark;
        Infrastructure.ThemeManager.Apply(Settings);
        PersistSettings();
        Log.Info(LogCategory.Configuration, "外观", $"主题已切换为 {Settings.Theme}。");
        OnPropertyChanged(nameof(ThemeGlyph));
        SettingsPage.ReloadFromSettings();
    }

    // ---------------- 连接流程 ----------------

    public async Task StartAsync()
    {
        if (Settings.AutoConnect) await ConnectAsync();
    }

    internal Task ConnectForTestAsync() => ConnectAsync();
    internal Task RefreshAllForTestAsync() => RefreshAllAsync(false);

    private async Task ConnectAsync()
    {
        if (IsConnecting || IsConnected) return;
        IsConnecting = true;
        LastError = null;
        var wasOffline = _wasOffline;
        var target = RouterClient.NormalizeHost(Host);
        Log.Info(LogCategory.Connection, "设备连接", $"开始连接 {target}（用户 {UserName}）。");
        try
        {
            Device?.Dispose();
            var device = await DeviceConnector.ConnectAsync(Host, UserName, Password,
                Settings.TimeoutSeconds, CancellationToken.None);
            device.RequestLogged += e =>
            {
                if (e.Error is not null)
                {
                    _lastRequestFailureUtc = DateTime.UtcNow;
                    Log.Warn(LogCategory.Communication, "设备请求",
                        $"{e.Method} {e.Endpoint} 失败：{e.Error}（{e.ElapsedMs} ms）");
                    return;
                }

                // 高频状态轮询成功不逐条记录；写操作和低频会话接口保留诊断信息。
                if (string.Equals(e.Method, "POST", StringComparison.OrdinalIgnoreCase) ||
                    e.Endpoint is "login.cgi#challenge" or "login.cgi#digest" or "auth-probe")
                {
                    Log.Info(LogCategory.Communication, "设备请求",
                        $"{e.Method} {e.Endpoint} 成功：HTTP {e.StatusCode}，{e.ElapsedMs} ms。");
                }
            };
            device.DiagnosticLogged += e =>
                Log.Add(e.Level, LogCategory.Communication, "会话", e.Message);
            Device = device;
            Api = device.Api;
            Host = device.Host;
            UserName = device.Username;
            RebuildNavigation();

            AuthEnforced = device.Kind == DeviceKind.NRadioLegacy &&
                           device is NRadioLegacyAdapter { AuthEnforced: false } ? false : true;
            if (!AuthEnforced)
            {
                Log.Warn(LogCategory.Connection, "设备认证",
                    "安全告警：该设备未校验登录凭据（无认证请求也能读取配置），详见「系统与维护」页说明。");
            }

            IsConnected = true;
            _consecutivePollFailures = 0;
            _wasOffline = false;
            _terminalBaselineReady = false;
            _knownOnlineMacs.Clear();
            _terminalMissingCounts.Clear();
            _terminalNames.Clear();
            var detectedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (wasOffline)
            {
                Log.Success(LogCategory.Connection, "设备在线",
                    $"设备重连成功；本地检测时间 {detectedAt}。固件未提供真实重连事件时间线，恢复原因未知；" +
                    (Settings.NotifyReconnect ? "已显示通知「设备已恢复连接」" : "通知开关关闭，未弹出恢复通知") + "。");
                if (Settings.NotifyReconnect)
                {
                    Notices.Raise(NoticeLevel.Success, "reconnect", "设备已恢复连接",
                        $"已重新连接到 {Host}（本地检测时间 {detectedAt}）", true,
                        LogCategory.Connection, "设备连接", logEvent: false);
                }
            }
            else
            {
                Log.Success(LogCategory.Connection, "设备在线",
                    $"连接与认证成功，设备已上线；本地首次检测时间 {detectedAt}。" +
                    "固件未提供真实上线事件时间线，不能把该时间当作设备实际开机时间。");
            }

            Settings.Host = Host;
            Settings.Username = UserName;
            Settings.RememberPassword = RememberPassword;
            Settings.SetPassword(RememberPassword ? Password : string.Empty);
            PersistSettings();

            _timer.Interval = TimeSpan.FromSeconds(PollIntervalSeconds);
            _timer.Start();

            await RefreshAllAsync(false);
        }
        catch (RouterException ex)
        {
            LastError = ex.UserMessage;
            var detail = ex.Kind == RouterErrorKind.AuthFailed
                ? "认证失败"
                : "连接失败";
            Log.Error(LogCategory.Connection, "设备连接",
                $"{detail}：{ex.UserMessage}（{ex.Kind}，接口 {ex.Endpoint ?? "-"}）；" +
                (Settings.NotifyDisconnect ? "已显示通知「连接失败」" : "通知开关关闭，未弹出通知"),
                ActivityLog.Sanitize(ex.ToString()));
            Notices.Raise(NoticeLevel.Error, "connect-failed", "连接失败", ex.UserMessage,
                Settings.NotifyDisconnect, LogCategory.Connection, "设备连接", logEvent: false);
            if (Settings.NetworkQualityAutoCheck) _ = MaybeProbeNetworkQualityAsync();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error(LogCategory.Connection, "设备连接",
                "连接发生未预期异常：" + ex.Message, ActivityLog.Sanitize(ex.ToString()));
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private void Disconnect()
        => Disconnect("用户手动断开连接", logEvent: true);

    private void Disconnect(string reason, bool logEvent)
    {
        _timer.Stop();
        _consecutivePollFailures = 0;
        if (IsConnected) Battery.MarkDisconnected(DateTime.UtcNow);
        Battery.SaveNow();
        Device?.Dispose();
        Device = null;
        Api = null;
        IsConnected = false;
        _terminalBaselineReady = false;
        _knownOnlineMacs.Clear();
        _terminalMissingCounts.Clear();
        _terminalNames.Clear();
        Summary = "尚未连接设备";
        if (logEvent)
        {
            Log.Info(LogCategory.Connection, "设备连接",
                $"已断开：{reason}；本地检测时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}。");
        }
    }

    public void ReportConnectionProblem(RouterException ex)
    {
        if (ex.Kind == RouterErrorKind.SessionExpired && IsConnected)
        {
            _wasOffline = true;
            LastError = ex.UserMessage;
            Log.Error(LogCategory.Connection, "设备连接",
                "登录会话自动恢复失败，已判定当前连接不可用；需要重新连接设备；" +
                (Settings.NotifyDisconnect ? "已显示通知「登录会话已失效」" : "通知开关关闭，未弹出通知"));
            Notices.Raise(NoticeLevel.Warning, "session-expired", "登录会话已失效", ex.UserMessage,
                Settings.NotifyDisconnect, LogCategory.Connection, "设备连接", logEvent: false);
            Disconnect("会话恢复失败", logEvent: false);
            return;
        }

        if (ex.Kind is RouterErrorKind.SessionExpired or RouterErrorKind.Network or RouterErrorKind.Timeout)
        {
            LastError = ex.UserMessage;
            if (ex.Kind != RouterErrorKind.SessionExpired)
            {
                // 单次超时可能只是无线抖动；连续两次失败才判定断连，避免轮询波动造成误报。
                _consecutivePollFailures++;
                if (_consecutivePollFailures < 2)
                {
                    Log.Info(LogCategory.Connection, "设备在线",
                        $"设备轮询失败 1 次（{ex.UserMessage}），按去抖规则暂不判定离线，等待下一轮复核。");
                    return;
                }

                _wasOffline = true;
                var detectedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                Log.Warn(LogCategory.Connection, "设备在线",
                    $"判定设备离线：连续 2 次轮询失败（最近错误：{ex.UserMessage}）；" +
                    $"本地检测时间 {detectedAt}，固件未提供真实离线事件时间；" +
                    (Settings.NotifyDisconnect ? "已显示通知「设备连接中断」" : "通知开关关闭，未弹出通知"));
                Notices.Raise(NoticeLevel.Warning, "offline", "设备连接中断", ex.UserMessage,
                    Settings.NotifyDisconnect, LogCategory.Connection, "设备在线", logEvent: false);
                Disconnect("通信失败，软件判定设备离线", logEvent: false);
            }
        }
    }

    private async Task RefreshAllAsync(bool userInitiated)
    {
        if (!IsConnected) return;
        if (userInitiated) Log.Info("手动刷新全部数据。");
        foreach (var nav in NavItems) await nav.Module.RefreshAsync();
        await PollAsync();
    }

    // ---------------- 轮询 ----------------

    private async Task PollAsync()
    {
        if (!IsConnected || Api is null) return;
        if (Settings.PauseWhenMinimized &&
            global::System.Windows.Application.Current?.MainWindow?.WindowState == global::System.Windows.WindowState.Minimized)
            return;
        if (!await _pollGate.WaitAsync(0)) return; // 上一轮未完成则跳过，避免请求堆积
        try
        {
            _pollCounter++;

            var status = await Api.GetStatusAsync(CancellationToken.None);
            Dashboard.ApplyStatus(status);
            if (Device?.Kind != DeviceKind.C2000Pro)
                Battery.Observe(status, DateTime.UtcNow);
            if (C2000Adapter?.LastSnapshot is { } c2000Snapshot)
            {
                if (SignalHistory.Observe(c2000Snapshot, DateTime.Now))
                    Signal2000.ApplySnapshot(c2000Snapshot);
            }
            DetectDeviceRestart(status);

            if (DateTime.UtcNow - _lastRoutineSuccessLogUtc >= TimeSpan.FromMinutes(10))
            {
                _lastRoutineSuccessLogUtc = DateTime.UtcNow;
                Log.Info(LogCategory.Communication, "设备请求",
                    "status_info 轮询正常；成功轮询按 10 分钟节流记录，避免高频日志膨胀。");
            }

            // 终端级计数器刷新频率较低（默认 15 秒），既能统计到每台设备，又不高频打扰设备
            DeviceManagementInfo? devices = null;
            var deviceTicks = Math.Max(1, Settings.DeviceRefreshSeconds / Math.Max(1, PollIntervalSeconds));
            var fetchDevices = _pollCounter % deviceTicks == 1;
            if (fetchDevices)
            {
                try { devices = await Api.GetDevicesAsync(CancellationToken.None); }
                catch (RouterException ex)
                {
                    Log.Warn(LogCategory.Communication, "设备请求",
                        "设备流量列表读取失败：" + ex.UserMessage);
                }
                if (devices is not null)
                    Devices.ApplyDeviceData(devices, status);
            }

            History.Sample(status, devices, DateTime.UtcNow);
            if (devices is not null) DetectTerminalPresence(devices);
            Dashboard.RefreshTrafficViews();
            CheckNotifications(status);
            _consecutivePollFailures = 0;

            if (++_ticksSinceHistorySave >= 12)
            {
                _ticksSinceHistorySave = 0;
                History.Save();
                SignalHistory.Save();
                CheckTrafficHistorySave();
            }

            // 首页身份信息变化较慢，降低频率（每 6 轮刷新一次）
            if (_pollCounter % 6 == 1)
                await Dashboard.ApplyDeviceInfoAsync(Api);

            // 网络质量检测默认关闭；开启后每 5 分钟最多一次，且不阻塞本轮状态刷新。
            _ = MaybeProbeNetworkQualityAsync();
        }
        catch (RouterException ex)
        {
            LastError = ex.UserMessage;
            if (DateTime.UtcNow - _lastRequestFailureUtc > TimeSpan.FromSeconds(2))
            {
                Log.Warn(LogCategory.Communication, "设备请求",
                    $"轮询失败：{ex.UserMessage}（{ex.Kind}，接口 {ex.Endpoint ?? "-"}）");
            }
            if (Settings.NetworkQualityAutoCheck) _ = MaybeProbeNetworkQualityAsync();
            ReportConnectionProblem(ex);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Warn(LogCategory.Communication, "设备请求",
                "轮询发生未预期异常：" + ex.Message);
        }
        finally
        {
            PollHint = $"更新于 {DateTime.Now:HH:mm:ss}";
            _pollGate.Release();
        }
    }

    /// <summary>
    /// 固件不提供重启事件，只能用 run_seconds 回退做本地判定。
    /// 这里明确记录“检测时间”和“依据”，不把检测时间伪装成真实重启时间。
    /// </summary>
    private void DetectDeviceRestart(StatusInfo status)
    {
        var runSeconds = J.Long(status.RunSeconds);
        if (runSeconds is null) return;

        if (_lastRunSeconds is { } previous && runSeconds.Value < previous)
        {
            Log.Warn(LogCategory.Device, "设备状态",
                $"检测到 run_seconds 回退（{previous} → {runSeconds.Value} 秒），设备可能已重启或更换。" +
                $"本地检测时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}；固件未提供真实重启时间。");
        }

        _lastRunSeconds = runSeconds.Value;
    }

    /// <summary>
    /// 固件不提供真实连接事件时间线。这里用设备每次返回的在线终端集合做本地检测：
    /// 新出现立即记录；消失连续两次轮询才判定离线，界面和日志明确标注本地检测时间。
    /// </summary>
    private void DetectTerminalPresence(DeviceManagementInfo info)
    {
        var current = (info.ClientList ?? new List<ClientEntry>())
            .Where(x => !string.IsNullOrWhiteSpace(x.Mac) && x.Status != "0" &&
                        !string.IsNullOrWhiteSpace(x.Ip) &&
                        !string.Equals(x.Ip, "NA", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Mac!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var item in current)
            _terminalNames[item.Key] = DisplayName(item.Key, item.Value.Name);

        if (!_terminalBaselineReady)
        {
            _terminalBaselineReady = true;
            _knownOnlineMacs.Clear();
            foreach (var mac in current.Keys) _knownOnlineMacs.Add(mac);
            _terminalMissingCounts.Clear();
            Log.Info(LogCategory.Device, "终端在线",
                $"已建立在线上线/离线检测基线：{current.Count} 台；本地检测时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}。");
            return;
        }

        foreach (var mac in current.Keys)
        {
            _terminalMissingCounts.Remove(mac);
            if (_knownOnlineMacs.Contains(mac)) continue;
            _knownOnlineMacs.Add(mac);
            var name = DisplayName(mac, current[mac].Name);
            var detectedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Log.Success(LogCategory.Device, "终端上线",
                $"检测到终端上线：{name}（{mac}）；本地检测时间 {detectedAt}。固件未提供真实接入事件时间，此时间不是设备记录的精确上线时间。");
            if (Settings.NotifyDeviceEvents)
                Notices.Raise(NoticeLevel.Success, $"terminal-online:{mac}", "终端上线",
                    $"{name}（{mac}）；本地检测时间 {detectedAt}。",
                    true, LogCategory.Device, "终端上线", logEvent: false);
        }

        foreach (var mac in _knownOnlineMacs.ToArray())
        {
            if (current.ContainsKey(mac))
                continue;
            var misses = _terminalMissingCounts.GetValueOrDefault(mac) + 1;
            _terminalMissingCounts[mac] = misses;
            if (misses < 2) continue;

            _knownOnlineMacs.Remove(mac);
            _terminalMissingCounts.Remove(mac);
            var name = _terminalNames.GetValueOrDefault(mac, "未知终端");
            var detectedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Log.Warn(LogCategory.Device, "终端离线",
                $"检测到终端离线：{name}（{mac}），连续 2 次终端列表未出现；本地检测时间 {detectedAt}。固件未提供真实离线事件时间。");
            if (Settings.NotifyDeviceEvents)
                Notices.Raise(NoticeLevel.Warning, $"terminal-offline:{mac}", "终端离线",
                    $"{name}（{mac}）；本地检测时间 {detectedAt}（连续 2 次未见）。",
                    true, LogCategory.Device, "终端离线", logEvent: false);
        }
    }

    private void RebuildNavigation()
    {
        var previousTitle = SelectedNav?.Title;
        NavItems.Clear();
        if (Device?.Kind == DeviceKind.C2000Pro)
        {
            NavItems.Add(new NavItem("概览", "\uE80F", Dashboard));
            NavItems.Add(new NavItem("蜂窝网络", "\uE704", Cellular2000));
            NavItems.Add(new NavItem("信号分析", "\uE9D2", Signal2000));
            NavItems.Add(new NavItem("锁频与小区", "\uE72E", Lock2000));
            NavItems.Add(new NavItem("SIM 卡管理", "\uE8C7", Sim2000));
            NavItems.Add(new NavItem("Wi-Fi 与局域网", "\uE701", Wifi2000));
            NavItems.Add(new NavItem("接入终端", "\uE7F4", Devices));
            NavItems.Add(new NavItem("短信", "\uE715", Sms2000));
            NavItems.Add(new NavItem("设备维护", "\uE713", Maintenance2000));
            NavItems.Add(new NavItem("通信日志", "\uE9D5", Logs));
            NavItems.Add(new NavItem("设置", "\uE713", SettingsPage, isSettings: true));
        }
        else
        {
            NavItems.Add(new NavItem("概览", "\uE80F", Dashboard));
            NavItems.Add(new NavItem("移动网络", "\uE704", Cellular));
            NavItems.Add(new NavItem("Wi-Fi", "\uE701", Wifi));
            NavItems.Add(new NavItem("局域网", "\uE968", Lan));
            NavItems.Add(new NavItem("设备与流量", "\uE7F4", Devices));
            NavItems.Add(new NavItem("电池监测", "\uE83F", Battery));
            NavItems.Add(new NavItem("系统与维护", "\uE713", System));
            NavItems.Add(new NavItem("通信日志", "\uE9D5", Logs));
            NavItems.Add(new NavItem("设置", "\uE713", SettingsPage, isSettings: true));
        }

        SelectedNav = NavItems.FirstOrDefault(n => n.Title == previousTitle)
                      ?? NavItems.FirstOrDefault(n => n.Title == Settings.LastPage && Settings.RememberLastPage)
                      ?? NavItems[0];
    }

    private void CheckTrafficHistorySave()
    {
        var error = History.LastSaveError;
        if (!string.IsNullOrWhiteSpace(error) &&
            !string.Equals(error, _lastTrafficSaveError, StringComparison.Ordinal))
        {
            _lastTrafficSaveError = error;
            Log.Error(LogCategory.Persistence, "流量历史", "流量历史保存失败：" + error);
        }
        else if (string.IsNullOrWhiteSpace(error) && _lastTrafficSaveError is not null)
        {
            _lastTrafficSaveError = null;
            Log.Success(LogCategory.Persistence, "流量历史", "流量历史保存已恢复正常。");
        }
    }

    private void CheckNotifications(StatusInfo status)
    {
        CheckSignalNotification(status);
        CheckTrafficThresholdNotification();
    }

    private void CheckSignalNotification(StatusInfo status)
    {
        if (!Settings.NotifyWeakSignal)
        {
            _signalAlerts.Reset();
            return;
        }

        var quality = J.Int(status.SignalQuality);
        var threshold = Settings.WeakSignalThresholdLevel;
        var evt = _signalAlerts.Observe(quality, threshold, DateTime.UtcNow);
        if (evt == SignalAlertKind.None) return;

        var c2000 = Device?.Kind == DeviceKind.C2000Pro;
        var rssiText = J.Int(status.Rssi) is { } rssi
            ? c2000 ? $"设备 RSRP {rssi} dBm" : $"设备 RSSI 原始值 {rssi} dBm"
            : c2000 ? "设备未返回 RSRP" : "设备未返回 RSSI";

        if (evt == SignalAlertKind.Weak)
        {
            Notices.Raise(NoticeLevel.Warning, "weak-signal", "信号持续偏弱",
                c2000
                    ? $"连续 3 次采样低于当前弱信号阈值档位（{quality}/5 ≤ {threshold}/5）。{rssiText}。" +
                      "档位由设备 RSRP 映射，原始 dBm 同时保留显示。"
                    : $"设备原始信号档位 {quality}/5（阈值 ≤ {threshold}/5，连续 3 次采样确认）。{rssiText}。" +
                      "此判断使用设备 signal_quality 原始档位，不换算工程页的基带编码值。",
                true, LogCategory.Network, "信号告警");
        }
        else
        {
            Notices.Raise(NoticeLevel.Success, "weak-signal-recovered", "信号已恢复",
                $"信号档位已连续 3 次回到 {quality}/5（阈值 ≤ {threshold}/5）。{rssiText}。",
                true, LogCategory.Network, "信号告警");
        }
    }

    private void CheckTrafficThresholdNotification()
    {
        if (!Settings.NotifyTrafficThreshold) return;

        var now = DateTime.Now;
        var period = Settings.TrafficThresholdScope;
        var threshold = Settings.TrafficThresholdGb;
        var key = TrafficAlertRules.DedupeKey(period, now, threshold);
        if (string.Equals(Settings.TrafficThresholdNotifiedKey, key, StringComparison.Ordinal)) return;

        var total = History.PeriodTotal(period, now);
        var bytes = total.Rx + total.Tx;
        if (!TrafficAlertRules.ReachedThreshold(bytes, threshold)) return;

        // 先写入去重键并落盘，确保软件重启后同一周期不会重复提醒。
        Settings.TrafficThresholdNotifiedKey = key;
        if (!PersistSettings())
            Log.Warn(LogCategory.Persistence, "数据持久化",
                "流量阈值去重状态保存失败：" + Settings.LastSaveError);

        var periodLabel = period == TrafficThresholdPeriod.Month ? "本月" : "当日";
        var coverage = History.UncapturedBytes > 0
            ? "本地历史存在未采集时段，实际设备用量可能更高。"
            : "本地采样覆盖连续。";

        Notices.Raise(NoticeLevel.Warning, "traffic-threshold", $"{periodLabel}流量达到阈值",
            $"本地已记录 {periodLabel}流量 {J.Bytes(bytes)}（↓ {J.Bytes(total.Rx)} · ↑ {J.Bytes(total.Tx)}），" +
            $"阈值 {threshold:0.##} GB。{coverage}",
            true, LogCategory.Data, "流量告警");
    }

    private async Task MaybeProbeNetworkQualityAsync()
    {
        if (!Settings.NetworkQualityAutoCheck || _qualityProbeRunning) return;
        if (DateTime.UtcNow < _nextQualityProbeUtc) return;

        _nextQualityProbeUtc = DateTime.UtcNow.AddMinutes(5);
        _qualityProbeRunning = true;
        Log.Info(LogCategory.Network, "网络质量", "开始自动网络质量检测。");
        try
        {
            var result = await NetworkQualityMonitor.ProbeAsync(Settings.Host);
            SettingsPage.ApplyNetworkQualityResult(result);
            var level = result.State switch
            {
                NetworkQualityState.Healthy => LogLevel.Success,
                NetworkQualityState.DetectionFailed => LogLevel.Warning,
                _ => LogLevel.Warning,
            };
            var stateChanged = result.State != _lastNetworkQualityState;
            _lastNetworkQualityState = result.State;
            Log.Add(level, LogCategory.Network, "网络质量",
                (stateChanged ? "检测结果（状态变化）：" : "检测结果：") + result.Summary);
        }
        catch (Exception ex)
        {
            SettingsPage.ApplyNetworkQualityFailure(ex.Message);
            Log.Warn(LogCategory.Network, "网络质量",
                "自动检测失败，不能据此判定断网：" + ex.Message);
        }
        finally
        {
            _qualityProbeRunning = false;
        }
    }
}
