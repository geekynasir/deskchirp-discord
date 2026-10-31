using DeveloperPresence.Core.Models;

namespace DeveloperPresence.Core.Discord;

/// <summary>
/// Abstraction for Discord IPC Named Pipe client on Windows.
/// </summary>
public interface IDiscordIpcClient : IAsyncDisposable
{
    /// <summary>
    /// Event triggered when connection state changes (Disconnected, Connecting, Connected, Error).
    /// </summary>
    event Action<PresenceConnectionStatus, string?>? StatusChanged;

    /// <summary>
    /// Event triggered when an incoming frame arrives from Discord.
    /// </summary>
    event Action<DiscordIpcFrame>? FrameReceived;

    /// <summary>
    /// Returns true if currently connected and handshaked with Discord.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Attempts to locate and connect to a running Discord instance via named pipes 0..9.
    /// Sends Opcode 0 Handshake upon successful stream connection.
    /// </summary>
    Task<bool> ConnectAsync(string clientId, CancellationToken ct = default);

    /// <summary>
    /// Sends a SET_ACTIVITY command for the active supported developer application.
    /// Formats atomic whole frame with process ID and nonce.
    /// </summary>
    Task SendActivityAsync(SupportedApp app, CancellationToken ct = default);

    /// <summary>
    /// Sends a SET_ACTIVITY command with activity: null to cleanly clear presence on Discord.
    /// </summary>
    Task ClearActivityAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends Opcode 2 Close and gracefully disconnects the pipe.
    /// </summary>
    Task CloseAsync();
}
