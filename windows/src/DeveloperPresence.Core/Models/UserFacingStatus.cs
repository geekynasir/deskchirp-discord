namespace DeveloperPresence.Core.Models;

/// <summary>Translates connection diagnostics into fixed, human-readable status text.</summary>
public static class UserFacingStatus
{
    public static (string Title, string? Recovery) Describe(PresenceState state, PresenceSettings settings,
        bool isLocked = false, bool isSuspended = false)
    {
        if (!settings.EnableSharing) return ("Sharing is off", null);
        if (settings.IsPaused) return ("Sharing is paused", null);
        if (isLocked || isSuspended) return ("Hidden while your PC is locked or asleep", null);
        if (string.IsNullOrEmpty(settings.DiscordClientId)) return ("Add your Discord application ID to start sharing", null);
        if (!settings.IsValidDiscordClientId()) return ("Check your Discord application ID", "Application IDs are 15 to 20 digits.");
        if (settings.FollowComputers && state.ComputerStatus != "Selected computer: this computer")
            return (state.ComputerStatus, null);
        if (state.ForegroundApp == SupportedApp.None)
            return ("Not sharing while another app is active", "Shares when T3 Code, Claude, or Codex is the active app.");
        if (state.ConnectionStatus == PresenceConnectionStatus.Connecting) return ("Connecting to Discord…", null);
        string diagnostic = state.StatusMessage.ToLowerInvariant();
        if (diagnostic.Contains("not found") || diagnostic.Contains("no discord"))
            return ("Couldn’t find Discord", "Open the Discord desktop app. Discord in a browser can’t show this status.");
        if (diagnostic.Contains("timed out") || diagnostic.Contains("timeout"))
            return ("Discord didn’t respond", "Make sure Discord is open and signed in. Developer Presence will keep trying.");
        if (diagnostic.Contains("set_activity") || diagnostic.Contains("rejected") || diagnostic.StartsWith("discord error:"))
            return ("Discord didn’t accept the update", "Check that your Discord application ID matches an app in the Developer Portal.");
        if (state.ConnectionStatus == PresenceConnectionStatus.Error) return ("Lost the connection to Discord", "Reconnecting…");
        if (state.ConnectionStatus == PresenceConnectionStatus.Connected)
            return (state.LastUpdate == null ? "Updating Discord…" : "Sharing on Discord", null);
        if (diagnostic.Contains("disconnect") || diagnostic.Contains("closed"))
            return ("Discord disconnected", "Reconnects when Discord is running again.");
        return ("Updating Discord…", null);
    }
}
