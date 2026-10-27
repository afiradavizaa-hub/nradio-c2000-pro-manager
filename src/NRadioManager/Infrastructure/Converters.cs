using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NRadioManager.Infrastructure;

/// <summary>value &gt;= parameter 时返回 true，用于信号格等分级显示。</summary>
public sealed class GreaterOrEqualConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var left = value is null ? 0 : System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        var right = parameter is null ? 0 : System.Convert.ToDouble(parameter, CultureInfo.InvariantCulture);
        return left >= right;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>非空字符串 → Visible。</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔取反，用于「隐藏 SSID」这类与设备字段相反的勾选项。</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : (object)false;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : (object)false;
}

/// <summary>数值为 0 时显示（用于空列表占位提示）。</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value is null ? 0 : System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return n <= 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
