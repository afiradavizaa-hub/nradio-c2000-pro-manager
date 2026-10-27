using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NRadioManager.Core;

public enum NetworkQualityState
{
    NotTested,
    Healthy,
    LocalNetworkUnavailable,
    RouterUnreachable,
    InternetUnreachable,
    DetectionFailed,
}

/// <summary>一次探测的原始观察结果。分类逻辑与网络调用分离，便于用确定性用例自检。</summary>
public sealed record ProbeObservation(
    string Target,
    bool TcpSuccess,
    bool TcpRefused,
    bool TcpDefinitiveUnreachable,
    bool TcpTimeout,
    int PingAttempts,
    int PingSuccesses,
    bool PingDefinitiveUnreachable,
    bool PingFailed,
    double? PingAverageMs)
{
    public bool Reachable => TcpSuccess || TcpRefused || PingSuccesses > 0;
}

public sealed record NetworkQualityResult(
    NetworkQualityState State,
    string Summary,
    string RouterText,
    string InternetText,
    string LatencyText,
    string LossText,
    DateTime CompletedAt);

/// <summary>
/// 网络质量检测：只使用 Windows 自带能力（ICMP + TCP 连接），不安装服务、不抓包、不引入依赖。
///
/// 判定原则（避免把“检测失败”当成断网）：
///   · 任一路径获得 TCP 成功、TCP 拒绝（RST，说明主机在线）或 ICMP 成功 → 该路径可达；
///   · 只有操作系统明确返回 Host/Network Unreachable 才判为不可达；
///   · 全部超时/无响应（常见于 ICMP 被防火墙丢弃）则如实标记“检测失败/无法判定”，
///     外网仅在路由器可达且两个独立目标均无任何响应时标记“外网连接失败”，并说明可能原因；
///   · ICMP 全部被丢弃时，不据此计算 100% 丢包，而是显示“无法统计”。
/// </summary>
public static class NetworkQualityMonitor
{
    private static readonly string[] ExternalTargets = { "223.5.5.5", "119.29.29.29" };

    public static async Task<NetworkQualityResult> ProbeAsync(string routerHost, CancellationToken ct = default)
    {
        var local = HasUsableLocalNetwork();
        if (local is null)
        {
            return new NetworkQualityResult(
                NetworkQualityState.DetectionFailed,
                "无法读取本机网卡状态，本次检测失败，不能据此判定断网。",
                "未测试。", "未测试。", "—", "—", DateTime.Now);
        }
        if (local == false)
        {
            return new NetworkQualityResult(
                NetworkQualityState.LocalNetworkUnavailable,
                "本机没有可用的局域网连接（网卡未连接或没有有效 IPv4 地址）。",
                "未测试：本机网络未连接。",
                "未测试。",
                "—", "—", DateTime.Now);
        }

        var (routerName, port) = ParseRouter(routerHost);

        // 路由器：ICMP 与 TCP 管理端口同时探测，任一有响应即可判定主机在线。
        var routerTask = ProbeEndpointAsync(routerName, port, pingAttempts: 4, ct);
        var externalTasks = ExternalTargets.Select(t => ProbeEndpointAsync(t, 443, pingAttempts: t == ExternalTargets[0] ? 4 : 0, ct));
        var all = await Task.WhenAll(externalTasks.Prepend(routerTask)).ConfigureAwait(false);
        var router = all[0];
        var external = all.Skip(1).ToList();

        return Evaluate(local.Value, router, external, DateTime.Now);
    }

    /// <summary>纯分类逻辑。输入为真实探测结果，测试中可用构造数据验证每一种状态。</summary>
    public static NetworkQualityResult Evaluate(bool localNetworkAvailable, ProbeObservation router,
        IReadOnlyList<ProbeObservation> external, DateTime completedAt)
    {
        if (!localNetworkAvailable)
        {
            return new NetworkQualityResult(NetworkQualityState.LocalNetworkUnavailable,
                "本机没有可用的局域网连接。", "未测试：本机网络未连接。", "未测试。", "—", "—", completedAt);
        }

        var routerText = DescribeRouter(router);
        var latency = DescribeLatency(router, external);
        var loss = DescribeLoss(router, external);

        if (router.Reachable)
        {
            var successes = external.Count(e => e.Reachable);
            var definitive = external.Any(e => e.TcpDefinitiveUnreachable || e.PingDefinitiveUnreachable);
            var timeoutOnly = external.Count > 0 && external.All(e =>
                !e.Reachable && (e.TcpTimeout || (e.PingAttempts > 0 && e.PingSuccesses == 0 && !e.TcpDefinitiveUnreachable)));

            if (successes > 0)
            {
                var internetText = external.Count == 1
                    ? "外网可达（获得 TCP/ICMP 响应）。"
                    : $"外网可达：{successes}/{external.Count} 个独立目标获得响应。";
                return new NetworkQualityResult(NetworkQualityState.Healthy,
                    "路由器与外网均可达。", routerText, internetText, latency, loss, completedAt);
            }

            if (definitive || timeoutOnly)
            {
                return new NetworkQualityResult(NetworkQualityState.InternetUnreachable,
                    "路由器可达，但所有外网探测均未成功。可能是运营商/WAN 断连，也可能是目标端口被防火墙拦截。",
                    routerText,
                    "外网连接失败：两个独立目标的 TCP/ICMP 均未获得响应。",
                    latency, loss, completedAt);
            }

            return new NetworkQualityResult(NetworkQualityState.DetectionFailed,
                "路由器可达，但外网探测结果不足以判定断网（检测失败）。",
                routerText, "外网检测失败：没有成功响应，也没有明确的不可达错误。", latency, loss, completedAt);
        }

        if (router.TcpDefinitiveUnreachable || router.PingDefinitiveUnreachable)
        {
            return new NetworkQualityResult(NetworkQualityState.RouterUnreachable,
                "路由器不可达：本机网络存在，但操作系统明确报告目标主机/网络不可达。",
                routerText, "未测试：路由器不可达。", latency, loss, completedAt);
        }

        return new NetworkQualityResult(NetworkQualityState.DetectionFailed,
            "路由器未响应，但无法区分设备关机、ICMP 被丢弃或管理端口被拦截；本次不算作断网。",
            routerText, "未测试：路由器状态无法判定。", latency, loss, completedAt);
    }

