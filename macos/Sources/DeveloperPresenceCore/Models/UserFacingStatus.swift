import Foundation

/// Keeps protocol diagnostics out of ordinary app status text.
public enum UserFacingStatus {
    public struct Message {
        public let title: String
        public let recovery: String?
        public init(_ title: String, _ recovery: String? = nil) {
            self.title = title
            self.recovery = recovery
        }
    }

    public static func describe(state: PresenceState, settings: PresenceSettings) -> Message {
        if !settings.enableSharing { return Message("Sharing is off") }
        if settings.isPaused { return Message("Sharing is paused") }
        if state.isSessionSuspended { return Message("Hidden while your Mac is locked or asleep") }
        if settings.discordClientId.isEmpty { return Message("Add your Discord application ID to start sharing") }
        if !settings.isValidDiscordClientId {
            return Message("Check your Discord application ID", "Application IDs are 15 to 20 digits.")
        }
        if state.foregroundApp == .none {
            return Message("Not sharing while another app is active", "Shares when T3 Code, Claude, or Codex is the active app.")
        }
        if state.connectionStatus == .connecting { return Message("Connecting to Discord…") }
        let diagnostic = state.statusMessage.lowercased()
        if diagnostic.contains("not found") || diagnostic.contains("no discord") {
            return Message("Couldn’t find Discord", "Open the Discord desktop app. Discord in a browser can’t show this status.")
        }
        if diagnostic.contains("timed out") || diagnostic.contains("timeout") {
            return Message("Discord didn’t respond", "Make sure Discord is open and signed in. Developer Presence will keep trying.")
        }
        if diagnostic.contains("set_activity") || diagnostic.contains("rejected") || diagnostic.hasPrefix("discord error:") {
            return Message("Discord didn’t accept the update", "Check that your Discord application ID matches an app in the Developer Portal.")
        }
        if state.connectionStatus == .error { return Message("Lost the connection to Discord", "Reconnecting…") }
        if state.connectionStatus == .connected {
            return Message(state.lastUpdate == nil ? "Updating Discord…" : "Sharing on Discord")
        }
        if diagnostic.contains("disconnect") || diagnostic.contains("closed") {
            return Message("Discord disconnected", "Reconnects when Discord is running again.")
        }
        return Message("Updating Discord…")
    }

    public static func t3Detail(status: T3ConnectionStatus, message: String?) -> String {
        let safeMessages: Set<String> = [
            "Paste the address from T3 Code first.",
            "Use an address that starts with https://. Plain http:// works only for T3 Code on this computer.",
            "This doesn’t look like a T3 Code address. Copy it again from Settings → Environments → Copy MCP URL.",
            "This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.",
            "T3 Code sent Developer Presence to a different address. Copy the address again from T3 Code.",
            "Couldn’t open your browser. Check that you have a default browser, then try again.",
            "Sign-in was cancelled.",
            "Sign-in was cancelled. Your existing connection wasn’t changed.",
            "Sign-in didn’t finish. Try connecting again.",
            "Sign-in didn’t finish. Your existing connection wasn’t changed.",
            "Sign-in timed out. Try connecting again.",
            "Couldn’t save your sign-in to Keychain. Try connecting again.",
            "Couldn’t remove your saved sign-in. Try disconnecting again.",
            "To revoke access in T3 too, open T3 Code Settings → Sharing & access."
        ]
        if let message = message, safeMessages.contains(message) { return message }
        if message?.contains("Keychain") == true { return "Couldn’t save your sign-in to Keychain. Try connecting again." }
        if status == .openingSignIn {
            return "Approve Read only access in your browser, then come back here. If T3 asks for a pairing code, create one in T3 Code under Settings → Sharing & access."
        }
        if status == .notConnected, let message = message, !message.isEmpty,
           message != status.recoveryText { return "Sign-in didn’t finish. Try connecting again." }
        return status.recoveryText
    }
}
