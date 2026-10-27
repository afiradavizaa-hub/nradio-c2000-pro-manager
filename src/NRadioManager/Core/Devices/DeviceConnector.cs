using System.Net;
using System.Net.Http;

namespace NRadioManager.Core;

/// <summary>通过设备公开登录页指纹选择协议适配器，再在适配器内执行各自认证。</summary>
public static class DeviceConnector
{
    public static async Task<IDeviceAdapter> ConnectAsync(string host, string username, string password,
        int timeoutSeconds, CancellationToken ct = default)
    {
        host = C2000ProClient.NormalizeHost(host);
        var kind = await DetectAsync(host, timeoutSeconds, ct).ConfigureAwait(false);
        if (kind == DeviceKind.C2000Pro)
        {
            // NROS 登录表单将用户名固定为 root；旧设置中的 admin 不应被误当成 C2000 用户名。
            var effectiveUser = string.Equals(username?.Trim(), "admin", StringComparison.OrdinalIgnoreCase) ||
                                string.IsNullOrWhiteSpace(username)
                ? "root"
                : username.Trim();
            var adapter = new C2000ProAdapter(host, effectiveUser, password, timeoutSeconds);
            try
            {
                await adapter.AuthenticateAsync(ct).ConfigureAwait(false);
                return adapter;
            }
            catch
            {
                adapter.Dispose();
                throw;
            }
        }

        var legacy = new NRadioLegacyAdapter(host, username, password, timeoutSeconds);
        try
        {
            await legacy.AuthenticateAsync(ct).ConfigureAwait(false);
            return legacy;
        }
        catch
        {
            legacy.Dispose();
            throw;
        }
    }

    public static async Task<DeviceKind> DetectAsync(string host, int timeoutSeconds = 5,
        CancellationToken ct = default)
    {
        host = C2000ProClient.NormalizeHost(host);
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            Proxy = null,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
        };
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 3, 15)),
        };
        try
        {
            using var response = await http.GetAsync(host + "/cgi-bin/luci", ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Contains("luci_password", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("luci-static/nradio", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("NRadio", StringComparison.OrdinalIgnoreCase))
                return DeviceKind.C2000Pro;
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                return DeviceKind.NRadioLegacy;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // 由后续协议认证返回更具体的连接错误；这里只说明指纹阶段未能完成。
        }
        return DeviceKind.NRadioLegacy;
    }
}