    /// <summary>true=有可用网卡，false=明确没有，null=读取失败（不能据此判定断网）。</summary>
    public static bool? HasUsableLocalNetwork()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
                nic.OperationalStatus == OperationalStatus.Up &&
                nic.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel &&
                nic.GetIPProperties().UnicastAddresses.Any(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(a.Address) &&
                    !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)));
        }
        catch
        {
            return null;
        }
    }

    private static (string Host, int Port) ParseRouter(string routerHost)
    {
        var normalized = RouterClient.NormalizeHost(routerHost);
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            return (uri.Host, uri.Port > 0 ? uri.Port : 80);
        return (normalized, 80);
    }

    private static async Task<ProbeObservation> ProbeEndpointAsync(string host, int port, int pingAttempts,
        CancellationToken ct)
    {
        var tcp = await ProbeTcpAsync(host, port, ct).ConfigureAwait(false);
        var ping = pingAttempts > 0
            ? await ProbePingAsync(host, pingAttempts, ct).ConfigureAwait(false)
            : (Successes: 0, Attempts: 0, DefinitiveUnreachable: false, Failed: false, AverageMs: (double?)null);

        return new ProbeObservation(host,
            tcp.Success, tcp.Refused, tcp.DefinitiveUnreachable, tcp.Timeout,
            ping.Attempts, ping.Successes, ping.DefinitiveUnreachable, ping.Failed, ping.AverageMs);
    }

    private static async Task<(bool Success, bool Refused, bool DefinitiveUnreachable, bool Timeout)> ProbeTcpAsync(
        string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return (true, false, false, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, false, false, true);
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => (false, true, false, false),
                SocketError.HostUnreachable or SocketError.NetworkUnreachable or
                    SocketError.HostDown or SocketError.NetworkDown => (false, false, true, false),
                SocketError.TimedOut => (false, false, false, true),
                _ => (false, false, false, false),
            };
        }
        catch
        {
            return (false, false, false, false);
        }
    }

    private static async Task<(int Successes, int Attempts, bool DefinitiveUnreachable, bool Failed, double? AverageMs)>
        ProbePingAsync(string host, int attempts, CancellationToken ct)
    {
        var success = 0;
        var definitive = false;
        var failed = false;
        var times = new List<double>();

        using var ping = new Ping();
        for (var i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(host, 800).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    success++;
                    times.Add(reply.RoundtripTime);
                }
                else if (reply.Status is IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable)
                {
                    definitive = true;
                }
            }
            catch
            {
                failed = true;
            }
        }

        return (success, attempts, definitive, failed, times.Count > 0 ? times.Average() : null);
    }

    private static string DescribeRouter(ProbeObservation router)
    {
        if (router.TcpSuccess) return $"路由器可达（TCP 管理端口已响应，目标 {router.Target}）。";
        if (router.TcpRefused) return $"路由器在线（管理端口拒绝连接，目标 {router.Target}）。";
        if (router.PingSuccesses > 0) return $"路由器可达（ICMP 有响应，目标 {router.Target}）。";
        if (router.TcpDefinitiveUnreachable || router.PingDefinitiveUnreachable)
            return $"路由器不可达（系统明确报告主机/网络不可达，目标 {router.Target}）。";
        return $"路由器未响应（ICMP/TCP 均未得到有效答复，目标 {router.Target}）。";
    }

    private static string DescribeLatency(ProbeObservation router, IReadOnlyList<ProbeObservation> external)
    {
        var parts = new List<string>();
        parts.Add(router.PingAverageMs is { } routerMs
            ? $"路由器平均延迟 {routerMs:0} ms（ICMP）"
            : "路由器延迟：ICMP 无响应，无法统计");

        var externalPing = external.FirstOrDefault(e => e.PingAverageMs is not null);
        if (externalPing is not null)
            parts.Add($"外网平均延迟 {externalPing.PingAverageMs:0} ms（{externalPing.Target}）");
        else if (external.Count > 0)
            parts.Add("外网延迟：ICMP 无响应，无法统计");

        return string.Join("；", parts);
    }

    private static string DescribeLoss(ProbeObservation router, IReadOnlyList<ProbeObservation> external)
    {
        var parts = new List<string> { LossText("路由器", router) };
        var externalPing = external.FirstOrDefault(e => e.PingAttempts > 0);
        if (externalPing is not null) parts.Add(LossText("外网", externalPing));
        return string.Join("；", parts);
    }

    private static string LossText(string label, ProbeObservation observation)
    {
        if (observation.PingAttempts == 0) return $"{label}丢包率：未执行 ICMP 探测";
        if (observation.PingSuccesses == 0)
            return $"{label}丢包率：ICMP 无响应，无法统计（不代表 100% 丢包）";
        var loss = (observation.PingAttempts - observation.PingSuccesses) * 100.0 / observation.PingAttempts;
        return $"{label}丢包率 {loss:0}%（{observation.PingSuccesses}/{observation.PingAttempts} 次 ICMP 成功）";
    }
}
