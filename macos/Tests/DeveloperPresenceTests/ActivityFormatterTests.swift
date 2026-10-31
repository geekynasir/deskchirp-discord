import XCTest
@testable import DeveloperPresenceCore

final class ActivityFormatterTests: XCTestCase {
    struct PresenceFixtureFile: Codable {
        let version: Int
        let cases: [PresenceTestCase]
    }

    struct PresenceTestCase: Codable {
        let id: String
        let input: PresenceTestInput
        let expected: PresenceTestExpected?
    }

    struct PresenceTestInput: Codable {
        let enabled: Bool
        let paused: Bool
        let suspended: Bool
        let applicationId: String
        let focusedApp: String?
    }

    struct PresenceTestExpected: Codable {
        let details: String?
    }

    func testPresenceCasesParityWithSpec() throws {
        let data = try TestFixtureLoader.loadResource(named: "presence-cases")
        let fixture = try JSONDecoder().decode(PresenceFixtureFile.self, from: data)

        XCTAssertEqual(fixture.version, 1)
        XCTAssertFalse(fixture.cases.isEmpty, "Test cases should not be empty")

        for testCase in fixture.cases {
            let input = testCase.input

            // Map string focusedApp to SupportedApp enum
            let app: SupportedApp
            if let focused = input.focusedApp {
                app = SupportedApp(rawValue: focused) ?? .none
            } else {
                app = .none
            }

            // Validate client ID using presence settings rules
            let settings = PresenceSettings(discordClientId: input.applicationId)
            let isValidClientId = settings.isValidDiscordClientId

            let result = ActivityFormatter.formatActivity(
                app: app,
                enableSharing: input.enabled,
                isPaused: input.paused,
                isSuspended: input.suspended,
                isValidClientId: isValidClientId
            )

            if let expected = testCase.expected {
                XCTAssertNotNil(result, "Case '\(testCase.id)' expected non-nil activity")
                XCTAssertEqual(result?.details, expected.details, "Case '\(testCase.id)' details mismatch")
            } else {
                XCTAssertNil(result, "Case '\(testCase.id)' expected nil activity (cleared)")
            }
        }
    }

    func testStrictPayloadSerializationNoExtraFields() throws {
        let activity = DiscordActivity(details: "Using T3 Code")
        let encoder = JSONEncoder()
        let data = try encoder.encode(activity)

        guard let jsonObject = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            XCTFail("Failed to deserialize activity json")
            return
        }

        // Strict privacy check: only "details" key is permitted!
        XCTAssertEqual(Set(jsonObject.keys), Set(["details"]))
        XCTAssertEqual(jsonObject["details"] as? String, "Using T3 Code")
    }

    func testSetActivityClearEncodesExplicitNull() throws {
        let payload = DiscordSetActivityPayload(pid: 42, activity: nil, nonce: "test-nonce")
        let data = try JSONEncoder().encode(payload)

        guard let json = String(data: data, encoding: .utf8) else {
            XCTFail("Failed to convert data to string")
            return
        }

        // Discord protocol requires "activity":null to clear
        XCTAssertTrue(json.contains("\"activity\":null"), "JSON must explicitly serialize null activity: \(json)")
    }

    func testHeadlines() {
        XCTAssertEqual(ActivityFormatter.getHeadline(for: .t3Code), "Using T3 Code")
        XCTAssertEqual(ActivityFormatter.getHeadline(for: .claude), "Using Claude")
        XCTAssertEqual(ActivityFormatter.getHeadline(for: .codex), "Using Codex")
        XCTAssertEqual(ActivityFormatter.getHeadline(for: .none), "None / Cleared")
    }
}
