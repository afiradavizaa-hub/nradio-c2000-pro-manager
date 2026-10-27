using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace NRadioManager.Infrastructure;

/// <summary>
/// 窗口外观修复。
///
/// 白边的真实来源：本软件是深色界面，但 Windows 仍按系统主题绘制非客户区
/// （标题栏 + 1 像素窗口边框 + 客户区外的系统底色）。系统处于浅色主题时，
/// 这一圈就是白的，视觉上表现为窗口四周有明显的白边。
///
/// 修复方式是让 DWM 用深色标题栏并把边框/标题栏颜色对齐到软件背景色，
/// **不使用**透明窗口或自绘边框去掩盖问题，因此拖动、缩放、最小化、最大化、关闭等原生行为完全保留。
/// </summary>
internal static class WindowChromeHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaBorderColor = 34;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Window window, bool dark, Color background)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var flag = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref flag, sizeof(int));

            // COLORREF 是 0x00BBGGRR
            var caption = background.R | (background.G << 8) | (background.B << 16);
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));

            // 边框颜色与标题栏一致，消除浅色主题下的那一圈白线
            var border = caption;
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch
        {
            // 旧版 Windows 不支持这些属性时忽略：仅是外观差异，不影响功能
        }
    }

    public static Color SurfaceColor(Window window, bool fallbackDark)
    {
        try
        {
            var brush = window.TryFindResource("B.Surface") as SolidColorBrush;
            if (brush is not null) return brush.Color;
        }
        catch
        {
            // 资源尚未加载
        }
        return fallbackDark ? Color.FromRgb(0x16, 0x1D, 0x26) : Colors.White;
    }
}
