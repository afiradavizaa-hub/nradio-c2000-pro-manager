using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NRadioManager.Infrastructure;
using NRadioManager.ViewModels;

namespace NRadioManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _placementRestored;

    public MainViewModel ViewModel => _vm;

    public MainWindow() : this(new MainViewModel())
    {
    }

    internal MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        InitializeComponent();
        DataContext = _vm;
        PwdBox.Password = _vm.Password;

        SourceInitialized += (_, _) => ApplyChrome();
        ThemeManager.ThemeChanged += ApplyChrome;
        _vm.SettingsChanged += ApplyAppearance;

        Loaded += async (_, _) =>
        {
            RestorePlacement();
            ApplyAppearance();
            await _vm.StartAsync();
        };
        Closing += OnClosing;
    }

    /// <summary>把 DWM 标题栏/边框颜色对齐到软件背景，消除浅色系统主题下的白边。</summary>
    private void ApplyChrome()
    {
        var color = WindowChromeHelper.SurfaceColor(this, ThemeManager.EffectiveDark);
        WindowChromeHelper.Apply(this, ThemeManager.EffectiveDark, color);
    }

    /// <summary>界面缩放：整体 LayoutTransform，保持布局真实（不是位图放大）。</summary>
    private void ApplyAppearance()
    {
        var scale = _vm.Settings.UiScale;
        ScaleHost.LayoutTransform = Math.Abs(scale - 1.0) < 0.001 ? null : new ScaleTransform(scale, scale);
    }

    private void RestorePlacement()
    {
        if (_placementRestored) return;
        _placementRestored = true;

        var s = _vm.Settings;
        if (!s.RememberWindowPlacement) return;

        if (s.WindowWidth is > 800 and < 8000 && s.WindowHeight is > 500 and < 8000)
        {
            Width = s.WindowWidth.Value;
            Height = s.WindowHeight.Value;
        }
        if (s.WindowLeft is not null && s.WindowTop is not null)
        {
            // 仅当位置仍落在可见屏幕内才恢复，避免拔掉外接显示器后窗口跑到屏幕外
            var vLeft = SystemParameters.VirtualScreenLeft;
            var vTop = SystemParameters.VirtualScreenTop;
            var vRight = vLeft + SystemParameters.VirtualScreenWidth;
            var vBottom = vTop + SystemParameters.VirtualScreenHeight;
            if (s.WindowLeft >= vLeft && s.WindowTop >= vTop &&
                s.WindowLeft < vRight - 100 && s.WindowTop < vBottom - 100)
            {
                Left = s.WindowLeft.Value;
                Top = s.WindowTop.Value;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
        }
        if (s.Maximized) WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var s = _vm.Settings;
        if (s.RememberWindowPlacement)
        {
            s.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                s.WindowLeft = Left;
                s.WindowTop = Top;
                s.WindowWidth = Width;
                s.WindowHeight = Height;
            }
        }
        s.LastPage = _vm.SelectedNav?.Title ?? "概览";
        try
        {
            // 关闭窗口时再次按界面集合顺序提交磁贴布局，避免鼠标捕获丢失或
            // 仍处于编辑态时退出导致只保存了旧配置。
            _vm.Dashboard.PersistTiles();
            _vm.Shutdown();
        }
        catch
        {
            // 退出流程中的异常不能阻止窗口关闭；日志底座会在自身内部保留写入错误。
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box) _vm.Password = box.Password;
    }

    /// <summary>
    /// 点击通知主体：磁贴提示在展开/折叠间切换，普通通知展开后暂停自动删除。
    /// 用代码处理而不是套一层 Button，避免与内部的删除按钮形成嵌套点击冲突。
    /// </summary>
    private void OnNoticeClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Core.Notice notice } && !notice.IsRemoving)
        {
            notice.Toggle();
            e.Handled = true;
        }
    }
}
