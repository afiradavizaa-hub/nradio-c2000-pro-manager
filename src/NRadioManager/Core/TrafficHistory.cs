using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NRadioManager.Core;

public sealed class ByteBucket
{
    [JsonPropertyName("rx")] public long Rx { get; set; }
    [JsonPropertyName("tx")] public long Tx { get; set; }
    [JsonIgnore] public long Total => Rx + Tx;

    public void Add(long rx, long tx) { Rx += rx; Tx += tx; }
}

public sealed class DeviceHistory
{
    public string? LastHostname { get; set; }
    public string? LastIp { get; set; }
    public long LastRx { get; set; } = -1;
    public long LastTx { get; set; } = -1;
    public Dictionary<string, ByteBucket> Daily { get; set; } = new();
    public Dictionary<string, ByteBucket> Hourly { get; set; } = new();
}

public sealed class HistoryFile
{
    public int Version { get; set; } = 2;
    public DateTime? FirstRecordedUtc { get; set; }
    public DateTime? LastSampleUtc { get; set; }
    /// <summary>软件未运行期间无法归属到任何一天的字节数（如实记录，不摊派到某天）。</summary>
    public long UncapturedBytes { get; set; }
    public long DeviceLastRx { get; set; } = -1;
    public long DeviceLastTx { get; set; } = -1;
    public Dictionary<string, ByteBucket> DeviceDaily { get; set; } = new();
    public Dictionary<string, ByteBucket> DeviceHourly { get; set; } = new();
    public Dictionary<string, DeviceHistory> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record TrafficPeriod(string Key, string Label, long Rx, long Tx, bool FromLocalSampling)
{
    public long Total => Rx + Tx;
}

/// <summary>
/// 本地流量历史。
///
/// 设备能力（真机核实）：各终端只提供**累计** rx/tx，另有本月（rxm/txm）与近 3 日（rx3/tx3）两个现成计数器，
/// 但**没有任何小时级、日级历史记录**，也没有 DNS/连接日志。
/// 因此：
///   · 「本月」既有设备口径（直接读取），也有本地口径（按日累加）——界面分别标注来源；
///   · 「当日」「近七天」只能由本地采样得出，覆盖范围 = 软件实际运行的时段；
///   · 软件未运行期间产生的流量**无法还原**，单独统计为"未采集量"，绝不摊派到某一天充数。
/// </summary>
public sealed class TrafficHistory
{
    /// <summary>两次采样间隔超过该值即视为"未采集区间"，不做归因。</summary>
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromMinutes(3);

    private readonly object _gate = new();
    private readonly HistoryFile _data;
    private readonly int _retentionDays;

    private TrafficHistory(HistoryFile data, int retentionDays)
    {
        _data = data;
        _retentionDays = retentionDays;
    }

