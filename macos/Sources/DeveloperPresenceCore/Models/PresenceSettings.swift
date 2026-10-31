import Foundation

/// User privacy and configuration settings.
/// Contains NO tokens, secrets, or sensitive credentials.
public struct PresenceSettings: Codable, Equatable, Sendable {
    public static let defaultDiscordApplicationId = "1566210608569782373"
    /// Master presence sharing toggle. OFF by default for privacy.
    public var enableSharing: Bool

    /// Temporary pause toggle for presence broadcast.
    public var isPaused: Bool

    /// Public numeric Discord Application ID.
    /// Publisher-owned public application; no user Developer Portal setup needed.
    public var discordClientId: String

    /// Minimum interval between Discord updates in seconds. Default 5 seconds per spec.
    public var updateIntervalSeconds: Int

    /// Custom bundle ID mappings to supported apps.
    /// Keys are normalized to lowercase.
    public var bundleMappings: [String: SupportedApp]

    /// Toggle to show rich T3 agent activity. OFF by default for privacy.
    public var showT3Activity: Bool

    /// User-configured T3 Code address (e.g. from Copy MCP URL).
    public var t3Address: String

    /// Toggle to show Discord app icons (t3_code, harness_*). OFF by default.
    public var showAppIcons: Bool
    public var followComputers: Bool
    public var computerLinkAddress: String

    public init(
        enableSharing: Bool = false,
        isPaused: Bool = false,
        discordClientId: String = Self.defaultDiscordApplicationId,
        updateIntervalSeconds: Int = 5,
        bundleMappings: [String: SupportedApp] = Self.defaultBundleMappings,
        showT3Activity: Bool = false,
        t3Address: String = "",
        showAppIcons: Bool = true,
        followComputers: Bool = false,
        computerLinkAddress: String = ""
    ) {
        self.enableSharing = enableSharing
        self.isPaused = isPaused
        self.discordClientId = discordClientId
        self.updateIntervalSeconds = updateIntervalSeconds
        self.bundleMappings = bundleMappings
        self.showT3Activity = showT3Activity
        self.t3Address = t3Address
        self.showAppIcons = showAppIcons
        self.followComputers = followComputers
        self.computerLinkAddress = computerLinkAddress
    }

    private enum CodingKeys: String, CodingKey {
        case enableSharing
        case isPaused
        case discordClientId
        case updateIntervalSeconds
        case bundleMappings
        case showT3Activity
        case t3Address
        case showAppIcons
        case followComputers, computerLinkAddress
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.enableSharing = try container.decodeIfPresent(Bool.self, forKey: .enableSharing) ?? false
        self.isPaused = try container.decodeIfPresent(Bool.self, forKey: .isPaused) ?? false
        self.discordClientId = try container.decodeIfPresent(String.self, forKey: .discordClientId) ?? Self.defaultDiscordApplicationId
        self.updateIntervalSeconds = try container.decodeIfPresent(Int.self, forKey: .updateIntervalSeconds) ?? 5
        self.bundleMappings = try container.decodeIfPresent([String: SupportedApp].self, forKey: .bundleMappings) ?? Self.defaultBundleMappings
        self.showT3Activity = try container.decodeIfPresent(Bool.self, forKey: .showT3Activity) ?? false
        self.t3Address = try container.decodeIfPresent(String.self, forKey: .t3Address) ?? ""
        self.showAppIcons = try container.decodeIfPresent(Bool.self, forKey: .showAppIcons) ?? true
        self.followComputers = try container.decodeIfPresent(Bool.self, forKey: .followComputers) ?? false
        self.computerLinkAddress = try container.decodeIfPresent(String.self, forKey: .computerLinkAddress) ?? ""
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(enableSharing, forKey: .enableSharing)
        try container.encode(isPaused, forKey: .isPaused)
        try container.encode(discordClientId, forKey: .discordClientId)
        try container.encode(updateIntervalSeconds, forKey: .updateIntervalSeconds)
        try container.encode(bundleMappings, forKey: .bundleMappings)
        try container.encode(showT3Activity, forKey: .showT3Activity)
        try container.encode(t3Address, forKey: .t3Address)
        try container.encode(showAppIcons, forKey: .showAppIcons)
        try container.encode(followComputers, forKey: .followComputers)
        try container.encode(computerLinkAddress, forKey: .computerLinkAddress)
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
