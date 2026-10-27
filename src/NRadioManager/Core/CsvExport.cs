using System.Globalization;
using System.IO;
using System.Text;

namespace NRadioManager.Core;

public sealed record TrafficCsvRow(string Period, long Rx, long Tx, bool FromLocalSampling);

public sealed record TerminalCsvRow(
    string Name,
    string Hostname,
    string Mac,
    string Ip,
    string ConnType,
    string Status,
    long? Rx,
    long? Tx,
    long? MonthRx,
    long? MonthTx,
    long? ThreeDayRx,
    long? ThreeDayTx);

/// <summary>
/// CSV 导出（不引入第三方库）。终端名称、MAC、IP 默认脱敏；
/// 同时防止以 = + - @ 开头的单元格被表格软件当作公式执行。
/// </summary>
public static class CsvExport
{
    public static string BuildTrafficCsv(IEnumerable<TrafficCsvRow> rows, DateTime generatedAt, string scopeText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("统计周期,下载字节,上传字节,合计字节,下载,上传,合计,数据口径");
        foreach (var row in rows)
        {
            var total = row.Rx + row.Tx;
            sb.AppendLine(string.Join(",",
                Cell(row.Period),
                Cell(row.Rx.ToString(CultureInfo.InvariantCulture)),
                Cell(row.Tx.ToString(CultureInfo.InvariantCulture)),
                Cell(total.ToString(CultureInfo.InvariantCulture)),
                Cell(J.Bytes(row.Rx)),
                Cell(J.Bytes(row.Tx)),
                Cell(J.Bytes(total)),
                Cell(row.FromLocalSampling ? "本机采样" : "设备口径")));
        }
        sb.AppendLine();
        sb.AppendLine(Cell("导出时间") + "," + Cell(generatedAt.ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine(Cell("统计范围") + "," + Cell(scopeText));
        return sb.ToString();
    }

    public static string BuildTerminalCsv(IEnumerable<TerminalCsvRow> rows, DateTime generatedAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("名称（脱敏）,主机名（脱敏）,MAC（脱敏）,IP（脱敏）,连接方式,状态,累计下载字节,累计上传字节,本月下载字节,本月上传字节,近3日下载字节,近3日上传字节");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",",
                Cell(MaskText(row.Name)),
                Cell(MaskText(row.Hostname)),
                Cell(MaskMac(row.Mac)),
                Cell(MaskIp(row.Ip)),
                Cell(row.ConnType),
                Cell(row.Status),
                Cell(Number(row.Rx)),
                Cell(Number(row.Tx)),
                Cell(Number(row.MonthRx)),
                Cell(Number(row.MonthTx)),
                Cell(Number(row.ThreeDayRx)),
                Cell(Number(row.ThreeDayTx))));
        }
        sb.AppendLine();
        sb.AppendLine(Cell("导出时间") + "," + Cell(generatedAt.ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine(Cell("脱敏说明") + "," +
                      Cell("名称、主机名保留首尾字符；MAC 保留前两段与后两段；IP 最后一段替换为 ***"));
        return sb.ToString();
    }

    public static string MaskText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "—") return "—";
        var text = value.Trim();
        if (text.Length <= 2) return new string('*', text.Length);
        var keep = Math.Min(2, (text.Length - 1) / 2);
        var stars = new string('*', Math.Max(3, text.Length - keep * 2));
        return text[..keep] + stars + text[^keep..];
    }

    public static string MaskMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "—") return "—";
        var parts = value.Trim().Split(':', '-');
        if (parts.Length < 6) return MaskText(value);
        return $"{parts[0]}:{parts[1]}:**:**:{parts[^2]}:{parts[^1]}";
    }

    public static string MaskIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "—") return "—";
        if (!System.Net.IPAddress.TryParse(value.Trim(), out var address)) return MaskText(value);
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var parts = value.Trim().Split('.');
            return string.Join(".", parts.Take(3)) + ".***";
        }
        return MaskText(value);
    }

    public static string MaskEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        if (!Uri.TryCreate(RouterClient.NormalizeHost(value), UriKind.Absolute, out var uri))
            return MaskText(value);
        var maskedHost = System.Net.IPAddress.TryParse(uri.Host, out _) ? MaskIp(uri.Host) : MaskText(uri.Host);
        return $"{uri.Scheme}://{maskedHost}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}";
    }

    /// <summary>把任意诊断文本中的 IPv4 与 MAC 地址替换为脱敏形式。</summary>
    public static string MaskSensitiveText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = System.Text.RegularExpressions.Regex.Replace(
            value, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", m => MaskIp(m.Value));
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", m => MaskMac(m.Value));
        return text;
    }

    public static string Escape(string? value)
    {
        var text = value ?? "";

        // CSV 公式注入防护：Excel/表格软件会把这类前缀当作公式。
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            text = "'" + text;

        if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        return text;
    }

    public static void WriteUtf8Bom(string path, string content)
        => File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    private static string Cell(string? value) => Escape(value);
    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
}
