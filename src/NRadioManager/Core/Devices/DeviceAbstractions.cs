namespace NRadioManager.Core;

public enum DeviceKind
{
    NRadioLegacy,
    C2000Pro,
}

public sealed record DeviceCapabilities(
    bool CellularRead,
    bool SignalAnalysis,
    bool BandCellLockRead,
    bool BandCellLockWrite,
    bool SimRead,
    bool SimSwitchWrite,
    bool WifiRead,
    bool WifiWrite,
    bool LanRead,
    bool TerminalRead,
    bool TerminalWrite,
    bool SmsRead,
    bool SmsSend,
    bool MaintenanceRead,
    bool RebootWrite)
{
    public static DeviceCapabilities NRadio { get; } = new(
        CellularRead: true,
        SignalAnalysis: true,
        BandCellLockRead: false,
        BandCellLockWrite: false,
        SimRead: true,
        SimSwitchWrite: true,
        WifiRead: true,
        WifiWrite: true,
        LanRead: true,
        TerminalRead: true,
        TerminalWrite: true,
        SmsRead: false,
        SmsSend: false,
        MaintenanceRead: true,
        RebootWrite: true);

    public static DeviceCapabilities C2000Pro { get; } = new(
        CellularRead: true,
        SignalAnalysis: true,
        BandCellLockRead: true,
        BandCellLockWrite: true,
        SimRead: true,
        SimSwitchWrite: true,
        WifiRead: true,
        WifiWrite: true,
        LanRead: true,
        TerminalRead: true,
        TerminalWrite: true,
        SmsRead: true,
        SmsSend: true,
        MaintenanceRead: true,
        RebootWrite: true);
}

public interface IDeviceAdapter : IDisposable
{
    DeviceKind Kind { get; }
    string Host { get; }
    string Username { get; }
    string DisplayName { get; }
    DeviceCapabilities Capabilities { get; }
    IDeviceApi Api { get; }
    bool IsAuthenticated { get; }
    DateTime? LastSuccessUtc { get; }

    event Action<RouterLogEntry>? RequestLogged;
    event Action<RouterDiagnosticEntry>? DiagnosticLogged;
    event Action? SessionLost;

    Task AuthenticateAsync(CancellationToken ct = default);
}
