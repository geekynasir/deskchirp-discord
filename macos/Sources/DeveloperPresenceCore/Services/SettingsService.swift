import Foundation

public protocol SettingsServicing: AnyObject, Sendable {
    func loadSettings() -> PresenceSettings
    func saveSettings(_ settings: PresenceSettings)
}

/// Manages persistence of user settings in UserDefaults.
/// Guarantees:
/// - Sharing is disabled (OFF) by default.
/// - Discord Client ID is EMPTY ("") by default.
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
    }

    public init(userDefaults: UserDefaults = .standard) {
        self.userDefaults = userDefaults
    }

    public func loadSettings() -> PresenceSettings {
        queue.sync {
            let enableSharing = userDefaults.bool(forKey: Keys.enableSharing)
            let isPaused = userDefaults.bool(forKey: Keys.isPaused)
            let discordClientId = userDefaults.string(forKey: Keys.discordClientId) ?? ""
            let interval = userDefaults.integer(forKey: Keys.updateIntervalSeconds)
            let updateIntervalSeconds = interval > 0 ? interval : 5

            var bundleMappings = PresenceSettings.defaultBundleMappings
            if let data = userDefaults.data(forKey: Keys.bundleMappings),
               let savedMappings = try? JSONDecoder().decode([String: SupportedApp].self, from: data) {
                bundleMappings = savedMappings
            }

            return PresenceSettings(
                enableSharing: enableSharing,
                isPaused: isPaused,
                discordClientId: discordClientId,
                updateIntervalSeconds: updateIntervalSeconds,
                bundleMappings: bundleMappings
            )
        }
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
        }
    }
}
