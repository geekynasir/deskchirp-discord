import XCTest
@testable import DeveloperPresenceCore

final class T3ActivityFormatterTests: XCTestCase {
    struct T3CasesFile: Codable {
        let description: String?
        let providers: [T3ProviderInfo]
        let cases: [T3TestCase]
    }

    struct T3TestCase: Codable {
        let name: String
        let threads: [T3ThreadItem]
        let expected: T3Expected
    }

    struct T3Expected: Codable {
        let details: String
        let state: String?
    }

    func testSpecCasesParityWithFixture() throws {
        let data = try TestFixtureLoader.loadResource(named: "t3-activity-cases")
        let fixture = try JSONDecoder().decode(T3CasesFile.self, from: data)

        XCTAssertFalse(fixture.cases.isEmpty, "Cases fixture must not be empty")

        for tc in fixture.cases {
            let selector = T3WinnerSelector()
            let (snapshot, winner) = selector.evaluate(
                providers: fixture.providers,
                threads: tc.threads,
                now: Date()
            )

            let formatted = T3ActivityFormatter.format(
                snapshot: snapshot,
                winningTool: winner,
                showAppIcons: false,
                now: snapshot.observedAt
            )

            XCTAssertEqual(
                formatted.details,
                tc.expected.details,
                "Case '\(tc.name)' details mismatch. Got: \(formatted.details), Expected: \(tc.expected.details)"
            )
            XCTAssertEqual(
                formatted.state,
                tc.expected.state,
                "Case '\(tc.name)' state mismatch. Got: \(String(describing: formatted.state)), Expected: \(String(describing: tc.expected.state))"
            )
        }
    }

    func testPluralizationAndSingularRules() {
        let now = Date()

        // 1 running task
        var counts1 = T3DriverCounts()
        counts1.runningTasks = 1
        var snap = T3ActivitySnapshot(observedAt: now, toolCounts: ["Antigravity": counts1])
        var formatted = T3ActivityFormatter.format(snapshot: snap, winningTool: "Antigravity", now: now)
        XCTAssertEqual(formatted.state, "1 task")

        // 2 running tasks
        var counts2 = T3DriverCounts()
        counts2.runningTasks = 2
        snap = T3ActivitySnapshot(observedAt: now, toolCounts: ["Antigravity": counts2])
        formatted = T3ActivityFormatter.format(snapshot: snap, winningTool: "Antigravity", now: now)
        XCTAssertEqual(formatted.state, "2 tasks")

        // 1 running helper
        var counts3 = T3DriverCounts()
        counts3.runningHelpers = 1
        snap = T3ActivitySnapshot(observedAt: now, toolCounts: ["Antigravity": counts3])
        formatted = T3ActivityFormatter.format(snapshot: snap, winningTool: "Antigravity", now: now)
        XCTAssertEqual(formatted.state, "1 helper")

        // 3 running helpers
        var counts4 = T3DriverCounts()
        counts4.runningHelpers = 3
        snap = T3ActivitySnapshot(observedAt: now, toolCounts: ["Antigravity": counts4])
        formatted = T3ActivityFormatter.format(snapshot: snap, winningTool: "Antigravity", now: now)
        XCTAssertEqual(formatted.state, "3 helpers")

        // 1 task and 2 helpers running
        var counts5 = T3DriverCounts()
        counts5.runningTasks = 1
        counts5.runningHelpers = 2
        snap = T3ActivitySnapshot(observedAt: now, toolCounts: ["Antigravity": counts5])
        formatted = T3ActivityFormatter.format(snapshot: snap, winningTool: "Antigravity", now: now)
        XCTAssertEqual(formatted.state, "1 task · 2 helpers")

        // 2 tasks and 1 helper running
        var counts6 = T3DriverCounts()
        counts6.runningTasks = 2
        counts6.runningHelpers = 1
        snap = T3ActivitySnapshot(observedAt: now, toolCounts: ["Antigravity": counts6])
        formatted = T3ActivityFormatter.format(snapshot: snap, winningTool: "Antigravity", now: now)
        XCTAssertEqual(formatted.state, "2 tasks · 1 helper")

        // Waiting tasks and helpers
        var countsWait = T3DriverCounts()
        countsWait.waitingTasks = 1
        countsWait.waitingHelpers = 1
        snap = T3ActivitySnapshot(observedAt: now, toolCounts: ["Antigravity": countsWait])
        formatted = T3ActivityFormatter.format(snapshot: snap, winningTool: "Antigravity", now: now)
        XCTAssertEqual(formatted.state, "1 task waiting · 1 helper waiting")

        var countsWait2 = T3DriverCounts()
        countsWait2.waitingTasks = 3
        snap = T3ActivitySnapshot(observedAt: now, toolCounts: ["Antigravity": countsWait2])
        formatted = T3ActivityFormatter.format(snapshot: snap, winningTool: "Antigravity", now: now)
        XCTAssertEqual(formatted.state, "3 tasks waiting")

        // Starting tasks and helpers
        var countsStart = T3DriverCounts()
        countsStart.startingTasks = 1
        countsStart.startingHelpers = 2
        snap = T3ActivitySnapshot(observedAt: now, toolCounts: ["Antigravity": countsStart])
        formatted = T3ActivityFormatter.format(snapshot: snap, winningTool: "Antigravity", now: now)
        XCTAssertEqual(formatted.state, "1 task starting · 2 helpers starting")
    }

