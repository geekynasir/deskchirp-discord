namespace DeveloperPresence.Core.Models;

public enum PresenceConnectionStatus
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Error = 3
}

/// <summary>
/// Snapshot of live presence and foreground status.
/// </summary>
public sealed class PresenceState
{
    public PresenceConnectionStatus ConnectionStatus { get; set; } = PresenceConnectionStatus.Disconnected;
    public SupportedApp ForegroundApp { get; set; } = SupportedApp.None;
    public string OutgoingHeadline { get; set; } = "None / Cleared";
    public DateTimeOffset? LastUpdate { get; set; }
    public string StatusMessage { get; set; } = "Ready";
    public string? LastError { get; set; }

    /// <summary>
    /// Creates a thread-safe snapshot clone of the current presence state.
    /// </summary>
    public PresenceState Clone() => new()
    {
        ConnectionStatus = ConnectionStatus,
        ForegroundApp = ForegroundApp,
        OutgoingHeadline = OutgoingHeadline,
        LastUpdate = LastUpdate,
        StatusMessage = StatusMessage,
        LastError = LastError
    };
}
