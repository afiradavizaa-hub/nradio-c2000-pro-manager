using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NRadioManager.Core;

namespace NRadioManager.Controls;

public sealed record BarItem(string Key, string Label, long Rx, long Tx, bool FromLocalSampling = true)
{
    public long Total => Rx + Tx;
}

/// <summary>
/// 分组柱状图（自研绘制）：每根柱子分下载/上传两段，支持悬停查看数值、点击查看该时间段的明细。
/// 数据全部来自真实来源（设备接口或本地采样），无数据时明确提示，不绘制占位柱。
/// </summary>
public sealed class BarChart : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable), typeof(BarChart),
        new PropertyMetadata(null, OnItemsChanged));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(BarChart),
        new PropertyMetadata("暂无数据"));

    public IEnumerable? Items
    {
        get => (IEnumerable?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <summary>点击某根柱子时触发（携带该时间段的原始数据）。</summary>
    public event Action<BarItem>? BarSelected;

    private int _hoverIndex = -1;
    private int _slotCount;

    public BarChart()
    {
        // 主题切换时立即重绘：颜色是在 OnRender 里按主题资源解析的，
        // 没有这次失效就会保留旧背景（上一版柱状图"有时更新"正是碰巧由数据变化触发的）。
        NRadioManager.Infrastructure.ThemeManager.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => NRadioManager.Infrastructure.ThemeManager.ThemeChanged -= OnThemeChanged;
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) => { _hoverIndex = -1; InvalidateVisual(); };
        MouseLeftButtonUp += OnMouseUp;
        Cursor = Cursors.Hand;
    }

    internal int ThemeRefreshes { get; private set; }

    private void OnThemeChanged()
    {
        ThemeRefreshes++;
        InvalidateVisual();
    }

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (BarChart)d;
        if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= chart.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged now) now.CollectionChanged += chart.OnCollectionChanged;
        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private List<BarItem> Snapshot()
        => Items is null ? new List<BarItem>() : Items.Cast<BarItem>().ToList();

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 8 || h <= 8) return;

        var bg = Brush("B.ChartBg", Color.FromRgb(0x12, 0x18, 0x20));
        var grid = Brush("B.ChartGrid", Color.FromRgb(0x24, 0x2F, 0x3C));
        var textBrush = Brush("B.ChartText", Color.FromRgb(0x7C, 0x8B, 0x9C));
        var down = Brush("B.Down", Color.FromRgb(0x3B, 0x82, 0xF6));
        var up = Brush("B.Up", Color.FromRgb(0x22, 0xD3, 0xEE));

        dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, w, h), 8, 8);

        var items = Snapshot();
        // 底部留出独立的图例带（46px），保证图例与鼠标提示永远不会共用同一区域
        var plot = new Rect(58, 12, Math.Max(1, w - 70), Math.Max(1, h - 62));
        var legendY = h - 22;
        var gridPen = new Pen(grid, 1);
        var typeface = Typeface();

        void Label(string text, Point at, Brush? brush = null)
            => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    typeface, 10.5, brush ?? textBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip), at);

        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + plot.Height * i / 4.0;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        if (items.Count == 0)
        {
            Label(EmptyText, new Point(plot.Left + 4, plot.Top + plot.Height / 2 - 8));
            return;
        }

        var max = items.Max(i => Math.Max(i.Rx, i.Tx));
        if (max <= 0)
        {
            Label("该时间段内没有记录到流量（本地采样覆盖不到时不代表没有消耗）。",
                new Point(plot.Left + 4, plot.Top + plot.Height / 2 - 8));
            return;
        }
        max = (long)(max * 1.12);

        for (var i = 0; i <= 4; i++)
            Label(J.Bytes((long)(max * (4 - i) / 4.0)), new Point(4, plot.Top + plot.Height * i / 4.0 - 7));

        var slot = plot.Width / items.Count;
        _slotCount = items.Count;
        var barWidth = Math.Max(2, Math.Min(18, slot * 0.30));

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var cx = plot.Left + slot * (i + 0.5);
            var hRx = plot.Height * Math.Clamp((double)item.Rx / max, 0, 1);
            var hTx = plot.Height * Math.Clamp((double)item.Tx / max, 0, 1);
            var hovered = i == _hoverIndex;

            var rxRect = new Rect(cx - barWidth - 1, plot.Bottom - hRx, barWidth, hRx);
            var txRect = new Rect(cx + 1, plot.Bottom - hTx, barWidth, hTx);

            dc.DrawRoundedRectangle(down, hovered ? new Pen(textBrush, 1) : null, rxRect, 3, 3);
            dc.DrawRoundedRectangle(up, hovered ? new Pen(textBrush, 1) : null, txRect, 3, 3);

            // X 轴标签：柱子多时按间隔抽取，避免文字重叠
            var step = items.Count <= 12 ? 1 : (int)Math.Ceiling(items.Count / 10.0);
            if (i % step == 0 || hovered)
                Label(item.Label, new Point(cx - 12, plot.Bottom + 6), hovered ? textBrush : null);
        }

        // 图例固定在左下角独立带上；提示信息改为跟随鼠标的浮动层，二者不再争用同一行
        Label("■ 下载", new Point(plot.Left, legendY), down);
        Label("■ 上传", new Point(plot.Left + 58, legendY), up);

        if (_hoverIndex >= 0 && _hoverIndex < items.Count)
        {
            var it = items[_hoverIndex];
            var title = it.Label;
            var l1 = $"下载 {J.Bytes(it.Rx)}   上传 {J.Bytes(it.Tx)}";
            var l2 = $"合计 {J.Bytes(it.Total)}" + (it.FromLocalSampling ? "（本地采样）" : "（设备口径）");

            var tf = Typeface();
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var fTitle = new FormattedText(title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 12,
                Brush("B.Text", Colors.White), dpi);
            var fL1 = new FormattedText(l1, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 11.5,
                Brush("B.Text", Colors.White), dpi);
            var fL2 = new FormattedText(l2, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 11,
                textBrush, dpi);

            var boxW = Math.Max(fTitle.Width, Math.Max(fL1.Width, fL2.Width)) + 20;
            var boxH = fTitle.Height + fL1.Height + fL2.Height + 18;

            // 跟随鼠标：默认显示在光标上方，空间不足时翻到下方，并始终夹在控件内
            var cx = plot.Left + slot * (_hoverIndex + 0.5);
            var above = plot.Bottom - plot.Height * Math.Clamp(
                Math.Max(items[_hoverIndex].Rx, items[_hoverIndex].Tx) / (double)max, 0, 1) - boxH - 10;
            var boxX = Math.Clamp(cx - boxW / 2, 4, Math.Max(4, w - boxW - 4));
            var boxY = above < plot.Top ? plot.Bottom - boxH - 4 : above;
            boxY = Math.Clamp(boxY, 4, Math.Max(4, h - boxH - 4));

            var rect = new Rect(boxX, boxY, boxW, boxH);
            var tipBg = Brush("B.BgElevated", Color.FromRgb(0x13, 0x1A, 0x22));
            var border = new Pen(Brush("B.Border", Color.FromRgb(0x26, 0x31, 0x3E)), 1);
            dc.DrawRoundedRectangle(tipBg, border, rect, 8, 8);
            dc.DrawText(fTitle, new Point(rect.Left + 10, rect.Top + 7));
            dc.DrawText(fL1, new Point(rect.Left + 10, rect.Top + 7 + fTitle.Height + 2));
            dc.DrawText(fL2, new Point(rect.Left + 10, rect.Top + 7 + fTitle.Height + fL1.Height + 4));
        }
        else
        {
            Label("悬停查看数值 · 点击查看该时间段明细",
                new Point(Math.Max(plot.Left + 130, plot.Right - 240), legendY));
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var items = Snapshot();
        if (items.Count == 0) return;
        var plot = new Rect(58, 12, Math.Max(1, ActualWidth - 70), Math.Max(1, ActualHeight - 62));
        var pos = e.GetPosition(this);
        if (!plot.Contains(pos)) { if (_hoverIndex != -1) { _hoverIndex = -1; InvalidateVisual(); } return; }

        var slot = plot.Width / items.Count;
        var index = (int)Math.Floor((pos.X - plot.Left) / slot);
        index = Math.Clamp(index, 0, items.Count - 1);
        if (index != _hoverIndex) { _hoverIndex = index; InvalidateVisual(); }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        var items = Snapshot();
        if (_hoverIndex >= 0 && _hoverIndex < items.Count) BarSelected?.Invoke(items[_hoverIndex]);
    }

    private static Typeface Typeface()
        => new(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private Brush Brush(string key, Color fallback)
    {
        if (TryFindResource(key) is SolidColorBrush b) return b;
        return new SolidColorBrush(fallback);
    }
}
