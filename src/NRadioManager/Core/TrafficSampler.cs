namespace NRadioManager.Core;

/// <summary>
/// 本机采样点。HasSpeed=false 表示该点只有基线/计数器缺失/回绕，
/// 不能当作真实的 0 B/s 绘制（图表会跳过该点，而不是伪造一条零速率曲线）。
/// </summary>
public sealed record SamplePoint(
    DateTime Time,
    double DownloadBytesPerSec,
    double UploadBytesPerSec,
    int? RssiDbm,
    int? Clients,
    bool HasSpeed = true);

/// <summary>
/// 本地采样器：记录本软件运行期间采集到的速率/信号序列。
/// 数据来源明确标注为「本机采样」，与设备自带的累计流量区分，避免口径混淆。
/// </summary>
public sealed class TrafficSampler
{
    private readonly int _capacity;
    private readonly List<SamplePoint> _points = new();
    private long? _lastRxBytes;
    private long? _lastTxBytes;
    private DateTime? _lastTime;

    public TrafficSampler(int capacity = 3600) => _capacity = capacity;

    public IReadOnlyList<SamplePoint> Points => _points;

    /// <summary>喂入设备返回的累计字节数与状态，计算本机采样速率。</summary>
    public SamplePoint Add(StatusInfo status)
    {
        var now = DateTime.Now;
        var rx = J.Long(status.RxByteAll);
        var tx = J.Long(status.TxByteAll);

        double dl = 0, ul = 0;
        var hasSpeed = false;
        if (_lastRxBytes is not null && _lastTxBytes is not null && _lastTime is not null && rx is not null && tx is not null)
        {
            var seconds = (now - _lastTime.Value).TotalSeconds;
            if (seconds > 0.2)
            {
                // 计数器回绕（设备重启/清零）时按 0 处理，不产生假峰值
                var dRx = rx.Value - _lastRxBytes.Value;
                var dTx = tx.Value - _lastTxBytes.Value;
                if (dRx >= 0 && dTx >= 0)
                {
                    dl = dRx / seconds;
                    ul = dTx / seconds;
                    hasSpeed = true;
                }
            }
        }

        _lastRxBytes = rx;
        _lastTxBytes = tx;
        _lastTime = now;

        var point = new SamplePoint(now, dl, ul, J.Int(status.Rssi), J.Int(status.WifiClientsNum), hasSpeed);
        _points.Add(point);
        if (_points.Count > _capacity) _points.RemoveRange(0, _points.Count - _capacity);
        return point;
    }

    public void Reset()
    {
        _points.Clear();
        _lastRxBytes = _lastTxBytes = null;
        _lastTime = null;
    }
}
