using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using NRadioManager.Core;

namespace NRadioManager.SelfTest;

internal static class C2000OfflineTests
{
    public static async Task<bool> RunAsync()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        await using var server = new FakeC2000Server(password);
        await server.StartAsync();

        using (var bad = new C2000ProClient(server.BaseUrl, "root", password + "x", 4))
        {
            try
            {
                await bad.LoginAsync();
                Console.WriteLine("FAIL  离线认证：错误密码被接受");
                return false;
            }
            catch (RouterException ex) when (ex.Kind == RouterErrorKind.AuthFailed)
            {
                Console.WriteLine("PASS  离线认证：错误密码被 Cookie 会话校验拒绝");
            }
        }

        using var client = new C2000ProClient(server.BaseUrl, "root", password, 4);
        await client.LoginAsync();
        var first = await client.GetRuntimeAsync();
        if (first.Result?.Global?.WifiTemp != "42")
        {
            Console.WriteLine("FAIL  离线会话：运行时混合类型字段解析失败");
            return false;
        }

        // 服务端下一次受保护请求返回 403；客户端必须重建 Cookie 并重试一次。
        server.ExpireNextProtectedRequest = true;
        var recovered = await client.GetRuntimeAsync();
        if (!server.SessionRecovered || recovered.Result?.Cpe?.FirstOrDefault()?.Rsrp != "-79")
        {
            Console.WriteLine("FAIL  离线会话：403 会话过期未自动恢复");
            return false;
        }
        Console.WriteLine("PASS  离线会话：Cookie 失效后重建会话并成功重试");

        try
        {
            await client.GetTextAsync("/cgi-bin/luci/missing", "offline-404");
            Console.WriteLine("FAIL  离线 HTTP：404 未被识别");
            return false;
        }
        catch (RouterException ex) when (ex.Kind == RouterErrorKind.NotSupported)
        {
            Console.WriteLine("PASS  离线 HTTP：404 映射为不支持");
        }

        try
        {
            await client.GetTextAsync("/cgi-bin/luci/failure", "offline-500");
            Console.WriteLine("FAIL  离线 HTTP：500 未被识别");
            return false;
        }
        catch (RouterException ex) when (ex.Kind == RouterErrorKind.Device)
        {
            Console.WriteLine("PASS  离线 HTTP：500 映射为设备错误");
        }

        try
        {
            using var timeoutClient = new C2000ProClient(server.BaseUrl, "root", password, 3);
            await timeoutClient.LoginAsync();
            await timeoutClient.GetTextAsync("/cgi-bin/luci/slow", "offline-timeout");
            Console.WriteLine("FAIL  离线超时：慢接口未超时");
            return false;
        }
        catch (RouterException ex) when (ex.Kind == RouterErrorKind.Timeout)
        {
            Console.WriteLine("PASS  离线超时：超时映射正确");
        }

