import XCTest
@testable import DeveloperPresenceCore

final class PresenceV3SettingsTests: XCTestCase {
    private let suiteName = "com.developerpresence.tests.v3settings"
    private var defaults: UserDefaults!

    override func setUp() {
        super.setUp()
        defaults = UserDefaults(suiteName: suiteName)
        defaults.removePersistentDomain(forName: suiteName)
    }

    override func tearDown() {
        defaults.removePersistentDomain(forName: suiteName)
        defaults = nil
        super.tearDown()
    }

    func testOlderSettingsDecodeWithPrivateDefaults() throws {
        let legacy = """
        { "enableSharing": true, "showT3Activity": true, "t3Address": "http://localhost:3773" }
        """.data(using: .utf8)!
        let settings = try JSONDecoder().decode(PresenceSettings.self, from: legacy)

        XCTAssertEqual(settings.t3Environments.count, 1, "A non-empty legacy t3Address becomes the first environment")
        XCTAssertEqual(settings.t3Environments.first?.address, "http://localhost:3773/mcp")
        XCTAssertEqual(settings.t3Environments.first?.label, "localhost")
        XCTAssertFalse(settings.t3Environments.first?.id.isEmpty ?? true)
        XCTAssertEqual(settings.cardStyle, .workload)
        XCTAssertEqual(settings.usageMetric, .tokens)
        XCTAssertEqual(settings.usageScope, .today)
        XCTAssertEqual(settings.tokenCategory, .total)
        XCTAssertEqual(settings.tokenRounding, .compact)
        XCTAssertEqual(settings.smallBadge, .status)
        XCTAssertFalse(settings.shareProjectAlias)
        XCTAssertTrue(settings.projectAliases.isEmpty)
        XCTAssertFalse(settings.shareModel)
        XCTAssertFalse(settings.shareEstimatedCost)
        XCTAssertTrue(settings.showElapsedTimer)
        XCTAssertFalse(settings.playfulWording)
        XCTAssertEqual(settings.buttons, .hidden)
        XCTAssertEqual(settings.publicRepoUrl, "")
        XCTAssertTrue(settings.excludedProjects.isEmpty)
        XCTAssertEqual(settings.pinnedTask, "")
        XCTAssertEqual(settings.pinnedProject, "")
        XCTAssertEqual(settings.selectionMode, .keepBackgroundVisible)
    }

    func testEmptyLegacyAddressMeansNoEnvironment() throws {
        let settings = try JSONDecoder().decode(PresenceSettings.self, from: Data("{}".utf8))
        XCTAssertTrue(settings.t3Environments.isEmpty)
    }

    func testUnknownOptionValuesFallBackToDefaults() throws {
        let json = """
        { "cardStyle": "hologram", "smallBadge": "none", "buttons": "companionAndRepo", "selectionMode": "followActiveApp" }
        """.data(using: .utf8)!
        let settings = try JSONDecoder().decode(PresenceSettings.self, from: json)
        XCTAssertEqual(settings.cardStyle, .workload)
        XCTAssertEqual(settings.smallBadge, .hidden)
        XCTAssertEqual(settings.buttons, .companionAndRepo)
        XCTAssertEqual(settings.selectionMode, .followActiveApp)
    }

    func testRoundTripKeepsEveryNewField() throws {
        var settings = PresenceSettings(t3Environments: [T3Environment(id: "env-1", address: "https://t3.example/mcp", label: "Studio")])
        settings.cardStyle = .usage
        settings.usageMetric = .context
        settings.usageScope = .pinnedTask
        settings.tokenCategory = .output
        settings.tokenRounding = .exact
        settings.smallBadge = .provider
        settings.shareProjectAlias = true
        settings.projectAliases = ["env-1/p": "Orbit"]
        settings.shareModel = true
        settings.shareEstimatedCost = true
        settings.showElapsedTimer = false
        settings.playfulWording = true
        settings.buttons = .companion
        settings.publicRepoUrl = "https://github.com/a/b"
        settings.excludedProjects = ["env-1/q"]
        settings.pinnedTask = "env-1/t"
        settings.pinnedProject = "env-1/p"
        settings.selectionMode = .followActiveApp

        let decoded = try JSONDecoder().decode(PresenceSettings.self, from: JSONEncoder().encode(settings))
        XCTAssertEqual(decoded, settings)
    }

