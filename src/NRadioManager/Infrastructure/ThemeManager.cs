using System.Windows;
using System.Windows.Media;
using NRadioManager.Core;
using Microsoft.Win32;

namespace NRadioManager.Infrastructure;

/// <summary>
/// 主题与外观管理：切换深浅色、强调色、磁贴圆角/间距/内边距。
/// 所有颜色都以 DynamicResource 暴露，因此切换后全部页面与控件同步生效。
/// </summary>
public static class ThemeManager
{
    /// <summary>当前是否为深色（跟随系统时按系统设置解析）。</summary>
    public static bool EffectiveDark { get; private set; } = true;

    public static event Action? ThemeChanged;

    public static void Apply(AppSettings s)
    {
        var app = Application.Current;
        if (app is null) return;

        EffectiveDark = s.Theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };

        var source = new Uri(EffectiveDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = source };

        // 约定：应用资源里第 0 个字典是主题色板，第 1 个是控件样式与通用尺寸
        if (app.Resources.MergedDictionaries.Count == 0) app.Resources.MergedDictionaries.Add(dict);
        else app.Resources.MergedDictionaries[0] = dict;

        ApplyAccent(s.AccentColor, EffectiveDark);

        app.Resources["R.TileRadius"] = new CornerRadius(s.TileCornerRadius);
        app.Resources["R.TileSpacing"] = s.TileSpacing;
        app.Resources["R.TilePadding"] = new Thickness(s.TilePadding);

        ThemeChanged?.Invoke();
    }

    /// <summary>强调色：主色直接使用，柔色按主题底色混合得到，保证文字可读。</summary>
    public static void ApplyAccent(string hex, bool dark)
    {
        var app = Application.Current;
        if (app is null) return;

        Color accent;
        try
        {
            accent = (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            accent = Color.FromRgb(0x3B, 0x82, 0xF6);
        }

        var baseColor = dark ? Color.FromRgb(0x16, 0x1D, 0x26) : Color.FromRgb(0xFF, 0xFF, 0xFF);
        var soft = Blend(accent, baseColor, dark ? 0.22 : 0.16);

        app.Resources["B.Accent"] = new SolidColorBrush(accent);
        app.Resources["B.AccentSoft"] = new SolidColorBrush(soft);
        app.Resources["B.AccentText"] = new SolidColorBrush(ContrastText(accent));
    }

    private static Color Blend(Color a, Color b, double ratio) => Color.FromRgb(
        (byte)(a.R * ratio + b.R * (1 - ratio)),
        (byte)(a.G * ratio + b.G * (1 - ratio)),
        (byte)(a.B * ratio + b.B * (1 - ratio)));

    /// <summary>根据背景亮度选择文字颜色，避免强调色太亮时白字看不清。</summary>
    private static Color ContrastText(Color c)
    {
        var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return luminance > 0.62 ? Color.FromRgb(0x10, 0x16, 0x1E) : Colors.White;
    }

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            return v is int i && i == 0;
        }
        catch
        {
            return true;
        }
    }
}
