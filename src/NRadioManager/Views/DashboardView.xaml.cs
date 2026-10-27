using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NRadioManager.Controls;
using NRadioManager.ViewModels;

namespace NRadioManager.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    /// <summary>点击柱状图的时间点：把该时段的各终端明细填到统计磁贴下方。</summary>
    private void OnBarSelected(BarItem item)
    {
        if (DataContext is DashboardViewModel vm) vm.ShowDetail(item);
    }

    /// <summary>「完成」按钮：退出编辑模式（停止摇摆、保存布局）。</summary>
    private void OnDoneEditing(object sender, RoutedEventArgs e)
    {
        if (DataContext is DashboardViewModel vm)
        {
            vm.App.Trace("OnDoneEditing（Click 路径）被调用");
            vm.FinishEditingCommand.Execute(null);
        }
        else FindGrid(this)?.ExitEditing();
    }

    private static TileGrid? FindGrid(DependencyObject root)
    {
        if (root is TileGrid g) return g;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindGrid(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }
}
