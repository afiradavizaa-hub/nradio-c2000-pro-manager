using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using NRadioManager.Infrastructure;

namespace NRadioManager.Core;

public enum NoticeLevel { Info, Success, Warning, Error }

/// <summary>
/// 通知类型决定超时后的行为：
///   TileEdit —— 磁贴编辑提示：5 秒后**折叠**为紧凑状态（内容保留），可再次点击展开；
///   Normal   —— 普通操作反馈：5 秒后**自动删除**（用户展开阅读时暂停删除）；
///   Persistent —— 需要长期保留的记录（当前仅通信日志使用独立列表，不进入这里）。
/// </summary>
public enum NoticeKind { TileEdit, Normal, Persistent }

/// <summary>
/// 单条通知：自带一次性计时器（到点触发一次后即释放），不使用高频轮询。
/// 计时从 ShownAt（真正显示到界面上）开始，而不是从创建开始。
/// </summary>
public sealed class Notice : ObservableObject
{
    private readonly TimeSpan _timeout;
    private DispatcherTimer? _timer;

    public Notice(NoticeKind kind, NoticeLevel level, string title, string message, TimeSpan timeout, bool removable = true)
    {
        Kind = kind;
        Level = level;
        Title = title;
        Message = message;
        IsRemovable = removable && kind != NoticeKind.Persistent;
        _timeout = timeout;
        IsCompact = false;
        RemoveCommand = new RelayCommand(_ => RequestRemove());
        ToggleCommand = new RelayCommand(_ => Toggle());
        CollapseCommand = new RelayCommand(_ => SetCompact(true));
    }

    public Guid Id { get; } = Guid.NewGuid();
    public NoticeKind Kind { get; }
    public NoticeLevel Level { get; }
    public string Title { get; }
    public string Message { get; }
    public DateTime CreatedAt { get; } = DateTime.Now;

    /// <summary>真正显示到界面上的时间点（计时基准）。</summary>
    public DateTime? ShownAt { get; private set; }
    public bool IsPersistent => Kind == NoticeKind.Persistent;
    public bool IsRemovable { get; }

    private bool _isCompact;
    /// <summary>是否处于折叠（紧凑）状态——磁贴编辑提示专用。</summary>
    public bool IsCompact
    {
        get => _isCompact;
        private set { if (Set(ref _isCompact, value)) OnPropertyChanged(nameof(IsExpandedContent)); }
    }

    public bool IsExpandedContent => !_isCompact;

    private bool _isExpanded;
    /// <summary>用户是否主动展开阅读（展开后暂停自动删除/折叠，避免"正在读就消失"）。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        private set => Set(ref _isExpanded, value);
    }

    private bool _hasTimedOut;
    public bool HasTimedOut
    {
        get => _hasTimedOut;
        private set => Set(ref _hasTimedOut, value);
    }

    private bool _isRemoving;
    /// <summary>正在播放移除动画（界面据此淡出并收缩高度）。</summary>
    public bool IsRemoving
    {
        get => _isRemoving;
        internal set => Set(ref _isRemoving, value);
    }

    public bool CanToggle => Kind == NoticeKind.TileEdit;
    public string TimeText => (ShownAt ?? CreatedAt).ToString("HH:mm:ss");

    /// <summary>由通知容器在真正显示时调用，启动一次性计时。</summary>
    internal void MarkShown()
    {
        if (ShownAt is not null || IsPersistent) return;
        ShownAt = DateTime.Now;
        OnPropertyChanged(nameof(TimeText));

        _timer = new DispatcherTimer { Interval = _timeout };
        _timer.Tick += (_, _) =>
        {
            StopTimer();
            HandleTimeout();
        };
        _timer.Start();
    }

    private void HandleTimeout()
    {
        HasTimedOut = true;

        // 阅读保护：用户已展开阅读时不做任何自动处理，交给用户自己折叠或删除
        if (IsExpanded) return;

        if (Kind == NoticeKind.TileEdit) SetCompact(true);
        else RequestRemove();
    }

    /// <summary>点击通知主体：磁贴提示在展开/折叠间切换；普通通知展开后暂停自动删除。</summary>
    public void Toggle()
    {
        if (Kind == NoticeKind.TileEdit)
        {
            SetCompact(!IsCompact);
            IsExpanded = !IsCompact;
            if (IsExpanded) StopTimer();   // 用户主动展开后不再被原来的 5 秒计时收起
            return;
        }

        IsExpanded = !IsExpanded;
        if (IsExpanded) StopTimer();
    }

    private void SetCompact(bool compact)
    {
        IsCompact = compact;
        if (compact && HasTimedOut) StopTimer();
    }

    /// <summary>请求移除：由界面播放动画后再真正删除（无界面承载时直接删除）。</summary>
    public void RequestRemove()
    {
        StopTimer();
        if (RemoveRequested is null) ForceRemove?.Invoke(this);
        else RemoveRequested(this);
    }

    /// <summary>连计时一并释放，避免残留回调。</summary>
    internal void StopTimer()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer = null;
    }

    internal Action<Notice>? RemoveRequested;
    internal Action<Notice>? ForceRemove;

    /// <summary>自检用：立刻走一次超时逻辑（避免测试真的等 5 秒）。</summary>
    internal void TriggerTimeoutForTest()
    {
        StopTimer();
        HandleTimeout();
    }

    public ICommand RemoveCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand CollapseCommand { get; }
}

