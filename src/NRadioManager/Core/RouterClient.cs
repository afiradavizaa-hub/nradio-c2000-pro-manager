using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NRadioManager.Core;

/// <summary>一次设备通信的记录，用于界面日志。</summary>
public sealed record RouterLogEntry(DateTime Time, string Method, string Endpoint, int StatusCode, long ElapsedMs, string? Error);

/// <summary>不适合逐请求记录、但需要进入统一运行日志的通信诊断事件。</summary>
public sealed record RouterDiagnosticEntry(DateTime Time, LogLevel Level, string Message);

/// <summary>
/// 设备通信层：封装 Digest 登录、请求发送、超时、重试、会话失效恢复与异常映射。
/// 所有页面共用此对象，避免每个模块重复实现登录与错误处理。
/// </summary>
public sealed class RouterClient : IDisposable
{
    private const string Module = "duster";

    private readonly HttpClient _http;
    private DigestAuth? _auth;
    private readonly SemaphoreSlim _authGate = new(1, 1);

    public string Host { get; private set; }
    public string Username { get; private set; }
    public string Password { get; private set; }
    public bool IsAuthenticated { get; private set; }
    public DateTime? LastSuccessUtc { get; private set; }

    /// <summary>
    /// 设备是否真的校验了登录凭据。实测发现该固件对 /xml_action.cgi 不校验 Authorization，
    /// 因此这里用一个"不带任何认证信息"的请求去探测，如实反映设备行为。
    /// </summary>
    public bool AuthEnforced { get; private set; } = true;

    public event Action<RouterLogEntry>? RequestLogged;
    public event Action? SessionLost;
    public event Action<RouterDiagnosticEntry>? DiagnosticLogged;

