using System.Text;

namespace NRadioManager.Core;

/// <summary>
/// 设备的文本字段存在两种 HEX 编码，均已在真机核对：
///
/// 1) SSID（UTF-16 大端，每字符 4 位十六进制）——与原厂前端 UniEncode/UniDecode 完全一致：
///    004D006900460069002D0031003300320042 → "MiFi-132B"
///    写入时必须用同一规则反向编码，否则设备端显示乱码。
///
/// 2) 终端名（单字节 ASCII 的十六进制，可能带空格分隔）：
///    " 44 45 53 4b 54 4f 50 2d 55 33 34 34 34 54 50" → "DESKTOP-U3444TP"
///
/// 注意：原厂前端对终端名也调用了 UniDecode（按 4 位分组），在上述空格分隔格式下会解出乱码，
/// 属固件与网页脚本不一致的历史问题。本软件按字段语义分别解码，并保证任何情况下都不输出乱码。
/// </summary>
public static class HexText
{
    private static string Compact(string? hex)
        => hex is null ? string.Empty : new string(hex.Where(Uri.IsHexDigit).ToArray());

    /// <summary>解码 SSID 等 UTF-16 字段（每 4 位十六进制一个字符，大端）。</summary>
    public static string DecodeUtf16(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return string.Empty;
        var compact = Compact(hex);
        if (compact.Length < 4 || compact.Length % 4 != 0) return hex.Trim();

        var sb = new StringBuilder(compact.Length / 4);
        for (var i = 0; i < compact.Length; i += 4)
        {
            if (!ushort.TryParse(compact.AsSpan(i, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
                return hex.Trim();
            sb.Append((char)code);
        }
        var text = sb.ToString().TrimEnd('\0');
        return text.Any(char.IsControl) ? hex.Trim() : text;
    }

    /// <summary>把文本编码为设备要求的 UTF-16 大端 HEX（与原厂 UniEncode 等价）。</summary>
    public static string Encode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length * 4);
        foreach (var ch in text) sb.Append(((int)ch).ToString("X4"));
        return sb.ToString();
    }

    /// <summary>
    /// 解码终端名：优先按单字节 ASCII hex 解释（真机实测格式），
    /// 若解出控制字符则退回 UTF-16 规则；两者都失败时原样返回设备值，保证不显示乱码也不丢信息。
    /// </summary>
    public static string DecodeName(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return string.Empty;
        var compact = Compact(hex);
        if (compact.Length >= 2 && compact.Length % 2 == 0)
        {
            var bytes = new byte[compact.Length / 2];
            var ok = true;
            for (var i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(compact.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
            {
                var ascii = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                if (!ascii.Any(char.IsControl)) return ascii;
            }
        }

        var utf16 = DecodeUtf16(hex);
        return utf16.Any(char.IsControl) ? hex.Trim() : utf16;
    }
}
