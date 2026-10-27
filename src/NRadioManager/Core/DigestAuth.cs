using System.Security.Cryptography;
using System.Text;

namespace NRadioManager.Core;

/// <summary>
/// 原厂后台使用的 HTTP Digest 认证（MD5 + qop=auth）。
/// 逆向自原厂 js/base.js 的 getAuthType() / doLogin() / getAuthHeader()。
///
/// 关键细节：参与摘要计算的 uri 与真实请求路径不同。
///   - 登录摘要：HA2 = MD5("GET:/cgi/protected.cgi")
///   - 业务摘要：HA2 = MD5(METHOD + ":/cgi/xml_action.cgi")
///   而业务请求真实路径是 /xml_action.cgi（服务端做了映射）。
///   实测：改动任一字符串都会返回 401，必须严格照抄。
/// </summary>
public sealed class DigestAuth
{
    public const string ProtectedUri = "/cgi/protected.cgi";
    public const string XmlActionUri = "/cgi/xml_action.cgi";

    private readonly string _username;
    private readonly string _password;
    private int _nc;

    public string Realm { get; private set; } = string.Empty;
    public string Nonce { get; private set; } = string.Empty;
    public string Qop { get; private set; } = "auth";

    public DigestAuth(string username, string password)
    {
        _username = username;
        _password = password;
    }

    /// <summary>从 WWW-Authenticate 响应头解析出 realm / nonce / qop。</summary>
    public void ParseChallenge(string header)
    {
        if (string.IsNullOrWhiteSpace(header) || !header.TrimStart().StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
            throw new RouterException(RouterErrorKind.Protocol, "设备返回的认证头不是 Digest 类型。");

        Realm = Extract(header, "realm") ?? throw new RouterException(RouterErrorKind.Protocol, "认证头缺少 realm。");
        Nonce = Extract(header, "nonce") ?? throw new RouterException(RouterErrorKind.Protocol, "认证头缺少 nonce。");
        Qop = Extract(header, "qop") ?? "auth";
        _nc = 0;
    }

    private static string? Extract(string header, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(header, key + "=\"?([^\",]+)\"?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private string Ha1()
        => Md5($"{_username}:{Realm}:{_password}");

    /// <summary>生成登录请求所需的摘要串（含固定 nc=00000001）。</summary>
    public string ComputeLoginResponse()
    {
        var cnonce = NewCnonce();
        var ha2 = Md5($"GET:{ProtectedUri}");
        var response = Md5($"{Ha1()}:{Nonce}:00000001:{cnonce}:{Qop}:{ha2}");
        LoginCnonce = cnonce;
        return response;
    }

    public string LoginCnonce { get; private set; } = string.Empty;

    /// <summary>生成业务请求的 Authorization 头（nc 自增）。</summary>
    public string BuildAuthorizationHeader(string method)
    {
        _nc++;
        var nc = _nc.ToString("x8");
        var cnonce = NewCnonce();
        var ha2 = Md5($"{method}:{XmlActionUri}");
        var response = Md5($"{Ha1()}:{Nonce}:{nc}:{cnonce}:{Qop}:{ha2}");
        return $"Digest username=\"{_username}\", realm=\"{Realm}\", nonce=\"{Nonce}\", uri=\"{XmlActionUri}\", " +
               $"response=\"{response}\", qop={Qop}, nc={nc}, cnonce=\"{cnonce}\"";
    }

    private static string NewCnonce()
    {
        Span<byte> buf = stackalloc byte[8];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }

    public static string Md5(string input)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
