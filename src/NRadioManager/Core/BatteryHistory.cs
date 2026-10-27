using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NRadioManager.Core;

/// <summary>
/// 设备可确认的充电状态。
/// 依据原厂 js/base.js：battery_charge 0=未充电、1=充电中、2/3=已充满；
/// battery_charging=1 也按充电中处理；两个字段明确冲突时返回 Unknown，不猜一个覆盖另一个。
/// </summary>
public enum BatteryChargeState { Unknown, Discharging, Charging, Full }

public static class BatteryState
{
    public static BatteryChargeState Of(int? chargeRaw, bool? charging)
    {
        if (chargeRaw is 2 or 3) return BatteryChargeState.Full;
        if (chargeRaw == 1)
            return charging == false ? BatteryChargeState.Unknown : BatteryChargeState.Charging;
        if (chargeRaw == 0)
            return charging == true ? BatteryChargeState.Unknown : BatteryChargeState.Discharging;
        if (charging == true) return BatteryChargeState.Charging;
        if (charging == false) return BatteryChargeState.Discharging;
        return BatteryChargeState.Unknown;
    }

    public static string Text(BatteryChargeState state) => state switch
    {
        BatteryChargeState.Charging => "充电中",
        BatteryChargeState.Full => "已充满",
        BatteryChargeState.Discharging => "未充电",
        _ => "未知",
    };

    public static string Describe(int? chargeRaw, bool? charging)
        => $"{Text(Of(chargeRaw, charging))}（设备 battery_charge={chargeRaw?.ToString() ?? "—"}，" +
           $"battery_charging={charging?.ToString() ?? "—"}）";
}

/// <summary>
/// 一次电池采样。只保存设备真实返回的字段；不可用时保存“缺失标记”，绝不补默认值。
/// </summary>
public sealed class BatterySample
{
    [JsonPropertyName("t")] public DateTime TimeUtc { get; set; }
    [JsonPropertyName("p")] public int? Percent { get; set; }
    [JsonPropertyName("cr")] public int? ChargeRaw { get; set; }
    [JsonPropertyName("ch")] public bool? Charging { get; set; }
    /// <summary>battery_connect 原值。原厂页面未使用该字段，语义未确认，仅作原始记录。</summary>
    [JsonPropertyName("bc")] public int? BatteryConnectRaw { get; set; }
    [JsonPropertyName("run")] public long? RunSeconds { get; set; }
    /// <summary>采样时软件与设备是否处于连接状态。</summary>
    [JsonPropertyName("con")] public bool Connected { get; set; } = true;
    /// <summary>设备是否上报了有效电量。</summary>
    [JsonPropertyName("av")] public bool Available { get; set; } = true;

    [JsonIgnore] public bool IsUsable => Connected && Available && Percent is >= 0 and <= 100;
    [JsonIgnore] public BatteryChargeState State => BatteryState.Of(ChargeRaw, Charging);
    [JsonIgnore] public DateTime TimeLocal => NormalizeUtc(TimeUtc).ToLocalTime();

    internal static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };
}

public enum BatterySampleResult { Recorded, Skipped, Duplicate, TimeRollback, TimeInvalid }

/// <summary>
/// 电池本地历史（轻量 JSON，按需低频采样）。
///
/// 只记录 status_info 实际提供的 battery_percent / battery_charge / battery_charging /
/// battery_connect / run_seconds；不推算电压、电流、温度、剩余时间或 Wh。
/// 设备断连时写入 Connected=false 的标记，设备未上报电量时写入 Available=false 的标记，
/// 图表据此断开曲线并区分“断连”和“采样缺失”。
/// </summary>
public sealed class BatteryHistory
{
    private const int FormatVersion = 1;
    private const int MaxSamples = 100_000;
    private const int MinStateChangeGapSeconds = 10;

    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly List<BatterySample> _samples = new();
    private readonly string _path;
    private DateTime _lastSaveUtc = DateTime.MinValue;
    private bool _dirty;

    private BatteryHistory(string path) => _path = path;

    public static string DefaultPath => Path.Combine(AppSettings.DataDir, "battery-history.json");

    public static BatteryHistory Load(int retentionDays, string? path = null)
    {
        var history = new BatteryHistory(path ?? DefaultPath);
        history.LoadFile(Math.Clamp(retentionDays, 1, 3650));
        return history;
    }

