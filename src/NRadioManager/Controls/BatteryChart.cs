using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NRadioManager.Core;

namespace NRadioManager.Controls;

/// <summary>
/// 电池电量趋势图（自研绘制，不依赖第三方图表库）。
///
/// 与 TrendChart 的差别：电池历史可能跨越 7 天、采样点远多于 300 个，且必须按真实时间轴绘制，
/// 不能把“几小时没采样”压缩成相邻两点。因此这里复用 TrendChart 的配色、悬停卡片与主题机制，
/// 但使用时间坐标，并把缺失、断连、重启区间显式断开和着色。
///
/// 交互：悬停查看采样详情；左键拖动平移；滚轮缩放；双击恢复当前时间范围。
/// </summary>
public sealed class BatteryChart : FrameworkElement
{
    private const double PlotLeft = 50;
    private const double PlotTop = 14;
    private const double PlotRightMargin = 12;
    private const double PlotBottomBand = 46;
    private static readonly TimeSpan MinViewSpan = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxViewSpan = TimeSpan.FromDays(31);

    private int _hoverIndex = -1;
    private Point _mousePosition;
    private List<BatterySample> _hoverable = new();
    private IReadOnlyList<BatterySample>? _cacheSource;
    private DateTime _cacheStart;
    private DateTime _cacheEnd;
    private List<BatterySample> _cacheInRange = new();
    private int _lastGapCount;
    private int _lastSegmentCount;
    private int _lastVisibleCount;

    private bool _dragging;
    private Point _dragStart;
    private DateTime _dragStartViewStart;
    private DateTime _dragStartViewEnd;

