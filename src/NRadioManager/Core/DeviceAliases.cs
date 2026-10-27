using System.IO;
using System.Text.Json;

namespace NRadioManager.Core;

/// <summary>
/// 设备自定义名称。以 MAC 为主键（IP 变化不影响识别），单独保存，不覆盖设备原始主机名。
/// </summary>
public sealed class DeviceAliases
{
    private Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
    private static string FilePath => Path.Combine(AppSettings.DataDir, "device-names.json");

    public static DeviceAliases Load()
    {
        var a = new DeviceAliases();
        try
        {
            if (File.Exists(FilePath))
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
                if (map is not null) a._map = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            a.LastLoadError = $"{ex.GetType().Name}: {ex.Message}";
        }
        return a;
    }

    public string? LastLoadError { get; private set; }
    public string? LastSaveError { get; private set; }

    public string? Get(string? mac)
        => string.IsNullOrWhiteSpace(mac) ? null : _map.TryGetValue(Normalize(mac), out var v) ? v : null;

    public void Set(string? mac, string? name)
    {
        if (string.IsNullOrWhiteSpace(mac)) return;
        var key = Normalize(mac);
        if (string.IsNullOrWhiteSpace(name)) _map.Remove(key);
        else _map[key] = name.Trim();
        Save();
    }

    public bool Has(string? mac) => !string.IsNullOrWhiteSpace(mac) && _map.ContainsKey(Normalize(mac));

    public int Count => _map.Count;

    private static string Normalize(string mac) => mac.Trim().ToLowerInvariant().Replace('-', ':');

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true }));
            LastSaveError = null;
        }
        catch (Exception ex)
        {
            LastSaveError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>导出给"备份/恢复"使用。</summary>
    public Dictionary<string, string> Export() => new(_map);

    public void Import(Dictionary<string, string>? map)
    {
        if (map is null) return;
        foreach (var kv in map) if (!string.IsNullOrWhiteSpace(kv.Key)) _map[Normalize(kv.Key)] = kv.Value;
        Save();
    }
}
