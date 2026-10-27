namespace NRadioManager.Core;

public enum SignalAlertKind { None, Weak, Recovered }

/// <summary>
/// 信号告警状态机。
/// 只有设备真实返回 0–5 的 signal_quality 时才参与判断；字段缺失/解析失败只表示“无法判定”，
/// 不会当作弱信号，也不会因此发出“已恢复”。
/// </summary>
public sealed class SignalAlertMonitor
{
    public const int WeakSamplesRequired = 3;
    public const int RecoverySamplesRequired = 3;
    public static readonly TimeSpan ReminderInterval = TimeSpan.FromMinutes(30);

    private int _weakSamples;
    private int _goodSamples;

    public bool IsWeak { get; private set; }
    public DateTime? LastWeakNoticeUtc { get; private set; }

    public SignalAlertKind Observe(int? rawQuality, int thresholdLevel, DateTime utcNow)
    {
        if (rawQuality is null or < 0 or > 5)
        {
            // 没有可信读数：保持当前状态，等待下一次有效采样。
            _weakSamples = 0;
            _goodSamples = 0;
            return SignalAlertKind.None;
        }

        thresholdLevel = Math.Clamp(thresholdLevel, 0, 4);

        if (rawQuality.Value <= thresholdLevel)
        {
            _goodSamples = 0;
            _weakSamples++;
            if (_weakSamples < WeakSamplesRequired) return SignalAlertKind.None;

            if (!IsWeak)
            {
                IsWeak = true;
                LastWeakNoticeUtc = utcNow;
                return SignalAlertKind.Weak;
            }

            // 持续异常仍会提醒，但限制为每 30 分钟一次，避免每个轮询周期都弹窗。
            if (LastWeakNoticeUtc is null || utcNow - LastWeakNoticeUtc.Value >= ReminderInterval)
            {
                LastWeakNoticeUtc = utcNow;
                return SignalAlertKind.Weak;
            }
            return SignalAlertKind.None;
        }

        _weakSamples = 0;
        if (rawQuality.Value >= Math.Min(5, thresholdLevel + 1))
        {
            _goodSamples++;
            if (_goodSamples >= RecoverySamplesRequired && IsWeak)
            {
                IsWeak = false;
                _goodSamples = 0;
                return SignalAlertKind.Recovered;
            }
        }
        else
        {
            _goodSamples = 0;
        }

        return SignalAlertKind.None;
    }

    public void Reset()
    {
        _weakSamples = 0;
        _goodSamples = 0;
        IsWeak = false;
        LastWeakNoticeUtc = null;
    }
}

public enum TrafficThresholdPeriod { Day, Month }

public static class TrafficAlertRules
{
    public static string PeriodKey(TrafficThresholdPeriod period, DateTime localNow)
        => period == TrafficThresholdPeriod.Month
            ? localNow.ToString("yyyy-MM")
            : localNow.ToString("yyyy-MM-dd");

    /// <summary>
    /// 去重键包含周期、周期键与阈值。配置改变后允许重新提醒；
    /// 同一周期内（包括软件重启后）同一个配置只提醒一次。
    /// </summary>
    public static string DedupeKey(TrafficThresholdPeriod period, DateTime localNow, double thresholdGb)
        => $"{(period == TrafficThresholdPeriod.Month ? "month" : "day")}:{PeriodKey(period, localNow)}:{thresholdGb:0.###}";

    public static bool ReachedThreshold(long totalBytes, double thresholdGb)
    {
        if (totalBytes <= 0 || thresholdGb <= 0) return false;
        return totalBytes >= thresholdGb * 1024.0 * 1024.0 * 1024.0;
    }
}
