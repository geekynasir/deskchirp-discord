import Foundation

/// One T3 environment Deskchirp follows. The label is a local name and never leaves this computer.
public struct T3Environment: Codable, Equatable, Hashable, Sendable, Identifiable {
    public var id: String
    /// Normalized MCP URL, for example `https://host:3773/mcp`.
    public var address: String
    public var label: String

    public init(id: String = UUID().uuidString.lowercased(), address: String, label: String) {
        self.id = id
        self.address = address
        self.label = label
    }

    /// Builds an environment from a Copy MCP URL value or an origin, using the existing validator.
    public static func make(fromAddress input: String, label: String? = nil) throws -> T3Environment {
        let endpoint = try T3UrlValidator.validateAndNormalize(input)
        let trimmedLabel = label?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return T3Environment(
            address: endpoint.mcpUrl.absoluteString,
            label: trimmedLabel.isEmpty ? defaultLabel(for: endpoint) : trimmedLabel
        )
    }

    /// Legacy `t3Address` migration. Invalid legacy values are kept verbatim so the user can fix them.
    public static func migrated(fromLegacyAddress address: String) -> T3Environment {
        let trimmed = address.trimmingCharacters(in: .whitespacesAndNewlines)
        if let environment = try? make(fromAddress: trimmed) { return environment }
        return T3Environment(address: trimmed, label: "T3 Code")
    }

    public static func defaultLabel(for endpoint: T3NormalizedEndpoint) -> String {
        endpoint.mcpUrl.host ?? "T3 Code"
    }

    public var endpoint: T3NormalizedEndpoint? {
        try? T3UrlValidator.validateAndNormalize(address)
    }

    private enum CodingKeys: String, CodingKey { case id, address, label }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.id = try container.decodeIfPresent(String.self, forKey: .id) ?? UUID().uuidString.lowercased()
        self.address = try container.decodeIfPresent(String.self, forKey: .address) ?? ""
        self.label = try container.decodeIfPresent(String.self, forKey: .label) ?? "T3 Code"
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(id, forKey: .id)
        try container.encode(address, forKey: .address)
        try container.encode(label, forKey: .label)
    }
}

public enum PresenceCardStyle: String, Codable, CaseIterable, Sendable {
    case appOnly, workload, usage
}

public enum PresenceUsageMetric: String, Codable, CaseIterable, Sendable {
    case tokens, context
}

public enum PresenceUsageScope: String, Codable, CaseIterable, Sendable {
    case today, session, pinnedTask
}

public enum PresenceTokenCategory: String, Codable, CaseIterable, Sendable {
    case total, input, output
}

public enum PresenceTokenRounding: String, Codable, CaseIterable, Sendable {
    case compact, exact
}

public enum PresenceSmallBadge: String, Codable, CaseIterable, Sendable {
    case status, provider, hidden = "none"
}

public enum PresenceButtons: String, Codable, CaseIterable, Sendable {
    case hidden = "none", companion, companionAndRepo
}

