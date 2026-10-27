using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NRadioManager.Core;

namespace NRadioManager.Controls;

/// <summary>
/// 实时趋势图（自研绘制，不依赖第三方图表库）。
/// 数据来源为软件本机采样（TrafficSampler），可绘制上下行速率或信号强度。
/// 无有效数据时明确显示占位提示，不会画出虚假曲线。
///
/// 交互：鼠标靠近曲线时显示同一时间点的十字线与浮动提示。
/// 速率图的两条曲线共用同一时间轴，提示中同时给出下载与上传，颜色与图例一致。
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    private const double PlotLeft = 58;
    private const double PlotTop = 12;
    private const double PlotRightMargin = 10;
    private const double PlotBottomBand = 52;
    private const int MaxPoints = 300;
    private const double HoverDistance = 30;

    private int _hoverIndex = -1;
    private Point _mousePosition;

    public TrendChart()
    {
        // 与 BarChart 同一套机制：主题切换时同步重绘。
        NRadioManager.Infrastructure.ThemeManager.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => NRadioManager.Infrastructure.ThemeManager.ThemeChanged -= OnThemeChanged;
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) =>
        {
            if (_hoverIndex == -1) return;
            _hoverIndex = -1;
            InvalidateVisual();
        };
        Cursor = Cursors.Cross;
    }

    /// <summary>主题重绘次数（供自检断言"主题切换确实触达了图表"）。</summary>
    internal int ThemeRefreshes { get; private set; }

    private void OnThemeChanged()
    {
        ThemeRefreshes++;
        InvalidateVisual();
    }

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(ObservableCollection<SamplePoint>), typeof(TrendChart),
        new PropertyMetadata(null, OnPointsChanged));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(TrendChartMode), typeof(TrendChart),
        new PropertyMetadata(TrendChartMode.Speed, (d, _) => ((TrendChart)d).InvalidateVisual()));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(TrendChart),
        new PropertyMetadata(string.Empty, (d, _) => ((TrendChart)d).InvalidateVisual()));

    public ObservableCollection<SamplePoint>? Points
    {
        get => (ObservableCollection<SamplePoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public TrendChartMode Mode
    {
        get => (TrendChartMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TrendChart)d;
        if (e.OldValue is ObservableCollection<SamplePoint> old)
            old.CollectionChanged -= chart.OnCollectionChanged;
        if (e.NewValue is ObservableCollection<SamplePoint> now)
            now.CollectionChanged += chart.OnCollectionChanged;
        chart._hoverIndex = -1;
        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => InvalidateVisual();

    /// <summary>自检入口：走与鼠标事件完全相同的命中逻辑。</summary>
    internal void HoverAtForTest(Point point) => UpdateHover(point);

    internal int HoverIndexForTest => _hoverIndex;

    /// <summary>自检用：返回某个采样点在当前控件坐标中的绘制位置（与真实渲染完全同源）。</summary>
    internal Point SeriesPointForTest(int index, bool secondSeries)
    {
        var slice = Snapshot();
        var plot = MakePlotRect(ActualWidth, ActualHeight);
        var x = plot.Left + plot.Width * index / Math.Max(1, slice.Count - 1);
        if (Mode == TrendChartMode.Speed)
        {
            var max = slice.Where(p => p.HasSpeed)
                .Max(p => Math.Max(p.DownloadBytesPerSec, p.UploadBytesPerSec));
            var axisMax = max <= 1 ? 1024 : max * 1.15;
            var value = secondSeries ? slice[index].UploadBytesPerSec : slice[index].DownloadBytesPerSec;
            return new Point(x, YFor(value, 0, axisMax, plot));
        }

        return new Point(x, YFor(slice[index].RssiDbm ?? -120, -120, -40, plot));
    }

    /// <summary>当前悬停提示的纯文本内容，供自检核对时间与上下行映射。</summary>
    internal string? HoverSummaryForTest
    {
        get
        {
            var slice = Snapshot();
            if (_hoverIndex < 0 || _hoverIndex >= slice.Count) return null;
            return string.Join("\n", BuildHoverLines(slice, _hoverIndex).Select(x => x.Text));
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 4 || h <= 4) return;

        var bg = Brush("B.ChartBg", Color.FromRgb(0x12, 0x18, 0x20));
        dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, w, h), 8, 8);

        var plot = MakePlotRect(w, h);
        var gridPen = new Pen(Brush("B.ChartGrid", Color.FromRgb(0x24, 0x2F, 0x3C)), 1);
        var labelBrush = Brush("B.ChartText", Color.FromRgb(0x7C, 0x8B, 0x9C));
        var typeface = Typeface();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        FormattedText Text(string text, double size, Brush brush)
            => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, dpi);

        void Label(string text, Point at, Brush? brush = null)
            => dc.DrawText(Text(text, 10.5, brush ?? labelBrush), at);

        void LabelRight(string text, double right, double y, Brush? brush = null)
        {
            var ft = Text(text, 10.5, brush ?? labelBrush);
            dc.DrawText(ft, new Point(right - ft.Width, y));
        }

        var divisions = GridDivisions(plot);
        for (var i = 0; i <= divisions; i++)
        {
            var y = plot.Top + plot.Height * i / divisions;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        var points = Points;
        var emptyText = Mode == TrendChartMode.Speed
            ? "等待有效的速率采样…（本机采样，至少需要 2 个有效点）"
            : "设备未返回有效 RSSI，无法绘制信号趋势。";

        if (points is null || points.Count < 2)
        {
            Label(emptyText, new Point(plot.Left + 4, plot.Top + plot.Height / 2 - 8));
            return;
        }

        var slice = Snapshot();

        if (Mode == TrendChartMode.Speed)
            DrawSpeedChart(dc, slice, plot, h, w, Text, Label, LabelRight);
        else
            DrawSignalChart(dc, slice, plot, h, w, Text, Label, LabelRight);
    }

    private void DrawSpeedChart(DrawingContext dc, List<SamplePoint> slice, Rect plot, double h, double w,
        Func<string, double, Brush, FormattedText> text, Action<string, Point, Brush?> label,
        Action<string, double, double, Brush?> labelRight)
    {
        var valid = slice.Count(p => p.HasSpeed);
        if (valid < 2)
        {
            label("等待有效的速率采样…设备累计计数器尚未返回可用值。",
                new Point(plot.Left + 4, plot.Top + plot.Height / 2 - 8), null);
            return;
        }

        var max = slice.Where(p => p.HasSpeed)
            .Max(p => Math.Max(p.DownloadBytesPerSec, p.UploadBytesPerSec));
        var axisMax = max <= 1 ? 1024 : max * 1.15;

        var divisions = GridDivisions(plot);
        for (var i = 0; i <= divisions; i++)
            label(J.Rate((long)(axisMax * (divisions - i) / divisions)),
                new Point(4, plot.Top + plot.Height * i / divisions - 7), null);

        var down = ColorOf("B.Down", Color.FromRgb(0x3B, 0x82, 0xF6));
        var up = ColorOf("B.Up", Color.FromRgb(0x22, 0xD3, 0xEE));

        // 先画下载、再画上传；图例顺序和颜色与此完全一致。
        DrawSeries(dc, slice, plot, axisMax, p => p.HasSpeed ? p.DownloadBytesPerSec : null, down);
        DrawSeries(dc, slice, plot, axisMax, p => p.HasSpeed ? p.UploadBytesPerSec : null, up);

        var legendY = h - 20;
        LabelColored(dc, "● 下载 ↓", new Point(plot.Left, legendY), down, text);
        LabelColored(dc, "● 上传 ↑", new Point(plot.Left + 72, legendY), up, text);

        if (w >= 520)
        {
            labelRight(J.Unit switch
                {
                    TrafficUnit.MB => "速率单位：MB/s · 同一时间轴",
                    TrafficUnit.GB => "速率单位：GB/s · 同一时间轴",
                    _ => "速率单位随数值自动换算（B/s 起）· 同一时间轴",
                },
                plot.Right, legendY, null);
        }
        else if (w >= 300)
        {
            labelRight(J.Unit switch
            {
                TrafficUnit.MB => "单位 MB/s",
                TrafficUnit.GB => "单位 GB/s",
                _ => "自动换算单位",
            }, plot.Right, legendY, null);
        }

        DrawTimeLabels(dc, slice, plot, text);
        DrawHover(dc, slice, plot, w, h, text);
    }

    private void DrawSignalChart(DrawingContext dc, List<SamplePoint> slice, Rect plot, double h, double w,
        Func<string, double, Brush, FormattedText> text, Action<string, Point, Brush?> label,
        Action<string, double, double, Brush?> labelRight)
    {
        var valid = slice.Count(p => IsValidRssi(p.RssiDbm));
        if (valid < 2)
        {
            label("设备未返回有效 RSSI（缺失或超出 dBm 合理范围），无法绘制信号趋势。",
                new Point(plot.Left + 4, plot.Top + plot.Height / 2 - 8), null);
            return;
        }

        const double lo = -120, hi = -40;
        var divisions = GridDivisions(plot);
        for (var i = 0; i <= divisions; i++)
            label($"{hi - (hi - lo) * i / divisions:0} dBm",
                new Point(4, plot.Top + plot.Height * i / divisions - 7), null);

        var green = ColorOf("B.Success", Color.FromRgb(0x22, 0xC5, 0x5E));
        DrawSeries(dc, slice, plot, hi, p => IsValidRssi(p.RssiDbm) ? p.RssiDbm : null, green, lo, hi);

        var legendY = h - 20;
        LabelColored(dc, "● RSSI（设备上报，dBm）", new Point(plot.Left, legendY), green, text);
        if (w >= 520)
            labelRight("单位 dBm · 不换算基带原始编码", plot.Right, legendY, null);

        DrawTimeLabels(dc, slice, plot, text);
        DrawHover(dc, slice, plot, w, h, text);
    }

    private static void LabelColored(DrawingContext dc, string value, Point at, Color color,
        Func<string, double, Brush, FormattedText> text)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        dc.DrawText(text(value, 10.5, brush), at);
    }

    private void DrawTimeLabels(DrawingContext dc, List<SamplePoint> slice, Rect plot,
        Func<string, double, Brush, FormattedText> text)
    {
        if (slice.Count == 0 || plot.Width < 120) return;
        var brush = Brush("B.ChartText", Color.FromRgb(0x7C, 0x8B, 0x9C));
        var y = plot.Bottom + 5;

        var first = text(slice[0].Time.ToString("HH:mm:ss"), 10, brush);
        var last = text(slice[^1].Time.ToString("HH:mm:ss"), 10, brush);

        dc.DrawText(first, new Point(plot.Left, y));
        if (first.Width + last.Width + 24 < plot.Width)
            dc.DrawText(last, new Point(plot.Right - last.Width, y));

        if (slice.Count >= 3 && plot.Width > 330 && first.Width + last.Width + 90 < plot.Width)
        {
            var mid = text(slice[slice.Count / 2].Time.ToString("HH:mm:ss"), 10, brush);
            dc.DrawText(mid, new Point(plot.Left + (plot.Width - mid.Width) / 2, y));
        }
    }

    /// <summary>
    /// 绘制一条系列。selector 返回 null 表示该时间点没有有效值：断开线段，不补 0、不插值。
    /// </summary>
    private static void DrawSeries(DrawingContext dc, List<SamplePoint> slice, Rect rect, double? max,
        Func<SamplePoint, double?> selector, Color color, double? fixedMin = null, double? fixedMax = null)
    {
        var lo = fixedMin ?? 0;
        var hi = fixedMax ?? max ?? 1;
        if (hi <= lo) hi = lo + 1;

        var geometry = new StreamGeometry();
        var any = false;
        using (var ctx = geometry.Open())
        {
            var active = false;
            var firstX = 0.0;
            var lastX = 0.0;

            for (var i = 0; i < slice.Count; i++)
            {
                var value = selector(slice[i]);
                if (value is null)
                {
                    if (active)
                    {
                        ctx.LineTo(new Point(lastX, rect.Bottom), false, false);
                        ctx.LineTo(new Point(firstX, rect.Bottom), false, false);
                        active = false;
                    }
                    continue;
                }

                var x = rect.Left + rect.Width * i / Math.Max(1, slice.Count - 1);
                var y = rect.Bottom - rect.Height * (Math.Clamp(value.Value, lo, hi) - lo) / (hi - lo);
                var pt = new Point(x, y);
                if (!active)
                {
                    ctx.BeginFigure(pt, true, true);
                    firstX = x;
                    active = true;
                }
                else
                {
                    ctx.LineTo(pt, true, false);
                }
                lastX = x;
                any = true;
            }

            if (active)
            {
                ctx.LineTo(new Point(lastX, rect.Bottom), false, false);
                ctx.LineTo(new Point(firstX, rect.Bottom), false, false);
            }
        }

        if (!any) return;
        geometry.Freeze();

        var fill = new LinearGradientBrush(
            Color.FromArgb(80, color.R, color.G, color.B),
            Color.FromArgb(4, color.R, color.G, color.B), 90);
        fill.Freeze();
        var stroke = new Pen(new SolidColorBrush(color), 1.6);
        stroke.Freeze();
        dc.DrawGeometry(fill, stroke, geometry);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
        => UpdateHover(e.GetPosition(this));

    private bool UpdateHover(Point position)
    {
        var slice = Snapshot();
        if (slice.Count < 2)
        {
            if (_hoverIndex == -1) return false;
            _hoverIndex = -1;
            InvalidateVisual();
            return true;
        }

        var plot = MakePlotRect(ActualWidth, ActualHeight);
        if (!plot.Contains(position))
        {
            if (_hoverIndex == -1) return false;
            _hoverIndex = -1;
            InvalidateVisual();
            return true;
        }

        var index = (int)Math.Round((position.X - plot.Left) / Math.Max(1, plot.Width) * (slice.Count - 1));
        index = Math.Clamp(index, 0, slice.Count - 1);

        var distance = DistanceToNearestSeries(slice, index, plot, position);
        var next = distance <= HoverDistance ? index : -1;

        var changed = next != _hoverIndex || position != _mousePosition;
        _hoverIndex = next;
        if (next >= 0) _mousePosition = position;
        if (changed) InvalidateVisual();
        return changed;
    }

    private double DistanceToNearestSeries(List<SamplePoint> slice, int index, Rect plot, Point position)
    {
        var x = plot.Left + plot.Width * index / Math.Max(1, slice.Count - 1);
        var min = double.MaxValue;

        if (Mode == TrendChartMode.Speed)
        {
            var point = slice[index];
            if (!point.HasSpeed) return min;
            var max = slice.Where(p => p.HasSpeed)
                .Max(p => Math.Max(p.DownloadBytesPerSec, p.UploadBytesPerSec));
            var axisMax = max <= 1 ? 1024 : max * 1.15;
            min = Math.Min(min, Distance(x, YFor(point.DownloadBytesPerSec, 0, axisMax, plot), position));
            min = Math.Min(min, Distance(x, YFor(point.UploadBytesPerSec, 0, axisMax, plot), position));
        }
        else
        {
            var rssi = slice[index].RssiDbm;
            if (!IsValidRssi(rssi)) return min;
            min = Distance(x, YFor(rssi!.Value, -120, -40, plot), position);
        }

        return min;
    }

    private static double Distance(double x, double y, Point p)
        => Math.Sqrt((x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y));

    private static double YFor(double value, double lo, double hi, Rect plot)
        => plot.Bottom - plot.Height * (Math.Clamp(value, lo, hi) - lo) / (hi - lo);

    private List<(string Text, Color Color)> BuildHoverLines(List<SamplePoint> slice, int index)
    {
        var result = new List<(string Text, Color Color)>();
        var textColor = ColorOf("B.Text", Colors.White);
        var faint = ColorOf("B.TextMuted", Color.FromRgb(0x8B, 0x9A, 0xAB));
        var point = slice[index];

        result.Add((point.Time.ToString("yyyy-MM-dd HH:mm:ss"), textColor));

        if (Mode == TrendChartMode.Speed)
        {
            var down = ColorOf("B.Down", Color.FromRgb(0x3B, 0x82, 0xF6));
            var up = ColorOf("B.Up", Color.FromRgb(0x22, 0xD3, 0xEE));
            result.Add(($"↓ 下载　{J.Rate((long)Math.Round(point.DownloadBytesPerSec))}", down));
            result.Add(($"↑ 上传　{J.Rate((long)Math.Round(point.UploadBytesPerSec))}", up));
            result.Add(("本机采样（计数器差分）", faint));
        }
        else
        {
            var green = ColorOf("B.Success", Color.FromRgb(0x22, 0xC5, 0x5E));
            result.Add(($"RSSI　{point.RssiDbm} dBm", green));
            result.Add(("设备上报原值", faint));
        }

        return result;
    }

    private void DrawHover(DrawingContext dc, List<SamplePoint> slice, Rect plot, double w, double h,
        Func<string, double, Brush, FormattedText> textFactory)
    {
        if (_hoverIndex < 0 || _hoverIndex >= slice.Count) return;

        var point = slice[_hoverIndex];
        var x = plot.Left + plot.Width * _hoverIndex / Math.Max(1, slice.Count - 1);
        var guide = new Pen(Brush("B.TextFaint", Color.FromRgb(0x64, 0x74, 0x8B)), 1)
        {
            DashStyle = new DashStyle(new double[] { 3, 3 }, 0),
        };
        guide.Freeze();
        dc.DrawLine(guide, new Point(x, plot.Top), new Point(x, plot.Bottom));

        if (Mode == TrendChartMode.Speed && point.HasSpeed)
        {
            var max = slice.Where(p => p.HasSpeed)
                .Max(p => Math.Max(p.DownloadBytesPerSec, p.UploadBytesPerSec));
            var axisMax = max <= 1 ? 1024 : max * 1.15;
            DrawHoverDot(dc, x, YFor(point.DownloadBytesPerSec, 0, axisMax, plot), ColorOf("B.Down", Color.FromRgb(0x3B, 0x82, 0xF6)));
            DrawHoverDot(dc, x, YFor(point.UploadBytesPerSec, 0, axisMax, plot), ColorOf("B.Up", Color.FromRgb(0x22, 0xD3, 0xEE)));
        }
        else if (Mode == TrendChartMode.Signal && IsValidRssi(point.RssiDbm))
        {
            DrawHoverDot(dc, x, YFor(point.RssiDbm!.Value, -120, -40, plot),
                ColorOf("B.Success", Color.FromRgb(0x22, 0xC5, 0x5E)));
        }

        var lines = BuildHoverLines(slice, _hoverIndex);
        var formatted = lines.Select(l =>
        {
            var brush = new SolidColorBrush(l.Color);
            brush.Freeze();
            return textFactory(l.Text, l == lines[0] ? 12 : 11.5, brush);
        }).ToList();

        var contentWidth = formatted.Max(f => f.Width);
        var contentHeight = formatted.Sum(f => f.Height) + Math.Max(0, formatted.Count - 1) * 3;
        var boxW = Math.Min(Math.Max(170, contentWidth + 20), Math.Max(40, w - 8));
        var boxH = contentHeight + 16;

        // 跟随鼠标：默认在右上方；空间不足时翻到左侧/下方，并始终夹在控件内部。
        var boxX = _mousePosition.X + 14;
        if (boxX + boxW > w - 4) boxX = _mousePosition.X - boxW - 14;
        boxX = Math.Clamp(boxX, 4, Math.Max(4, w - boxW - 4));

        var boxY = _mousePosition.Y - boxH - 14;
        if (boxY < 4) boxY = _mousePosition.Y + 16;
        boxY = Math.Clamp(boxY, 4, Math.Max(4, h - boxH - 4));

        var rect = new Rect(boxX, boxY, boxW, boxH);
        var tipBg = Brush("B.BgElevated", Color.FromRgb(0x13, 0x1A, 0x22));
        var border = new Pen(Brush("B.Border", Color.FromRgb(0x26, 0x31, 0x3E)), 1);
        dc.DrawRoundedRectangle(tipBg, border, rect, 8, 8);

        var y = rect.Top + 8;
        for (var i = 0; i < formatted.Count; i++)
        {
            dc.DrawText(formatted[i], new Point(rect.Left + 10, y));
            y += formatted[i].Height + 3;
        }
    }

    private static void DrawHoverDot(DrawingContext dc, double x, double y, Color color)
    {
        var fill = new SolidColorBrush(color);
        fill.Freeze();
        var pen = new Pen(BrushStatic(Colors.White), 1.2);
        pen.Freeze();
        dc.DrawEllipse(fill, pen, new Point(x, y), 3.2, 3.2);
    }

    private static Brush BrushStatic(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    private List<SamplePoint> Snapshot()
    {
        var points = Points;
        if (points is null || points.Count == 0) return new List<SamplePoint>();
        var take = Math.Min(points.Count, MaxPoints);
        var slice = new List<SamplePoint>(take);
        for (var i = points.Count - take; i < points.Count; i++) slice.Add(points[i]);
        return slice;
    }

    private static Rect MakePlotRect(double w, double h)
        => new(PlotLeft, PlotTop, Math.Max(1, w - PlotLeft - PlotRightMargin),
            Math.Max(1, h - PlotTop - PlotBottomBand));

    private static int GridDivisions(Rect plot) => plot.Height < 76 ? 2 : 4;

    private static bool IsValidRssi(int? rssi) => rssi is >= -140 and <= -20;

    private static Typeface Typeface()
        => new(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private Brush Brush(string key, Color fallback)
        => TryFindResource(key) as SolidColorBrush ?? BrushStatic(fallback);

    private Color ColorOf(string key, Color fallback)
        => TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;
}

public enum TrendChartMode
{
    Speed,
    Signal,
}
