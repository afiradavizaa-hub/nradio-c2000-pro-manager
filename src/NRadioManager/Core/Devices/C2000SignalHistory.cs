using System.Globalization;
using System.IO;
using System.Text.Json;

namespace NRadioManager.Core;

public sealed record C2000SignalSample(
    DateTime Time,
    double? RsrpDbm,
    double? RsrqDb,
    double? SinrDb,
    bool Connected,
    string CellId,
    string Band,
    string Earfcn,
    string Pci);

/// <summary>C2000 Pro 信号历史。只保存设备真实返回值；断连点与缺失指标保留 null。</summary>
public sealed class C2000SignalHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly int _retentionDays;
    private readonly List<C2000SignalSample> _samples = new();
    private DateTime _lastSample;
    private int _unsaved;

    public C2000SignalHistory(int retentionDays)
    {
        _retentionDays = Math.Clamp(retentionDays, 7, 365);
        FilePath = Path.Combine(AppSettings.DataDir, "c2000-signal-history.json");
        Load();
    }

    public string FilePath { get; }
    public string? LastSaveError { get; private set; }
    public IReadOnlyList<C2000SignalSample> Samples => _samples;

    public bool Observe(C2000ProRuntimeSnapshot snapshot, DateTime now)
    {
        if (_lastSample != default && (now - _lastSample).TotalSeconds < 2) return false;
        _lastSample = now;
        var c = snapshot.Cell.Status;
        var sample = new C2000SignalSample(
            now,
            Number(c?.Rsrp),
            Number(c?.Rsrq),
            Number(c?.Sinr),
            c?.Status == 0 && !string.IsNullOrWhiteSpace(c.Isp),
            c?.Cell ?? "",
            c?.Band ?? "",
            c?.Earfcn ?? "",
            c?.Pci ?? "");

        var last = _samples.LastOrDefault();
        if (last is not null &&
            last.RsrpDbm == sample.RsrpDbm &&
            last.RsrqDb == sample.RsrqDb &&
            last.SinrDb == sample.SinrDb &&
            last.Connected == sample.Connected &&
            last.CellId == sample.CellId &&
            (now - last.Time).TotalSeconds < 10)
            return false;

        _samples.Add(sample);
        _unsaved++;
        Prune(now);
        return true;
    }

    public IReadOnlyList<C2000SignalSample> InRange(DateTime from, DateTime to)
        => _samples.Where(x => x.Time >= from && x.Time <= to).ToArray();

    public bool Save(bool force = false)
    {
        if (!force && _unsaved < 12) return true;
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_samples, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
            _unsaved = 0;
            LastSaveError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastSaveError = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var loaded = JsonSerializer.Deserialize<List<C2000SignalSample>>(File.ReadAllText(FilePath), JsonOptions);
            if (loaded is not null) _samples.AddRange(loaded);
            Prune(DateTime.Now);
        }
        catch (Exception ex)
        {
            LastSaveError = "历史读取失败：" + ex.Message;
        }
    }

    private void Prune(DateTime now)
    {
        var cutoff = now.AddDays(-_retentionDays);
        _samples.RemoveAll(x => x.Time < cutoff);
        if (_samples.Count > 100_000)
            _samples.RemoveRange(0, _samples.Count - 100_000);
    }

    private static double? Number(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
}
