using NRadioManager.Core;
using NRadioManager.Infrastructure;

namespace NRadioManager.ViewModels;

/// <summary>
/// 各功能模块的基类：统一持有宿主（连接与应用上下文）、统一的忙碌状态与错误呈现。
/// 所有页面都通过它访问设备，避免每个模块重复实现登录、超时与异常处理。
/// </summary>
public abstract class ModuleViewModel : ObservableObject
{
    protected ModuleViewModel(MainViewModel host, string title, string subtitle)
    {
        Host = host;
        Title = title;
        Subtitle = subtitle;
    }

    protected MainViewModel Host { get; }
    protected ActivityLog Log => Host.Log;

    public string Title { get; }
    public string Subtitle { get; }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle)); }
    }

    public bool IsIdle => !IsBusy;

    /// <summary>
    /// 统一通知出口：模块提示不再各自渲染横幅，而是交给全局通知中心，
    /// 由它统一负责 5 秒生命周期、折叠、删除与动画（避免各页面重复且行为不一致的实现）。
    /// 保留这个属性是为了兼容既有代码里的赋值写法；界面上的模块横幅已不再显示。
    /// </summary>
    public string? Message
    {
        get => null;
        protected set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            Host.Notices.Raise(IsError ? NoticeLevel.Error : NoticeLevel.Info, Title, value!);
        }
    }

    public bool HasMessage => false;

    private bool _isError;
    public bool IsError
    {
        get => _isError;
        protected set => Set(ref _isError, value);
    }

    /// <summary>页面首次进入或刷新时调用；未连接设备时安全跳过。</summary>
    public async Task RefreshAsync()
    {
        if (!Host.IsConnected) return;
        try
        {
            await RunAsync(() => LoadAsync());
        }
        catch
        {
            // RunAsync 内部已完成错误呈现
        }
    }

    protected abstract Task LoadAsync();

    /// <summary>执行一次设备交互，统一处理忙碌状态、错误映射与结果提示。</summary>
    protected async Task RunAsync(Func<Task> action, string? successMessage = null, bool reloadAfter = true)
    {
        if (Host.Api is null)
        {
            ShowMessage("尚未连接设备。", isError: true);
            return;
        }

        IsBusy = true;
        try
        {
            await action();
            if (successMessage is not null)
            {
                IsError = false;
                Log.Success(LogCategory.Other, Title, successMessage + "；已显示成功通知。");
                Host.Notices.Raise(NoticeLevel.Success, Title, successMessage,
                    logCategory: LogCategory.Other, logSource: Title, logEvent: false);
            }
            if (reloadAfter) await LoadAsync();
        }
        catch (RouterException ex)
        {
            IsError = true;
            Log.Error(LogCategory.Other, Title,
                $"{ex.UserMessage}（接口 {ex.Endpoint ?? "-"}）；已显示错误通知。",
                ActivityLog.Sanitize(ex.ToString()));
            Host.Notices.Raise(NoticeLevel.Error, Title, ex.UserMessage,
                logCategory: LogCategory.Other, logSource: Title, logEvent: false);
            if (ex.Kind is RouterErrorKind.SessionExpired or RouterErrorKind.Network or RouterErrorKind.Timeout)
                Host.ReportConnectionProblem(ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            var message = "发生未预期的错误：" + ex.Message;
            Log.Error(LogCategory.Other, Title, message + "；已显示错误通知。",
                ActivityLog.Sanitize(ex.ToString()));
            Host.Notices.Raise(NoticeLevel.Error, Title, message,
                logCategory: LogCategory.Other, logSource: Title, logEvent: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected void ShowMessage(string message, bool isError)
    {
        IsError = isError;
        Message = message;
    }

    public void ClearMessage() => Message = null;
}
