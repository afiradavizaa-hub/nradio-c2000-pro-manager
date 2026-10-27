using System.Windows;
using System.Windows.Threading;

namespace NRadioManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 任何未捕获的界面线程异常都不应让软件直接崩溃，统一提示并记录
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        if (e.Args.Any(a => string.Equals(a, "--ui-smoke", StringComparison.OrdinalIgnoreCase)))
        {
            // 自动化界面自检：实例化全部页面并捕获 XAML/绑定错误，然后退出。
            // 用于开发期验证，不影响正常使用。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var live = e.Args.Any(a => string.Equals(a, "--live", StringComparison.OrdinalIgnoreCase));
            var report = UiSmokeTest.Run(live);
            try
            {
                var path = System.IO.Path.Combine(AppContext.BaseDirectory, "ui-smoke-report.txt");
                System.IO.File.WriteAllText(path, report);
            }
            catch
            {
                // 报告落盘失败不影响结论输出
            }
            Shutdown(report.StartsWith("FAIL", StringComparison.Ordinal) ? 1 : 0);
            return;
        }

        new MainWindow().Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 同一次运行只提示一次：避免连环弹窗把用户淹没，也避免在异常状态下继续跑出更多错
        if (_errorShown)
        {
            e.Handled = true;
            return;
        }
        _errorShown = true;

        var vm = (MainWindow as MainWindow)?.ViewModel;
        try
        {
            vm?.Log.Error(Core.LogCategory.Application, "未捕获异常",
                $"{e.Exception.GetType().Name}: {e.Exception.Message}",
                Core.ActivityLog.Sanitize(e.Exception.ToString()));
            vm?.Log.Flush(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 日志失败不能引发第二次异常。
        }

        // 未处理异常另留一份现场文件，便于在没有调试器的机器上定位。
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NRadioManager");
            System.IO.Directory.CreateDirectory(dir);
            var safe = Core.ActivityLog.Sanitize(e.Exception.ToString());
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(dir, "last-error.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 未处理的界面异常\r\n{safe}\r\n");
        }
        catch { }

        MessageBox.Show(
            "软件遇到未处理的错误：\n\n" + e.Exception.Message +
            "\n\n错误详情已写入 %APPDATA%\\NRadioManager\\last-error.txt。为避免状态错乱，软件将退出，请重新打开。",
            "NRadio C2000 Pro 高级管理器", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
        Shutdown(1);
    }

    private bool _errorShown;

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            (MainWindow as MainWindow)?.ViewModel.Log.Flush(TimeSpan.FromSeconds(2));
        }
        catch { }
        base.OnExit(e);
    }
}
