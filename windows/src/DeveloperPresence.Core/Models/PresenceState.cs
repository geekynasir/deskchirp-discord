namespace DeveloperPresence.Core.Models;

public enum PresenceConnectionStatus
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Error = 3
}

public enum T3ConnectionStatus
{
    NotConnected = 0,
    OpeningSignIn = 1,
    Connected = 2,
    SignInAgain = 3,
    CouldNotReach = 4
}

/// <summary>
/// Snapshot of live presence, T3 connection, and foreground status.
/// </summary>
public sealed class PresenceState
{
    public PresenceConnectionStatus ConnectionStatus { get; set; } = PresenceConnectionStatus.Disconnected;
    public SupportedApp ForegroundApp { get; set; } = SupportedApp.None;
    public string OutgoingHeadline { get; set; } = "None / Cleared";
    public string? OutgoingState { get; set; }
    public string? ActivityNote { get; set; }
    public DateTimeOffset? LastUpdate { get; set; }
    public string StatusMessage { get; set; } = "Ready";
    public string? LastError { get; set; }
    public string ComputerStatus { get; set; } = "Not linked";

    public T3ConnectionStatus T3ConnectionStatus { get; set; } = T3ConnectionStatus.NotConnected;
    public string T3StatusDetail { get; set; } = "Paste the T3 address, then connect.";

    /// <summary>
    /// Creates a thread-safe snapshot clone of the current presence state.
    /// </summary>
    public PresenceState Clone() => new()
    {
        ConnectionStatus = ConnectionStatus,
        ForegroundApp = ForegroundApp,
        OutgoingHeadline = OutgoingHeadline,
        OutgoingState = OutgoingState,
        ActivityNote = ActivityNote,
        LastUpdate = LastUpdate,
        StatusMessage = StatusMessage,
        LastError = LastError,
        ComputerStatus = ComputerStatus,
        T3ConnectionStatus = T3ConnectionStatus,
        T3StatusDetail = T3StatusDetail
    };
}
