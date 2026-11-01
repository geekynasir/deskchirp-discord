import Foundation

public protocol SettingsServicing: AnyObject, Sendable {
    func loadSettings() -> PresenceSettings
    func saveSettings(_ settings: PresenceSettings)
}

/// Manages persistence of user settings in UserDefaults.
/// Guarantees:
/// - Sharing is disabled (OFF) by default.
/// - Show T3 Activity is disabled (OFF) by default.
/// - Discord uses Deskchirp’s publisher-owned public application ID by default.
/// - Contains NO secrets, passwords, tokens, or credentials.
public final class SettingsService: SettingsServicing, @unchecked Sendable {
    public static let shared = SettingsService()

    private let userDefaults: UserDefaults
    private let queue = DispatchQueue(label: "com.developerpresence.settings")

    public enum Keys {
        public static let enableSharing = "developer_presence_enable_sharing"
        public static let isPaused = "developer_presence_is_paused"
        public static let discordClientId = "developer_presence_discord_client_id"
        public static let updateIntervalSeconds = "developer_presence_update_interval_seconds"
        public static let bundleMappings = "developer_presence_bundle_mappings"
        public static let showT3Activity = "developer_presence_show_t3_activity"
        public static let t3Address = "developer_presence_t3_address"
        public static let showAppIcons = "developer_presence_show_app_icons"
        public static let followComputers = "developer_presence_follow_computers"
        public static let computerLinkAddress = "developer_presence_computer_link_address"
        /// JSON blob holding the presence controls v3 fields (spec/presence-v3.md §3).
        public static let presenceControlsV3 = "developer_presence_controls_v3"
    }

    public init(userDefaults: UserDefaults = .standard) {
        self.userDefaults = userDefaults
    }

    public func loadSettings() -> PresenceSettings {
        queue.sync {
            let enableSharing = userDefaults.bool(forKey: Keys.enableSharing)
            let isPaused = userDefaults.bool(forKey: Keys.isPaused)
            let savedId = userDefaults.string(forKey: Keys.discordClientId) ?? ""
            let discordClientId = savedId.isEmpty ? PresenceSettings.defaultDiscordApplicationId : savedId
            let interval = userDefaults.integer(forKey: Keys.updateIntervalSeconds)
            let updateIntervalSeconds = interval > 0 ? interval : 5

            var bundleMappings = PresenceSettings.defaultBundleMappings
            if let data = userDefaults.data(forKey: Keys.bundleMappings),
               let savedMappings = try? JSONDecoder().decode([String: SupportedApp].self, from: data) {
                bundleMappings = savedMappings
            }

            let showT3Activity = userDefaults.bool(forKey: Keys.showT3Activity)
            let t3Address = userDefaults.string(forKey: Keys.t3Address) ?? ""
            let artworkVersionKey = "developer_presence_artwork_defaults_version"
            let showAppIcons: Bool
            if userDefaults.integer(forKey: artworkVersionKey) < 1 {
                showAppIcons = true
                userDefaults.set(true, forKey: Keys.showAppIcons)
                userDefaults.set(1, forKey: artworkVersionKey)
            } else { showAppIcons = userDefaults.bool(forKey: Keys.showAppIcons) }

            var settings = PresenceSettings(
                enableSharing: enableSharing,
                isPaused: isPaused,
                discordClientId: discordClientId,
                updateIntervalSeconds: updateIntervalSeconds,
                bundleMappings: bundleMappings,
                showT3Activity: showT3Activity,
                t3Address: t3Address,
                showAppIcons: showAppIcons,
                followComputers: userDefaults.bool(forKey: Keys.followComputers),
                computerLinkAddress: userDefaults.string(forKey: Keys.computerLinkAddress) ?? "",
                t3Environments: []
            )

            if let data = userDefaults.data(forKey: Keys.presenceControlsV3),
               let saved = try? JSONDecoder().decode(PresenceSettings.self, from: data) {
                Self.copyControls(from: saved, to: &settings)
            } else {
                // First launch on v3: the legacy address becomes the first environment. Persist the
                // migrated list right away so the random environment ID stays stable.
                settings.t3Environments = PresenceSettings.migratedEnvironments(legacyAddress: t3Address)
                if let data = try? JSONEncoder().encode(settings) {
                    userDefaults.set(data, forKey: Keys.presenceControlsV3)
                }
            }
            return settings
        }
    }

    private static func copyControls(from source: PresenceSettings, to target: inout PresenceSettings) {
        target.t3Environments = source.t3Environments
        target.cardStyle = source.cardStyle
        target.usageMetric = source.usageMetric
        target.usageScope = source.usageScope
        target.tokenCategory = source.tokenCategory
        target.tokenRounding = source.tokenRounding
        target.smallBadge = source.smallBadge
        target.shareProjectAlias = source.shareProjectAlias
        target.projectAliases = source.projectAliases
        target.shareModel = source.shareModel
        target.shareEstimatedCost = source.shareEstimatedCost
        target.showElapsedTimer = source.showElapsedTimer
        target.playfulWording = source.playfulWording
        target.buttons = source.buttons
        target.publicRepoUrl = source.publicRepoUrl
        target.excludedProjects = source.excludedProjects
        target.pinnedTask = source.pinnedTask
        target.pinnedProject = source.pinnedProject
        target.selectionMode = source.selectionMode
    }

    public func saveSettings(_ settings: PresenceSettings) {
        queue.sync {
            userDefaults.set(settings.enableSharing, forKey: Keys.enableSharing)
            userDefaults.set(settings.isPaused, forKey: Keys.isPaused)
            userDefaults.set(settings.discordClientId, forKey: Keys.discordClientId)
            userDefaults.set(settings.updateIntervalSeconds, forKey: Keys.updateIntervalSeconds)

            if let data = try? JSONEncoder().encode(settings.bundleMappings) {
                userDefaults.set(data, forKey: Keys.bundleMappings)
            }

            userDefaults.set(settings.showT3Activity, forKey: Keys.showT3Activity)
            userDefaults.set(settings.t3Address, forKey: Keys.t3Address)
            userDefaults.set(settings.showAppIcons, forKey: Keys.showAppIcons)
            userDefaults.set(settings.followComputers, forKey: Keys.followComputers)
            userDefaults.set(settings.computerLinkAddress, forKey: Keys.computerLinkAddress)
            if let data = try? JSONEncoder().encode(settings) {
                userDefaults.set(data, forKey: Keys.presenceControlsV3)
            }
        }
    }
}
