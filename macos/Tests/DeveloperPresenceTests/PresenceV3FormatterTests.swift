import XCTest
@testable import DeveloperPresenceCore

/// Runs every shared case in spec/presence-v3-cases.json (mirrored byte-for-byte under Resources).
final class PresenceV3FormatterTests: XCTestCase {
    private func loadFixture() throws -> [String: Any] {
        let data = try TestFixtureLoader.loadResource(named: "presence-v3-cases")
        return try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    private func intValue(_ any: Any?) -> Int? {
        (any as? NSNumber)?.intValue
    }

    /// Spec defaults with sharing, Show T3 activity and app icons on, then the case overrides.
    private func makeSettings(_ overrides: [String: Any]) throws -> PresenceSettings {
        let base = PresenceSettings(enableSharing: true, showT3Activity: true, showAppIcons: true, t3Environments: [])
        let baseData = try JSONEncoder().encode(base)
        var object = try XCTUnwrap(JSONSerialization.jsonObject(with: baseData) as? [String: Any])
        for (key, value) in overrides { object[key] = value }
        let merged = try JSONSerialization.data(withJSONObject: object)
        return try JSONDecoder().decode(PresenceSettings.self, from: merged)
    }

    private func makeThreads(_ raw: [[String: Any]]) -> [T3ThreadItem] {
        raw.map { thread in
            T3ThreadItem(
                threadId: thread["threadId"] as? String ?? "",
                providerInstanceId: thread["providerInstanceId"] as? String ?? "",
                relationshipToParent: thread["relationshipToParent"] as? String,
                status: thread["status"] as? String ?? "",
                projectId: thread["projectId"] as? String,
                model: thread["model"] as? String,
                parentThreadId: thread["parentThreadId"] as? String,
                createdAt: thread["createdAt"] as? String,
                updatedAt: thread["updatedAt"] as? String,
                title: thread["title"] as? String)
        }
    }

    private func makeUsageValue(_ raw: [String: Any]?) -> T3UsageValue? {
        guard let raw = raw else { return nil }
        var value = T3UsageValue()
        if let tokens = raw["tokens"] as? [String: Any] {
            value.tokens = T3TokenTotals(
                uncachedInputTokens: intValue(tokens["uncachedInputTokens"]) ?? 0,
                cachedInputTokens: intValue(tokens["cachedInputTokens"]) ?? 0,
                cacheCreationTokens: intValue(tokens["cacheCreationTokens"]) ?? 0,
                outputTokens: intValue(tokens["outputTokens"]) ?? 0,
                reasoningTokens: intValue(tokens["reasoningTokens"]) ?? 0)
        }
        let partial = raw["partial"] as? Bool ?? false
        value.tokensPartial = partial
        value.costPartial = partial || (raw["costPartial"] as? Bool ?? false)
        value.costUsd = (raw["costUsd"] as? NSNumber)?.doubleValue
        value.contextUsedTokens = intValue(raw["contextUsedTokens"])
        value.contextMaxTokens = intValue(raw["contextMaxTokens"])
        return value
    }

    func testEverySharedCase() throws {
        let fixture = try loadFixture()
        let workStart = try XCTUnwrap(intValue(fixture["workSessionStart"]))
        let appStart = try XCTUnwrap(intValue(fixture["appShownStart"]))
        let now = Date(timeIntervalSince1970: TimeInterval(workStart + 600))
        let providers = try XCTUnwrap(fixture["providers"] as? [[String: Any]]).map {
            T3ProviderInfo(providerInstanceId: $0["providerInstanceId"] as? String ?? "", driverKind: $0["driverKind"] as? String ?? "")
        }
        let cases = try XCTUnwrap(fixture["cases"] as? [[String: Any]])
        XCTAssertEqual(cases.count, 31, "The shared fixture defines 31 formatter cases")

        for testCase in cases {
            let name = testCase["name"] as? String ?? "unnamed"
            let settings = try makeSettings(testCase["settings"] as? [String: Any] ?? [:])
            let environments = (testCase["environments"] as? [[String: Any]] ?? []).map { env in
                T3EnvironmentSnapshot(
                    environmentId: env["id"] as? String ?? "",
                    observedAt: now,
                    providers: providers,
                    threads: makeThreads(env["threads"] as? [[String: Any]] ?? []),
                    projects: (env["projects"] as? [[String: Any]] ?? []).map {
                        T3ProjectInfo(projectId: $0["projectId"] as? String ?? "", title: $0["title"] as? String)
                    })
            }
            var usage: T3UsageSnapshot?
            if let rawUsage = testCase["usage"] as? [String: Any] {
                usage = T3UsageSnapshot(
                    observedAt: now,
                    today: makeUsageValue(rawUsage["today"] as? [String: Any]),
                    session: makeUsageValue(rawUsage["session"] as? [String: Any]),
                    pinnedTask: makeUsageValue(rawUsage["pinnedTask"] as? [String: Any]))
            }
            let card = PresenceCardFormatter.format(
                settings: settings,
                snapshot: T3CombinedSnapshot(environments: environments),
                usage: usage,
                timer: PresenceTimerState(
                    appShownSince: Date(timeIntervalSince1970: TimeInterval(appStart)),
                    workSessionStart: Date(timeIntervalSince1970: TimeInterval(workStart))),
                shownApp: .t3Code,
                now: now)
            let activity = try XCTUnwrap(card.activity, "Case '\(name)' produced no activity")
            let expected = testCase["expected"] as? [String: Any] ?? [:]

            func check(_ key: String, _ actual: String?) {
                guard expected.keys.contains(key) else { return }
                if expected[key] is NSNull {
                    XCTAssertNil(actual, "Case '\(name)': \(key) must be absent")
                } else {
                    XCTAssertEqual(actual, expected[key] as? String, "Case '\(name)': \(key)")
                }
            }
            check("details", activity.details)
            check("state", activity.state)
            check("largeImage", activity.assets?.large_image)
            check("largeText", activity.assets?.large_text)
            check("smallImage", activity.assets?.small_image)
            check("smallText", activity.assets?.small_text)

            if expected.keys.contains("timestampsStart") {
                if expected["timestampsStart"] is NSNull {
                    XCTAssertNil(activity.timestamps, "Case '\(name)': timer must be absent")
                } else {
                    XCTAssertEqual(activity.timestamps?.start, (expected["timestampsStart"] as? NSNumber)?.int64Value, "Case '\(name)': timer")
                }
            }
            if expected.keys.contains("buttons") {
                if expected["buttons"] is NSNull {
                    XCTAssertNil(activity.buttons, "Case '\(name)': buttons must be absent")
                } else {
                    let raw = expected["buttons"] as? [[String: Any]] ?? []
                    let wanted = raw.map { DiscordActivityButton(label: $0["label"] as? String ?? "", url: $0["url"] as? String ?? "") }
                    XCTAssertEqual(activity.buttons ?? [], wanted, "Case '\(name)': buttons")
                }
            }
            if let warnings = expected["fitWarnings"] as? [String] {
                XCTAssertEqual(card.fitWarnings, warnings, "Case '\(name)': fit warnings")
            }

            // Bait never appears in any outgoing field or preview row.
            var outgoing = [activity.details, activity.state ?? "", activity.assets?.large_image ?? "",
                            activity.assets?.large_text ?? "", activity.assets?.small_image ?? "",
                            activity.assets?.small_text ?? ""]
            outgoing += (activity.buttons ?? []).flatMap { [$0.label, $0.url] }
            outgoing += card.previewRows.map { $0.value }
            let encoded = String(data: try JSONEncoder().encode(activity), encoding: .utf8) ?? ""
            var forbidden = expected["forbidden"] as? [String] ?? []
            forbidden.append("PRIVATE")
            for bait in forbidden {
                for value in outgoing {
                    XCTAssertFalse(value.contains(bait), "Case '\(name)': '\(bait)' leaked into '\(value)'")
                }
                XCTAssertFalse(encoded.contains(bait), "Case '\(name)': '\(bait)' leaked into the payload")
            }
            XCTAssertLessThanOrEqual(activity.details.count, 128)
            XCTAssertLessThanOrEqual((activity.buttons ?? []).count, 2)
        }
    }

    func testCompactExactAndCostTables() throws {
        let fixture = try loadFixture()
        let compact = try XCTUnwrap(fixture["compactNumbers"] as? [[Any]])
        XCTAssertFalse(compact.isEmpty)
        for pair in compact {
            let input = try XCTUnwrap((pair[0] as? NSNumber)?.intValue)
            XCTAssertEqual(PresenceNumberFormat.compact(input), pair[1] as? String, "compact \(input)")
        }
        let exact = try XCTUnwrap(fixture["exactNumbers"] as? [[Any]])
        for pair in exact {
            let input = try XCTUnwrap((pair[0] as? NSNumber)?.intValue)
            XCTAssertEqual(PresenceNumberFormat.exact(input), pair[1] as? String, "exact \(input)")
        }
        let costs = try XCTUnwrap(fixture["costs"] as? [[Any]])
        for pair in costs {
            let input = try XCTUnwrap((pair[0] as? NSNumber)?.doubleValue)
            XCTAssertEqual(PresenceNumberFormat.cost(input), pair[1] as? String, "cost \(input)")
        }
    }

    func testDiscordPayloadEncodesTimestampsAndButtons() throws {
        let activity = DiscordActivity(
            details: "T3 Code · Codex",
            state: "1 task",
            timestamps: DiscordActivityTimestamps(start: 1793500000),
            buttons: [PresenceCardFormatter.companionButton])
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(activity)) as? [String: Any])
        XCTAssertEqual((json["timestamps"] as? [String: Any])?["start"] as? Int, 1793500000)
        let buttons = try XCTUnwrap(json["buttons"] as? [[String: String]])
        XCTAssertEqual(buttons, [["label": "Get Deskchirp", "url": "https://github.com/geekynasir/deskchirp-discord"]])

