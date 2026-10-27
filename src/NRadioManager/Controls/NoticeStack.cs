using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NRadioManager.Core;

namespace NRadioManager.Controls;

/// <summary>
/// 通知容器：负责"真正显示时开始计时"与"移除动画播放完再删除"两件事。
///
/// · 计时基准：条目被加入可视集合的这一刻才 MarkShown()，所以 5 秒是从显示开始算的；
/// · 移除动画：先把 IsRemoving 置位（XAML 里做淡出 + 高度收缩），动画时长到点后再真正从集合删除，
///   每条通知一个一次性的 DelayedInvoke，不用轮询、不建线程，删除或卸载时立刻释放；
/// · 关闭动画时直接删除，不做任何等待。
/// </summary>
public sealed class NoticeStack : ItemsControl
{
    private const int RemoveAnimationMs = 190;

    public NoticeStack()
    {
        ItemsSource = null;
    }

    protected override void OnItemsSourceChanged(System.Collections.IEnumerable oldValue, System.Collections.IEnumerable newValue)
    {
        base.OnItemsSourceChanged(oldValue, newValue);
        if (oldValue is INotifyCollectionChanged oldC) oldC.CollectionChanged -= OnItemsChanged;
        if (newValue is INotifyCollectionChanged newC) newC.CollectionChanged += OnItemsChanged;
        if (newValue is System.Collections.IEnumerable items) foreach (var n in items.OfType<Notice>()) Attach(n);
        InvalidateVisual();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var n in (e.NewItems ?? Array.Empty<object>()).OfType<Notice>()) Attach(n);
        foreach (var n in (e.OldItems ?? Array.Empty<object>()).OfType<Notice>()) Detach(n);
    }

    private void Attach(Notice notice)
    {
        if (notice.RemoveRequested is not null) return;
        notice.RemoveRequested = OnRemoveRequested;
        notice.ForceRemove = n => (ItemsSource as System.Collections.ObjectModel.ObservableCollection<Notice>)?.Remove(n);
        // 加入可视集合即视为"已显示"，计时此刻才开始
        notice.MarkShown();
    }

    private void Detach(Notice notice)
    {
        notice.RemoveRequested = null;
        notice.StopTimer();
    }

    private void OnRemoveRequested(Notice notice)
    {
        notice.StopTimer();
        var animationsOn = DataContext is ViewModels.MainViewModel vm && vm.Settings.AnimationsEnabled;

        if (!animationsOn)
        {
            Complete(notice);
            return;
        }

        notice.IsRemoving = true;
        // 一次性延迟（非轮询）：动画播完就删除，并释放计时与事件引用
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RemoveAnimationMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Complete(notice);
        };
        timer.Start();
    }

    private void Complete(Notice notice)
    {
        if (DataContext is ViewModels.MainViewModel vm) vm.Notices.CompleteRemoval(notice);
        else (ItemsSource as System.Collections.ObjectModel.ObservableCollection<Notice>)?.Remove(notice);
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (VisualParent is null && ItemsSource is System.Collections.IEnumerable items)
            foreach (var n in items.OfType<Notice>()) Detach(n);
    }
}
