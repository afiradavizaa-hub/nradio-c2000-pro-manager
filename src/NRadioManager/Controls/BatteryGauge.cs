using System.Windows;
using System.Windows.Media;

namespace NRadioManager.Controls;

/// <summary>电池图标：只按真实电量百分比绘制；-1 表示设备未上报，不画填充。</summary>
public sealed class BatteryGauge : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(int), typeof(BatteryGauge),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ChargingProperty = DependencyProperty.Register(
        nameof(Charging), typeof(bool), typeof(BatteryGauge),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FullProperty = DependencyProperty.Register(
        nameof(Full), typeof(bool), typeof(BatteryGauge),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Percent
    {
        get => (int)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public bool Charging
    {
        get => (bool)GetValue(ChargingProperty);
        set => SetValue(ChargingProperty, value);
    }

    public bool Full
    {
        get => (bool)GetValue(FullProperty);
        set => SetValue(FullProperty, value);
    }

    public BatteryGauge()
    {
        NRadioManager.Infrastructure.ThemeManager.ThemeChanged += InvalidateVisual;
        Unloaded += (_, _) => NRadioManager.Infrastructure.ThemeManager.ThemeChanged -= InvalidateVisual;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 8 || h <= 8) return;

        var body = new Rect(1, h * 0.14, Math.Max(1, w - 15), h * 0.72);
        var radius = Math.Min(8, body.Height / 3);
        var outline = Brush("B.TextMuted", Color.FromRgb(0x8B, 0x9A, 0xAB));
        var border = new Pen(outline, 1.6);
        border.Freeze();

        dc.DrawRoundedRectangle(null, border, body, radius, radius);
        var nub = new Rect(body.Right + 2, body.Top + body.Height * 0.3, 7, body.Height * 0.4);
        dc.DrawRoundedRectangle(outline, null, nub, 2, 2);

        if (Percent < 0) return;
        var pct = Math.Clamp(Percent, 0, 100);
        var inner = new Rect(body.Left + 3, body.Top + 3, Math.Max(0, body.Width - 6), Math.Max(0, body.Height - 6));
        var fillWidth = inner.Width * pct / 100.0;
        if (fillWidth < 1) return;

        var color = Charging
            ? ColorOf("B.Success", Color.FromRgb(0x22, 0xC5, 0x5E))
            : Full
                ? ColorOf("B.Cyan", Color.FromRgb(0x22, 0xD3, 0xEE))
                : pct <= 20
                    ? ColorOf("B.Danger", Color.FromRgb(0xEF, 0x44, 0x44))
                    : ColorOf("B.Accent", Color.FromRgb(0x3B, 0x82, 0xF6));
        var fill = new SolidColorBrush(color);
        fill.Freeze();
        dc.PushClip(new RectangleGeometry(inner, 4, 4));
        dc.DrawRectangle(fill, null, new Rect(inner.Left, inner.Top, fillWidth, inner.Height));
        dc.Pop();

        if (Charging)
        {
            // 简单闪电标记，避免用字符字体导致跨机器缺字。
            var cx = inner.Left + inner.Width / 2;
            var cy = inner.Top + inner.Height / 2;
            var bolt = new StreamGeometry();
            using (var ctx = bolt.Open())
            {
                ctx.BeginFigure(new Point(cx + 2, cy - inner.Height * 0.34), true, true);
                ctx.LineTo(new Point(cx - 7, cy + 2), true, false);
                ctx.LineTo(new Point(cx - 1, cy + 2), true, false);
                ctx.LineTo(new Point(cx - 3, cy + inner.Height * 0.34), true, false);
                ctx.LineTo(new Point(cx + 7, cy - 3), true, false);
                ctx.LineTo(new Point(cx + 1, cy - 3), true, false);
            }
            bolt.Freeze();
            dc.DrawGeometry(BrushStatic(Colors.White), null, bolt);
        }
    }

    private Brush Brush(string key, Color fallback)
        => TryFindResource(key) as SolidColorBrush ?? BrushStatic(fallback);

    private Color ColorOf(string key, Color fallback)
        => TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    private static Brush BrushStatic(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }
}