    func testSettingsServiceMigratesLegacyAddressOnceWithStableId() {
        defaults.set("http://localhost:3773", forKey: SettingsService.Keys.t3Address)
        let service = SettingsService(userDefaults: defaults)
        let first = service.loadSettings()
        let second = service.loadSettings()
        XCTAssertEqual(first.t3Environments.count, 1)
        XCTAssertEqual(first.t3Environments.first?.id, second.t3Environments.first?.id, "Environment IDs must stay stable")

        var changed = first
        changed.cardStyle = .appOnly
        changed.excludedProjects = ["x/y"]
        changed.t3Environments.append(T3Environment(id: "remote", address: "https://remote.example/mcp", label: "Remote"))
        service.saveSettings(changed)
        let reloaded = service.loadSettings()
        XCTAssertEqual(reloaded.cardStyle, .appOnly)
        XCTAssertEqual(reloaded.excludedProjects, ["x/y"])
        XCTAssertEqual(reloaded.t3Environments.map { $0.id }, [first.t3Environments[0].id, "remote"])
    }

    func testEnvironmentFromOriginOrMcpUrlUsesValidator() throws {
        let fromOrigin = try T3Environment.make(fromAddress: "https://Build.Example:8443")
        XCTAssertEqual(fromOrigin.address, "https://build.example:8443/mcp")
        XCTAssertEqual(fromOrigin.label, "build.example")
        let fromMcp = try T3Environment.make(fromAddress: "https://build.example:8443/mcp", label: "  Build box ")
        XCTAssertEqual(fromMcp.label, "Build box")
        XCTAssertThrowsError(try T3Environment.make(fromAddress: "http://remote.example:3773"))
        XCTAssertNotEqual(fromOrigin.id, fromMcp.id)
    }

    func testAvailabilityReasons() {
        var settings = PresenceSettings(showT3Activity: true, t3Environments: [])
        XCTAssertEqual(PresenceAvailability.cardStyleReason(.workload, settings: settings, anyEnvironmentConnected: false, anyUsageAllowed: false),
                       "Connect T3 Code to show work.")
        XCTAssertEqual(PresenceAvailability.cardStyleReason(.usage, settings: settings, anyEnvironmentConnected: true, anyUsageAllowed: false),
                       "Allow token usage for an environment first.")
        XCTAssertNil(PresenceAvailability.cardStyleReason(.usage, settings: settings, anyEnvironmentConnected: true, anyUsageAllowed: true))
        XCTAssertNil(PresenceAvailability.cardStyleReason(.appOnly, settings: settings, anyEnvironmentConnected: false, anyUsageAllowed: false))

        XCTAssertEqual(PresenceAvailability.scopeReason(.pinnedTask, settings: settings), "Pick a task to pin.")
        settings.excludedProjects = ["e/p"]
        XCTAssertEqual(PresenceAvailability.scopeReason(.today, settings: settings), "Today’s totals can’t skip excluded projects.")
        settings.usageMetric = .context
        XCTAssertEqual(PresenceAvailability.scopeReason(.session, settings: settings), "Context is measured per task.")

        settings.cardStyle = .usage
        settings.usageMetric = .tokens
        settings.usageScope = .session
        XCTAssertFalse(PresenceAvailability.showsCostOption(settings))
        settings.usageScope = .today
        XCTAssertTrue(PresenceAvailability.showsCostOption(settings))

        settings.shareProjectAlias = true
        settings.publicRepoUrl = "https://github.com/a/b/c"
        XCTAssertEqual(PresenceAvailability.repoButtonReason(settings), "Use a public GitHub repository link.")
        settings.publicRepoUrl = "https://github.com/a/b"
        XCTAssertNil(PresenceAvailability.repoButtonReason(settings))
    }

    func testArtworkUsesTaggedWhitelistIncludingStatusBadges() {
        XCTAssertTrue(DiscordArtwork.baseURL.contains("/artwork-v2/"))
        for key in ["status_working", "status_attention", "status_idle"] {
            XCTAssertEqual(DiscordArtwork.url(for: key), DiscordArtwork.baseURL + key + ".png")
        }
        XCTAssertNil(DiscordArtwork.url(for: "../secret"))

        let activity = DiscordActivity(
            details: "T3 Code · Codex", state: "1 task",
            assets: DiscordActivityAssets(largeImage: "t3_code", largeText: "T3 Code · Tokens counted for the pinned task",
                                          smallImage: "status_working", smallText: "Working"),
            timestamps: DiscordActivityTimestamps(start: 42),
            buttons: [PresenceCardFormatter.companionButton])
        let resolved = DiscordArtwork.apply(to: activity, app: .t3Code, enabled: true)
        XCTAssertEqual(resolved?.assets?.large_text, "T3 Code · Tokens counted for the pinned task")
        XCTAssertEqual(resolved?.assets?.small_image, DiscordArtwork.baseURL + "status_working.png")
        XCTAssertEqual(resolved?.timestamps?.start, 42)
        XCTAssertEqual(resolved?.buttons?.count, 1)
    }
}
