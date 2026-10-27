namespace NRadioManager.Core;

/// <summary>保留原 NRadio/Marvell Digest 协议，作为独立适配器继续工作。</summary>
public sealed class NRadioLegacyAdapter : IDeviceAdapter
{
    private readonly RouterClient _client;
    private readonly RouterApi _api;

    public NRadioLegacyAdapter(string host, string username, string password, int timeoutSeconds = 10)
    {
        _client = new RouterClient(host, username, password, timeoutSeconds);
        _client.RequestLogged += e => RequestLogged?.Invoke(e);
        _client.DiagnosticLogged += e => DiagnosticLogged?.Invoke(e);
        _client.SessionLost += () => SessionLost?.Invoke();
        _api = new RouterApi(_client);
    }

    public DeviceKind Kind => DeviceKind.NRadioLegacy;
    public string Host => _client.Host;
    public string Username => _client.Username;
    public string DisplayName => "NRadio / Marvell";
    public DeviceCapabilities Capabilities => DeviceCapabilities.NRadio;
    public IDeviceApi Api => _api;
    public bool IsAuthenticated => _client.IsAuthenticated;
    public DateTime? LastSuccessUtc => _client.LastSuccessUtc;
    public RouterClient Client => _client;
    public bool AuthEnforced => _client.AuthEnforced;

    public event Action<RouterLogEntry>? RequestLogged;
    public event Action<RouterDiagnosticEntry>? DiagnosticLogged;
    public event Action? SessionLost;

    public Task AuthenticateAsync(CancellationToken ct = default) => _client.LoginAsync(ct);

    public void Dispose() => _client.Dispose();
}
