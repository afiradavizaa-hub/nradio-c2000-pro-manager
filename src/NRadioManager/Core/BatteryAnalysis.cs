namespace NRadioManager.Core;

public enum BatteryTrend { InsufficientData, Falling, Rising, Stable, Charging }

/// <summary>
/// 电池历史分析结果。所有数值都来自真实采样；数据缺口、断连和重启区间不参与速率计算。
/// 不推算剩余续航、Wh 或功率（设备没有电流/电压字段）。
/// </summary>
public sealed record BatteryAnalysis(
    int SampleCount,
    int UsableSamples,
    int? StartPercent,
    int? EndPercent,
    int? NetChange,
    double DischargePercent,
    double ChargePercent,
    double CalibrationRisePercent,
    TimeSpan DischargeDuration,
    TimeSpan ChargeDuration,
    TimeSpan FullDuration,
    TimeSpan StableDuration,
    TimeSpan MissingDuration,
    TimeSpan LongestGap,
    int GapCount,
    int DisconnectCount,
    int RestartCount,
    double? AverageDropPerHour,
    BatteryTrend Trend,
    double CoverageRatio)
{
    public bool HasData => UsableSamples >= 2;
    public bool HasRate => AverageDropPerHour is not null;
}

public static class BatteryAnalyzer
{
    /// <summary>
    /// 分析一个时间窗口。只有“两端都是有效采样、间隔不超过 gapThreshold、且没有设备重启”的区间
    /// 才计入覆盖率、变化量和平均下降速度；其余区间一律作为缺失/断连/重启处理。
    /// </summary>
    public static BatteryAnalysis Analyze(IReadOnlyList<BatterySample> samples,
        DateTime startUtc, DateTime endUtc, TimeSpan gapThreshold)
    {
        var window = endUtc - startUtc;
        if (window <= TimeSpan.Zero) window = TimeSpan.FromMinutes(1);

        if (gapThreshold <= TimeSpan.Zero) gapThreshold = TimeSpan.FromMinutes(1);

        var list = samples
            .Where(s => s.TimeUtc >= startUtc && s.TimeUtc <= endUtc)
            .OrderBy(s => s.TimeUtc)
            .GroupBy(s => s.TimeUtc)
            .Select(g => g.Last())
            .ToList();
        var usable = list.Where(s => s.IsUsable).ToList();

        double discharge = 0, charge = 0, calibration = 0;
        var dischargeStateDuration = TimeSpan.Zero;
        var chargeStateDuration = TimeSpan.Zero;
        var fullStateDuration = TimeSpan.Zero;
        var stableDuration = TimeSpan.Zero;
        var covered = TimeSpan.Zero;
        var restartCount = 0;
        var gapIntervals = new List<(DateTime Start, DateTime End)>();
        var disconnectIntervals = new List<(DateTime Start, DateTime End)>();

        for (var i = 0; i < list.Count - 1; i++)
        {
            var a = list[i];
            var b = list[i + 1];
            var dt = b.TimeUtc - a.TimeUtc;
            if (dt <= TimeSpan.Zero) continue;

            var restart = a.RunSeconds is { } ar && b.RunSeconds is { } br && br < ar;
            if (restart)
            {
                restartCount++;
                gapIntervals.Add((a.TimeUtc, b.TimeUtc));
                continue;
            }

            if (!a.Connected || !b.Connected)
            {
                gapIntervals.Add((a.TimeUtc, b.TimeUtc));
                disconnectIntervals.Add((a.TimeUtc, b.TimeUtc));
                continue;
            }

            if (!a.IsUsable || !b.IsUsable || dt > gapThreshold)
            {
                gapIntervals.Add((a.TimeUtc, b.TimeUtc));
                continue;
            }

            covered += dt;
            var delta = b.Percent!.Value - a.Percent!.Value;
            if (delta < 0) discharge += -delta;
            else if (delta > 0)
            {
                if (b.State is BatteryChargeState.Charging or BatteryChargeState.Full)
                    charge += delta;
                else
                    calibration += delta;
            }
            else stableDuration += dt;

            switch (b.State)
            {
                case BatteryChargeState.Charging:
                    chargeStateDuration += dt;
                    break;
                case BatteryChargeState.Full:
                    fullStateDuration += dt;
                    break;
                case BatteryChargeState.Discharging:
                    dischargeStateDuration += dt;
                    break;
            }
        }

        // 图表会把窗口两端的空白标成“无采样”，这里的缺口统计也保持一致。
        if (list.Count > 0)
        {
            var leading = list[0].TimeUtc - startUtc;
            var trailing = endUtc - list[^1].TimeUtc;
            if (leading > gapThreshold)
                gapIntervals.Add((startUtc, list[0].TimeUtc));
            if (trailing > gapThreshold)
                gapIntervals.Add((list[^1].TimeUtc, endUtc));
        }

        var mergedGaps = MergeIntervals(gapIntervals);
        var mergedDisconnects = MergeIntervals(disconnectIntervals);
        var gapCount = mergedGaps.Count;
        var disconnectCount = mergedDisconnects.Count;
        var longestGap = mergedGaps.Count == 0
            ? TimeSpan.Zero
            : mergedGaps.Max(x => x.End - x.Start);

        // 历史覆盖不到窗口两端时，同样算作未覆盖。
        var missing = window - covered;
        if (missing < TimeSpan.Zero) missing = TimeSpan.Zero;

        int? startPercent = usable.Count == 0 ? null : usable[0].Percent;
        int? endPercent = usable.Count == 0 ? null : usable[^1].Percent;
        int? netChange = startPercent is not null && endPercent is not null ? endPercent - startPercent : null;

        double? rate = null;
        if (dischargeStateDuration >= TimeSpan.FromMinutes(10) && discharge >= 1)
            rate = discharge / dischargeStateDuration.TotalHours;

        var trend = usable.Count < 2
            ? BatteryTrend.InsufficientData
            : chargeStateDuration > dischargeStateDuration && charge > 0
                ? BatteryTrend.Charging
                : netChange <= -1 ? BatteryTrend.Falling
                : netChange >= 1 ? BatteryTrend.Rising
                : BatteryTrend.Stable;

        var coverage = Math.Clamp(covered.TotalSeconds / window.TotalSeconds, 0, 1);

        return new BatteryAnalysis(
            list.Count, usable.Count, startPercent, endPercent, netChange,
            discharge, charge, calibration,
            dischargeStateDuration, chargeStateDuration, fullStateDuration, stableDuration,
            missing, longestGap, gapCount, disconnectCount, restartCount,
            rate, trend, coverage);
    }

    /// <summary>
    /// 合并重叠或首尾相接的区间。断连标记会同时产生“有效点→断连点”和“断连点→恢复点”
    /// 两个区间，若不去重会把一次断连误算成两次。
    /// </summary>
    private static List<(DateTime Start, DateTime End)> MergeIntervals(
        List<(DateTime Start, DateTime End)> intervals)
    {
        var result = new List<(DateTime Start, DateTime End)>();
        foreach (var interval in intervals.OrderBy(x => x.Start))
        {
            if (interval.End <= interval.Start) continue;
            if (result.Count == 0 || interval.Start > result[^1].End)
            {
                result.Add(interval);
                continue;
            }

            result[^1] = (result[^1].Start,
                interval.End > result[^1].End ? interval.End : result[^1].End);
        }
        return result;
    }
}
