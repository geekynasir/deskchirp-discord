import XCTest
@testable import DeveloperPresenceCore

/// Multi-environment aggregation, failure isolation, exclusions, timer and selection rules.
final class PresenceV3EnvironmentTests: XCTestCase {
    /// A T3 MCP server stub returning the given thread rows. Records tool names it was asked for.
    private func mcpHandler(threadsJSON: String, toolLog: ToolLog) -> @Sendable (URLRequest) throws -> (Data, HTTPURLResponse) {
        let total = ((try? JSONSerialization.jsonObject(with: Data(threadsJSON.utf8))) as? [Any])?.count ?? 0
        return { request in
            guard let body = request.httpBody,
                  let object = try? JSONSerialization.jsonObject(with: body) as? [String: Any],
                  let method = object["method"] as? String else { throw HTTPTransportError.invalidResponse }
            let id = object["id"] as? Int ?? 0
            let ok: (String) -> (Data, HTTPURLResponse) = { text in
                (Data(text.utf8), HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil,
                                                  headerFields: ["Content-Type": "application/json"])!)
            }
            switch method {
            case "initialize":
                return ok(#"{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-06-18"}}"#)
            case "notifications/initialized":
                return (Data(), HTTPURLResponse(url: request.url!, statusCode: 202, httpVersion: nil, headerFields: nil)!)
            case "tools/call":
                let name = (object["params"] as? [String: Any])?["name"] as? String ?? ""
                toolLog.append(name)
                switch name {
                case "orchestrator_capabilities":
                    return ok(#"{"jsonrpc":"2.0","id":\#(id),"result":{"structuredContent":{"providers":[{"providerInstanceId":"claude-1","driverKind":"claudeAgent","displayName":"PRIVATE account"}]}}}"#)
                case "t3_thread_list":
                    return ok(#"{"jsonrpc":"2.0","id":\#(id),"result":{"structuredContent":{"threads":\#(threadsJSON),"nextCursor":null,"total":\#(total)}}}"#)
                case "t3_project_list":
                    return ok(#"{"jsonrpc":"2.0","id":\#(id),"result":{"structuredContent":{"projects":[{"projectId":"p1","title":"PRIVATE project"}],"nextCursor":null}}}"#)
                default:
                    throw HTTPTransportError.invalidResponse
                }
            default:
                throw HTTPTransportError.invalidResponse
            }
        }
    }

    private func connectedService(origin: String, transport: MockHTTPTransport, keychain: MockKeychain) throws -> T3ActivityService {
        try keychain.saveCredential(T3StoredCredential(accessToken: "token-\(origin)", expiresAt: Date().addingTimeInterval(3600)),
                                    forService: KeychainService.defaultService, account: origin)
        let service = T3ActivityService(transport: transport, keychain: keychain, mcpClient: T3McpClient(transport: transport))
        service.restoreSavedConnection(address: origin)
        return service
    }

    private func waitUntil(timeout: TimeInterval = 2, _ condition: () -> Bool) {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() && Date() < deadline {
            RunLoop.current.run(until: Date().addingTimeInterval(0.02))
        }
    }

    func testEnvironmentsPollIndependentlyAndAFailureContributesNothing() throws {
        let keychain = MockKeychain()
        let toolLog = ToolLog()
        let healthy = MockHTTPTransport(handler: mcpHandler(threadsJSON: """
        [{"threadId":"t1","providerInstanceId":"claude-1","status":"running","projectId":"p1","model":"claude-opus-5-5","title":"PRIVATE title"},
         {"threadId":"t2","providerInstanceId":"claude-1","status":"running","relationshipToParent":"subagent","parentThreadId":"t1","projectId":"p1"}]
        """, toolLog: toolLog))
        let failing = MockHTTPTransport(handler: { _ in throw HTTPTransportError.httpError(statusCode: 503, message: nil) })

        let remote = try connectedService(origin: "https://remote.example", transport: healthy, keychain: keychain)
        let broken = try connectedService(origin: "https://broken.example", transport: failing, keychain: keychain)
        remote.updateEligibility(isEligibleToPoll: true)
        broken.updateEligibility(isEligibleToPoll: true)
        defer {
            remote.updateEligibility(isEligibleToPoll: false)
            broken.updateEligibility(isEligibleToPoll: false)
        }

        waitUntil { remote.environmentSnapshot(environmentId: "remote") != nil && broken.connectionStatus == .couldNotReach }

        let remoteSnapshot = try XCTUnwrap(remote.environmentSnapshot(environmentId: "remote"))
        XCTAssertNil(broken.environmentSnapshot(environmentId: "broken"))
        XCTAssertEqual(remote.connectionStatus, .connected, "One failing environment must not affect another")
        XCTAssertEqual(remoteSnapshot.threads.first?.projectId, "p1")
        XCTAssertEqual(remoteSnapshot.threads.first?.model, "claude-opus-5-5")
        XCTAssertEqual(remoteSnapshot.threads.last?.parentThreadId, "t1")
        XCTAssertEqual(remoteSnapshot.projects.first?.projectId, "p1")
        XCTAssertTrue(toolLog.names.allSatisfy { T3McpClient.allowedTools.contains($0) })
        XCTAssertTrue(toolLog.names.contains("t3_project_list"))

        var combinedEnvironments = [remoteSnapshot]
        if let other = broken.environmentSnapshot(environmentId: "broken") { combinedEnvironments.append(other) }
        let combined = T3CombinedSnapshot(environments: combinedEnvironments)
        XCTAssertTrue(combined.isFresh())

        let settings = PresenceSettings(enableSharing: true, showT3Activity: true, t3Environments: [])
        let card = PresenceCardFormatter.format(
            settings: settings, snapshot: combined, usage: nil, timer: PresenceTimerState(),
            shownApp: .t3Code, now: Date())
        XCTAssertEqual(card.activity?.details, "T3 Code · Claude Code")
        XCTAssertEqual(card.activity?.state, "1 task · 1 helper")
        let outgoing = [card.activity?.details ?? "", card.activity?.state ?? ""] + card.previewRows.map { $0.value }
        XCTAssertFalse(outgoing.contains { $0.contains("PRIVATE") })
    }

    func testStaleEnvironmentsAreDroppedNotZeroed() {
        let now = Date()
        let providers = [T3ProviderInfo(providerInstanceId: "c", driverKind: "codex")]
        let fresh = T3EnvironmentSnapshot(environmentId: "a", observedAt: now, providers: providers,
                                          threads: [T3ThreadItem(threadId: "x", providerInstanceId: "c", status: "running")])
        let stale = T3EnvironmentSnapshot(environmentId: "b", observedAt: now.addingTimeInterval(-60), providers: providers,
                                          threads: [T3ThreadItem(threadId: "y", providerInstanceId: "c", status: "running")])
        let combined = T3CombinedSnapshot(environments: [fresh, stale])
        XCTAssertTrue(combined.isFresh(now: now))
        let work = T3WorkAggregator.workThreads(snapshot: combined, excludedProjects: [], now: now)
        XCTAssertEqual(work.map { $0.threadKey }, ["a/x"])
        XCTAssertFalse(T3CombinedSnapshot(environments: [stale]).isFresh(now: now))
    }

    func testExclusionsFollowHelperChainsWithinTheEnvironment() {
        let now = Date()
        let providers = [T3ProviderInfo(providerInstanceId: "c", driverKind: "claudeAgent")]
        let threads = [
            T3ThreadItem(threadId: "root", providerInstanceId: "c", status: "running", projectId: "secret"),
            T3ThreadItem(threadId: "h1", providerInstanceId: "c", relationshipToParent: "subagent", status: "running", projectId: "open", parentThreadId: "root"),
            T3ThreadItem(threadId: "h2", providerInstanceId: "c", relationshipToParent: "subagent", status: "running", projectId: "open", parentThreadId: "h1"),
            T3ThreadItem(threadId: "keep", providerInstanceId: "c", status: "running", projectId: "open")
        ]
        let envA = T3EnvironmentSnapshot(environmentId: "a", observedAt: now, providers: providers, threads: threads)
        // Same project ID in another environment is a different project and is not excluded.
        let envB = T3EnvironmentSnapshot(environmentId: "b", observedAt: now, providers: providers,
                                         threads: [T3ThreadItem(threadId: "root", providerInstanceId: "c", status: "running", projectId: "secret")])
        let work = T3WorkAggregator.workThreads(
            snapshot: T3CombinedSnapshot(environments: [envA, envB]), excludedProjects: ["a/secret"], now: now)
        XCTAssertEqual(Set(work.map { $0.threadKey }), ["a/keep", "b/root"])
        let counts = T3WorkAggregator.toolCounts(work)
        XCTAssertEqual(counts["Claude Code"]?.runningTasks, 2)
        XCTAssertEqual(counts["Claude Code"]?.runningHelpers, 0)
    }

    func testCombinedSelectorKeepsHysteresisOverCounts() {
        let selector = T3WinnerSelector()
        let start = Date()
        XCTAssertEqual(selector.evaluate(toolCounts: ["Codex": T3DriverCounts(runningTasks: 1)], now: start), "Codex")
        let challenger: [String: T3DriverCounts] = ["Codex": T3DriverCounts(runningTasks: 1), "Claude Code": T3DriverCounts(runningTasks: 3)]
        XCTAssertEqual(selector.evaluate(toolCounts: challenger, now: start.addingTimeInterval(1)), "Codex")
        XCTAssertEqual(selector.evaluate(toolCounts: challenger, now: start.addingTimeInterval(12)), "Claude Code")
        XCTAssertEqual(T3WinnerSelector.bestTool(toolCounts: ["A": T3DriverCounts(waitingTasks: 1), "B": T3DriverCounts(waitingTasks: 1)], previous: "B"), "B")
    }

    func testWorkSessionTimerTransitions() {
        var clock = PresenceWorkClock()
        let t0 = Date(timeIntervalSince1970: 1_000_000)
        clock.updateWork(hasUnfinishedWork: false, sharingActive: true, now: t0)
        XCTAssertNil(clock.workSessionStart)

        clock.updateWork(hasUnfinishedWork: true, sharingActive: true, now: t0.addingTimeInterval(5))
        XCTAssertEqual(clock.workSessionStart, t0.addingTimeInterval(5))
        // running → waiting → starting is still unfinished work: the session survives.
        clock.updateWork(hasUnfinishedWork: true, sharingActive: true, now: t0.addingTimeInterval(60))
        XCTAssertEqual(clock.workSessionStart, t0.addingTimeInterval(5))
        // A gap shorter than 120 s keeps the session.
        clock.updateWork(hasUnfinishedWork: false, sharingActive: true, now: t0.addingTimeInterval(170))
        XCTAssertEqual(clock.workSessionStart, t0.addingTimeInterval(5))
        clock.updateWork(hasUnfinishedWork: true, sharingActive: true, now: t0.addingTimeInterval(175))
        XCTAssertEqual(clock.workSessionStart, t0.addingTimeInterval(5))
        // More than 120 s without unfinished work ends it.
        clock.updateWork(hasUnfinishedWork: false, sharingActive: true, now: t0.addingTimeInterval(296))
        XCTAssertNil(clock.workSessionStart)
        clock.updateWork(hasUnfinishedWork: true, sharingActive: true, now: t0.addingTimeInterval(300))
        XCTAssertEqual(clock.workSessionStart, t0.addingTimeInterval(300))
        // Stopping activity sharing ends it immediately.
        clock.updateWork(hasUnfinishedWork: true, sharingActive: false, now: t0.addingTimeInterval(301))
        XCTAssertNil(clock.workSessionStart)

        clock.updateShownApp(.t3Code, now: t0)
        clock.updateShownApp(.t3Code, now: t0.addingTimeInterval(10))
        XCTAssertEqual(clock.appShownSince, t0)
        clock.updateShownApp(.codex, now: t0.addingTimeInterval(20))
        XCTAssertEqual(clock.appShownSince, t0.addingTimeInterval(20))
        clock.updateShownApp(.none, now: t0.addingTimeInterval(30))
        XCTAssertNil(clock.appShownSince)
    }

    func testSelectionModes() {
        var result = PresenceSelection.select(mode: .keepBackgroundVisible, foreground: .claude, t3Running: true,
                                              otherSupportedAppRunning: true, hasFreshT3Work: true)
        XCTAssertEqual(result.app, .t3Code)
        result = PresenceSelection.select(mode: .keepBackgroundVisible, foreground: .claude, t3Running: true,
                                          otherSupportedAppRunning: true, hasFreshT3Work: false)
        XCTAssertEqual(result.app, .claude)

        result = PresenceSelection.select(mode: .followActiveApp, foreground: .claude, t3Running: true,
                                          otherSupportedAppRunning: true, hasFreshT3Work: true)
        XCTAssertEqual(result.app, .claude)
        result = PresenceSelection.select(mode: .followActiveApp, foreground: .t3Code, t3Running: true,
                                          otherSupportedAppRunning: true, hasFreshT3Work: true)
        XCTAssertEqual(result.app, .t3Code)
        XCTAssertTrue(result.richAllowed)
        result = PresenceSelection.select(mode: .followActiveApp, foreground: .none, t3Running: true,
                                          otherSupportedAppRunning: false, hasFreshT3Work: true)
        XCTAssertEqual(result.app, .t3Code)
        XCTAssertTrue(result.richAllowed, "T3 is the only running supported app")
        result = PresenceSelection.select(mode: .followActiveApp, foreground: .none, t3Running: true,
                                          otherSupportedAppRunning: true, hasFreshT3Work: true)
        XCTAssertFalse(result.richAllowed)
    }

    func testRichDataHiddenWhenSelectionForbidsIt() {
        let now = Date()
        let settings = PresenceSettings(enableSharing: true, showT3Activity: true, t3Environments: [])
        let snapshot = T3CombinedSnapshot(environments: [T3EnvironmentSnapshot(
            environmentId: "e", observedAt: now, providers: [T3ProviderInfo(providerInstanceId: "c", driverKind: "codex")],
            threads: [T3ThreadItem(threadId: "t", providerInstanceId: "c", status: "running")])])
        let card = PresenceCardFormatter.format(settings: settings, snapshot: snapshot, usage: nil, timer: PresenceTimerState(),
                                                shownApp: .t3Code, now: now, richAllowed: false)
        XCTAssertEqual(card.activity?.details, "Using T3 Code")
        XCTAssertNil(card.activity?.state)
    }
}

final class ToolLog: @unchecked Sendable {
    private let lock = NSLock()
    private var storage: [String] = []
    func append(_ name: String) { lock.lock(); storage.append(name); lock.unlock() }
    var names: [String] { lock.lock(); defer { lock.unlock() }; return storage }
}
