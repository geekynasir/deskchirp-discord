import XCTest
@testable import DeveloperPresenceCore

final class SettingsMigrationTests: XCTestCase {
    func testLegacyV1JsonDecodesWithSafeT3Defaults() throws {
        // Legacy JSON without T3 keys
        let legacyJson = """
        {
            "enableSharing": true,
            "isPaused": false,
            "discordClientId": "123456789012345678",
            "updateIntervalSeconds": 5,
            "bundleMappings": {
                "com.t3tools.t3code": "t3",
                "com.anthropic.claudefordesktop": "claude"
            }
        }
        """.data(using: .utf8)!

        let settings = try JSONDecoder().decode(PresenceSettings.self, from: legacyJson)

        XCTAssertTrue(settings.enableSharing)
        XCTAssertFalse(settings.isPaused)
        XCTAssertEqual(settings.discordClientId, "123456789012345678")
        XCTAssertEqual(settings.updateIntervalSeconds, 5)

        // Safe defaults for newly introduced keys
        XCTAssertFalse(settings.showT3Activity, "showT3Activity must default to false for privacy on migration")
        XCTAssertEqual(settings.t3Address, "", "t3Address must default to empty string")
        XCTAssertTrue(settings.showAppIcons, "Bundled public artwork is on by default")

        // Existing bundle mappings must be preserved
        XCTAssertEqual(settings.bundleMappings["com.t3tools.t3code"], .t3Code)
        XCTAssertEqual(settings.bundleMappings["com.anthropic.claudefordesktop"], .claude)
    }

    func testCompletelyEmptyJsonAppliesAllDefaults() throws {
        let emptyJson = "{}".data(using: .utf8)!
        let settings = try JSONDecoder().decode(PresenceSettings.self, from: emptyJson)

        XCTAssertFalse(settings.enableSharing)
        XCTAssertFalse(settings.isPaused)
        XCTAssertEqual(settings.discordClientId, PresenceSettings.defaultDiscordApplicationId)
        XCTAssertEqual(settings.updateIntervalSeconds, 5)
        XCTAssertFalse(settings.showT3Activity)
        XCTAssertEqual(settings.t3Address, "")
        XCTAssertTrue(settings.showAppIcons)
        XCTAssertEqual(settings.bundleMappings["com.t3tools.t3code"], .t3Code)
    }
}
