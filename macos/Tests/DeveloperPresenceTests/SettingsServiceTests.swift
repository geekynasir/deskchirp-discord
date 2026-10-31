import XCTest
@testable import DeveloperPresenceCore

final class SettingsServiceTests: XCTestCase {
    private var testDefaults: UserDefaults!
    private let suiteName = "com.developerpresence.tests.settings"

    override func setUp() {
        super.setUp()
        testDefaults = UserDefaults(suiteName: suiteName)
        testDefaults?.removePersistentDomain(forName: suiteName)
    }

    override func tearDown() {
        testDefaults?.removePersistentDomain(forName: suiteName)
        testDefaults = nil
        super.tearDown()
    }

    func testDefaultSettingsPrivacyGuarantees() {
        let service = SettingsService(userDefaults: testDefaults)
        let settings = service.loadSettings()

        // Master toggle must be OFF by default
        XCTAssertFalse(settings.enableSharing, "Sharing must be OFF by default")

        // Must NOT be paused by default
        XCTAssertFalse(settings.isPaused, "Paused should be false by default")

        // Discord Client ID must be EMPTY by default (never an invented or synthetic ID)
        XCTAssertEqual(settings.discordClientId, "", "Discord Client ID must be EMPTY by default")
        XCTAssertFalse(settings.isValidDiscordClientId, "Empty Client ID must not be valid")

        // 5 seconds default throttle
        XCTAssertEqual(settings.updateIntervalSeconds, 5)

        // Default bundle mappings must include T3 Code and Claude Desktop
        XCTAssertEqual(settings.bundleMappings["com.t3tools.t3code"], .t3Code)
        XCTAssertEqual(settings.bundleMappings["com.anthropic.claudefordesktop"], .claude)
    }

    func testSaveAndLoadSettings() {
        let service = SettingsService(userDefaults: testDefaults)
        var settings = service.loadSettings()

        settings.enableSharing = true
        settings.isPaused = true
        settings.discordClientId = "123456789012345678"
        settings.updateIntervalSeconds = 10
        settings.bundleMappings["com.custom.codex"] = .codex

        service.saveSettings(settings)

        let reloaded = service.loadSettings()
        XCTAssertTrue(reloaded.enableSharing)
        XCTAssertTrue(reloaded.isPaused)
        XCTAssertEqual(reloaded.discordClientId, "123456789012345678")
        XCTAssertTrue(reloaded.isValidDiscordClientId)
        XCTAssertEqual(reloaded.updateIntervalSeconds, 10)
        XCTAssertEqual(reloaded.bundleMappings["com.custom.codex"], .codex)
    }

    func testDiscordClientIdValidation() {
        var settings = PresenceSettings()

        settings.discordClientId = ""
        XCTAssertFalse(settings.isValidDiscordClientId)

        settings.discordClientId = "   "
        XCTAssertFalse(settings.isValidDiscordClientId)

        settings.discordClientId = "12345" // Too short
        XCTAssertFalse(settings.isValidDiscordClientId)

        settings.discordClientId = "12345678901234" // 14 chars, too short
        XCTAssertFalse(settings.isValidDiscordClientId)

        settings.discordClientId = "123456789012345" // 15 chars, valid
        XCTAssertTrue(settings.isValidDiscordClientId)

        settings.discordClientId = "123456789012345678" // 18 chars, valid
        XCTAssertTrue(settings.isValidDiscordClientId)

        settings.discordClientId = "12345678901234567890" // 20 chars, valid
        XCTAssertTrue(settings.isValidDiscordClientId)

        settings.discordClientId = "12345678901234567890123" // 23 chars, too long
        XCTAssertFalse(settings.isValidDiscordClientId)

        settings.discordClientId = "12345678901234567a" // contains letter
        XCTAssertFalse(settings.isValidDiscordClientId)

        settings.discordClientId = "١٢٣٤٥٦٧٨٩٠١٢٣٤٥"
        XCTAssertFalse(settings.isValidDiscordClientId)

        settings.discordClientId = "123456789012345678901" // 21 chars, too long
        XCTAssertFalse(settings.isValidDiscordClientId)

        settings.discordClientId = "not-a-token"
        XCTAssertFalse(settings.isValidDiscordClientId)
    }
}
