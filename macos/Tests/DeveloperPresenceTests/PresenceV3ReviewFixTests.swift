import XCTest
@testable import DeveloperPresenceCore

/// Regression tests for the PR #4 review findings (usage gating rules).
final class PresenceV3ReviewFixTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_793_500_600)
    private let providers = [T3ProviderInfo(providerInstanceId: "c", driverKind: "claudeAgent")]

    private func snapshot(_ threads: [T3ThreadItem], environmentId: String = "e") -> T3CombinedSnapshot {
        T3CombinedSnapshot(environments: [T3EnvironmentSnapshot(
            environmentId: environmentId, observedAt: now, providers: providers, threads: threads)])
    }

    private func usageSettings() -> PresenceSettings {
        var settings = PresenceSettings(enableSharing: true, showT3Activity: true, t3Environments: [])
        settings.cardStyle = .usage
        return settings
    }

    private let threads = [
        T3ThreadItem(threadId: "open", providerInstanceId: "c", status: "running", projectId: "public"),
        T3ThreadItem(threadId: "secret", providerInstanceId: "c", status: "running", projectId: "private"),
        T3ThreadItem(threadId: "child", providerInstanceId: "c", relationshipToParent: "subagent", status: "running",
                     projectId: "public", parentThreadId: "secret")
    ]

    // 1. Pinned task in an excluded project (or a helper of one) is never requested or shown.
    func testExcludedPinnedTaskUsageIsNeitherRequestedNorShown() {
        for pinned in ["e/secret", "e/child", "e/gone"] {
            var settings = usageSettings()
            settings.usageMetric = .context
            settings.pinnedTask = pinned
            settings.excludedProjects = ["e/private"]
            let combined = snapshot(threads)
            let visible = T3WorkAggregator.workThreads(snapshot: combined, excludedProjects: settings.excludedProjects, now: now)
            let env = T3Environment(id: "e", address: "https://remote.example/mcp", label: "Remote")
            let requests = T3UsageService.plans(settings: settings, work: visible, allowedEnvironments: [env], sessionStart: now)
            XCTAssertTrue(requests.isEmpty, "\(pinned) must not be requested")

            let usage = T3UsageSnapshot(observedAt: now, pinnedTask: T3UsageValue(contextUsedTokens: 50, contextMaxTokens: 100),
                                        pinnedTaskKey: pinned)
            let card = PresenceCardFormatter.format(settings: settings, snapshot: combined, usage: usage,
                                                    timer: PresenceTimerState(), shownApp: .t3Code, now: now)
            XCTAssertEqual(card.activity?.state, "1 task", "\(pinned) falls back to workload")
            XCTAssertFalse(card.showsUsage)
        }

        // A visible pinned task still works.
        var settings = usageSettings()
        settings.usageMetric = .context
        settings.pinnedTask = "e/open"
        settings.excludedProjects = ["e/private"]
        let combined = snapshot(threads)
        let visible = T3WorkAggregator.workThreads(snapshot: combined, excludedProjects: settings.excludedProjects, now: now)
        let env = T3Environment(id: "e", address: "https://remote.example/mcp", label: "Remote")
        XCTAssertEqual(T3UsageService.plans(settings: settings, work: visible, allowedEnvironments: [env], sessionStart: now)
            .first?.plan.pinnedThreadId, "open")
        let usage = T3UsageSnapshot(observedAt: now, pinnedTask: T3UsageValue(contextUsedTokens: 50, contextMaxTokens: 100))
        XCTAssertEqual(PresenceCardFormatter.format(settings: settings, snapshot: combined, usage: usage,
                                                    timer: PresenceTimerState(), shownApp: .t3Code, now: now).activity?.state,
                       "50% of context used")
    }

    // 2. Session work in an environment without the usage permission makes the session partial.
    func testSessionIsPartialWhenWorkRunsWithoutUsagePermission() {
        var settings = usageSettings()
        settings.usageScope = .session
        let allowed = T3Environment(id: "a", address: "https://a.example/mcp", label: "A")
        let work = [
            T3WorkThread(environmentId: "a", thread: T3ThreadItem(threadId: "t1", providerInstanceId: "c", status: "running"), toolName: "Claude Code"),
            T3WorkThread(environmentId: "b", thread: T3ThreadItem(threadId: "t2", providerInstanceId: "c", status: "running"), toolName: "Claude Code")
        ]
        let mixed = T3UsageService.plans(settings: settings, work: work, allowedEnvironments: [allowed], sessionStart: now)
        XCTAssertEqual(mixed.first?.plan.sessionThreadIds, ["t1"])
        XCTAssertEqual(mixed.first?.plan.sessionOverflow, true)

        let onlyAllowed = T3UsageService.plans(settings: settings, work: [work[0]], allowedEnvironments: [allowed], sessionStart: now)
        XCTAssertEqual(onlyAllowed.first?.plan.sessionOverflow, false)
    }

    // 3. Session usage measured for another work session never shows.
    func testSessionUsageFromAnotherWorkSessionFallsBack() {
        var settings = usageSettings()
        settings.usageScope = .session
        let combined = snapshot([threads[0]])
        let value = T3UsageValue(tokens: T3TokenTotals(outputTokens: 5000), costPartial: true)
        let oldStart = now.addingTimeInterval(-900)
        let newStart = now.addingTimeInterval(-60)

        func state(usageStart: Date?, timerStart: Date?) -> String? {
            PresenceCardFormatter.format(
                settings: settings, snapshot: combined,
                usage: T3UsageSnapshot(observedAt: now, session: value, sessionStart: usageStart),
                timer: PresenceTimerState(appShownSince: oldStart, workSessionStart: timerStart),
                shownApp: .t3Code, now: now).activity?.state
        }
        XCTAssertEqual(state(usageStart: newStart, timerStart: newStart), "5K tokens this session")
        XCTAssertEqual(state(usageStart: oldStart, timerStart: newStart), "1 task")
        XCTAssertEqual(state(usageStart: nil, timerStart: newStart), "1 task")
        XCTAssertEqual(state(usageStart: newStart, timerStart: nil), "1 task")
    }

    // 4. Context is judged on its own used/max even when the turn tokens are partial.
    func testContextShownWhenTurnTokensArePartial() {
        var settings = usageSettings()
        settings.usageMetric = .context
        settings.pinnedTask = "e/open"
        let usage = T3UsageSnapshot(observedAt: now, pinnedTask: T3UsageValue(
            tokens: T3TokenTotals(outputTokens: 10), tokensPartial: true, costPartial: true,
            contextUsedTokens: 124_999, contextMaxTokens: 200_000))
        let card = PresenceCardFormatter.format(settings: settings, snapshot: snapshot([threads[0]]), usage: usage,
                                                timer: PresenceTimerState(), shownApp: .t3Code, now: now)
        XCTAssertEqual(card.activity?.state, "62% of context used")

        // And the token metric on the same value still falls back.
        settings.usageMetric = .tokens
        settings.usageScope = .pinnedTask
        XCTAssertEqual(PresenceCardFormatter.format(settings: settings, snapshot: snapshot([threads[0]]), usage: usage,
                                                    timer: PresenceTimerState(), shownApp: .t3Code, now: now).activity?.state,
                       "1 task")
    }

    // B. Pinned usage needs fresh, exclusion-filtered metadata for the task and its parent.
    func testPinnedTaskNeedsFreshMetadataForTaskAndParent() {
        let env = T3Environment(id: "e", address: "https://remote.example/mcp", label: "Remote")
        var settings = usageSettings()
        settings.usageMetric = .context

        func requested(_ pinned: String, _ combined: T3CombinedSnapshot?) -> Bool {
            settings.pinnedTask = pinned
            let visible = T3WorkAggregator.workThreads(snapshot: combined, excludedProjects: settings.excludedProjects, now: now)
            return !T3UsageService.plans(settings: settings, work: visible, allowedEnvironments: [env], sessionStart: now).isEmpty
        }
        let parent = T3ThreadItem(threadId: "p", providerInstanceId: "c", status: "running", projectId: "public")
        let helper = T3ThreadItem(threadId: "h", providerInstanceId: "c", relationshipToParent: "subagent",
                                  status: "running", projectId: "public", parentThreadId: "p")

        XCTAssertFalse(requested("e/p", nil), "No snapshot (for example right after exclusions changed)")
        XCTAssertFalse(requested("e/p", snapshot([parent], environmentId: "other")), "Environment missing from the snapshot")
        let stale = T3CombinedSnapshot(environments: [T3EnvironmentSnapshot(
            environmentId: "e", observedAt: now.addingTimeInterval(-46), providers: providers, threads: [parent])])
        XCTAssertFalse(requested("e/p", stale), "Stale metadata")
        XCTAssertFalse(requested("e/h", snapshot([helper])), "Parent not confirmed")
        XCTAssertTrue(requested("e/h", snapshot([parent, helper])))
        XCTAssertTrue(requested("e/p", snapshot([parent])))

        // The formatter rechecks on its own.
        settings.pinnedTask = "e/h"
        let usage = T3UsageSnapshot(observedAt: now, pinnedTask: T3UsageValue(contextUsedTokens: 1, contextMaxTokens: 2))
        let unconfirmed = PresenceCardFormatter.format(settings: settings, snapshot: snapshot([helper, threads[0]]), usage: usage,
                                                       timer: PresenceTimerState(), shownApp: .t3Code, now: now)
        XCTAssertFalse(unconfirmed.showsUsage)
        let confirmed = PresenceCardFormatter.format(settings: settings, snapshot: snapshot([parent, helper]), usage: usage,
                                                     timer: PresenceTimerState(), shownApp: .t3Code, now: now)
        XCTAssertEqual(confirmed.activity?.state, "50% of context used")
    }

    // C. A pinned project always restricts counts; without matching unfinished work the card is app-only.
    func testPinnedProjectAlwaysRestricts() {
        var settings = usageSettings()
        settings.cardStyle = .workload
        settings.pinnedProject = "e/elsewhere"
        let card = PresenceCardFormatter.format(settings: settings, snapshot: snapshot([threads[0]]), usage: nil,
                                                timer: PresenceTimerState(), shownApp: .t3Code, now: now)
        XCTAssertEqual(card.activity?.details, "Using T3 Code")
        XCTAssertNil(card.activity?.state)
        XCTAssertFalse(card.hasFreshWork)

        settings.pinnedProject = "e/public"
        let pinned = PresenceCardFormatter.format(settings: settings, snapshot: snapshot(threads), usage: nil,
                                                  timer: PresenceTimerState(), shownApp: .t3Code, now: now)
        XCTAssertEqual(pinned.activity?.state, "1 task · 1 helper")

        XCTAssertTrue(PresenceAvailability.pinnedProjectNotFound("e/gone", catalogs: ["e": [T3ProjectInfo(projectId: "public")]]))
        XCTAssertFalse(PresenceAvailability.pinnedProjectNotFound("e/public", catalogs: ["e": [T3ProjectInfo(projectId: "public")]]))
        XCTAssertTrue(PresenceAvailability.pinnedProjectNotFound("e/public", catalogs: ["f": [T3ProjectInfo(projectId: "public")]]),
                      "The project ID must belong to that environment")
        XCTAssertFalse(PresenceAvailability.pinnedProjectNotFound("e/public", catalogs: [:]), "Unknown without any catalog")
        XCTAssertEqual(PresenceAvailability.pinnedProjectNotFoundText, "Pinned project not found")
    }

    func testSingleTokenIsSingular() {
        var settings = usageSettings()
        let usage = T3UsageSnapshot(observedAt: now, today: T3UsageValue(tokens: T3TokenTotals(outputTokens: 1)))
        XCTAssertEqual(PresenceCardFormatter.format(settings: settings, snapshot: snapshot([threads[0]]), usage: usage,
                                                    timer: PresenceTimerState(), shownApp: .t3Code, now: now).activity?.state,
                       "1 token today")
        settings.playfulWording = true
        XCTAssertEqual(PresenceCardFormatter.format(settings: settings, snapshot: snapshot([threads[0]]), usage: usage,
                                                    timer: PresenceTimerState(), shownApp: .t3Code, now: now).activity?.state,
                       "Crunched 1 token today")
    }
}