    private static string FilePath => Path.Combine(AppSettings.DataDir, "traffic-history.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public static TrafficHistory Load(int retentionDays)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var f = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(FilePath), JsonOpts);
                if (f is not null) return new TrafficHistory(f, retentionDays);
            }
        }
        catch (Exception ex)
        {
            return new TrafficHistory(new HistoryFile(), retentionDays)
            {
                LoadWarning = $"{ex.GetType().Name}: {ex.Message}",
            };
        }
        return new TrafficHistory(new HistoryFile(), retentionDays);
    }

    public string? LoadWarning { get; private set; }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, JsonOpts));
                LastSaveError = null;
            }
            catch (Exception ex)
            {
                LastSaveError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    public string? LastSaveError { get; private set; }

    // ---------------- 状态与覆盖范围 ----------------

    public DateTime? FirstRecordedUtc => _data.FirstRecordedUtc;
    public DateTime? LastSampleUtc => _data.LastSampleUtc;
    public long UncapturedBytes => _data.UncapturedBytes;
    public bool HasLocalHistory => _data.FirstRecordedUtc is not null;

    public string CoverageText
    {
        get
        {
            if (_data.FirstRecordedUtc is null) return "尚无本地采样记录（软件开始运行后才会计数）";
            var from = _data.FirstRecordedUtc.Value.ToLocalTime();
            var to = _data.LastSampleUtc?.ToLocalTime();
            var span = to is null ? "—" : $"{to:yyyy-MM-dd HH:mm:ss}";
            return $"本地采样覆盖：{from:yyyy-MM-dd HH:mm:ss} 起，最后采样 {span}";
        }
    }

    // ---------------- 采样 ----------------

    /// <summary>
    /// 写入一次采样。device 级与终端级都从累计计数器做差；
    /// 计数器回绕（变小）或采样间隔过长时**不做归因**，只更新基线并累计"未采集量"。
    /// </summary>
    public void Sample(StatusInfo status, DeviceManagementInfo? devices, DateTime utcNow)
    {
        lock (_gate)
        {
            var gap = _data.LastSampleUtc is null ? TimeSpan.MaxValue : utcNow - _data.LastSampleUtc.Value;
            var attributable = gap <= MaxSampleGap;
            var dayKey = utcNow.ToLocalTime().ToString("yyyy-MM-dd");
            var hourKey = utcNow.ToLocalTime().ToString("yyyy-MM-ddTHH");
            var maxClientDelta = MaxClientDelta(gap);

            _data.FirstRecordedUtc ??= utcNow;

            // ---- 设备级 ----
            var devLastRx = _data.DeviceLastRx;
            var devLastTx = _data.DeviceLastTx;
            var devDelta = AddCounter(_data.DeviceDaily, _data.DeviceHourly, dayKey, hourKey,
                ref devLastRx, ref devLastTx,
                J.Long(status.RxByteAll) ?? -1, J.Long(status.TxByteAll) ?? -1, attributable,
                long.MaxValue, long.MaxValue, bytes => _data.UncapturedBytes += bytes);
            _data.DeviceLastRx = devLastRx;
            _data.DeviceLastTx = devLastTx;

            // ---- 终端级 ----
            if (devices?.ClientList is not null)
            {
                // 终端流量不可能比整机流量高出数倍；固件失真的计数器跳变在这里被丢弃，只更新基线。
                var maxRx = Math.Max(maxClientDelta, devDelta.Accepted ? devDelta.Rx * 4 : 0);
                var maxTx = Math.Max(maxClientDelta, devDelta.Accepted ? devDelta.Tx * 4 : 0);
                foreach (var c in devices.ClientList)
                {
                    if (string.IsNullOrWhiteSpace(c.Mac)) continue;
                    var mac = c.Mac.ToLowerInvariant();
                    if (!_data.Devices.TryGetValue(mac, out var dh))
                    {
                        dh = new DeviceHistory();
                        _data.Devices[mac] = dh;
                    }
                    dh.LastHostname = c.Name;
                    dh.LastIp = c.Ip;
                    var lastRx = dh.LastRx;
                    var lastTx = dh.LastTx;
                    AddCounter(dh.Daily, dh.Hourly, dayKey, hourKey, ref lastRx, ref lastTx,
                        J.Long(c.Rx) ?? -1, J.Long(c.Tx) ?? -1, attributable, maxRx, maxTx, null);
                    dh.LastRx = lastRx;
                    dh.LastTx = lastTx;
                }
            }

            _data.LastSampleUtc = utcNow;
            Prune(utcNow);
        }
    }

    private readonly record struct CounterDelta(long Rx, long Tx, bool Accepted);

    /// <summary>
    /// 单次采样窗口内单终端的最大可信增量：按 1 Gbps 的 2 倍估算，另加 64 MB 余量。
    /// 真机出现过单终端计数器从 142 GB 跳到 36 TB 的固件故障，这类增量不能进历史。
    /// </summary>
    private static long MaxClientDelta(TimeSpan gap)
    {
        var seconds = Math.Clamp(gap.TotalSeconds, 1, MaxSampleGap.TotalSeconds);
        return (long)(seconds * 125_000_000 * 2) + 64L * 1024 * 1024;
    }

    private static CounterDelta AddCounter(Dictionary<string, ByteBucket> daily, Dictionary<string, ByteBucket> hourly,
        string dayKey, string hourKey, ref long lastRx, ref long lastTx, long rx, long tx, bool attributable,
        long maxDRx, long maxDTx, Action<long>? onUnattributed)
    {
        if (rx < 0 || tx < 0) return default;

        if (lastRx < 0 || lastTx < 0)
        {
            // 首次见到该计数器：只建立基线，不凭空造流量
            lastRx = rx;
            lastTx = tx;
            return default;
        }

        var dRx = rx - lastRx;
        var dTx = tx - lastTx;
        lastRx = rx;
        lastTx = tx;

        if (dRx < 0 || dTx < 0)
        {
            // 计数器回绕/清零（设备重启、统计被重置）：本期差值不可信，丢弃
            return default;
        }

        if (dRx > maxDRx || dTx > maxDTx)
        {
            // 计数器异常跳变（固件失真）：保留新基线，但不计入任何桶
            return new CounterDelta(dRx, dTx, false);
        }

        if (!attributable)
        {
            // 软件未运行期间的增量无法归属到具体某天，单独记账
            onUnattributed?.Invoke(dRx + dTx);
            return new CounterDelta(dRx, dTx, false);
        }

        if (!daily.TryGetValue(dayKey, out var d)) daily[dayKey] = d = new ByteBucket();
        d.Add(dRx, dTx);

        if (!hourly.TryGetValue(hourKey, out var h)) hourly[hourKey] = h = new ByteBucket();
        h.Add(dRx, dTx);

        return new CounterDelta(dRx, dTx, true);
    }

    /// <summary>按保留期限裁剪历史，并把小时级数据限制在近 7 天，避免文件无限增长。</summary>
    private void Prune(DateTime utcNow)
    {
        var cutoffDay = utcNow.ToLocalTime().Date.AddDays(-_retentionDays).ToString("yyyy-MM-dd");
        var cutoffHour = utcNow.ToLocalTime().AddDays(-7).ToString("yyyy-MM-ddTHH");

        PruneMap(_data.DeviceDaily, cutoffDay);
        PruneMap(_data.DeviceHourly, cutoffHour);
        foreach (var dh in _data.Devices.Values)
        {
            PruneMap(dh.Daily, cutoffDay);
            PruneMap(dh.Hourly, cutoffHour);
        }
    }

    private static void PruneMap(Dictionary<string, ByteBucket> map, string cutoffKey)
    {
        if (map.Count < 32) return;
        foreach (var key in map.Keys.Where(k => string.CompareOrdinal(k, cutoffKey) < 0).ToList())
            map.Remove(key);
    }

    // ---------------- 查询 ----------------

    public ByteBucket Day(DateTime localDate, bool deviceLevel)
    {
        lock (_gate)
        {
            var key = localDate.ToString("yyyy-MM-dd");
            return deviceLevel
                ? _data.DeviceDaily.TryGetValue(key, out var d) ? d : new ByteBucket()
                : new ByteBucket();
        }
    }

    public ByteBucket TodayDeviceLevel() => Day(DateTime.Today, true);

    /// <summary>
    /// 每日/每月设备级本地累计（仅统计真实采样写入的桶）。
    /// 该值不会补齐软件未运行期间的流量；调用方必须把“可能不完整”如实展示出来。
    /// </summary>
    public (long Rx, long Tx) PeriodTotal(TrafficThresholdPeriod period, DateTime localNow)
    {
        lock (_gate)
        {
            var prefix = period == TrafficThresholdPeriod.Month
                ? localNow.ToString("yyyy-MM")
                : localNow.ToString("yyyy-MM-dd");

            long rx = 0, tx = 0;
            foreach (var (key, bucket) in _data.DeviceDaily)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                rx += bucket.Rx;
                tx += bucket.Tx;
            }
            return (rx, tx);
        }
    }

    /// <summary>当日按小时（本地采样，仅覆盖软件运行时段）。</summary>
    public IReadOnlyList<TrafficPeriod> TodayHourly(bool deviceLevel)
    {
        lock (_gate)
        {
            var prefix = DateTime.Today.ToString("yyyy-MM-dd") + "T";
            var map = deviceLevel ? _data.DeviceHourly : new Dictionary<string, ByteBucket>();
            return Enumerable.Range(0, 24).Select(h =>
            {
                var key = prefix + h.ToString("D2");
                map.TryGetValue(key, out var b);
                return new TrafficPeriod(key, $"{h:D2}:00", b?.Rx ?? 0, b?.Tx ?? 0, true);
            }).ToList();
        }
    }

    /// <summary>近 N 天（本地采样）。</summary>
    public IReadOnlyList<TrafficPeriod> LastDays(int days, bool deviceLevel)
    {
        lock (_gate)
        {
            var result = new List<TrafficPeriod>();
            for (var i = days - 1; i >= 0; i--)
            {
                var d = DateTime.Today.AddDays(-i);
                var key = d.ToString("yyyy-MM-dd");
                ByteBucket? b = null;
                if (deviceLevel) _data.DeviceDaily.TryGetValue(key, out b);
                result.Add(new TrafficPeriod(key, d.ToString("MM-dd"), b?.Rx ?? 0, b?.Tx ?? 0, true));
            }
            return result;
        }
    }

    /// <summary>本月按天（本地采样）。</summary>
    public IReadOnlyList<TrafficPeriod> MonthDaily(bool deviceLevel)
    {
        lock (_gate)
        {
            var today = DateTime.Today;
            var first = new DateTime(today.Year, today.Month, 1);
            var result = new List<TrafficPeriod>();
            for (var d = first; d <= today; d = d.AddDays(1))
            {
                var key = d.ToString("yyyy-MM-dd");
                ByteBucket? b = null;
                if (deviceLevel) _data.DeviceDaily.TryGetValue(key, out b);
                result.Add(new TrafficPeriod(key, d.Day.ToString(), b?.Rx ?? 0, b?.Tx ?? 0, true));
            }
            return result;
        }
    }

    /// <summary>某一天各终端的流量（本地采样）。</summary>
    public IReadOnlyList<(string Mac, string? Hostname, long Rx, long Tx)> DayByDevice(DateTime localDate)
    {
        lock (_gate)
        {
            var key = localDate.ToString("yyyy-MM-dd");
            return _data.Devices
                .Where(kv => kv.Value.Daily.ContainsKey(key))
                .Select(kv => (kv.Key, kv.Value.LastHostname, kv.Value.Daily[key].Rx, kv.Value.Daily[key].Tx))
                .OrderByDescending(x => x.Rx + x.Tx)
                .ToList();
        }
    }

    /// <summary>某一天的某小时各终端流量（本地采样）。</summary>
    public IReadOnlyList<(string Mac, string? Hostname, long Rx, long Tx)> HourByDevice(string hourKey)
    {
        lock (_gate)
        {
            return _data.Devices
                .Where(kv => kv.Value.Hourly.ContainsKey(hourKey))
                .Select(kv => (kv.Key, kv.Value.LastHostname, kv.Value.Hourly[hourKey].Rx, kv.Value.Hourly[hourKey].Tx))
                .OrderByDescending(x => x.Rx + x.Tx)
                .ToList();
        }
    }

    /// <summary>单个终端在若干天内的每日流量。</summary>
    public IReadOnlyList<TrafficPeriod> DeviceDaily(string mac, int days)
    {
        lock (_gate)
        {
            var key = mac.ToLowerInvariant();
            var dh = _data.Devices.TryGetValue(key, out var d) ? d : null;
            var result = new List<TrafficPeriod>();
            for (var i = days - 1; i >= 0; i--)
            {
                var day = DateTime.Today.AddDays(-i);
                var k = day.ToString("yyyy-MM-dd");
                ByteBucket? b = null;
                dh?.Daily.TryGetValue(k, out b);
                result.Add(new TrafficPeriod(k, day.ToString("MM-dd"), b?.Rx ?? 0, b?.Tx ?? 0, true));
            }
            return result;
        }
    }

    public (long Rx, long Tx) DeviceTotal(string mac, DateTime localDate)
    {
        lock (_gate)
        {
            var key = mac.ToLowerInvariant();
            if (_data.Devices.TryGetValue(key, out var d) && d.Daily.TryGetValue(localDate.ToString("yyyy-MM-dd"), out var b))
                return (b.Rx, b.Tx);
            return (0, 0);
        }
    }

    public string? KnownHostname(string mac)
    {
        lock (_gate)
            return _data.Devices.TryGetValue(mac.ToLowerInvariant(), out var d) ? d.LastHostname : null;
    }

    public int KnownDeviceCount { get { lock (_gate) return _data.Devices.Count; } }

    /// <summary>清空本地采样数据（与设备端累计计数器无关，界面必须区分说明）。</summary>
    public void ResetLocal()
    {
        lock (_gate)
        {
            _data.Devices.Clear();
            _data.DeviceDaily.Clear();
            _data.DeviceHourly.Clear();
            _data.DeviceLastRx = _data.DeviceLastTx = -1;
            _data.UncapturedBytes = 0;
            _data.FirstRecordedUtc = null;
            _data.LastSampleUtc = null;
        }
        Save();
    }

    /// <summary>清空指定终端的本地历史，保留用户别名与设备清单。</summary>
    public void ResetDevice(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return;
        lock (_gate)
        {
            _data.Devices.Remove(mac.ToLowerInvariant());
        }
        Save();
    }
}
