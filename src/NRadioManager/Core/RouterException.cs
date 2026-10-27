namespace NRadioManager.Core;

public enum RouterErrorKind
{
    /// <summary>网络不可达 / DNS / 连接被拒绝。</summary>
    Network,
    /// <summary>请求超时。</summary>
    Timeout,
    /// <summary>账号或密码错误。</summary>
    AuthFailed,
    /// <summary>会话失效（被顶下线 / nonce 过期）。</summary>
    SessionExpired,
    /// <summary>响应不是预期的 JSON，说明协议不符或设备异常。</summary>
    Protocol,
    /// <summary>设备明确回复 api not found。</summary>
    NotSupported,
    /// <summary>设备返回了业务错误。</summary>
    Device,
}

public sealed class RouterException : Exception
{
    public RouterErrorKind Kind { get; }
    public string? Endpoint { get; }

    public RouterException(RouterErrorKind kind, string message, string? endpoint = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Endpoint = endpoint;
    }

    /// <summary>面向用户的简短描述。</summary>
    public string UserMessage => Kind switch
    {
        RouterErrorKind.Network => "无法连接到路由器，请检查电脑是否已连接到该路由器的 Wi-Fi。",
        RouterErrorKind.Timeout => "路由器响应超时。",
        RouterErrorKind.AuthFailed => "用户名或密码错误。",
        RouterErrorKind.SessionExpired => "登录会话已失效，请重新登录。",
        RouterErrorKind.Protocol => "设备返回了无法解析的数据。",
        RouterErrorKind.NotSupported => "该设备不支持此功能。",
        _ => Message,
    };
}