    public BatteryChart()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        NRadioManager.Infrastructure.ThemeManager.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => NRadioManager.Infrastructure.ThemeManager.ThemeChanged -= OnThemeChanged;
        Loaded += (_, _) => EnsureView(resetIfEmpty: true);
        SizeChanged += (_, _) => InvalidateVisual();
        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseWheel += OnMouseWheel;
    }

    internal int ThemeRefreshes { get; private set; }

    private void OnThemeChanged()
    {
        ThemeRefreshes++;
        InvalidateVisual();
    }

    // ---------------- 依赖属性 ----------------

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IReadOnlyList<BatterySample>), typeof(BatteryChart),
        new PropertyMetadata(null, (d, _) => ((BatteryChart)d).OnSamplesChanged()));

    public static readonly DependencyProperty RangeSecondsProperty = DependencyProperty.Register(
        nameof(RangeSeconds), typeof(int), typeof(BatteryChart),
        new PropertyMetadata(3600, (d, _) => ((BatteryChart)d).OnRangeChanged()));

    public static readonly DependencyProperty GapThresholdSecondsProperty = DependencyProperty.Register(
        nameof(GapThresholdSeconds), typeof(int), typeof(BatteryChart),
        new PropertyMetadata(300, (d, _) => ((BatteryChart)d).InvalidateVisual()));

    public static readonly DependencyProperty ViewStartUtcProperty = DependencyProperty.Register(
        nameof(ViewStartUtc), typeof(DateTime), typeof(BatteryChart),
        new FrameworkPropertyMetadata(DateTime.MinValue,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((BatteryChart)d).InvalidateVisual()));

    public static readonly DependencyProperty ViewEndUtcProperty = DependencyProperty.Register(
        nameof(ViewEndUtc), typeof(DateTime), typeof(BatteryChart),
        new FrameworkPropertyMetadata(DateTime.MinValue,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((BatteryChart)d).InvalidateVisual()));

    public static readonly DependencyProperty FollowLatestProperty = DependencyProperty.Register(
        nameof(FollowLatest), typeof(bool), typeof(BatteryChart),
        new PropertyMetadata(true, (d, _) =>
        {
            var chart = (BatteryChart)d;
            if (chart.FollowLatest) chart.AdvanceToNow(chart.CurrentSpan());
            chart.InvalidateVisual();
        }));

    public IReadOnlyList<BatterySample>? Samples
    {
        get => (IReadOnlyList<BatterySample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public int RangeSeconds
    {
        get => (int)GetValue(RangeSecondsProperty);
        set => SetValue(RangeSecondsProperty, value);
    }

    public int GapThresholdSeconds
    {
        get => (int)GetValue(GapThresholdSecondsProperty);
        set => SetValue(GapThresholdSecondsProperty, value);
    }

    public DateTime ViewStartUtc
    {
        get => (DateTime)GetValue(ViewStartUtcProperty);
        set => SetValue(ViewStartUtcProperty, value);
    }

    public DateTime ViewEndUtc
    {
        get => (DateTime)GetValue(ViewEndUtcProperty);
        set => SetValue(ViewEndUtcProperty, value);
    }

    public bool FollowLatest
    {
        get => (bool)GetValue(FollowLatestProperty);
        set => SetValue(FollowLatestProperty, value);
    }

    private void OnSamplesChanged()
    {
        _hoverIndex = -1;
        _cacheSource = null;
        if (FollowLatest) AdvanceToNow(CurrentSpan());
        else InvalidateVisual();
    }

    private void OnRangeChanged()
    {
        FollowLatest = true;
        AdvanceToNow(TimeSpan.FromSeconds(Math.Clamp(RangeSeconds, 300, (int)MaxViewSpan.TotalSeconds)));
    }

    private TimeSpan CurrentSpan()
    {
        var span = ViewEndUtc - ViewStartUtc;
        if (span < MinViewSpan || span > MaxViewSpan)
            span = TimeSpan.FromSeconds(Math.Clamp(RangeSeconds, 300, (int)MaxViewSpan.TotalSeconds));
        return span;
    }

    private void EnsureView(bool resetIfEmpty)
    {
        if (resetIfEmpty && (ViewStartUtc == DateTime.MinValue || ViewEndUtc == DateTime.MinValue))
            AdvanceToNow(TimeSpan.FromSeconds(Math.Clamp(RangeSeconds, 300, (int)MaxViewSpan.TotalSeconds)));
        else if (FollowLatest)
            AdvanceToNow(CurrentSpan());
    }

    private void AdvanceToNow(TimeSpan span)
    {
        var now = DateTime.UtcNow;
        var end = now;
        var start = end - span;
        SetView(start, end);
    }

    private void SetView(DateTime start, DateTime end)
    {
        var span = end - start;
        if (span < MinViewSpan) span = MinViewSpan;
        if (span > MaxViewSpan) span = MaxViewSpan;
        var now = DateTime.UtcNow;
        if (end > now) end = now;
        var earliest = now - MaxViewSpan;
        if (end < earliest + span) end = earliest + span;
        start = end - span;
        if (start < earliest)
        {
            start = earliest;
            end = start + span;
        }
        SetCurrentValue(ViewStartUtcProperty, start);
        SetCurrentValue(ViewEndUtcProperty, end);
        InvalidateVisual();
    }

    /// <summary>恢复当前选择范围，并重新跟随最新采样。</summary>
    public void ResetView()
    {
        FollowLatest = true;
        AdvanceToNow(TimeSpan.FromSeconds(Math.Clamp(RangeSeconds, 300, (int)MaxViewSpan.TotalSeconds)));
    }

    // ---------------- 绘制 ----------------

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 4 || h <= 4) return;

        var bg = Brush("B.ChartBg", Color.FromRgb(0x12, 0x18, 0x20));
        dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, w, h), 8, 8);

        var plot = MakePlotRect(w, h);
        var labelBrush = Brush("B.ChartText", Color.FromRgb(0x7C, 0x8B, 0x9C));
        var gridPen = new Pen(Brush("B.ChartGrid", Color.FromRgb(0x24, 0x2F, 0x3C)), 1);
        var typeface = Typeface();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        FormattedText Text(string text, double size, Brush brush)
            => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, dpi);

        DrawAxes(dc, plot, Text, labelBrush, gridPen);

        var start = ViewStartUtc;
        var end = ViewEndUtc;
        if (end <= start)
        {
            dc.DrawText(Text("时间范围尚未初始化…", 11, labelBrush),
                new Point(plot.Left + 6, plot.Top + plot.Height / 2 - 8));
            return;
        }

        var all = Samples ?? Array.Empty<BatterySample>();
        if (!ReferenceEquals(_cacheSource, all) || _cacheStart != start || _cacheEnd != end)
        {
            _cacheInRange = all.Where(s => s.TimeUtc >= start && s.TimeUtc <= end)
                .GroupBy(s => s.TimeUtc)
                .Select(g => g.Last())
                .OrderBy(s => s.TimeUtc)
                .ToList();
            _cacheSource = all;
            _cacheStart = start;
            _cacheEnd = end;
        }
        var inRange = _cacheInRange;
        _hoverable = inRange;
        _lastVisibleCount = inRange.Count;

        if (inRange.Count == 0)
        {
            dc.DrawText(Text("该时间范围没有本地电池采样。软件未运行或设备断连时不会产生数据。",
                11.5, labelBrush), new Point(plot.Left + 6, plot.Top + plot.Height / 2 - 8));
            _lastGapCount = 0;
            _lastSegmentCount = 0;
            return;
        }

        // 为曲线连续性补上窗口边界外的相邻点，但绘制时裁剪到绘图区。
        var drawList = new List<BatterySample>(inRange);
        var before = all.LastOrDefault(s => s.TimeUtc < start);
        var after = all.FirstOrDefault(s => s.TimeUtc > end);
        if (before is not null) drawList.Insert(0, before);
        if (after is not null) drawList.Add(after);

        var gapThreshold = TimeSpan.FromSeconds(Math.Max(60, GapThresholdSeconds));
        var gaps = new List<(DateTime Start, DateTime End, string Kind)>();
        var segments = new List<(BatteryChargeState State, List<Point> Points)>();
        var restarts = new List<DateTime>();
        BuildSegments(drawList, plot, start, end, gapThreshold, gaps, segments, restarts);

        // 历史尚未覆盖窗口两端时，把空白区也明确标成“无采样”，避免看起来像从未断过。
        if (inRange.Count > 0)
        {
            var first = inRange[0];
            var last = inRange[^1];
            if (first.TimeUtc - start > gapThreshold)
                gaps.Insert(0, (start, first.TimeUtc, "无采样"));
            if (end - last.TimeUtc > gapThreshold)
                gaps.Add((last.TimeUtc, end, "无采样"));
        }

        gaps = NormalizeGaps(gaps);
        _lastGapCount = gaps.Count;
        _lastSegmentCount = segments.Count;

        dc.PushClip(new RectangleGeometry(plot));

        DrawGaps(dc, gaps, plot, Text);
        foreach (var (state, points) in segments) DrawSegment(dc, state, points, plot);
        DrawRestartMarkers(dc, restarts, plot, Text);
        DrawSampleDots(dc, inRange, plot);

        dc.Pop();

        DrawHover(dc, plot, w, h, Text);
        DrawLegend(dc, plot, w, h, Text);
    }

    private void DrawAxes(DrawingContext dc, Rect plot, Func<string, double, Brush, FormattedText> text,
        Brush labelBrush, Pen gridPen)
    {
        for (var i = 0; i <= 4; i++)
        {
            var pct = 100 - i * 25;
            var y = plot.Top + plot.Height * i / 4;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = text($"{pct}%", 10.5, labelBrush);
            dc.DrawText(ft, new Point(plot.Left - ft.Width - 8, y - ft.Height / 2));
        }

        var span = ViewEndUtc - ViewStartUtc;
        var fmt = span.TotalHours <= 36 ? "HH:mm" : span.TotalDays <= 10 ? "MM-dd HH:mm" : "MM-dd";
        var ticks = new[] { 0.0, 1.0 / 3, 2.0 / 3, 1.0 };
        var lastRight = double.MinValue;
        foreach (var t in ticks)
        {
            var time = ViewStartUtc + span * t;
            var label = text(time.ToLocalTime().ToString(fmt), 10, labelBrush);
            var x = plot.Left + plot.Width * t;
            if (t == 0) x = plot.Left;
            else if (t == 1) x = plot.Right - label.Width;
            else x -= label.Width / 2;
            if (x < lastRight + 6) continue;
            dc.DrawText(label, new Point(x, plot.Bottom + 6));
            lastRight = x + label.Width;
        }
    }

    private void BuildSegments(List<BatterySample> list, Rect plot, DateTime start, DateTime end,
        TimeSpan gapThreshold, List<(DateTime Start, DateTime End, string Kind)> gaps,
        List<(BatteryChargeState State, List<Point> Points)> segments, List<DateTime> restarts)
    {
        List<Point>? current = null;
        var currentState = BatteryChargeState.Unknown;

        for (var i = 0; i < list.Count - 1; i++)
        {
            var a = list[i];
            var b = list[i + 1];
            var dt = b.TimeUtc - a.TimeUtc;
            if (dt <= TimeSpan.Zero) continue;

            var restart = a.RunSeconds is { } ar && b.RunSeconds is { } br && br < ar;
            var valid = a.Connected && b.Connected && a.IsUsable && b.IsUsable &&
                        !restart && dt <= gapThreshold;
            if (!valid)
            {
                current = null;
                var gs = a.TimeUtc < start ? start : a.TimeUtc;
                var ge = b.TimeUtc > end ? end : b.TimeUtc;
                if (ge > gs)
                {
                    var kind = restart ? "设备重启"
                        : !a.Connected || !b.Connected ? "设备断连"
                        : "无采样";
                    gaps.Add((gs, ge, kind));
                }
                if (restart) restarts.Add(b.TimeUtc);
                continue;
            }

            var state = b.State;
            var pa = new Point(XFor(a.TimeUtc, plot), YFor(a.Percent!.Value, plot));
            var pb = new Point(XFor(b.TimeUtc, plot), YFor(b.Percent!.Value, plot));
            if (current is null || currentState != state)
            {
                current = new List<Point> { pa };
                segments.Add((state, current));
                currentState = state;
            }
            current.Add(pb);
        }
    }

    private void DrawGaps(DrawingContext dc, List<(DateTime Start, DateTime End, string Kind)> gaps,
        Rect plot, Func<string, double, Brush, FormattedText> text)
    {
        var fill = new SolidColorBrush(Color.FromArgb(30, 148, 163, 184));
        fill.Freeze();
        var pen = new Pen(Brush("B.Border", Color.FromRgb(0x26, 0x31, 0x3E)), 1)
        {
            DashStyle = new DashStyle(new double[] { 3, 3 }, 0),
        };
        pen.Freeze();
        var labelBrush = Brush("B.TextMuted", Color.FromRgb(0x8B, 0x9A, 0xAB));

        var labelled = 0;
        foreach (var gap in gaps.OrderByDescending(g => g.End - g.Start))
        {
            var x1 = XFor(gap.Start, plot);
            var x2 = XFor(gap.End, plot);
            if (x2 - x1 < 1) continue;
            var rect = new Rect(x1, plot.Top, Math.Max(1, x2 - x1), plot.Height);
            dc.DrawRectangle(fill, pen, rect);
            if (rect.Width > 52 && labelled < 6)
            {
                labelled++;
                var ft = text(gap.Kind, 10, labelBrush);
                dc.DrawText(ft, new Point(rect.Left + (rect.Width - ft.Width) / 2, plot.Top + 5));
            }
        }
    }

    private void DrawSegment(DrawingContext dc, BatteryChargeState state, List<Point> points, Rect plot)
    {
        if (points.Count < 2) return;
        var color = state switch
        {
            BatteryChargeState.Charging => ColorOf("B.Success", Color.FromRgb(0x22, 0xC5, 0x5E)),
            BatteryChargeState.Full => ColorOf("B.Cyan", Color.FromRgb(0x22, 0xD3, 0xEE)),
            BatteryChargeState.Discharging => ColorOf("B.Down", Color.FromRgb(0x3B, 0x82, 0xF6)),
            _ => ColorOf("B.TextMuted", Color.FromRgb(0x8B, 0x9A, 0xAB)),
        };

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, plot.Bottom), true, true);
            ctx.LineTo(points[0], false, false);
            for (var i = 1; i < points.Count; i++) ctx.LineTo(points[i], true, false);
            ctx.LineTo(new Point(points[^1].X, plot.Bottom), false, false);
        }
        geometry.Freeze();

        var fill = new LinearGradientBrush(
            Color.FromArgb(70, color.R, color.G, color.B),
            Color.FromArgb(4, color.R, color.G, color.B), 90);
        fill.Freeze();
        var stroke = new Pen(new SolidColorBrush(color), 2);
        stroke.Freeze();
        dc.DrawGeometry(fill, stroke, geometry);
    }

    private void DrawRestartMarkers(DrawingContext dc, List<DateTime> restarts, Rect plot,
        Func<string, double, Brush, FormattedText> text)
    {
        if (restarts.Count == 0) return;
        var color = ColorOf("B.Warn", Color.FromRgb(0xF5, 0x9E, 0x0B));
        var pen = new Pen(new SolidColorBrush(color), 1)
        {
            DashStyle = new DashStyle(new double[] { 4, 3 }, 0),
        };
        pen.Freeze();
        foreach (var t in restarts.Distinct())
        {
            var x = XFor(t, plot);
            if (x < plot.Left || x > plot.Right) continue;
            dc.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
    }

    private void DrawSampleDots(DrawingContext dc, List<BatterySample> inRange, Rect plot)
    {
        if (inRange.Count > 300) return; // 密集视图不画逐点圆点，曲线本身仍由真实采样构成
        foreach (var s in inRange.Where(s => s.IsUsable))
        {
            var color = s.State switch
            {
                BatteryChargeState.Charging => ColorOf("B.Success", Color.FromRgb(0x22, 0xC5, 0x5E)),
                BatteryChargeState.Full => ColorOf("B.Cyan", Color.FromRgb(0x22, 0xD3, 0xEE)),
                _ => ColorOf("B.Down", Color.FromRgb(0x3B, 0x82, 0xF6)),
            };
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            dc.DrawEllipse(brush, null, new Point(XFor(s.TimeUtc, plot), YFor(s.Percent!.Value, plot)), 2.2, 2.2);
        }
    }

    private void DrawLegend(DrawingContext dc, Rect plot, double w, double h,
        Func<string, double, Brush, FormattedText> text)
    {
        var y = h - 18;
        var discharge = ColorOf("B.Down", Color.FromRgb(0x3B, 0x82, 0xF6));
        var charge = ColorOf("B.Success", Color.FromRgb(0x22, 0xC5, 0x5E));
        var full = ColorOf("B.Cyan", Color.FromRgb(0x22, 0xD3, 0xEE));
        var muted = ColorOf("B.TextMuted", Color.FromRgb(0x8B, 0x9A, 0xAB));

        var x = plot.Left;
        foreach (var (label, color) in new[]
                 {
                     ("● 放电", discharge), ("● 充电", charge), ("● 已充满", full),
                     ("▨ 缺失/断连", muted),
                 })
        {
            var ft = text(label, 10.5, new SolidColorBrush(color));
            dc.DrawText(ft, new Point(x, y));
            x += ft.Width + 14;
        }

        if (w >= 640)
        {
            var hint = text("拖动平移 · 滚轮缩放 · 双击恢复", 10, new SolidColorBrush(muted));
            dc.DrawText(hint, new Point(plot.Right - hint.Width, y));
        }
    }

    private double XFor(DateTime time, Rect plot)
    {
        var span = (ViewEndUtc - ViewStartUtc).TotalSeconds;
        if (span <= 0) return plot.Left;
        return plot.Left + (time - ViewStartUtc).TotalSeconds / span * plot.Width;
    }

    private static double YFor(int percent, Rect plot)
        => plot.Bottom - plot.Height * Math.Clamp(percent, 0, 100) / 100.0;

    private static Rect MakePlotRect(double w, double h)
        => new(PlotLeft, PlotTop, Math.Max(1, w - PlotLeft - PlotRightMargin),
            Math.Max(1, h - PlotTop - PlotBottomBand));

    private static List<(DateTime Start, DateTime End, string Kind)> NormalizeGaps(
        List<(DateTime Start, DateTime End, string Kind)> gaps)
    {
        var merged = new List<(DateTime Start, DateTime End, string Kind)>();
        foreach (var gap in gaps.Where(g => g.End > g.Start).OrderBy(g => g.Start))
        {
            if (merged.Count == 0 || gap.Start > merged[^1].End)
            {
                merged.Add(gap);
                continue;
            }

            var previous = merged[^1];
            merged[^1] = (previous.Start,
                gap.End > previous.End ? gap.End : previous.End,
                GapPriority(previous.Kind) >= GapPriority(gap.Kind) ? previous.Kind : gap.Kind);
        }
        return merged;
    }

    private static int GapPriority(string kind) => kind switch
    {
        "设备重启" => 3,
        "设备断连" => 2,
        _ => 1,
    };

    // ---------------- 悬停 ----------------

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        if (_dragging)
        {
            PanTo(_dragStartViewStart, _dragStartViewEnd, position.X - _dragStart.X);
            return;
        }
        UpdateHover(position);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (_hoverIndex == -1) return;
        _hoverIndex = -1;
        InvalidateVisual();
    }

    private void UpdateHover(Point position)
    {
        var plot = MakePlotRect(ActualWidth, ActualHeight);
        if (!plot.Contains(position) || _hoverable.Count == 0)
        {
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                InvalidateVisual();
            }
            return;
        }

        // 稀疏采样时相邻两点可能相隔数百像素，只允许圆点附近命中会让大部分曲线无法悬停。
        // 这里按真实时间轴命中最近的采样点，卡片仍锚定在该点的实际坐标。
        var targetTime = ViewStartUtc +
                         (ViewEndUtc - ViewStartUtc) *
                         Math.Clamp((position.X - plot.Left) / plot.Width, 0, 1);
        var best = _hoverable.BinarySearchByTime(targetTime);

        if (best == _hoverIndex) return;
        _hoverIndex = best;
        if (best >= 0)
        {
            var hovered = _hoverable[best];
            _mousePosition = new Point(XFor(hovered.TimeUtc, plot),
                hovered.IsUsable ? YFor(hovered.Percent!.Value, plot) : plot.Top + plot.Height / 2);
        }
        InvalidateVisual();
    }

    private void DrawHover(DrawingContext dc, Rect plot, double w, double h,
        Func<string, double, Brush, FormattedText> textFactory)
    {
        if (_hoverIndex < 0 || _hoverIndex >= _hoverable.Count) return;
        var sample = _hoverable[_hoverIndex];
        var x = XFor(sample.TimeUtc, plot);
        var guide = new Pen(Brush("B.TextFaint", Color.FromRgb(0x64, 0x74, 0x8B)), 1)
        {
            DashStyle = new DashStyle(new double[] { 3, 3 }, 0),
        };
        guide.Freeze();
        dc.DrawLine(guide, new Point(x, plot.Top), new Point(x, plot.Bottom));
        if (sample.IsUsable)
        {
            var color = sample.State switch
            {
                BatteryChargeState.Charging => ColorOf("B.Success", Color.FromRgb(0x22, 0xC5, 0x5E)),
                BatteryChargeState.Full => ColorOf("B.Cyan", Color.FromRgb(0x22, 0xD3, 0xEE)),
                _ => ColorOf("B.Down", Color.FromRgb(0x3B, 0x82, 0xF6)),
            };
            var fill = new SolidColorBrush(color);
            fill.Freeze();
            dc.DrawEllipse(fill, new Pen(BrushStatic(Colors.White), 1.2),
                new Point(x, YFor(sample.Percent!.Value, plot)), 3.4, 3.4);
        }

        var lines = BuildHoverLines(sample);
        var formatted = lines.Select((l, i) =>
        {
            var brush = new SolidColorBrush(l.Color);
            brush.Freeze();
            return textFactory(l.Text, i == 0 ? 12 : 11.5, brush);
        }).ToList();

        var contentWidth = formatted.Max(f => f.Width);
        var contentHeight = formatted.Sum(f => f.Height) + Math.Max(0, formatted.Count - 1) * 3;
        var boxW = Math.Min(Math.Max(190, contentWidth + 20), Math.Max(40, w - 8));
        var boxH = contentHeight + 16;
        var boxX = _mousePosition.X + 14;
        if (boxX + boxW > w - 4) boxX = _mousePosition.X - boxW - 14;
        boxX = Math.Clamp(boxX, 4, Math.Max(4, w - boxW - 4));
        var boxY = _mousePosition.Y - boxH - 14;
        if (boxY < 4) boxY = _mousePosition.Y + 16;
        boxY = Math.Clamp(boxY, 4, Math.Max(4, h - boxH - 4));

        var rect = new Rect(boxX, boxY, boxW, boxH);
        dc.DrawRoundedRectangle(Brush("B.BgElevated", Color.FromRgb(0x13, 0x1A, 0x22)),
            new Pen(Brush("B.Border", Color.FromRgb(0x26, 0x31, 0x3E)), 1), rect, 8, 8);
        var y = rect.Top + 8;
        foreach (var ft in formatted)
        {
            dc.DrawText(ft, new Point(rect.Left + 10, y));
            y += ft.Height + 3;
        }
    }

    private List<(string Text, Color Color)> BuildHoverLines(BatterySample sample)
    {
        var result = new List<(string Text, Color Color)>();
        var text = ColorOf("B.Text", Colors.White);
        var faint = ColorOf("B.TextMuted", Color.FromRgb(0x8B, 0x9A, 0xAB));
        result.Add((sample.TimeLocal.ToString("yyyy-MM-dd HH:mm:ss"), text));

        if (!sample.Connected)
        {
            result.Add(("设备断连 / 无采样", ColorOf("B.Warn", Color.FromRgb(0xF5, 0x9E, 0x0B))));
        }
        else if (!sample.Available)
        {
            result.Add(("设备未上报有效电量", ColorOf("B.Warn", Color.FromRgb(0xF5, 0x9E, 0x0B))));
        }
        else
        {
            var color = sample.State switch
            {
                BatteryChargeState.Charging => ColorOf("B.Success", Color.FromRgb(0x22, 0xC5, 0x5E)),
                BatteryChargeState.Full => ColorOf("B.Cyan", Color.FromRgb(0x22, 0xD3, 0xEE)),
                _ => ColorOf("B.Down", Color.FromRgb(0x3B, 0x82, 0xF6)),
            };
            result.Add(($"电量　{sample.Percent}%", color));
            result.Add(($"状态　{BatteryState.Text(sample.State)}", color));
        }

        result.Add(("真实采样（本机记录）", faint));
        return result;
    }

    // ---------------- 平移 / 缩放 ----------------

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(this);
        var plot = MakePlotRect(ActualWidth, ActualHeight);
        if (!plot.Contains(position)) return;
        if (e.ClickCount >= 2)
        {
            ResetView();
            return;
        }

        _dragging = true;
        _dragStart = position;
        _dragStartViewStart = ViewStartUtc;
        _dragStartViewEnd = ViewEndUtc;
        FollowLatest = false;
        Cursor = Cursors.SizeWE;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Cursor = Cursors.Cross;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void PanTo(DateTime baseStart, DateTime baseEnd, double deltaX)
    {
        var plot = MakePlotRect(ActualWidth, ActualHeight);
        var span = baseEnd - baseStart;
        if (span <= TimeSpan.Zero || plot.Width <= 1) return;
        var shift = -deltaX / plot.Width * span;
        SetView(baseStart + shift, baseEnd + shift);
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(this), e.Delta > 0);
        e.Handled = true;
    }

    private void ZoomAt(Point position, bool zoomIn)
    {
        var plot = MakePlotRect(ActualWidth, ActualHeight);
        if (!plot.Contains(position)) return;

        var span = ViewEndUtc - ViewStartUtc;
        if (span <= TimeSpan.Zero) return;
        var ratio = Math.Clamp((position.X - plot.Left) / plot.Width, 0, 1);
        var anchor = ViewStartUtc + span * ratio;
        var factor = zoomIn ? 0.75 : 1.33;
        var newSpan = TimeSpan.FromTicks((long)(span.Ticks * factor));
        if (newSpan < MinViewSpan) newSpan = MinViewSpan;
        if (newSpan > MaxViewSpan) newSpan = MaxViewSpan;

        var newStart = anchor - newSpan * ratio;
        var newEnd = newStart + newSpan;
        FollowLatest = newEnd >= DateTime.UtcNow - TimeSpan.FromMinutes(1) &&
                       newSpan >= TimeSpan.FromSeconds(Math.Max(60, RangeSeconds)) * 0.98;
        SetView(newStart, newEnd);
    }

    // ---------------- 自检入口 ----------------

    internal void HoverAtForTest(Point point) => UpdateHover(point);
    internal string? HoverSummaryForTest =>
        _hoverIndex >= 0 && _hoverIndex < _hoverable.Count
            ? string.Join("\n", BuildHoverLines(_hoverable[_hoverIndex]).Select(x => x.Text))
            : null;
    internal int VisibleSamplesForTest => _lastVisibleCount;
    internal int GapCountForTest => _lastGapCount;
    internal int SegmentCountForTest => _lastSegmentCount;
    internal (DateTime Start, DateTime End) ViewForTest => (ViewStartUtc, ViewEndUtc);
    internal DateTime? HoverTimeForTest =>
        _hoverIndex >= 0 && _hoverIndex < _hoverable.Count ? _hoverable[_hoverIndex].TimeUtc : null;
    internal void ResetViewForTest() => ResetView();
    internal void PanForTest(double deltaX)
    {
        PanTo(ViewStartUtc, ViewEndUtc, deltaX);
    }
    internal void ZoomForTest(Point position, bool zoomIn) => ZoomAt(position, zoomIn);
    internal Point PointForTimeForTest(DateTime timeUtc)
    {
        var plot = MakePlotRect(ActualWidth, ActualHeight);
        return new Point(XFor(timeUtc, plot), plot.Top + plot.Height / 2);
    }

    private static Brush BrushStatic(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    private Brush Brush(string key, Color fallback)
        => TryFindResource(key) as SolidColorBrush ?? BrushStatic(fallback);

    private Color ColorOf(string key, Color fallback)
        => TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    private static Typeface Typeface()
        => new(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
}

internal static class BatterySampleSearch
{
    /// <summary>按时间找到最接近的索引；用于曲线任意位置的悬停命中。</summary>
    public static int BinarySearchByTime(this List<BatterySample> list, DateTime time)
    {
        if (list.Count == 0) return -1;
        var lo = 0;
        var hi = list.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var t = list[mid].TimeUtc;
            if (t == time) return mid;
            if (t < time) lo = mid + 1;
            else hi = mid - 1;
        }
        if (lo >= list.Count) return list.Count - 1;
        if (hi < 0) return 0;
        return time - list[hi].TimeUtc <= list[lo].TimeUtc - time ? hi : lo;
    }
}
