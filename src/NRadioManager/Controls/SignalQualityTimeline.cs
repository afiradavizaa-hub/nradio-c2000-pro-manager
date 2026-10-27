using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NRadioManager.Core;

namespace NRadioManager.Controls;

/// <summary>信号质量时间带：用分级色块代替折线，缺失采样保持空白。</summary>
public sealed class SignalQualityTimeline : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(ObservableCollection<SamplePoint>), typeof(SignalQualityTimeline),
        new PropertyMetadata(null, OnPointsChanged));

    private int _hoverIndex = -1;

    public ObservableCollection<SamplePoint>? Points
    {
        get => (ObservableCollection<SamplePoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public SignalQualityTimeline()
    {
        Cursor = Cursors.Cross;
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) =>
        {
            _hoverIndex = -1;
            ToolTip = null;
            InvalidateVisual();
        };
        NRadioManager.Infrastructure.ThemeManager.ThemeChanged += InvalidateVisual;
        Unloaded += (_, _) => NRadioManager.Infrastructure.ThemeManager.ThemeChanged -= InvalidateVisual;
    }

    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (SignalQualityTimeline)d;
        if (e.OldValue is ObservableCollection<SamplePoint> old)
            old.CollectionChanged -= chart.OnCollectionChanged;
        if (e.NewValue is ObservableCollection<SamplePoint> now)
            now.CollectionChanged += chart.OnCollectionChanged;
        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => InvalidateVisual();

    internal void HoverAtForTest(int index)
    {
        var slice = Points?.TakeLast(300).ToList() ?? new List<SamplePoint>();
        if (index < 0 || index >= slice.Count) return;
        _hoverIndex = index;
        ToolTip = slice[index].RssiDbm is { } rsrp
            ? $"{slice[index].Time:yyyy-MM-dd HH:mm:ss}\nRSRP {rsrp} dBm\n{QualityText(rsrp)}"
            : null;
        InvalidateVisual();
    }

    internal string? HoverTextForTest => ToolTip?.ToString();

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 20 || height < 20) return;

        var bg = Brush("B.ChartBg", Color.FromRgb(0x12, 0x18, 0x20));
        var textBrush = Brush("B.ChartText", Color.FromRgb(0x7C, 0x8B, 0x9C));
        var grid = new Pen(Brush("B.ChartGrid", Color.FromRgb(0x24, 0x2F, 0x3C)), 1);
        dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, width, height), 8, 8);

        var plot = new Rect(45, 12, Math.Max(10, width - 54), Math.Max(10, height - 48));
        DrawThreshold(dc, plot, -80, "优秀 ≥ -80", Color.FromRgb(0x22, 0xC5, 0x5E), textBrush, grid);
        DrawThreshold(dc, plot, -90, "良好 ≥ -90", Color.FromRgb(0x3B, 0x82, 0xF6), textBrush, grid);
        DrawThreshold(dc, plot, -100, "一般 ≥ -100", Color.FromRgb(0xF5, 0x9E, 0x0B), textBrush, grid);
        DrawThreshold(dc, plot, -110, "较差 < -100", Color.FromRgb(0xEF, 0x44, 0x44), textBrush, grid);

        var points = Points;
        if (points is null || points.Count == 0)
        {
            DrawLabel(dc, "等待信号采样…", new Point(plot.Left + 8, plot.Top + plot.Height / 2), textBrush);
            return;
        }

        var slice = points.TakeLast(300).ToList();
        var barWidth = Math.Min(12, Math.Max(3, plot.Width / Math.Max(1, slice.Count) * 0.5));
        for (var i = 0; i < slice.Count; i++)
        {
            if (slice[i].RssiDbm is not { } rsrp) continue;
            var x = plot.Left + plot.Width * i / Math.Max(1, slice.Count - 1);
            var y = Y(rsrp, plot);
            var rectHeight = Math.Max(3, plot.Bottom - y);
            var brush = new SolidColorBrush(ColorFor(rsrp));
            dc.DrawRoundedRectangle(brush, null,
                new Rect(x - barWidth / 2, y, barWidth, rectHeight), 1.5, 1.5);
        }

        if (slice.Count > 0)
        {
            DrawLabel(dc, slice[0].Time.ToString("HH:mm:ss"), new Point(plot.Left, plot.Bottom + 5), textBrush, 9);
            var lastLabel = slice[^1].Time.ToString("HH:mm:ss");
            var lastText = new FormattedText(lastLabel, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), 9, textBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(lastText, new Point(plot.Right - lastText.Width, plot.Bottom + 5));
        }

        if (_hoverIndex >= 0 && _hoverIndex < slice.Count && slice[_hoverIndex].RssiDbm is { } hoverValue)
        {
            var x = plot.Left + plot.Width * _hoverIndex / Math.Max(1, slice.Count - 1);
            dc.DrawLine(new Pen(Brushes.White, 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
            var y = Y(hoverValue, plot);
            dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), new Point(x, y), 4, 4);
            ToolTip = $"{slice[_hoverIndex].Time:yyyy-MM-dd HH:mm:ss}\nRSRP {hoverValue} dBm\n{QualityText(hoverValue)}";
        }
    }

    private void DrawThreshold(DrawingContext dc, Rect plot, double rsrp, string label, Color color,
        Brush textBrush, Pen grid)
    {
        var y = Y(rsrp, plot);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(100, color.R, color.G, color.B)), 1),
            new Point(plot.Left, y), new Point(plot.Right, y));
        DrawLabel(dc, label, new Point(3, y - 7), textBrush, 9);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var points = Points;
        if (points is null || points.Count == 0) return;
        var slice = points.TakeLast(300).ToList();
        var plotLeft = 45d;
        var plotWidth = Math.Max(10, ActualWidth - 54);
        var ratio = Math.Clamp((e.GetPosition(this).X - plotLeft) / plotWidth, 0, 1);
        _hoverIndex = (int)Math.Round(ratio * Math.Max(0, slice.Count - 1));
        InvalidateVisual();
    }

    private static double Y(double rsrp, Rect plot)
        => plot.Bottom - Math.Clamp((rsrp + 140) / 100, 0, 1) * plot.Height;

    private static Color ColorFor(double rsrp) => rsrp >= -80
        ? Color.FromRgb(0x22, 0xC5, 0x5E)
        : rsrp >= -90
            ? Color.FromRgb(0x3B, 0x82, 0xF6)
            : rsrp >= -100
                ? Color.FromRgb(0xF5, 0x9E, 0x0B)
                : Color.FromRgb(0xEF, 0x44, 0x44);

    private static string QualityText(double rsrp)
        => rsrp >= -80 ? "优秀" : rsrp >= -90 ? "良好" : rsrp >= -100 ? "一般" : "较差";

    private void DrawLabel(DrawingContext dc, string value, Point at, Brush brush, double size = 10)
    {
        var typeface = new Typeface("Microsoft YaHei UI");
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, at);
    }

    private Brush Brush(string key, Color fallback)
        => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