        let plain = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(DiscordActivity(details: "Using Codex"))) as? [String: Any])
        XCTAssertNil(plain["timestamps"])
        XCTAssertNil(plain["buttons"])
    }

    func testRepoUrlValidation() {
        XCTAssertTrue(PresenceCardFormatter.isValidPublicRepoUrl("https://github.com/octo-org/orbit-notes"))
        XCTAssertTrue(PresenceCardFormatter.isValidPublicRepoUrl("https://github.com/a/b.c_d-e"))
        XCTAssertFalse(PresenceCardFormatter.isValidPublicRepoUrl("https://github.com/octo-org/orbit-notes/tree/main"))
        XCTAssertFalse(PresenceCardFormatter.isValidPublicRepoUrl("https://github.com/octo-org/orbit-notes?token=x"))
        XCTAssertFalse(PresenceCardFormatter.isValidPublicRepoUrl("https://github.com/octo-org/orbit-notes#readme"))
        XCTAssertFalse(PresenceCardFormatter.isValidPublicRepoUrl("http://github.com/octo-org/orbit-notes"))
        XCTAssertFalse(PresenceCardFormatter.isValidPublicRepoUrl("https://gitlab.com/octo-org/orbit-notes"))
        XCTAssertFalse(PresenceCardFormatter.isValidPublicRepoUrl("https://github.com/octo_org/orbit"))
        XCTAssertFalse(PresenceCardFormatter.isValidPublicRepoUrl("https://github.com/octo/.."))
    }

    func testHardCapAndOtherAppsNeverCarryT3Data() {
        let long = String(repeating: "a", count: 200)
        let capped = PresenceCardFormatter.cap(long)
        XCTAssertEqual(capped.count, 128)
        XCTAssertTrue(capped.hasSuffix("…"))

        var settings = PresenceSettings(enableSharing: true, showT3Activity: true, t3Environments: [])
        settings.shareProjectAlias = true
        settings.projectAliases = ["e/p": "Orbit"]
        let snapshot = T3CombinedSnapshot(environments: [T3EnvironmentSnapshot(
            environmentId: "e", observedAt: Date(),
            providers: [T3ProviderInfo(providerInstanceId: "c", driverKind: "codex")],
            threads: [T3ThreadItem(threadId: "t", providerInstanceId: "c", status: "running", projectId: "p")])])
        let card = PresenceCardFormatter.format(
            settings: settings, snapshot: snapshot, usage: nil, timer: PresenceTimerState(),
            shownApp: .claude, now: Date())
        XCTAssertEqual(card.activity?.details, "Using Claude")
        XCTAssertNil(card.activity?.state)
        XCTAssertFalse(card.hasFreshWork)
    }

    func testStaleSnapshotFallsBackToAppOnly() {
        let settings = PresenceSettings(enableSharing: true, showT3Activity: true, t3Environments: [])
        let old = Date(timeIntervalSince1970: 1_000)
        let snapshot = T3CombinedSnapshot(environments: [T3EnvironmentSnapshot(
            environmentId: "e", observedAt: old,
            providers: [T3ProviderInfo(providerInstanceId: "c", driverKind: "codex")],
            threads: [T3ThreadItem(threadId: "t", providerInstanceId: "c", status: "running")])])
        let card = PresenceCardFormatter.format(
            settings: settings, snapshot: snapshot, usage: nil, timer: PresenceTimerState(),
            shownApp: .t3Code, now: old.addingTimeInterval(46))
        XCTAssertEqual(card.activity?.details, "Using T3 Code")
        XCTAssertNil(card.activity?.state)
    }

    func testUsageTodayIsUnavailableWhileProjectsAreExcluded() {
        var settings = PresenceSettings(enableSharing: true, showT3Activity: true, t3Environments: [])
        settings.cardStyle = .usage
        settings.usageScope = .today
        settings.excludedProjects = ["e/secret"]
        let now = Date()
        let snapshot = T3CombinedSnapshot(environments: [T3EnvironmentSnapshot(
            environmentId: "e", observedAt: now,
            providers: [T3ProviderInfo(providerInstanceId: "c", driverKind: "codex")],
            threads: [T3ThreadItem(threadId: "t", providerInstanceId: "c", status: "running", projectId: "open")])])
        let usage = T3UsageSnapshot(observedAt: now, today: T3UsageValue(tokens: T3TokenTotals(outputTokens: 5000)))
        let card = PresenceCardFormatter.format(
            settings: settings, snapshot: snapshot, usage: usage, timer: PresenceTimerState(),
            shownApp: .t3Code, now: now)
        XCTAssertEqual(card.activity?.state, "1 task")
        XCTAssertFalse(card.showsUsage)

        // Stale usage (older than 180 s) also falls back.
        settings.excludedProjects = []
        let stale = T3UsageSnapshot(observedAt: now.addingTimeInterval(-181), today: T3UsageValue(tokens: T3TokenTotals(outputTokens: 5000)))
        let staleCard = PresenceCardFormatter.format(
            settings: settings, snapshot: snapshot, usage: stale, timer: PresenceTimerState(),
            shownApp: .t3Code, now: now)
        XCTAssertEqual(staleCard.activity?.state, "1 task")
    }
}
