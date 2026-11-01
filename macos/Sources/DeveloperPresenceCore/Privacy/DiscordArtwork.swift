import Foundation

/// Fixed public artwork only. Discord fetches images; local paths and user content never become URLs.
public enum DiscordArtwork {
    /// Release tag holding the public PNGs. Change only this constant when the artwork pack moves.
    public static let artworkTag = "artwork-v2"
    public static let baseURL = "https://raw.githubusercontent.com/geekynasir/deskchirp-discord/\(DiscordArtwork.artworkTag)/assets/discord/"
    public static let keys: Set<String> = [
        "deskchirp", "t3_code", "harness_claude", "harness_codex", "harness_antigravity", "harness_cursor",
        "harness_opencode", "harness_grok", "status_working", "status_attention", "status_idle"
    ]

    public static func url(for key: String?) -> String? {
        guard let key, keys.contains(key) else { return nil }
        return baseURL + key + ".png"
    }

    public static func apply(to activity: DiscordActivity?, app: SupportedApp, enabled: Bool) -> DiscordActivity? {
        guard let activity, enabled else { return activity }
        let defaultKey: String
        switch app {
        case .t3Code: defaultKey = "t3_code"
        case .claude: defaultKey = "harness_claude"
        case .codex: defaultKey = "harness_codex"
        case .none: return activity
        }
        let source = activity.assets
        let smallImage = url(for: source?.small_image)
        let assets = DiscordActivityAssets(
            largeImage: url(for: source?.large_image ?? defaultKey), largeText: source?.large_text ?? app.displayName,
            smallImage: smallImage, smallText: smallImage == nil ? nil : source?.small_text)
        return DiscordActivity(
            details: activity.details, state: activity.state, assets: assets,
            timestamps: activity.timestamps, buttons: activity.buttons)
    }
}
