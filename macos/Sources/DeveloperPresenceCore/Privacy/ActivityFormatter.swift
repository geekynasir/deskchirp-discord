import Foundation

/// Strict privacy gate and presence formatter.
/// Guaranteed behavior:
/// - Only formats whitelisted static strings: "Using T3 Code", "Using Claude", "Using Codex".
/// - Window titles, file names, URLs, repositories, and screens are NEVER read or formatted.
/// - Never fabricates fake task counts or agent activity.
/// - Returns nil activity (clearing Discord presence) when disabled, paused, session is suspended/locked,
///   application ID is invalid/empty, or focused application is unsupported.
public enum ActivityFormatter {
    public static let headlineT3Code = "Using T3 Code"
    public static let headlineClaude = "Using Claude"
    public static let headlineCodex = "Using Codex"
    public static let headlineNone = "None / Cleared"

    public static func getHeadline(for app: SupportedApp) -> String {
        app.presenceDetails ?? headlineNone
    }

    public static func getHeadline(for activity: DiscordActivity?) -> String {
        activity?.details ?? headlineNone
    }

    /// Formats a safe DiscordActivity object adhering to the sharing gate specification.
    /// Returns nil if sharing is disabled, paused, session is suspended/locked,
    /// application ID is invalid/empty, or focused application is unsupported.
    public static func formatActivity(
        app: SupportedApp,
        enableSharing: Bool,
        isPaused: Bool,
        isSuspended: Bool,
        isValidClientId: Bool
    ) -> DiscordActivity? {
        // Sharing gate conditions:
        // 1. Sharing must be enabled
        // 2. Not paused
        // 3. Not suspended (session unlocked and awake)
        // 4. Valid numeric application ID configured
        // 5. Observed supported foreground application
        guard enableSharing, !isPaused, !isSuspended, isValidClientId else {
            return nil
        }
        guard let details = app.presenceDetails else {
            return nil
        }
        return DiscordActivity(details: details)
    }
}