        using var adapter = new C2000ProAdapter(server.BaseUrl, "root", password, 4);
        await adapter.AuthenticateAsync();
        var snapshot = await adapter.GetSnapshotAsync();
        if (snapshot.Identity.Model != "C2000-515" || snapshot.Cell.Status?.Band != "41" ||
            snapshot.Runtime.TerminalAll?.Count != 1)
        {
            Console.WriteLine("FAIL  离线映射：设备身份/蜂窝/终端字段不一致");
            return false;
        }
        Console.WriteLine("PASS  离线映射：运行时、蜂窝、身份与终端字段");
        var usage = await adapter.GetDataUsageAsync();
        if (usage.Sims.Count != 2 || Math.Abs(usage.Sims[0].MonthlyUsedGb - 48.77147) > 0.001 ||
            Math.Abs(usage.Sims[0].TotalUsedGb - 254.24404) > 0.001)
        {
            Console.WriteLine("FAIL  离线映射：SIM 月/累计流量字段不正确");
            return false;
        }
        Console.WriteLine("PASS  离线映射：SIM 月用量与累计用量");
        return true;
    }

    private sealed class FakeC2000Server : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        private int _runtimeRequests;
        private bool _expireOnce;
        private string? _session;
        private readonly string _password;

        public FakeC2000Server(string password) => _password = password;

        public string BaseUrl { get; private set; } = "";
        public bool ExpireNextProtectedRequest { get => _expireOnce; set => _expireOnce = value; }
        public bool SessionRecovered { get; private set; }

        public Task StartAsync()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _loop = AcceptLoopAsync();
            return Task.CompletedTask;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { break; }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true))
            {
                var requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(requestLine)) return;
                var parts = requestLine.Split(' ');
                var method = parts[0];
                var path = parts[1];
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    var idx = line.IndexOf(':');
                    if (idx > 0) headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                }

                var body = "";
                if (headers.TryGetValue("Content-Length", out var lengthRaw) && int.TryParse(lengthRaw, out var length))
                {
                    var buffer = new char[length];
                    var read = 0;
                    while (read < length)
                    {
                        var n = await reader.ReadAsync(buffer.AsMemory(read, length - read));
                        if (n == 0) break;
                        read += n;
                    }
                    body = new string(buffer, 0, read);
                }

                var cookie = headers.GetValueOrDefault("Cookie") ?? "";
                var authenticated = _session is not null && cookie.Contains("sysauth=" + _session, StringComparison.Ordinal);
                if (path == "/cgi-bin/luci" && method == "GET")
                {
                    await RespondAsync(stream, authenticated ? 200 : 403, authenticated ? "home" : LoginHtml(),
                        "text/html; charset=utf-8");
                    return;
                }
                if (path == "/cgi-bin/luci" && method == "POST")
                {
                    var query = ParseForm(body);
                    if (query.GetValueOrDefault("luci_username") == "root" &&
                        query.GetValueOrDefault("luci_password") == _password)
                    {
                        _session = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
                        _runtimeRequests = 0;
                        _expireOnce = false;
                        await RespondAsync(stream, 200, "<title>Status</title>", "text/html; charset=utf-8",
                            $"sysauth={_session}; path=/cgi-bin/luci/");
                    }
                    else
                    {
                        await RespondAsync(stream, 403, LoginHtml(), "text/html; charset=utf-8");
                    }
                    return;
                }

                if (!authenticated)
                {
                    await RespondAsync(stream, 403, LoginHtml(), "text/html; charset=utf-8");
                    return;
                }

                if (path.StartsWith("/cgi-bin/luci/nradio/status/runtime", StringComparison.Ordinal))
                {
                    _runtimeRequests++;
                    if (_expireOnce && _runtimeRequests >= 2)
                    {
                        _expireOnce = false;
                        SessionRecovered = true;
                        _session = null;
                        await RespondAsync(stream, 403, "session expired", "text/plain");
                        return;
                    }
                    await RespondAsync(stream, 200, RuntimeJson, "application/json");
                    return;
                }
                if (path == "/cgi-bin/luci/nradio/cellular/cpestatus/cellinfo")
                {
                    await RespondAsync(stream, 200, CellJson, "application/json");
                    return;
                }
                if (path == "/cgi-bin/luci/nradio/cellular/cpedata/refresh")
                {
                    await RespondAsync(stream, 200, DataUsageJson, "application/json");
                    return;
                }
                if (path == "/cgi-bin/luci/nradio/system/overview")
                {
                    await RespondAsync(stream, 200, OverviewHtml, "text/html; charset=utf-8");
                    return;
                }
                if (path == "/cgi-bin/luci/failure")
                {
                    await RespondAsync(stream, 500, "server error", "text/plain");
                    return;
                }
                if (path == "/cgi-bin/luci/slow")
                {
                    await Task.Delay(TimeSpan.FromSeconds(6), _cts.Token);
                    await RespondAsync(stream, 200, "late", "text/plain");
                    return;
                }
                await RespondAsync(stream, 404, "not found", "text/plain");
            }
        }

        private static async Task RespondAsync(NetworkStream stream, int status, string body, string contentType,
            string? setCookie = null)
        {
            var payload = Encoding.UTF8.GetBytes(body);
            var reason = status switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
            var header =
                $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\n" +
                "Connection: close\r\nCache-Control: no-cache\r\n" +
                (setCookie is null ? "" : $"Set-Cookie: {setCookie}\r\n") + "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
            await stream.WriteAsync(payload);
        }

        private static Dictionary<string, string> ParseForm(string body)
            => body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split('=', 2))
                .ToDictionary(x => WebUtility.UrlDecode(x[0]),
                    x => x.Length > 1 ? WebUtility.UrlDecode(x[1]) : "");

        private static string LoginHtml()
            => "<html><body><input type=\"password\" name=\"luci_password\"><a href=\"/luci-static/nradio/\">NRadio</a></body></html>";

        private const string RuntimeJson = """
        {"result":{"global":{"wifi_temp":42,"cpu_percent":7,"mem_percent":58,"uptime":100,"net_type":"Cellular"},
        "terminal_all":{"count":1,"client":{"00:11:22:33:44:55":{"txbytes":"2048","rxbytes":"1024","hostname":"OfflineClient","ipaddr":"192.168.66.2","mac":"00:11:22:33:44:55","type":"wireless","exsit":1}}},
        "lans":[{"proto":"static","status":0,"ipaddrs":[{"ipaddr":"192.168.66.1/24","netmask":"255.255.255.0"}]}],
        "cpe":[{"name":"wan0","model":"RG200U-CN","status":0,"mode":"NR SA","band":"41","pci":"887","earfcn":"504990","rsrp":"-79","rsrq":"-6","sinr":"15","simcount":2,"simno":"1","isp":"China Mobile","V4":{"ipaddrs":["10.0.0.2"],"dns":["1.1.1.1"]}}],
        "bat":{"charging":0,"percent":-1},"wlan":{"radio":[{"ifname":"wlan1","ssid":"offline-test","channel":"149","frequency":"5.745"}]}}}
        """;

        private const string CellJson = """
        {"result":[{"status":{"name":"wan0","model":"RG200U-CN","status":0,"mode":"NR SA","band":"41","pci":"887","earfcn":"504990","rsrp":"-79","rsrq":"-6","sinr":"15","simcount":2,"simno":"1","isp":"China Mobile","DLBW":"100000"},"uptime":100,"up":0}]}
        """;

        private const string OverviewHtml = """
        <div>Model:</div><div>C2000-515</div>
        <div>Firmware Version:</div><div>NROS-2.2.5.0.1</div>
        <div>MAC Address:</div><div>00:11:22:33:44:55</div>
        <script>var board_name = "test-board";</script>
        """;

        private const string DataUsageJson = """
        {"result":{"data":[[{"enabled":1,"index":1,"modelname":"wan0","alias":"Embedded SIM1","threshold_smds":48.77147,"threshold_cds":254.24404},{"enabled":1,"index":2,"modelname":"wan0","alias":"Embedded SIM2","threshold_smds":0,"threshold_cds":0}]]}}
        """;

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            if (_loop is not null)
            {
                try { await _loop; } catch { }
            }
            _cts.Dispose();
        }
    }
}
