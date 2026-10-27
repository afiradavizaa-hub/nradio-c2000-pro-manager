using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NRadioManager.Controls;

public enum MetricGaugeKind
{
    SignalRsrp,
    SignalRsrq,
    SignalSinr,
    Cqi,
    Percent,
    Temperature,
}

/// <summary>可交互环形指标：信号、CPU/内存占用和设备温度共用一套清晰的可视化。</summary>
public sealed class MetricGauge : FrameworkElement
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(MetricGauge),
        new FrameworkPropertyMetadata("指标", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double?), typeof(MetricGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(MetricGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(MetricGauge),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(MetricGauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(MetricGaugeKind), typeof(MetricGauge),
        new FrameworkPropertyMetadata(MetricGaugeKind.Percent,
            FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(MetricGauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public MetricGaugeKind Kind { get => (MetricGaugeKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public MetricGauge()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Cursor = Cursors.Hand;
        ToolTipService.SetInitialShowDelay(this, 0);
        ToolTipService.SetShowDuration(this, 60000);
        MouseEnter += (_, _) => InvalidateVisual();
        MouseLeave += (_, _) => InvalidateVisual();
        MouseMove += (_, _) => UpdateToolTip();
        NRadioManager.Infrastructure.ThemeManager.ThemeChanged += InvalidateVisual;
        Unloaded += (_, _) => NRadioManager.Infrastructure.ThemeManager.ThemeChanged -= InvalidateVisual;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (MetricGauge)d;
        gauge.UpdateToolTip();
        gauge.InvalidateVisual();
    }

    private void UpdateToolTip()
    {
        if (Value is null)
        {
            ToolTip = $"{Title}\n设备未返回该指标。\n{Description}";
            return;
        }
        ToolTip = $"{Title}：{FormatValue()}\n状态：{StatusText(Value.Value)}\n{Description}";
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 20 || height < 20) return;

        var text = Brush("B.Text", Color.FromRgb(0xE6, 0xED, 0xF3));
        var muted = Brush("B.TextMuted", Color.FromRgb(0x8B, 0x9A, 0xAB));
        var track = Brush("B.Border", Color.FromRgb(0x26, 0x31, 0x3E));
        var surface = Brush("B.SurfaceAlt", Color.FromRgb(0x1C, 0x25, 0x30));
        var typeface = new Typeface("Microsoft YaHei UI");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        FormattedText Make(string value, double size, Brush brush, FontWeight weight)
            => new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(typeface.FontFamily, FontStyles.Normal, weight, FontStretches.Normal),
                size, brush, dpi);

        var title = Make(Title, 13, muted, FontWeights.Medium);
        dc.DrawText(title, new Point((width - title.Width) / 2, 2));

        var diameter = Math.Min(width - 22, height - 52);
        diameter = Math.Max(28, diameter);
        var cx = width / 2;
        var cy = 20 + diameter / 2;
        var radius = diameter / 2 - Math.Max(6, diameter * 0.07);
        var thickness = Math.Max(7, diameter * 0.075);
        var center = new Point(cx, cy);

        if (IsMouseOver)
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)), null,
                center, radius + thickness, radius + thickness);

        var trackPen = new Pen(track, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        var ratio = Ratio(Value);
        var color = ColorFor(Value, Kind);
        if (ratio > 0.001)
        {
            var pen = new Pen(new SolidColorBrush(color), thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            dc.DrawGeometry(null, pen, ArcGeometry(center, radius, 135, 135 + 270 * ratio));
        }

        var valueText = Make(FormatValue(), Math.Clamp(diameter * 0.20, 17, 30),
            new SolidColorBrush(color), FontWeights.SemiBold);
        dc.DrawText(valueText, new Point((width - valueText.Width) / 2,
            cy - valueText.Height / 2 - 2));

        var status = Make(Value is { } statusValue ? StatusText(statusValue) : "等待数据",
            11, new SolidColorBrush(color), FontWeights.Medium);
        dc.DrawText(status, new Point((width - status.Width) / 2,
            cy + valueText.Height / 2 - 2));

        var description = Description;
        if (!string.IsNullOrWhiteSpace(description))
        {
            var clipped = description.Length > 24 ? description[..24] + "…" : description;
            var note = Make(clipped, 9.5, muted, FontWeights.Normal);
            dc.DrawText(note, new Point((width - note.Width) / 2, height - note.Height - 2));
        }
    }

    private string FormatValue()
    {
        if (Value is not { } value) return "—";
        return Kind == MetricGaugeKind.SignalRsrp
            ? value.ToString("0", CultureInfo.CurrentCulture)
            : value.ToString("0.#", CultureInfo.CurrentCulture) + Unit;
    }

    private double Ratio(double? value)
    {
        if (value is null || Maximum <= Minimum) return 0;
        return Math.Clamp((value.Value - Minimum) / (Maximum - Minimum), 0, 1);
    }

    private string StatusText(double value)
        => Kind switch
        {
            MetricGaugeKind.SignalRsrp => value >= -80 ? "优秀" : value >= -90 ? "良好" : value >= -100 ? "一般" : "较差",
            MetricGaugeKind.SignalRsrq => value >= -10 ? "优秀" : value >= -15 ? "良好" : value >= -20 ? "一般" : "较差",
            MetricGaugeKind.SignalSinr => value >= 20 ? "优秀" : value >= 13 ? "良好" : value >= 0 ? "一般" : "较差",
            MetricGaugeKind.Cqi => value >= 12 ? "优秀" : value >= 9 ? "良好" : value >= 5 ? "一般" : "较差",
            MetricGaugeKind.Temperature => value <= 45 ? "正常" : value <= 60 ? "良好" : value <= 70 ? "偏高" : "过热",
            _ => value <= 50 ? "优秀" : value <= 70 ? "良好" : value <= 85 ? "一般" : "较高",
        };

    private static Color ColorFor(double? value, MetricGaugeKind kind)
    {
        if (value is not { } v) return Color.FromRgb(0x64, 0x74, 0x8B);
        return kind switch
        {
            MetricGaugeKind.SignalRsrp => v >= -80
                ? Color.FromRgb(0x22, 0xC5, 0x5E)
                : v >= -90
                    ? Color.FromRgb(0x3B, 0x82, 0xF6)
                    : v >= -100
                        ? Color.FromRgb(0xF5, 0x9E, 0x0B)
                        : Color.FromRgb(0xEF, 0x44, 0x44),
            MetricGaugeKind.SignalRsrq => v >= -10
                ? Color.FromRgb(0x22, 0xC5, 0x5E)
                : v >= -15
                    ? Color.FromRgb(0x3B, 0x82, 0xF6)
                    : v >= -20
                        ? Color.FromRgb(0xF5, 0x9E, 0x0B)
                        : Color.FromRgb(0xEF, 0x44, 0x44),
            MetricGaugeKind.SignalSinr => v >= 20
                ? Color.FromRgb(0x22, 0xC5, 0x5E)
                : v >= 13
                    ? Color.FromRgb(0x3B, 0x82, 0xF6)
                    : v >= 0
                        ? Color.FromRgb(0xF5, 0x9E, 0x0B)
                        : Color.FromRgb(0xEF, 0x44, 0x44),
            MetricGaugeKind.Cqi => v >= 12
                ? Color.FromRgb(0x22, 0xC5, 0x5E)
                : v >= 9
                    ? Color.FromRgb(0x3B, 0x82, 0xF6)
                    : v >= 5
                        ? Color.FromRgb(0xF5, 0x9E, 0x0B)
                        : Color.FromRgb(0xEF, 0x44, 0x44),
            MetricGaugeKind.Temperature => v <= 45
                ? Color.FromRgb(0x22, 0xC5, 0x5E)
                : v <= 60
                    ? Color.FromRgb(0x3B, 0x82, 0xF6)
                    : v <= 70
                        ? Color.FromRgb(0xF5, 0x9E, 0x0B)
                        : Color.FromRgb(0xEF, 0x44, 0x44),
            _ => v <= 50
                ? Color.FromRgb(0x22, 0xC5, 0x5E)
                : v <= 70
                    ? Color.FromRgb(0x3B, 0x82, 0xF6)
                    : v <= 85
                        ? Color.FromRgb(0xF5, 0x9E, 0x0B)
                        : Color.FromRgb(0xEF, 0x44, 0x44),
        };
    }

    private static Geometry ArcGeometry(Point center, double radius, double startDegrees, double endDegrees)
    {
        var start = PointAt(center, radius, startDegrees);
        var end = PointAt(center, radius, endDegrees);
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(start, false, false);
        ctx.ArcTo(end, new Size(radius, radius), 0, endDegrees - startDegrees > 180,
            SweepDirection.Clockwise, true, false);
        geometry.Freeze();
        return geometry;
    }

    private static Point PointAt(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private Brush Brush(string key, Color fallback)
        => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