    public RouterClient(string host, string username, string password, int timeoutSeconds = 10)
    {
        Host = NormalizeHost(host);
        Username = username;
        Password = password;

        // 关键：必须绕开系统代理。用户环境的 Clash(127.0.0.1:7890) 会把局域网请求带到外网。
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            Proxy = null,
            CookieContainer = new CookieContainer(),
            UseCookies = true,
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        // 与原厂前端一致的禁缓存头；Expires 属于内容类头，需用 TryAddWithoutValidation 避免校验报错
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-store, no-cache, must-revalidate");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Pragma", "no-cache");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Expires", "-1");
    }

    public static string NormalizeHost(string host)
    {
        host = (host ?? string.Empty).Trim();
        if (host.Length == 0) host = "http://192.168.1.1";
        if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            host = "http://" + host;
        return host.TrimEnd('/');
    }

    public void UpdateCredentials(string host, string username, string password)
    {
        Host = NormalizeHost(host);
        Username = username;
        Password = password;
        _auth = null;
        IsAuthenticated = false;
    }

    // ---------- 认证 ----------

    /// <summary>走完原厂的 Digest 登录流程：取质询 → 计算摘要 → 调用 /login.cgi。</summary>
    public async Task LoginAsync(CancellationToken ct = default)
    {
        await _authGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var auth = new DigestAuth(Username, Password);

            using var challengeReq = new HttpRequestMessage(HttpMethod.Get, Host + "/login.cgi");
            using var challengeResp = await SendAsync(challengeReq, "login.cgi#challenge", ct).ConfigureAwait(false);

            var wwwAuth = challengeResp.Headers.WwwAuthenticate.FirstOrDefault()?.ToString();
            if (string.IsNullOrWhiteSpace(wwwAuth) && challengeResp.StatusCode != HttpStatusCode.Unauthorized)
            {
                // 设备在已登录状态下可能直接 200，此时先去拿一次受保护资源触发 401
                using var probe = new HttpRequestMessage(HttpMethod.Get, Host + "/cgi/protected.cgi");
                using var probeResp = await SendAsync(probe, "protected.cgi#challenge", ct).ConfigureAwait(false);
                wwwAuth = probeResp.Headers.WwwAuthenticate.FirstOrDefault()?.ToString();
            }

            if (string.IsNullOrWhiteSpace(wwwAuth))
                throw new RouterException(RouterErrorKind.Protocol, "未能从设备获取 Digest 质询。");

            auth.ParseChallenge(wwwAuth);

            var response = auth.ComputeLoginResponse();
            var url = $"{Host}/login.cgi?Action=Digest&username={Uri.EscapeDataString(Username)}" +
                      $"&realm={Uri.EscapeDataString(auth.Realm)}&nonce={Uri.EscapeDataString(auth.Nonce)}" +
                      $"&response={response}&qop={auth.Qop}&cnonce={auth.LoginCnonce}&temp=marvell";

            using var loginReq = new HttpRequestMessage(HttpMethod.Get, url);
            using var loginResp = await SendAsync(loginReq, "login.cgi#digest", ct).ConfigureAwait(false);
            var body = await loginResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!IsLoginOk(loginResp, body))
                throw new RouterException(RouterErrorKind.AuthFailed, "登录被设备拒绝。", "login.cgi");

            _auth = auth;
            IsAuthenticated = true;

            // 关键校验：设备对错误密码也可能返回 200（原厂前端只检查响应体里是否含 "200 OK"），
            // 因此这里再用一次真实的受保护请求复核凭据，失败即判定为账号密码错误。
            try
            {
                await RequestAsync(HttpMethod.Get, RouterEndpoints.LoginInfo, null, ct, allowRelogin: false).ConfigureAwait(false);
            }
            catch (RouterException ex) when (ex.Kind is RouterErrorKind.SessionExpired or RouterErrorKind.Protocol)
            {
                IsAuthenticated = false;
                _auth = null;
                throw new RouterException(RouterErrorKind.AuthFailed, "用户名或密码错误。", "login.cgi", ex);
            }

            AuthEnforced = await ProbeAuthEnforcementAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _authGate.Release();
        }
    }

    /// <summary>
    /// 探测设备是否强制执行认证：发起一个不带 Authorization 头的数据请求。
    /// 若仍能拿到正常数据，说明设备固件没有校验凭据（本软件会在界面上如实提示，不掩饰）。
    /// </summary>
    private async Task<bool> ProbeAuthEnforcementAsync(CancellationToken ct)
    {
        try
        {
            var url = $"{Host}/xml_action.cgi?method=get&module={Module}&file=json_{RouterEndpoints.LoginInfo}{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await SendAsync(req, "auth-probe", ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized) return false;
            if (IsSessionProblem(body)) return false;
            return !body.Contains('{');
        }
        catch
        {
            // 探测本身失败时不妄下结论
            return true;
        }
    }

    private static bool IsLoginOk(HttpResponseMessage resp, string body)
    {
        if (resp.StatusCode != HttpStatusCode.OK) return false;
        if (body.Contains("UNAUTHORIZED", StringComparison.OrdinalIgnoreCase)) return false;
        if (body.Contains("login_failed", StringComparison.OrdinalIgnoreCase)) return false;
        // 原厂前端 login_done() 的判定依据就是响应体里出现 "200 OK"
        if (body.Contains("200 OK", StringComparison.OrdinalIgnoreCase)) return true;
        // 部分固件登录成功时返回空响应体，交由调用处的复核请求做最终判定
        return string.IsNullOrWhiteSpace(body);
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (IsAuthenticated && _auth is not null) return;
        await LoginAsync(ct).ConfigureAwait(false);
    }

    // ---------- 业务请求 ----------

    /// <summary>读取信息类接口（method=get）。</summary>
    public Task<string> GetJsonAsync(string endpoint, CancellationToken ct = default)
        => RequestAsync(HttpMethod.Get, endpoint, null, ct);

    /// <summary>写入配置类接口（method=post）。</summary>
    public Task<string> PostJsonAsync(string endpoint, JsonObject payload, CancellationToken ct = default)
        => RequestAsync(HttpMethod.Post, endpoint, payload, ct);

    private async Task<string> RequestAsync(HttpMethod method, string endpoint, JsonObject? payload, CancellationToken ct,
        bool allowRelogin = true)
    {
        // 会话失效时自动重登一次再重试
        var maxAttempts = allowRelogin ? 2 : 1;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (allowRelogin) await EnsureAuthenticatedAsync(ct).ConfigureAwait(false);
            else if (_auth is null) throw new RouterException(RouterErrorKind.SessionExpired, "尚未登录。", endpoint);

            var auth = _auth!;

            var verb = method == HttpMethod.Post ? "post" : "get";
            var url = $"{Host}/xml_action.cgi?method={verb}&module={Module}&file=json_{endpoint}{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

            using var req = new HttpRequestMessage(method, url);
            req.Headers.TryAddWithoutValidation("Authorization", auth.BuildAuthorizationHeader(method.Method));
            if (payload is not null)
                req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            using var resp = await SendAsync(req, endpoint, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized || IsSessionProblem(body))
            {
                IsAuthenticated = false;
                _auth = null;
                if (attempt + 1 < maxAttempts)
                {
                    DiagnosticLogged?.Invoke(new RouterDiagnosticEntry(
                        DateTime.Now, LogLevel.Warning,
                        $"接口 {endpoint} 返回会话失效；正在重新认证并重试一次（第 {attempt + 1} 次尝试）。"));
                    continue; // 重登后重试
                }

                DiagnosticLogged?.Invoke(new RouterDiagnosticEntry(
                    DateTime.Now, LogLevel.Error,
                    $"接口 {endpoint} 会话恢复失败；本次请求未得到有效响应。"));
                SessionLost?.Invoke();
                throw new RouterException(RouterErrorKind.SessionExpired, "会话已失效。", endpoint);
            }

            if (string.IsNullOrWhiteSpace(body))
                throw new RouterException(RouterErrorKind.Protocol, "设备返回了空响应。", endpoint);

            // 设备把「接口不存在」也作为 200 返回
            if (body.Contains("api not found", StringComparison.OrdinalIgnoreCase))
                throw new RouterException(RouterErrorKind.NotSupported, "设备不支持该接口。", endpoint);

            LastSuccessUtc = DateTime.UtcNow;
            return body;
        }

        throw new RouterException(RouterErrorKind.SessionExpired, "会话已失效。", endpoint);
    }

    private static bool IsSessionProblem(string body)
        => body.Contains("UNAUTHORIZED", StringComparison.OrdinalIgnoreCase)
        || body.Contains("KICKOFF", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析设备返回的 JSON 对象（设备所有值均为字符串）。</summary>
    public static JsonObject ParseObject(string body, string endpoint)
    {
        var start = body.IndexOf('{');
        var end = body.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new RouterException(RouterErrorKind.Protocol, "响应中找不到 JSON 对象。", endpoint);

        var slice = body[start..(end + 1)];
        try
        {
            return JsonNode.Parse(slice) as JsonObject
                   ?? throw new RouterException(RouterErrorKind.Protocol, "响应不是 JSON 对象。", endpoint);
        }
        catch (JsonException ex)
        {
            throw new RouterException(RouterErrorKind.Protocol, "JSON 解析失败：" + ex.Message, endpoint, ex);
        }
    }

    /// <summary>带计时的底层发送，统一做异常映射。</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, string endpoint, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();
            RequestLogged?.Invoke(new RouterLogEntry(DateTime.Now, req.Method.Method, endpoint, (int)resp.StatusCode, sw.ElapsedMilliseconds, null));
            return resp;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            RequestLogged?.Invoke(new RouterLogEntry(DateTime.Now, req.Method.Method, endpoint, 0, sw.ElapsedMilliseconds, "超时"));
            throw new RouterException(RouterErrorKind.Timeout, "请求超时。", endpoint);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            RequestLogged?.Invoke(new RouterLogEntry(DateTime.Now, req.Method.Method, endpoint, 0, sw.ElapsedMilliseconds, ex.Message));
            throw new RouterException(RouterErrorKind.Network, ex.Message, endpoint, ex);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _authGate.Dispose();
    }
}
