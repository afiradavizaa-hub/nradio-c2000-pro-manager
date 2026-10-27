using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NRadioManager.Core;

/// <summary>
/// NRadio C2000 Pro / NROS LuCI 通信客户端。
/// 认证是 Cookie 会话表单，不是 NRadio 的 Digest；所有局域网请求强制绕过系统代理。
/// </summary>
public sealed class C2000ProClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        Converters = { new FlexibleStringJsonConverter() },
    };

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private readonly SemaphoreSlim _authGate = new(1, 1);

    public string Host { get; private set; }
    public string Username { get; private set; }
    public string Password { get; private set; }
    public bool IsAuthenticated { get; private set; }
    public DateTime? LastSuccessUtc { get; private set; }

    public event Action<RouterLogEntry>? RequestLogged;
    public event Action<RouterDiagnosticEntry>? DiagnosticLogged;
    public event Action? SessionLost;

    public C2000ProClient(string host, string username, string password, int timeoutSeconds = 10)
    {
        Host = NormalizeHost(host);
        Username = string.IsNullOrWhiteSpace(username) ? "root" : username.Trim();
        Password = password;

        var handler = new HttpClientHandler
        {
            UseProxy = false,
            Proxy = null,
            UseCookies = true,
            CookieContainer = _cookies,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 3, 30)) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/html, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-store");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Pragma", "no-cache");
    }

    public static string NormalizeHost(string host)
    {
        host = (host ?? string.Empty).Trim();
        if (host.Length == 0) host = "http://192.168.66.1";
        if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            host = "http://" + host;
        return host.TrimEnd('/');
    }

    public async Task LoginAsync(CancellationToken ct = default)
    {
        await _authGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 先取得登录页和初始 Cookie，同时确认该地址确实是 NROS/LuCI。
            using var loginPage = await SendAsync(HttpMethod.Get, "/cgi-bin/luci", null, "login-page", ct).ConfigureAwait(false);
            var loginHtml = await loginPage.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!loginHtml.Contains("luci_password", StringComparison.OrdinalIgnoreCase) &&
                !loginHtml.Contains("nradio", StringComparison.OrdinalIgnoreCase))
            {
                if (loginPage.StatusCode == HttpStatusCode.Forbidden)
                    throw new RouterException(RouterErrorKind.Protocol, "设备返回了未识别的登录页面。", "/cgi-bin/luci");
            }

            var form = new Dictionary<string, string>
            {
                ["luci_username"] = Username,
                ["luci_password"] = Password,
                ["luci_channel"] = "",
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, Host + "/cgi-bin/luci")
            {
                Content = new FormUrlEncodedContent(form),
            };
            using var response = await SendAsync(request, "login", ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var hasSession = _cookies.GetCookies(new Uri(Host + "/cgi-bin/luci/"))
                .Cast<Cookie>()
                .Any(c => string.Equals(c.Name, "sysauth", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Value));
            if (!hasSession || response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ||
                body.Contains("luci_password", StringComparison.OrdinalIgnoreCase))
            {
                IsAuthenticated = false;
                throw new RouterException(RouterErrorKind.AuthFailed, "C2000 Pro 登录被拒绝，请检查管理密码。", "login");
            }

            IsAuthenticated = true;
            try
            {
                await ReadJsonAsync<C2000RuntimeEnvelope>(
                    "/cgi-bin/luci/nradio/status/runtime/wan,cpe_4", "login-check", ct, allowRelogin: false)
                    .ConfigureAwait(false);
            }
            catch (RouterException ex) when (ex.Kind is RouterErrorKind.SessionExpired or RouterErrorKind.Protocol)
            {
                IsAuthenticated = false;
                throw new RouterException(RouterErrorKind.AuthFailed,
                    "登录 Cookie 未通过受保护接口验证：" + ex.UserMessage, "login", ex);
            }
        }
        finally
        {
            _authGate.Release();
        }
    }

    public async Task<C2000RuntimeEnvelope> GetRuntimeAsync(CancellationToken ct = default)
        => await ReadJsonAsync<C2000RuntimeEnvelope>(
            "/cgi-bin/luci/nradio/status/runtime/wan,cpe_4", "runtime", ct).ConfigureAwait(false);

    public async Task<C2000CellInfoEnvelope> GetCellInfoAsync(CancellationToken ct = default)
        => await ReadJsonAsync<C2000CellInfoEnvelope>(
            "/cgi-bin/luci/nradio/cellular/cpestatus/cellinfo", "cellinfo", ct).ConfigureAwait(false);

    public async Task<C2000SmsUnreadEnvelope> GetSmsUnreadAsync(CancellationToken ct = default)
        => await ReadJsonAsync<C2000SmsUnreadEnvelope>(
            "/cgi-bin/luci/nradio/cellular/sms/unread", "sms-unread", ct).ConfigureAwait(false);

    public async Task<C2000SmsEnvelope> GetSmsListAsync(string channel = "wan0", CancellationToken ct = default)
    {
        var body = await PostFormAsync(
            "/cgi-bin/luci/nradio/cellular/sms/list",
            new Dictionary<string, string> { ["channel"] = channel },
            "sms-list",
            ct).ConfigureAwait(false);
        return Deserialize<C2000SmsEnvelope>(body, "sms-list");
    }

    public Task<string> GetOverviewHtmlAsync(CancellationToken ct = default)
        => GetTextAsync("/cgi-bin/luci/nradio/system/overview", "overview", ct);

    public Task<string> GetLockHtmlAsync(string channel = "wan0", CancellationToken ct = default)
        => GetTextAsync($"/cgi-bin/luci/nradio/cellular/cpelock/model/{Uri.EscapeDataString(channel)}", "cpelock", ct);

    public Task<string> GetSimSwitchHtmlAsync(string channel = "wan0", CancellationToken ct = default)
        => GetTextAsync($"/cgi-bin/luci/nradio/cellular/switch/model/{Uri.EscapeDataString(channel)}", "cpeswitch", ct);

    public Task<string> GetWifiHtmlAsync(CancellationToken ct = default)
        => GetTextAsync("/cgi-bin/luci/nradio/basic/wifi", "wifi", ct);

    public Task<string> GetDhcpHtmlAsync(CancellationToken ct = default)
        => GetTextAsync("/cgi-bin/luci/nradioadv/network/dhcp", "dhcp", ct);

    public Task<string> GetLanHtmlAsync(CancellationToken ct = default)
        => GetTextAsync("/cgi-bin/luci/nradioadv/network/lan", "lan", ct);

    public Task<string> GetSystemLogHtmlAsync(CancellationToken ct = default)
        => GetTextAsync("/cgi-bin/luci/nradioadv/system/logread", "system-log", ct);

    public async Task<string> GetTextAsync(string path, string endpoint, CancellationToken ct = default)
    {
        var response = await SendProtectedAsync(HttpMethod.Get, path, null, endpoint, ct).ConfigureAwait(false);
        using (response)
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> PostFormAsync(string path, IReadOnlyDictionary<string, string> form,
        string endpoint, CancellationToken ct = default)
    {
        var response = await SendProtectedAsync(HttpMethod.Post, path,
            new FormUrlEncodedContent(form), endpoint, ct).ConfigureAwait(false);
        using (response)
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> PostMultipartAsync(string path, IReadOnlyDictionary<string, string> form,
        string endpoint, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        foreach (var item in form)
            content.Add(new StringContent(item.Value), item.Key);
        var response = await SendProtectedAsync(HttpMethod.Post, path, content, endpoint, ct).ConfigureAwait(false);
        using (response)
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private async Task<T> ReadJsonAsync<T>(string path, string endpoint, CancellationToken ct, bool allowRelogin = true)
    {
        var body = await GetTextProtectedAsync(path, endpoint, ct, allowRelogin).ConfigureAwait(false);
        return Deserialize<T>(body, endpoint);
    }

    public static T Deserialize<T>(string body, string endpoint)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions)
                   ?? throw new RouterException(RouterErrorKind.Protocol, "设备返回空 JSON。", endpoint);
        }
        catch (JsonException ex)
        {
            throw new RouterException(RouterErrorKind.Protocol, "设备 JSON 解析失败：" + ex.Message, endpoint, ex);
        }
    }

    private async Task<string> GetTextProtectedAsync(string path, string endpoint, CancellationToken ct, bool allowRelogin)
    {
        var response = await SendProtectedAsync(HttpMethod.Get, path, null, endpoint, ct, allowRelogin).ConfigureAwait(false);
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true &&
                (body.Contains("luci_password", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)))
            {
                IsAuthenticated = false;
                SessionLost?.Invoke();
                throw new RouterException(RouterErrorKind.SessionExpired, "登录会话已失效。", endpoint);
            }
            LastSuccessUtc = DateTime.UtcNow;
            return body;
        }
    }

    private async Task<HttpResponseMessage> SendProtectedAsync(HttpMethod method, string path, HttpContent? content,
        string endpoint, CancellationToken ct, bool allowRelogin = true)
    {
        if (!IsAuthenticated)
            await LoginAsync(ct).ConfigureAwait(false);

        var response = await SendAsync(method, path, content, endpoint, ct).ConfigureAwait(false);
        var status = response.StatusCode;
        if (status is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized))
        {
            if (response.IsSuccessStatusCode)
                return response;
            response.Dispose();
            throw status == HttpStatusCode.NotFound
                ? new RouterException(RouterErrorKind.NotSupported, "设备接口不存在（HTTP 404）。", endpoint)
                : new RouterException(RouterErrorKind.Device, $"设备返回 HTTP {(int)status}。", endpoint);
        }

        response.Dispose();
        if (!allowRelogin)
        {
            IsAuthenticated = false;
            SessionLost?.Invoke();
            throw new RouterException(RouterErrorKind.SessionExpired, "设备拒绝了当前会话。", endpoint);
        }

        DiagnosticLogged?.Invoke(new RouterDiagnosticEntry(DateTime.Now, LogLevel.Warning,
            $"接口 {endpoint} 返回 {(int)status}，正在重建 Cookie 会话并重试一次。"));
        IsAuthenticated = false;
        await LoginAsync(ct).ConfigureAwait(false);

        if (content is FormUrlEncodedContent || content is MultipartFormDataContent)
            throw new RouterException(RouterErrorKind.SessionExpired,
                "设备在写入过程中报告会话失效；为避免重复提交，未自动重放该写请求。", endpoint);

        var retried = await SendAsync(method, path, content, endpoint, ct).ConfigureAwait(false);
        if (retried.IsSuccessStatusCode)
            return retried;
        var retriedStatus = retried.StatusCode;
        retried.Dispose();
        throw retriedStatus == HttpStatusCode.NotFound
            ? new RouterException(RouterErrorKind.NotSupported, "设备接口不存在（HTTP 404）。", endpoint)
            : new RouterException(RouterErrorKind.Device, $"设备返回 HTTP {(int)retriedStatus}。", endpoint);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content,
        string endpoint, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Host + path) { Content = content };
        return await SendAsync(request, endpoint, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string endpoint, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();
            RequestLogged?.Invoke(new RouterLogEntry(DateTime.Now, request.Method.Method, endpoint,
                (int)response.StatusCode, sw.ElapsedMilliseconds, null));
            return response;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            RequestLogged?.Invoke(new RouterLogEntry(DateTime.Now, request.Method.Method, endpoint, 0,
                sw.ElapsedMilliseconds, "超时"));
            throw new RouterException(RouterErrorKind.Timeout, "请求超时。", endpoint);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            RequestLogged?.Invoke(new RouterLogEntry(DateTime.Now, request.Method.Method, endpoint, 0,
                sw.ElapsedMilliseconds, ex.Message));
            throw new RouterException(RouterErrorKind.Network, ex.Message, endpoint, ex);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _authGate.Dispose();
    }
}

/// <summary>NROS 对“同义字段”有时返回字符串、有时返回数字；统一转换为字符串，原始数值不丢精度。</summary>
internal sealed class FlexibleStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.ValueSpan),
            JsonTokenType.True => "1",
            JsonTokenType.False => "0",
            JsonTokenType.Null => null,
            _ => throw new JsonException($"无法把 {reader.TokenType} 转换为字符串。"),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