    /// <summary>主文件损坏时尝试 .bak；仍失败则从空历史开始，并把原因暴露给界面。</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>最近一次保存失败的原因；成功保存后清空。</summary>
    public string? LastSaveError { get; private set; }

    public int Count { get { lock (_gate) return _samples.Count; } }

    public BatterySample? Latest
    {
        get { lock (_gate) return _samples.Count == 0 ? null : _samples[^1]; }
    }

    public DateTime? FirstTimeUtc
    {
        get { lock (_gate) return _samples.Count == 0 ? null : _samples[0].TimeUtc; }
    }

    public DateTime? LastTimeUtc
    {
        get { lock (_gate) return _samples.Count == 0 ? null : _samples[^1].TimeUtc; }
    }

    private void LoadFile(int retentionDays)
    {
        try
        {
            if (!File.Exists(_path)) return;
            var file = JsonSerializer.Deserialize<BatteryHistoryFile>(File.ReadAllText(_path));
            if (file?.Samples is null) return;
            ReplaceSanitized(file.Samples, retentionDays);
        }
        catch
        {
            try
            {
                var backup = _path + ".bak";
                if (File.Exists(backup))
                {
                    var file = JsonSerializer.Deserialize<BatteryHistoryFile>(File.ReadAllText(backup));
                    if (file?.Samples is not null)
                    {
                        ReplaceSanitized(file.Samples, retentionDays);
                        LoadWarning = "主电池历史文件损坏，已从备份恢复。";
                        return;
                    }
                }
            }
            catch
            {
                // 备份也不可用时从空历史开始
            }
            LoadWarning = "电池历史文件损坏，已从空历史开始（新的采样仍会正常保存）。";
        }
    }

    private void ReplaceSanitized(IEnumerable<BatterySample> samples, int retentionDays)
    {
        var now = DateTime.UtcNow;
        var minTime = now.AddDays(-Math.Clamp(retentionDays, 1, 3650));
        var valid = new List<BatterySample>();
        foreach (var s in samples)
        {
            if (s is null || s.TimeUtc == default || s.TimeUtc.Year < 2000) continue;
            s.TimeUtc = BatterySample.NormalizeUtc(s.TimeUtc);
            if (s.TimeUtc > now.AddDays(2)) continue; // 系统时间错乱产生的未来点
            if (s.Percent is < 0 or > 100)
            {
                s.Percent = null;
                s.Available = false;
            }
            if (!s.Available) s.Percent = null;
            if (s.TimeUtc < minTime) continue;
            valid.Add(s);
        }

        valid.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
        var deduped = new List<BatterySample>(valid.Count);
        foreach (var s in valid)
        {
            if (deduped.Count > 0 && deduped[^1].TimeUtc == s.TimeUtc)
                deduped[^1] = s;
            else
                deduped.Add(s);
        }

        lock (_gate)
        {
            _samples.Clear();
            _samples.AddRange(deduped);
            if (_samples.Count > MaxSamples)
                _samples.RemoveRange(0, _samples.Count - MaxSamples);
        }
    }

    /// <summary>
    /// 按配置间隔记录一次采样。电量变化本身不绕过间隔；充电/断连等状态变化可提前记录（至少间隔 10 秒），
    /// 避免 5 秒轮询把历史文件撑爆。
    /// </summary>
    public BatterySampleResult MaybeSample(StatusInfo status, DateTime utcNow,
        int sampleSeconds, int retentionDays, out BatterySample? sample)
    {
        utcNow = BatterySample.NormalizeUtc(utcNow);
        if (utcNow > DateTime.UtcNow.AddMinutes(2))
        {
            sample = null;
            return BatterySampleResult.TimeInvalid;
        }

        var percent = J.Int(status.BatteryPercent);
        var chargeRaw = J.Int(status.BatteryCharge);
        var charging = J.Bool(status.BatteryCharging);
        var connectRaw = J.Int(status.BatteryConnect);
        var runSeconds = J.Long(status.RunSeconds);
        var available = percent is >= 0 and <= 100;

        sample = null;
        lock (_gate)
        {
            var last = _samples.Count == 0 ? null : _samples[^1];
            if (last is not null)
            {
                if (utcNow <= last.TimeUtc) return BatterySampleResult.TimeRollback;
                var elapsed = utcNow - last.TimeUtc;
                if (elapsed.TotalSeconds < 1) return BatterySampleResult.Duplicate;

                var stateChanged = last.Connected != true || last.Available != available ||
                                   last.State != BatteryState.Of(chargeRaw, charging);
                var interval = Math.Max(15, sampleSeconds);
                if (elapsed.TotalSeconds < interval &&
                    !(stateChanged && elapsed.TotalSeconds >= MinStateChangeGapSeconds))
                    return BatterySampleResult.Skipped;

                // 连续不可用不重复落盘，只在“可用→不可用”的转折点写一个缺失标记。
                if (!available && !last.Available) return BatterySampleResult.Skipped;
            }

            sample = new BatterySample
            {
                TimeUtc = utcNow,
                Percent = available ? percent : null,
                ChargeRaw = chargeRaw,
                Charging = charging,
                BatteryConnectRaw = connectRaw,
                RunSeconds = runSeconds,
                Connected = true,
                Available = available,
            };
            _samples.Add(sample);
            _dirty = true;
            Prune(utcNow, retentionDays);
            return BatterySampleResult.Recorded;
        }
    }

