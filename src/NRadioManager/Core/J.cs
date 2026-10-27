using System.Globalization;

namespace NRadioManager.Core;

/// <summary>
/// 设备返回的所有字段都是字符串（含数字与布尔），这里集中做安全转换。
/// 转换失败一律返回 null，绝不编造默认值冒充真实数据。
/// </summary>
public static class J
{
    /// <summary>全局流量单位偏好，由设置中心写入。</summary>
    public static TrafficUnit Unit { get; set; } = TrafficUnit.Auto;

    public static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static long? Long(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static double? Double(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static bool? Bool(string? s) => s switch
    {
        "1" => true,
        "0" => false,
        _ => bool.TryParse(s, out var v) ? v : null,
    };

    /// <summary>字节数格式化为可读文本。</summary>
    public static string Bytes(long? value)
    {
        if (value is null) return "—";
        double v = value.Value;

        switch (Unit)
        {
            case TrafficUnit.MB:
                return $"{v / 1024 / 1024:0.##} MB";
            case TrafficUnit.GB:
                return $"{v / 1024 / 1024 / 1024:0.###} GB";
            default:
                string[] units = ["B", "KB", "MB", "GB", "TB"];
                var i = 0;
                while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
                return i == 0 ? $"{(long)v} {units[i]}" : $"{v:0.##} {units[i]}";
        }
    }

    /// <summary>比特率格式化（设备下发的是 B/s）。</summary>
    public static string Rate(long? bytesPerSecond)
        => bytesPerSecond is null ? "—" : Bytes(bytesPerSecond) + "/s";

    public static string Duration(long? seconds)
    {
        if (seconds is null) return "—";
        var t = TimeSpan.FromSeconds(seconds.Value);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays} 天 {t.Hours} 小时 {t.Minutes} 分" : $"{t.Hours} 小时 {t.Minutes} 分 {t.Seconds} 秒";
    }
}