public enum PresenceSelectionMode: String, Codable, CaseIterable, Sendable {
    case followActiveApp, keepBackgroundVisible
}

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

    /// Toggle to show bundled app/agent artwork. ON by default.
    public var showAppIcons: Bool
    public var followComputers: Bool
    public var computerLinkAddress: String

    // MARK: Presence controls v3 (spec/presence-v3.md §3). Nothing new is shared until enabled.

    /// Ordered T3 environments. The first entry mirrors the legacy `t3Address`.
    public var t3Environments: [T3Environment]
    public var cardStyle: PresenceCardStyle
    public var usageMetric: PresenceUsageMetric
    public var usageScope: PresenceUsageScope
    public var tokenCategory: PresenceTokenCategory
    public var tokenRounding: PresenceTokenRounding
    public var smallBadge: PresenceSmallBadge
    public var shareProjectAlias: Bool
    /// `"{environmentId}/{projectId}"` → user-written alias.
    public var projectAliases: [String: String]
    public var shareModel: Bool
    public var shareEstimatedCost: Bool
    public var showElapsedTimer: Bool
    public var playfulWording: Bool
    public var buttons: PresenceButtons
    public var publicRepoUrl: String
    /// Set of `"{environmentId}/{projectId}"`.
    public var excludedProjects: Set<String>
    /// `"{environmentId}/{threadId}"` or empty.
    public var pinnedTask: String
    /// `"{environmentId}/{projectId}"` or empty.
    public var pinnedProject: String
    public var selectionMode: PresenceSelectionMode

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
        computerLinkAddress: String = "",
        t3Environments: [T3Environment]? = nil,
        cardStyle: PresenceCardStyle = .workload,
        usageMetric: PresenceUsageMetric = .tokens,
        usageScope: PresenceUsageScope = .today,
        tokenCategory: PresenceTokenCategory = .total,
        tokenRounding: PresenceTokenRounding = .compact,
        smallBadge: PresenceSmallBadge = .status,
        shareProjectAlias: Bool = false,
        projectAliases: [String: String] = [:],
        shareModel: Bool = false,
        shareEstimatedCost: Bool = false,
        showElapsedTimer: Bool = true,
        playfulWording: Bool = false,
        buttons: PresenceButtons = .hidden,
        publicRepoUrl: String = "",
        excludedProjects: Set<String> = [],
        pinnedTask: String = "",
        pinnedProject: String = "",
        selectionMode: PresenceSelectionMode = .keepBackgroundVisible
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
        self.t3Environments = t3Environments ?? Self.migratedEnvironments(legacyAddress: t3Address)
        self.cardStyle = cardStyle
        self.usageMetric = usageMetric
        self.usageScope = usageScope
        self.tokenCategory = tokenCategory
        self.tokenRounding = tokenRounding
        self.smallBadge = smallBadge
        self.shareProjectAlias = shareProjectAlias
        self.projectAliases = projectAliases
        self.shareModel = shareModel
        self.shareEstimatedCost = shareEstimatedCost
        self.showElapsedTimer = showElapsedTimer
        self.playfulWording = playfulWording
        self.buttons = buttons
        self.publicRepoUrl = publicRepoUrl
        self.excludedProjects = excludedProjects
        self.pinnedTask = pinnedTask
        self.pinnedProject = pinnedProject
        self.selectionMode = selectionMode
    }

    private static func decodeOption<T: RawRepresentable>(
        _ type: T.Type, from container: KeyedDecodingContainer<CodingKeys>, forKey key: CodingKeys, default value: T
    ) throws -> T where T.RawValue == String {
        guard let raw = try container.decodeIfPresent(String.self, forKey: key) else { return value }
        return T(rawValue: raw) ?? value
    }

    /// A non-empty legacy `t3Address` becomes the first environment.
    public static func migratedEnvironments(legacyAddress: String) -> [T3Environment] {
        let trimmed = legacyAddress.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? [] : [T3Environment.migrated(fromLegacyAddress: trimmed)]
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
        case t3Environments, cardStyle, usageMetric, usageScope, tokenCategory, tokenRounding
        case smallBadge, shareProjectAlias, projectAliases, shareModel, shareEstimatedCost
        case showElapsedTimer, playfulWording, buttons, publicRepoUrl, excludedProjects
        case pinnedTask, pinnedProject, selectionMode
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.enableSharing = try container.decodeIfPresent(Bool.self, forKey: .enableSharing) ?? false
        self.isPaused = try container.decodeIfPresent(Bool.self, forKey: .isPaused) ?? false
        self.discordClientId = try container.decodeIfPresent(String.self, forKey: .discordClientId) ?? Self.defaultDiscordApplicationId
        self.updateIntervalSeconds = try container.decodeIfPresent(Int.self, forKey: .updateIntervalSeconds) ?? 5
        self.bundleMappings = try container.decodeIfPresent([String: SupportedApp].self, forKey: .bundleMappings) ?? Self.defaultBundleMappings
        self.showT3Activity = try container.decodeIfPresent(Bool.self, forKey: .showT3Activity) ?? false
        let legacyAddress = try container.decodeIfPresent(String.self, forKey: .t3Address) ?? ""
        self.t3Address = legacyAddress
        self.showAppIcons = try container.decodeIfPresent(Bool.self, forKey: .showAppIcons) ?? true
        self.followComputers = try container.decodeIfPresent(Bool.self, forKey: .followComputers) ?? false
        self.computerLinkAddress = try container.decodeIfPresent(String.self, forKey: .computerLinkAddress) ?? ""

        // Unknown enum strings fall back to defaults instead of failing the whole settings file.
        if let environments = try container.decodeIfPresent([T3Environment].self, forKey: .t3Environments) {
            self.t3Environments = environments
        } else {
            self.t3Environments = Self.migratedEnvironments(legacyAddress: legacyAddress)
        }
        self.cardStyle = try Self.decodeOption(PresenceCardStyle.self, from: container, forKey: .cardStyle, default: .workload)
        self.usageMetric = try Self.decodeOption(PresenceUsageMetric.self, from: container, forKey: .usageMetric, default: .tokens)
        self.usageScope = try Self.decodeOption(PresenceUsageScope.self, from: container, forKey: .usageScope, default: .today)
        self.tokenCategory = try Self.decodeOption(PresenceTokenCategory.self, from: container, forKey: .tokenCategory, default: .total)
        self.tokenRounding = try Self.decodeOption(PresenceTokenRounding.self, from: container, forKey: .tokenRounding, default: .compact)
        self.smallBadge = try Self.decodeOption(PresenceSmallBadge.self, from: container, forKey: .smallBadge, default: .status)
        self.shareProjectAlias = try container.decodeIfPresent(Bool.self, forKey: .shareProjectAlias) ?? false
        self.projectAliases = try container.decodeIfPresent([String: String].self, forKey: .projectAliases) ?? [:]
        self.shareModel = try container.decodeIfPresent(Bool.self, forKey: .shareModel) ?? false
        self.shareEstimatedCost = try container.decodeIfPresent(Bool.self, forKey: .shareEstimatedCost) ?? false
        self.showElapsedTimer = try container.decodeIfPresent(Bool.self, forKey: .showElapsedTimer) ?? true
        self.playfulWording = try container.decodeIfPresent(Bool.self, forKey: .playfulWording) ?? false
        self.buttons = try Self.decodeOption(PresenceButtons.self, from: container, forKey: .buttons, default: .hidden)
        self.publicRepoUrl = try container.decodeIfPresent(String.self, forKey: .publicRepoUrl) ?? ""
        self.excludedProjects = Set(try container.decodeIfPresent([String].self, forKey: .excludedProjects) ?? [])
        self.pinnedTask = try container.decodeIfPresent(String.self, forKey: .pinnedTask) ?? ""
        self.pinnedProject = try container.decodeIfPresent(String.self, forKey: .pinnedProject) ?? ""
        self.selectionMode = try Self.decodeOption(PresenceSelectionMode.self, from: container, forKey: .selectionMode, default: .keepBackgroundVisible)
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
        try container.encode(t3Environments, forKey: .t3Environments)
        try container.encode(cardStyle.rawValue, forKey: .cardStyle)
        try container.encode(usageMetric.rawValue, forKey: .usageMetric)
        try container.encode(usageScope.rawValue, forKey: .usageScope)
        try container.encode(tokenCategory.rawValue, forKey: .tokenCategory)
        try container.encode(tokenRounding.rawValue, forKey: .tokenRounding)
        try container.encode(smallBadge.rawValue, forKey: .smallBadge)
        try container.encode(shareProjectAlias, forKey: .shareProjectAlias)
        try container.encode(projectAliases, forKey: .projectAliases)
        try container.encode(shareModel, forKey: .shareModel)
        try container.encode(shareEstimatedCost, forKey: .shareEstimatedCost)
        try container.encode(showElapsedTimer, forKey: .showElapsedTimer)
        try container.encode(playfulWording, forKey: .playfulWording)
        try container.encode(buttons.rawValue, forKey: .buttons)
        try container.encode(publicRepoUrl, forKey: .publicRepoUrl)
        try container.encode(excludedProjects.sorted(), forKey: .excludedProjects)
        try container.encode(pinnedTask, forKey: .pinnedTask)
        try container.encode(pinnedProject, forKey: .pinnedProject)
        try container.encode(selectionMode.rawValue, forKey: .selectionMode)
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
