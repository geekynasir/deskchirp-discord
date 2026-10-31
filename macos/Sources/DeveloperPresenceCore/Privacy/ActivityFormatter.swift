import Foundation

/// Strict privacy gate and presence formatter.
/// Guaranteed behavior:
/// - Only formats whitelisted static strings or verified T3 agent tool names and counts.
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
        isValidClientId: Bool,
        t3FormattedActivity: T3FormattedActivity? = nil
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
        guard app != .none else {
            return nil
        }

        switch app {
        case .none:
            return nil
        case .claude:
            // Other desktop apps retain app-name-only presence. T3 data never goes onto Claude card.
            return DiscordActivity(details: "Using Claude")
        case .codex:
            // Other desktop apps retain app-name-only presence. T3 data never goes onto Codex card.
            return DiscordActivity(details: "Using Codex")
        case .t3Code:
            if let t3 = t3FormattedActivity {
                return DiscordActivity(
                    details: t3.details,
                    state: t3.state,
                    assets: t3.assets
                )
            } else {
                return DiscordActivity(details: "Using T3 Code")
            }
        }
    }
}
