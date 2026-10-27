using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NRadioManager.Controls;

/// <summary>信号页使用的真实历史点；缺失值保持 null，不插值、不补零。</summary>
public sealed record SignalMetricPoint(
    DateTime Time,
    double? RsrpDbm,
    double? RsrqDb,
    double? SinrDb,
    bool Connected,
    string Cell = "",
    string Band = "",
    string Earfcn = "",
    string Pci = "");

/// <summary>
/// 三段式信号分析图：RSRP 质量色带、RSRQ 和 SINR 趋势共用真实时间轴。
/// 悬停显示精确时间、三项指标、连接状态和小区信息；断连及缺失值保留空白。
/// </summary>
public sealed class SignalAnalysisChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(ObservableCollection<SignalMetricPoint>), typeof(SignalAnalysisChart),
        new PropertyMetadata(null, OnPointsChanged));

    private int _hoverIndex = -1;
    private Point _mousePosition;

    public SignalAnalysisChart()
    {
        Cursor = Cursors.Cross;
        ClipToBounds = true;
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) =>
        {
            if (_hoverIndex == -1) return;
            _hoverIndex = -1;
            InvalidateVisual();
        };
        NRadioManager.Infrastructure.ThemeManager.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => NRadioManager.Infrastructure.ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    public ObservableCollection<SignalMetricPoint>? Points
    {
        get => (ObservableCollection<SignalMetricPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    internal int HoverIndexForTest => _hoverIndex;
    internal string? HoverTextForTest => _hoverIndex >= 0
        ? BuildHoverText(Snapshot(), _hoverIndex)
        : null;

    internal void HoverAtForTest(int index)
    {
        var points = Snapshot();
        if (index < 0 || index >= points.Count) return;
        _hoverIndex = index;
        _mousePosition = new Point(ActualWidth * 0.5, ActualHeight * 0.5);
        ToolTip = BuildHoverText(points, index);
        InvalidateVisual();
    }

    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (SignalAnalysisChart)d;
        if (e.OldValue is ObservableCollection<SignalMetricPoint> old)
            old.CollectionChanged -= chart.OnCollectionChanged;
        if (e.NewValue is ObservableCollection<SignalMetricPoint> now)
            now.CollectionChanged += chart.OnCollectionChanged;
        chart._hoverIndex = -1;
        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => InvalidateVisual();

    private void OnThemeChanged()
    {
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 80 || height < 100) return;

        var bg = Brush("B.ChartBg", Color.FromRgb(0x12, 0x18, 0x20));
        var text = Brush("B.Text", Color.FromRgb(0xE6, 0xED, 0xF3));
        var muted = Brush("B.TextMuted", Color.FromRgb(0x8B, 0x9A, 0xAB));
        var grid = Brush("B.ChartGrid", Color.FromRgb(0x24, 0x2F, 0x3C));
        dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, width, height), 10, 10);

        var left = width < 520 ? 54d : 64d;
        var plot = new Rect(left, 14, Math.Max(10, width - left - 16), Math.Max(10, height - 52));
        var gap = 9d;
        var rsrpHeight = plot.Height * 0.48;
        var rsrqHeight = plot.Height * 0.23;
        var sinrHeight = plot.Height - rsrpHeight - rsrqHeight - gap * 2;
        var rsrpLane = new Rect(plot.Left, plot.Top, plot.Width, rsrpHeight);
        var rsrqLane = new Rect(plot.Left, rsrpLane.Bottom + gap, plot.Width, rsrqHeight);
        var sinrLane = new Rect(plot.Left, rsrqLane.Bottom + gap, plot.Width, sinrHeight);

        DrawRsrpBands(dc, rsrpLane);
        DrawLaneGrid(dc, rsrpLane, grid);
        DrawLaneGrid(dc, rsrqLane, grid);
        DrawLaneGrid(dc, sinrLane, grid);

        var points = Points is null
            ? new List<SignalMetricPoint>()
            : Points.ToList();
        if (points.Count == 0)
        {
            Label(dc, "等待 C2000 信号采样…", new Point(plot.Left + 8, plot.Top + 20), muted, 11.5);
            return;
        }

        var down = ColorOf("B.Down", Color.FromRgb(0x3B, 0x82, 0xF6));
        var up = ColorOf("B.Up", Color.FromRgb(0x22, 0xD3, 0xEE));
        var purple = Color.FromRgb(0xA7, 0x8B, 0xFA);

        DrawSeries(dc, points, rsrpLane, -120, -60, p => p.RsrpDbm, p => p.RsrpDbm is { } v ? QualityColor(v) : down);
        DrawSeries(dc, points, rsrqLane, -25, 0, p => p.RsrqDb, _ => up);
        DrawSeries(dc, points, sinrLane, -10, 30, p => p.SinrDb, _ => purple);

        var last = points[^1];
        DrawMetricLabel(dc, "RSRP", last.RsrpDbm is { } rsrp ? $"{rsrp:0} dBm" : "—", rsrpLane, text, muted);
        DrawMetricLabel(dc, "RSRQ", last.RsrqDb is { } rsrq ? $"{rsrq:0.#} dB" : "—", rsrqLane, text, muted);
        DrawMetricLabel(dc, "SINR", last.SinrDb is { } sinr ? $"{sinr:0.#} dB" : "—", sinrLane, text, muted);

        Label(dc, "优秀", new Point(rsrpLane.Right - 176, rsrpLane.Top + 4), QualityBrush(QualityColor(-75)), 10);
        Label(dc, "良好", new Point(rsrpLane.Right - 126, rsrpLane.Top + 4), QualityBrush(QualityColor(-85)), 10);
        Label(dc, "一般", new Point(rsrpLane.Right - 76, rsrpLane.Top + 4), QualityBrush(QualityColor(-95)), 10);
        Label(dc, "较差", new Point(rsrpLane.Right - 26, rsrpLane.Top + 4), QualityBrush(QualityColor(-105)), 10);

        DrawTimeAxis(dc, points, plot, muted);

        if (_hoverIndex >= 0 && _hoverIndex < points.Count)
            DrawHover(dc, points, plot, rsrpLane, rsrqLane, sinrLane, text, muted);
    }

    private void DrawRsrpBands(DrawingContext dc, Rect lane)
    {
        DrawBand(dc, lane, -60, -80, Color.FromArgb(26, 0x22, 0xC5, 0x5E));
        DrawBand(dc, lane, -80, -90, Color.FromArgb(24, 0x3B, 0x82, 0xF6));
        DrawBand(dc, lane, -90, -100, Color.FromArgb(24, 0xF5, 0x9E, 0x0B));
        DrawBand(dc, lane, -100, -120, Color.FromArgb(26, 0xEF, 0x44, 0x44));
    }

    private static void DrawBand(DrawingContext dc, Rect lane, double high, double low, Color color)
    {
        var y1 = Y(high, -120, -60, lane);
        var y2 = Y(low, -120, -60, lane);
        dc.DrawRectangle(new SolidColorBrush(color), null,
            new Rect(lane.Left, Math.Min(y1, y2), lane.Width, Math.Abs(y2 - y1)));
    }

    private void DrawLaneGrid(DrawingContext dc, Rect lane, Brush grid)
    {
        var pen = new Pen(grid, 1);
        for (var i = 0; i <= 2; i++)
        {
            var y = lane.Top + lane.Height * i / 2d;
            dc.DrawLine(pen, new Point(lane.Left, y), new Point(lane.Right, y));
        }
        dc.DrawLine(pen, new Point(lane.Left, lane.Bottom), new Point(lane.Right, lane.Bottom));
    }

    private void DrawSeries(DrawingContext dc, List<SignalMetricPoint> points, Rect lane,
        double minimum, double maximum, Func<SignalMetricPoint, double?> selector, Func<SignalMetricPoint, Color> color)
    {
        Point? previous = null;
        SignalMetricPoint? previousPoint = null;
        for (var i = 0; i < points.Count; i++)
        {
            var value = selector(points[i]);
            if (value is null)
            {
                previous = null;
                previousPoint = null;
                continue;
            }

            var current = new Point(
                lane.Left + lane.Width * i / Math.Max(1, points.Count - 1),
                Y(value.Value, minimum, maximum, lane));
            if (previous is { } from && previousPoint is { } pp)
            {
                var brush = QualityBrush(color(pp));
                var pen = new Pen(brush, 2)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                };
                pen.Freeze();
                dc.DrawLine(pen, from, current);
            }

            if (points.Count <= 38 || i == points.Count - 1 || i == _hoverIndex)
            {
                var dot = QualityBrush(color(points[i]));
                dc.DrawEllipse(dot, null, current, i == _hoverIndex ? 4.5 : 2.4, i == _hoverIndex ? 4.5 : 2.4);
            }
            previous = current;
            previousPoint = points[i];
        }
    }

    private void DrawMetricLabel(DrawingContext dc, string name, string value, Rect lane, Brush text, Brush muted)
    {
        var fontSize = ActualWidth < 520 ? 9.5 : 10.5;
        Label(dc, name, new Point(4, lane.Top + lane.Height / 2 - 16), muted, fontSize);
        Label(dc, value, new Point(4, lane.Top + lane.Height / 2 + 1), text, fontSize);
    }

    private void DrawTimeAxis(DrawingContext dc, List<SignalMetricPoint> points, Rect plot, Brush muted)
    {
        if (plot.Width < 100) return;
        var y = plot.Bottom + 5;
        Label(dc, points[0].Time.ToString("MM-dd HH:mm"), new Point(plot.Left, y), muted, 10);
        if (points.Count >= 3)
        {
            var middle = points[points.Count / 2].Time.ToString("MM-dd HH:mm");
            var middleText = Text(middle, 10, muted);
            dc.DrawText(middleText, new Point(plot.Left + (plot.Width - middleText.Width) / 2, y));
        }
        var lastText = Text(points[^1].Time.ToString("MM-dd HH:mm"), 10, muted);
        dc.DrawText(lastText, new Point(plot.Right - lastText.Width, y));
    }

    private void DrawHover(DrawingContext dc, List<SignalMetricPoint> points, Rect plot,
        Rect rsrpLane, Rect rsrqLane, Rect sinrLane, Brush text, Brush muted)
    {
        var point = points[_hoverIndex];
        var x = plot.Left + plot.Width * _hoverIndex / Math.Max(1, points.Count - 1);
        var guide = new Pen(Brush("B.TextFaint", Color.FromRgb(0x64, 0x74, 0x8B)), 1)
        {
            DashStyle = new DashStyle(new double[] { 3, 3 }, 0),
        };
        guide.Freeze();
        dc.DrawLine(guide, new Point(x, plot.Top), new Point(x, plot.Bottom));

        if (point.RsrpDbm is { } rsrp)
            dc.DrawEllipse(QualityBrush(QualityColor(rsrp)), null, new Point(x, Y(rsrp, -120, -60, rsrpLane)), 4.5, 4.5);
        if (point.RsrqDb is { } rsrq)
            dc.DrawEllipse(QualityBrush(ColorOf("B.Up", Color.FromRgb(0x22, 0xD3, 0xEE))), null,
                new Point(x, Y(rsrq, -25, 0, rsrqLane)), 4.5, 4.5);
        if (point.SinrDb is { } sinr)
            dc.DrawEllipse(QualityBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)), null,
                new Point(x, Y(sinr, -10, 30, sinrLane)), 4.5, 4.5);

        var lines = new List<string>
        {
            point.Time.ToString("yyyy-MM-dd HH:mm:ss"),
            $"RSRP  {Fmt(point.RsrpDbm, " dBm")}   {QualityText(point.RsrpDbm)}",
            $"RSRQ  {Fmt(point.RsrqDb, " dB", "0.#")}",
            $"SINR  {Fmt(point.SinrDb, " dB", "0.#")}",
            point.Connected ? "状态  已连接" : "状态  未连接或未知",
        };
        var location = string.Join(" · ", new[]
        {
            string.IsNullOrWhiteSpace(point.Band) ? "" : $"Band {point.Band}",
            string.IsNullOrWhiteSpace(point.Pci) ? "" : $"PCI {point.Pci}",
            string.IsNullOrWhiteSpace(point.Earfcn) ? "" : $"ARFCN {point.Earfcn}",
        }.Where(s => s.Length > 0));
        if (location.Length > 0) lines.Add(location);

        var formatted = lines.Select((line, index) =>
            Text(line, index == 0 ? 11.5 : 10.8, index == 0 ? text : muted)).ToList();
        var boxWidth = Math.Min(Math.Max(formatted.Max(f => f.Width) + 20, 220), Math.Max(60, ActualWidth - 16));
        var boxHeight = formatted.Sum(f => f.Height) + (formatted.Count - 1) * 2 + 14;
        var boxX = _mousePosition.X + 16;
        if (boxX + boxWidth > ActualWidth - 8) boxX = _mousePosition.X - boxWidth - 16;
        boxX = Math.Clamp(boxX, 8, Math.Max(8, ActualWidth - boxWidth - 8));
        var boxY = Math.Clamp(_mousePosition.Y - boxHeight - 12, 8, Math.Max(8, ActualHeight - boxHeight - 8));

        var box = new Rect(boxX, boxY, boxWidth, boxHeight);
        var border = new Pen(Brush("B.Border", Color.FromRgb(0x26, 0x31, 0x3E)), 1);
        dc.DrawRoundedRectangle(Brush("B.BgElevated", Color.FromRgb(0x13, 0x1A, 0x22)), border, box, 9, 9);
        var y = box.Top + 7;
        foreach (var line in formatted)
        {
            dc.DrawText(line, new Point(box.Left + 10, y));
            y += line.Height + 2;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var points = Snapshot();
        if (points.Count == 0) return;
        var position = e.GetPosition(this);
        var left = ActualWidth < 520 ? 54d : 64d;
        var plot = new Rect(left, 14, Math.Max(10, ActualWidth - left - 16), Math.Max(10, ActualHeight - 52));
        if (!plot.Contains(position))
        {
            if (_hoverIndex == -1) return;
            _hoverIndex = -1;
            InvalidateVisual();
            return;
        }

        var index = (int)Math.Round((position.X - plot.Left) / plot.Width * (points.Count - 1));
        index = Math.Clamp(index, 0, points.Count - 1);
        _mousePosition = position;
        if (_hoverIndex != index) _hoverIndex = index;
        InvalidateVisual();
    }

    private List<SignalMetricPoint> Snapshot()
        => Points is null ? new List<SignalMetricPoint>() : Points.ToList();

    private string BuildHoverText(List<SignalMetricPoint> points, int index)
    {
        if (index < 0 || index >= points.Count) return "";
        var point = points[index];
        return $"{point.Time:yyyy-MM-dd HH:mm:ss}\n" +
               $"RSRP {Fmt(point.RsrpDbm, " dBm")} · {QualityText(point.RsrpDbm)}\n" +
               $"RSRQ {Fmt(point.RsrqDb, " dB", "0.#")} · SINR {Fmt(point.SinrDb, " dB", "0.#")}\n" +
               $"{(point.Connected ? "已连接" : "未连接或状态未知")}";
    }

    private static string Fmt(double? value, string unit, string format = "0")
        => value is { } v ? v.ToString(format, CultureInfo.CurrentCulture) + unit : "—";

    private static string QualityText(double? rsrp)
        => rsrp is not { } value ? "数据缺失"
            : value >= -80 ? "优秀"
            : value >= -90 ? "良好"
            : value >= -100 ? "一般"
            : "较差";

    private static Color QualityColor(double value)
        => value >= -80 ? Color.FromRgb(0x22, 0xC5, 0x5E)
            : value >= -90 ? Color.FromRgb(0x3B, 0x82, 0xF6)
            : value >= -100 ? Color.FromRgb(0xF5, 0x9E, 0x0B)
            : Color.FromRgb(0xEF, 0x44, 0x44);

    private static double Y(double value, double minimum, double maximum, Rect lane)
        => lane.Bottom - lane.Height * Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);

    private void Label(DrawingContext dc, string value, Point at, Brush brush, double size)
        => dc.DrawText(Text(value, size, brush), at);

    private FormattedText Text(string value, double size, Brush brush)
        => new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal,
                FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private Color ColorOf(string key, Color fallback)
        => TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;

    private Brush QualityBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private Brush Brush(string key, Color fallback)
        => TryFindResource(key) as Brush ?? QualityBrush(fallback);
}