/// <summary>
/// 通知中心：全软件唯一的临时通知出口（所有页面的操作反馈都汇总到这里）。
/// 通知本身只在本进程内显示；每条通知同时写入通信日志（ActivityLog），
/// 通知生命周期清理不会删除已持久化的运行事件。
/// </summary>
public sealed class NoticeCenter : ObservableObject
{
    /// <summary>同时展示的最大条数，超出时优先移除最旧的、未被用户展开的普通通知。</summary>
    private const int MaxVisible = 5;
    private readonly ActivityLog? _activityLog;

    /// <summary>正常超时时间；自检可临时改小以便验证生命周期。</summary>
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    public ObservableCollection<Notice> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    public ICommand ClearAllCommand { get; }

    public NoticeCenter(ActivityLog? activityLog = null)
    {
        _activityLog = activityLog;
        ClearAllCommand = new RelayCommand(_ => ClearAll(), _ => Items.Any(n => n.IsRemovable));
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            (ClearAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
        };
    }

    public Notice Raise(
        NoticeLevel level,
        string title,
        string message,
        NoticeKind kind = NoticeKind.Normal,
        LogCategory logCategory = LogCategory.Notification,
        string logSource = "通知中心",
        bool logEvent = true)
    {
        if (logEvent)
        {
            _activityLog?.Add(ToLogLevel(level), logCategory, logSource,
                $"通知：{title}", message);
        }

        var notice = new Notice(kind, level, title, message, Timeout);
        Add(notice);
        return notice;
    }

    public Notice Raise(
        NoticeLevel level,
        string key,
        string title,
        string message,
        bool enabled,
        LogCategory logCategory = LogCategory.Notification,
        string logSource = "通知中心",
        bool logEvent = true)
    {
        _ = key;   // 去重键保留在调用方语义里；同一事件由调用方控制触发频率
        return enabled
            ? Raise(level, title, message, NoticeKind.Normal, logCategory, logSource, logEvent)
            : null!;
    }

    /// <summary>磁贴编辑提示：5 秒后折叠而不是删除。</summary>
    public Notice RaiseTileEdit(string title, string message)
        => Raise(NoticeLevel.Info, title, message, NoticeKind.TileEdit,
            LogCategory.Application, "概览磁贴");

    private void Add(Notice notice)
    {
        // 兜底删除路径：即使当前没有可视容器（例如页面尚未渲染），删除也必须生效
        notice.ForceRemove = n => CompleteRemoval(n);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void Do()
        {
            Items.Insert(0, notice);
            TrimOverflow();
        }
        if (dispatcher is null || dispatcher.CheckAccess()) Do();
        else dispatcher.Invoke(Do);
    }

    /// <summary>超出上限时移除最旧的、未被用户展开的普通通知（不打断正在阅读的内容）。</summary>
    private void TrimOverflow()
    {
        while (Items.Count > MaxVisible)
        {
            var victim = Items.LastOrDefault(n => !n.IsExpanded && n.IsRemovable && n.Kind == NoticeKind.Normal)
                         ?? Items.LastOrDefault(n => !n.IsExpanded && n.IsRemovable);
            if (victim is null) break;
            Items.Remove(victim);
            victim.StopTimer();
        }
    }

    /// <summary>清除全部可移除通知（不影响通信日志、设备统计与配置）。</summary>
    public void ClearAll()
    {
        // 交给界面播放逐条移除动画；没有界面承载时 Notice.RequestRemove 会直接删除
        foreach (var n in Items.Where(n => n.IsRemovable).ToList()) n.RequestRemove();
    }

    /// <summary>容器完成移除动画后回调，真正从集合中删除并释放计时。</summary>
    public void CompleteRemoval(Notice notice)
    {
        notice.StopTimer();
        if (Items.Contains(notice)) Items.Remove(notice);
    }

    public void Clear()
    {
        foreach (var n in Items.ToList()) n.StopTimer();
        Items.Clear();
    }

    private static LogLevel ToLogLevel(NoticeLevel level) => level switch
    {
        NoticeLevel.Success => LogLevel.Success,
        NoticeLevel.Warning => LogLevel.Warning,
        NoticeLevel.Error => LogLevel.Error,
        _ => LogLevel.Info,
    };
}
