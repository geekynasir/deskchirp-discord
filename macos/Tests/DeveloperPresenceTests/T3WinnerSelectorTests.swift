import XCTest
@testable import DeveloperPresenceCore

final class T3WinnerSelectorTests: XCTestCase {
    private var selector: T3WinnerSelector!

    override func setUp() {
        super.setUp()
        selector = T3WinnerSelector()
    }

    override func tearDown() {
        selector.reset()
        selector = nil
        super.tearDown()
    }

    func testHysteresisRequires10SecondSustainedLead() {
        let providers = [
            T3ProviderInfo(providerInstanceId: "p-claude", driverKind: "claudeAgent"),
            T3ProviderInfo(providerInstanceId: "p-ag", driverKind: "antigravity")
        ]

        let t0 = Date(timeIntervalSince1970: 1000)

        // Initially, Antigravity has 2 running tasks, Claude has 1 running task
        let threadsInitial = [
            T3ThreadItem(threadId: "ag-1", providerInstanceId: "p-ag", status: "running"),
            T3ThreadItem(threadId: "ag-2", providerInstanceId: "p-ag", status: "running"),
            T3ThreadItem(threadId: "c-1", providerInstanceId: "p-claude", status: "running")
        ]

        let (snap0, winner0) = selector.evaluate(providers: providers, threads: threadsInitial, now: t0)
        XCTAssertEqual(winner0, "Antigravity")
        XCTAssertEqual(snap0.toolCounts["Antigravity"]?.runningTasks, 2)
        XCTAssertEqual(snap0.toolCounts["Claude Code"]?.runningTasks, 1)

        // At t = 5s, Claude surges to 3 running tasks (challenger lead)
        let t5 = Date(timeIntervalSince1970: 1005)
        let threadsChallenger = [
            T3ThreadItem(threadId: "ag-1", providerInstanceId: "p-ag", status: "running"),
            T3ThreadItem(threadId: "ag-2", providerInstanceId: "p-ag", status: "running"),
            T3ThreadItem(threadId: "c-1", providerInstanceId: "p-claude", status: "running"),
            T3ThreadItem(threadId: "c-2", providerInstanceId: "p-claude", status: "running"),
            T3ThreadItem(threadId: "c-3", providerInstanceId: "p-claude", status: "running")
        ]

        let (_, winner5) = selector.evaluate(providers: providers, threads: threadsChallenger, now: t5)
        // Less than 10s has passed, so Antigravity is retained
        XCTAssertEqual(winner5, "Antigravity")

        // At t = 9.9s, still less than 10s of sustained lead
        let t9 = Date(timeIntervalSince1970: 1009.9)
        let (_, winner9) = selector.evaluate(providers: providers, threads: threadsChallenger, now: t9)
        XCTAssertEqual(winner9, "Antigravity")

        // At t = 15.1s (10.1s after challenger first seen at t5), sustained lead threshold met!
        let t15 = Date(timeIntervalSince1970: 1015.1)
        let (_, winner15) = selector.evaluate(providers: providers, threads: threadsChallenger, now: t15)
        XCTAssertEqual(winner15, "Claude Code")
    }

    func testImmediateLeadWhenCurrentWinnerHasNoRunningWork() {
        let providers = [
            T3ProviderInfo(providerInstanceId: "p-claude", driverKind: "claudeAgent"),
            T3ProviderInfo(providerInstanceId: "p-ag", driverKind: "antigravity")
        ]

        let t0 = Date(timeIntervalSince1970: 1000)

        // Initially Antigravity is winning
        let threadsInitial = [
            T3ThreadItem(threadId: "ag-1", providerInstanceId: "p-ag", status: "running")
        ]
        let (_, winner0) = selector.evaluate(providers: providers, threads: threadsInitial, now: t0)
        XCTAssertEqual(winner0, "Antigravity")

        // At t = 1s, Antigravity has finished (0 running work), Claude has 1 running task
        let t1 = Date(timeIntervalSince1970: 1001)
        let threadsNow = [
            T3ThreadItem(threadId: "ag-1", providerInstanceId: "p-ag", status: "completed"),
            T3ThreadItem(threadId: "c-1", providerInstanceId: "p-claude", status: "running")
        ]

        // Since current winner (Antigravity) has 0 running work, Claude wins immediately without 10s delay
        let (_, winner1) = selector.evaluate(providers: providers, threads: threadsNow, now: t1)
        XCTAssertEqual(winner1, "Claude Code")
    }