    /// <summary>设备断连：写一个断开标记，图表会把这段区间标成“设备断连”。</summary>
    public void MarkDisconnected(DateTime utcNow)
    {
        lock (_gate)
        {
            var last = _samples.Count == 0 ? null : _samples[^1];
            if (last is { Connected: false }) return;
            if (last is not null && utcNow <= last.TimeUtc) return;
            _samples.Add(new BatterySample
            {
                TimeUtc = utcNow,
                Percent = null,
                Connected = false,
                Available = false,
            });
            _dirty = true;
        }
    }

    public IReadOnlyList<BatterySample> Snapshot(DateTime startUtc, DateTime endUtc)
    {
        lock (_gate)
        {
            return _samples.Where(s => s.TimeUtc >= startUtc && s.TimeUtc <= endUtc).ToList();
        }
    }

    public IReadOnlyList<BatterySample> AllSnapshot()
    {
        lock (_gate) return _samples.ToList();
    }

    public void SaveIfDue(DateTime utcNow, int retentionDays, TimeSpan? minInterval = null)
    {
        var interval = minInterval ?? TimeSpan.FromMinutes(5);
        lock (_saveGate)
        {
            lock (_gate)
            {
                if (!_dirty) return;
                if (_lastSaveUtc != DateTime.MinValue && utcNow - _lastSaveUtc < interval) return;
            }
            SaveNowCore(utcNow, retentionDays);
        }
    }

    public bool SaveNow(DateTime utcNow, int retentionDays)
    {
        lock (_saveGate)
            return SaveNowCore(utcNow, retentionDays);
    }

    private bool SaveNowCore(DateTime utcNow, int retentionDays)
    {
        try
        {
            string json;
            lock (_gate)
            {
                Prune(utcNow, retentionDays);
                json = JsonSerializer.Serialize(new BatteryHistoryFile
                {
                    Version = FormatVersion,
                    Samples = _samples,
                });
            }

            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(_path))
            {
                try { File.Copy(_path, _path + ".bak", overwrite: true); } catch { }
            }
            File.Move(temp, _path, overwrite: true);
            lock (_gate)
            {
                _lastSaveUtc = utcNow;
                _dirty = false;
            }
            LastSaveError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastSaveError = ex.Message;
            return false;
        }
    }

    public void Reset(int retentionDays)
    {
        lock (_gate)
        {
            _samples.Clear();
            _dirty = true;
        }
        SaveNow(DateTime.UtcNow, retentionDays);
    }

    /// <summary>按保留期限裁剪；硬上限 10 万条，避免异常配置或时间跳变导致无限增长。</summary>
    public void Prune(DateTime utcNow, int retentionDays)
    {
        var cutoff = utcNow.AddDays(-Math.Clamp(retentionDays, 1, 3650));
        lock (_gate)
        {
            _samples.RemoveAll(s => s.TimeUtc < cutoff);
            if (_samples.Count > MaxSamples)
                _samples.RemoveRange(0, _samples.Count - MaxSamples);
        }
    }

    private sealed class BatteryHistoryFile
    {
        [JsonPropertyName("Version")] public int Version { get; set; } = FormatVersion;
        [JsonPropertyName("Samples")] public List<BatterySample> Samples { get; set; } = new();
    }
}