    func testStaleSnapshotFallsBackToAppName() {
        let observedTime = Date(timeIntervalSince1970: 1000)
        let laterStaleTime = Date(timeIntervalSince1970: 1050) // 50s later (> 45s TTL)

        var counts = T3DriverCounts()
        counts.runningTasks = 2
        let snapshot = T3ActivitySnapshot(observedAt: observedTime, toolCounts: ["Antigravity": counts])

        let formatted = T3ActivityFormatter.format(
            snapshot: snapshot,
            winningTool: "Antigravity",
            showAppIcons: false,
            now: laterStaleTime
        )

        XCTAssertEqual(formatted.details, "Using T3 Code")
        XCTAssertNil(formatted.state)
        XCTAssertNil(formatted.winningTool)
    }

    func testAppIconsAssets() {
        let now = Date()
        var counts = T3DriverCounts()
        counts.runningTasks = 1

        let snapClaude = T3ActivitySnapshot(observedAt: now, toolCounts: ["Claude Code": counts])
        let snapAG = T3ActivitySnapshot(observedAt: now, toolCounts: ["Antigravity": counts])
        let snapCodex = T3ActivitySnapshot(observedAt: now, toolCounts: ["Codex": counts])
        let snapOther = T3ActivitySnapshot(observedAt: now, toolCounts: ["Other tools": counts])

        // When showAppIcons is true
        let fClaude = T3ActivityFormatter.format(snapshot: snapClaude, winningTool: "Claude Code", showAppIcons: true, now: now)
        XCTAssertEqual(fClaude.assets?.large_image, "t3_code")
        XCTAssertEqual(fClaude.assets?.small_image, "harness_claude")

        let fAG = T3ActivityFormatter.format(snapshot: snapAG, winningTool: "Antigravity", showAppIcons: true, now: now)
        XCTAssertEqual(fAG.assets?.large_image, "t3_code")
        XCTAssertEqual(fAG.assets?.small_image, "harness_antigravity")

        let fCodex = T3ActivityFormatter.format(snapshot: snapCodex, winningTool: "Codex", showAppIcons: true, now: now)
        XCTAssertEqual(fCodex.assets?.large_image, "t3_code")
        XCTAssertEqual(fCodex.assets?.small_image, "harness_codex")

        let fOther = T3ActivityFormatter.format(snapshot: snapOther, winningTool: "Other tools", showAppIcons: true, now: now)
        XCTAssertEqual(fOther.assets?.large_image, "t3_code")
        XCTAssertNil(fOther.assets?.small_image)

        // When showAppIcons is false
        let fClaudeNoIcons = T3ActivityFormatter.format(snapshot: snapClaude, winningTool: "Claude Code", showAppIcons: false, now: now)
        XCTAssertNil(fClaudeNoIcons.assets)
    }
}