    func testTiesRetainCurrentWinner() {
        let providers = [
            T3ProviderInfo(providerInstanceId: "p-claude", driverKind: "claudeAgent"),
            T3ProviderInfo(providerInstanceId: "p-ag", driverKind: "antigravity")
        ]

        let t0 = Date(timeIntervalSince1970: 1000)
        let threadsInitial = [
            T3ThreadItem(threadId: "ag-1", providerInstanceId: "p-ag", status: "running"),
            T3ThreadItem(threadId: "ag-2", providerInstanceId: "p-ag", status: "running")
        ]
        let (_, winner0) = selector.evaluate(providers: providers, threads: threadsInitial, now: t0)
        XCTAssertEqual(winner0, "Antigravity")

        // At t = 15s, Claude ties Antigravity with 2 running tasks
        let t15 = Date(timeIntervalSince1970: 1015)
        let threadsTied = [
            T3ThreadItem(threadId: "ag-1", providerInstanceId: "p-ag", status: "running"),
            T3ThreadItem(threadId: "ag-2", providerInstanceId: "p-ag", status: "running"),
            T3ThreadItem(threadId: "c-1", providerInstanceId: "p-claude", status: "running"),
            T3ThreadItem(threadId: "c-2", providerInstanceId: "p-claude", status: "running")
        ]
        let (_, winnerTied) = selector.evaluate(providers: providers, threads: threadsTied, now: t15)
        XCTAssertEqual(winnerTied, "Antigravity", "Ties must retain the current winner")
    }

    func testFallbackToWaitingAndStartingWork() {
        let providers = [
            T3ProviderInfo(providerInstanceId: "p-claude", driverKind: "claudeAgent"),
            T3ProviderInfo(providerInstanceId: "p-ag", driverKind: "antigravity")
        ]

        let t0 = Date(timeIntervalSince1970: 1000)

        // All running work is 0. Claude has 1 waiting task, Antigravity has 0
        let threadsWaiting = [
            T3ThreadItem(threadId: "c-wait", providerInstanceId: "p-claude", status: "waiting")
        ]
        let (_, winnerWaiting) = selector.evaluate(providers: providers, threads: threadsWaiting, now: t0)
        XCTAssertEqual(winnerWaiting, "Claude Code")

        // All running and waiting is 0. Antigravity has 1 starting task
        let threadsStarting = [
            T3ThreadItem(threadId: "ag-start", providerInstanceId: "p-ag", status: "starting")
        ]
        let (_, winnerStarting) = selector.evaluate(providers: providers, threads: threadsStarting, now: t0)
        XCTAssertEqual(winnerStarting, "Antigravity")

        // All work completed -> winner is nil
        let threadsCompleted = [
            T3ThreadItem(threadId: "ag-done", providerInstanceId: "p-ag", status: "completed")
        ]
        let (_, winnerCompleted) = selector.evaluate(providers: providers, threads: threadsCompleted, now: t0)
        XCTAssertNil(winnerCompleted)
    }

    func testHelperGroupingUnderOwnDriverAndForksAsTasks() {
        let providers = [
            T3ProviderInfo(providerInstanceId: "p-claude", driverKind: "claudeAgent"),
            T3ProviderInfo(providerInstanceId: "p-ag", driverKind: "antigravity")
        ]

        let threads = [
            // Claude helper
            T3ThreadItem(threadId: "c-h", providerInstanceId: "p-claude", relationshipToParent: "subagent", status: "running"),
            // Antigravity fork (remains task)
            T3ThreadItem(threadId: "ag-f", providerInstanceId: "p-ag", relationshipToParent: "fork", status: "running")
        ]

        let (snapshot, _) = selector.evaluate(providers: providers, threads: threads)
        XCTAssertEqual(snapshot.toolCounts["Claude Code"]?.runningHelpers, 1)
        XCTAssertEqual(snapshot.toolCounts["Claude Code"]?.runningTasks, 0)
        XCTAssertEqual(snapshot.toolCounts["Antigravity"]?.runningTasks, 1)
        XCTAssertEqual(snapshot.toolCounts["Antigravity"]?.runningHelpers, 0)
    }

    func testUnknownDriverKindMapsToOtherTools() {
        let providers = [
            T3ProviderInfo(providerInstanceId: "p-custom", driverKind: "private-custom-driver")
        ]

        let threads = [
            T3ThreadItem(threadId: "task-1", providerInstanceId: "p-custom", status: "running")
        ]

        let (snapshot, winner) = selector.evaluate(providers: providers, threads: threads)
        XCTAssertEqual(winner, "Other tools")
        XCTAssertEqual(snapshot.toolCounts["Other tools"]?.runningTasks, 1)
        XCTAssertNil(snapshot.toolCounts["private-custom-driver"])
    }
}
