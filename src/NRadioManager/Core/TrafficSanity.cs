namespace NRadioManager.Core;

/// <summary>
/// 终端流量计数器可信度校验。
///
/// 真机证据（2026-10-17）：固件会为部分终端返回明显失真的累计计数器——
/// OnePlus-15 上报 rx=36.37 TB、tx=7.15 TB，而同一时刻整机自开机以来只有 16.8 GB；
/// 同一终端 4 小时前计数器还是 142 GB。原厂页面本身也不展示这些数值。
/// 这类值不能当作真实流量展示，更不能写入本地历史。
/// </summary>
public static class TrafficSanity
{
    /// <summary>整机与终端计数器不是同一时刻读取，留出的误差余量。</summary>
    private const long SlackBytes = 256L * 1024 * 1024;

    /// <summary>按 1 Gbps 估算的单向理论峰值，用于设备累计值缺失时兜底。</summary>
    private const long MaxLinkBytesPerSecond = 125_000_000;

    public sealed record ClientCounters(
        long? Rx, long? Tx, long? MonthRx, long? MonthTx, long? Day3Rx, long? Day3Tx,
        bool Reliable, string? Reason, string? RawSummary)
    {
        public long? CumulativeTotal => Rx is not null && Tx is not null ? Rx + Tx : null;
    }

    /// <summary>校验一台终端的设备侧计数器；不可信时返回原因，调用方应改为展示本地采样或「—」。</summary>
    public static ClientCounters Inspect(ClientEntry client, StatusInfo? status)
    {
        var rx = J.Long(client.Rx);
        var tx = J.Long(client.Tx);
        var monthRx = J.Long(client.RxMonth);
        var monthTx = J.Long(client.TxMonth);
        var day3Rx = J.Long(client.Rx3Day);
        var day3Tx = J.Long(client.Tx3Day);

        if (rx is null || tx is null)
            return new(rx, tx, monthRx, monthTx, day3Rx, day3Tx, false,
                "设备未上报可解析的累计计数器", null);

        var cumulative = rx.Value + tx.Value;
        var reason = CheckAgainstDevice(cumulative, client.AccessTime, status)
                     ?? CheckAgainstBootTime(cumulative, status);
        if (reason is null)
            return new(rx, tx, monthRx, monthTx, day3Rx, day3Tx, true, null, null);

        var raw = $"设备原始值：累计 ↓ {J.Bytes(rx)} · ↑ {J.Bytes(tx)}；" +
                  $"本月 ↓ {J.Bytes(monthRx)} · ↑ {J.Bytes(monthTx)}；" +
                  $"近 3 日 ↓ {J.Bytes(day3Rx)} · ↑ {J.Bytes(day3Tx)}";
        return new(rx, tx, monthRx, monthTx, day3Rx, day3Tx, false, reason, raw);
    }

    /// <summary>
    /// 单台终端的累计流量不可能比整机累计高出两个数量级。
    /// 终端计数器可能早于本次开机建立，因此留 2 倍 + 256 MB 余量；真机案例是 39.6 TB 对 16.8 GB。
    /// </summary>
    private static string? CheckAgainstDevice(long cumulative, string? accessTime, StatusInfo? status)
    {
        var devRx = J.Long(status?.RxByteAll);
        var devTx = J.Long(status?.TxByteAll);
        if (devRx is null || devTx is null) return null;

        var deviceTotal = devRx.Value + devTx.Value;
        if (deviceTotal < SlackBytes) return null;

        // 若终端接入时间早于本次开机，说明它的计数器可能跨重启保留，
        // 此时整机累计不能作为上限（否则会误伤真实的长期重度用户），改用开机时长兜底。
        if (PredatesBoot(accessTime, status)) return null;

        if (cumulative <= deviceTotal * 2 + SlackBytes) return null;

        return $"终端累计 {J.Bytes(cumulative)}，超过整机累计 {J.Bytes(deviceTotal)} 的 2 倍";
    }

    /// <summary>设备累计值缺失时的兜底：任何终端都不可能以超过 1 Gbps 的速度跑满开机时长。</summary>
    private static string? CheckAgainstBootTime(long cumulative, StatusInfo? status)
    {
        var runSeconds = J.Long(status?.RunSeconds);
        if (runSeconds is null || runSeconds < 300) return null;

        var theoreticalMax = runSeconds.Value * MaxLinkBytesPerSecond;
        if (cumulative <= theoreticalMax * 3 + SlackBytes) return null;

        return $"终端累计 {J.Bytes(cumulative)}，超过开机 {J.Duration(runSeconds)} 内 1 Gbps 理论上限";
    }

    private static bool PredatesBoot(string? accessTime, StatusInfo? status)
    {
        var runSeconds = J.Long(status?.RunSeconds);
        if (runSeconds is null || runSeconds <= 0 || string.IsNullOrWhiteSpace(accessTime)) return false;
        if (!DateTime.TryParse(accessTime, out var access)) return false;

        var bootTime = DateTime.Now.AddSeconds(-runSeconds.Value);
        return access < bootTime.AddMinutes(-5);
    }
}
