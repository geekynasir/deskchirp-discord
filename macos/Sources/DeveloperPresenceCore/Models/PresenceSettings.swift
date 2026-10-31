import Foundation

/// User privacy and configuration settings.
/// Contains NO tokens, secrets, or sensitive credentials.
public struct PresenceSettings: Codable, Equatable, Sendable {
    /// Master presence sharing toggle. OFF by default for privacy.
    public var enableSharing: Bool

    /// Temporary pause toggle for presence broadcast.
    public var isPaused: Bool

    /// Public numeric Discord Application ID.
    /// Must default to EMPTY string (never an invented or synthetic ID).
    public var discordClientId: String

    /// Minimum interval between Discord updates in seconds. Default 5 seconds per spec.
    public var updateIntervalSeconds: Int

    /// Custom bundle ID mappings to supported apps.
    /// Keys are normalized to lowercase.
    public var bundleMappings: [String: SupportedApp]

    public init(
        enableSharing: Bool = false,
        isPaused: Bool = false,
        discordClientId: String = "",
        updateIntervalSeconds: Int = 5,
        bundleMappings: [String: SupportedApp] = Self.defaultBundleMappings
    ) {
        self.enableSharing = enableSharing
        self.isPaused = isPaused
        self.discordClientId = discordClientId
        self.updateIntervalSeconds = updateIntervalSeconds
        self.bundleMappings = bundleMappings
    }

    /// Default verified bundle ID mappings.
    /// - T3 Code confirmed upstream: com.t3tools.t3code
    /// - Claude Desktop confirmed Homebrew cask: com.anthropic.claudefordesktop
    /// - Codex: user-configurable (no unverified CLI matching)
    public static var defaultBundleMappings: [String: SupportedApp] {
        [
            "com.t3tools.t3code": .t3Code,
            "com.anthropic.claudefordesktop": .claude
        ]
    }

    /// Validates that Discord Client ID consists purely of ASCII digits (15-20 characters).
    public var isValidDiscordClientId: Bool {
        let trimmed = discordClientId.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.count >= 15 && trimmed.count <= 20 else { return false }
        return trimmed.utf8.allSatisfy { $0 >= 48 && $0 <= 57 }
    }
}
