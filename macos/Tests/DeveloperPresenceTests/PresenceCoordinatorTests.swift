import XCTest
@testable import DeveloperPresenceCore

final class MockForegroundDetector: ForegroundDetecting, @unchecked Sendable {
    var onForegroundAppChanged: (@Sendable (SupportedApp, String?) -> Void)?
    var currentApp: SupportedApp = .none
    var currentBundleId: String? = nil
    var bundleMappings: [String: SupportedApp] = [:]

    func start() {}
    func stop() {}

    func updateBundleMappings(_ mappings: [String: SupportedApp]) {
        self.bundleMappings = mappings
    }

    func currentForegroundApp() -> (SupportedApp, String?) {
        (currentApp, currentBundleId)
    }

    func simulateAppChange(app: SupportedApp, bundleId: String?) {
        currentApp = app
        currentBundleId = bundleId
        onForegroundAppChanged?(app, bundleId)
    }
}

final class MockSessionObserver: SessionStateObserving, @unchecked Sendable {
    var isSuspended: Bool = false
    var onSessionStateChanged: (@Sendable (Bool) -> Void)?

    func start() {}
    func stop() {}

    func simulateSessionSuspended(_ suspended: Bool) {
        isSuspended = suspended
        onSessionStateChanged?(suspended)
    }
}

final class MockDiscordTransport: DiscordTransport, @unchecked Sendable {
    var onDataReceived: (@Sendable (Data) -> Void)?
    var onDisconnected: (@Sendable (Error?) -> Void)?

    var sentData: [Data] = []
    var isConnected: Bool = false

    func connect(to path: String, completion: @escaping @Sendable (Result<Void, Error>) -> Void) {
        isConnected = true
        completion(.success(()))
    }

    func send(data: Data, completion: @escaping @Sendable (Result<Void, Error>) -> Void) {
        sentData.append(data)
        completion(.success(()))
    }

    func disconnect() {
        isConnected = false
    }
}

final class PresenceCoordinatorTests: XCTestCase {
    private var testDefaults: UserDefaults!
    private let suiteName = "com.developerpresence.tests.coordinator"

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

    @MainActor
    func testCoordinatorInitialState() async {
        let settingsService = SettingsService(userDefaults: testDefaults)
        let mockDetector = MockForegroundDetector()
        let mockSession = MockSessionObserver()
        let mockTransport = MockDiscordTransport()
        let client = DiscordIpcClient(transport: mockTransport)

        let coordinator = PresenceCoordinator(
            settingsService: settingsService,
            foregroundDetector: mockDetector,
            sessionObserver: mockSession,
            discordClient: client
        )

        coordinator.start()
        defer { coordinator.stop() }

        // Initial state guarantees
        XCTAssertFalse(coordinator.settings.enableSharing)
        XCTAssertFalse(coordinator.settings.isPaused)
        XCTAssertEqual(coordinator.settings.discordClientId, "")
        XCTAssertEqual(coordinator.state.outgoingHeadline, "None / Cleared")
        XCTAssertEqual(coordinator.state.statusMessage, "Disabled")
    }

    @MainActor
    func testEligibleObservationUpdatesHeadline() async {
        let settingsService = SettingsService(userDefaults: testDefaults)
        let mockDetector = MockForegroundDetector()
        let mockSession = MockSessionObserver()
        let mockTransport = MockDiscordTransport()
        let client = DiscordIpcClient(transport: mockTransport)

        let coordinator = PresenceCoordinator(
            settingsService: settingsService,
            foregroundDetector: mockDetector,
            sessionObserver: mockSession,
            discordClient: client
        )

        coordinator.start()
        defer { coordinator.stop() }

        // Configure valid client ID and enable sharing
        coordinator.setDiscordClientId("123456789012345678")
        coordinator.toggleSharing()
        XCTAssertTrue(coordinator.settings.enableSharing)

        // Foreground detector reports T3 Code
        mockDetector.simulateAppChange(app: .t3Code, bundleId: "com.t3tools.t3code")

        // Let main queue process Task
        let exp = expectation(description: "Wait for dispatch")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            XCTAssertEqual(coordinator.state.foregroundApp, .t3Code)
            XCTAssertEqual(coordinator.state.outgoingHeadline, "Using T3 Code")
            exp.fulfill()
        }
        await fulfillment(of: [exp], timeout: 1.0)
    }

    @MainActor
    func testPauseClearsPresence() async {
        let settingsService = SettingsService(userDefaults: testDefaults)
        let mockDetector = MockForegroundDetector()
        let mockSession = MockSessionObserver()
        let mockTransport = MockDiscordTransport()
        let client = DiscordIpcClient(transport: mockTransport)

        let coordinator = PresenceCoordinator(
            settingsService: settingsService,
            foregroundDetector: mockDetector,
            sessionObserver: mockSession,
            discordClient: client
        )

        coordinator.start()
        defer { coordinator.stop() }
        coordinator.setDiscordClientId("123456789012345678")
        coordinator.toggleSharing()
        mockDetector.simulateAppChange(app: .t3Code, bundleId: "com.t3tools.t3code")

        // Now toggle pause
        coordinator.togglePause()
        XCTAssertTrue(coordinator.settings.isPaused)
        XCTAssertEqual(coordinator.state.outgoingHeadline, "None / Cleared")
        XCTAssertEqual(coordinator.state.statusMessage, "Paused")
    }

    @MainActor
    func testSessionSuspensionClearsPresenceImmediately() async {
        let settingsService = SettingsService(userDefaults: testDefaults)
        let mockDetector = MockForegroundDetector()
        let mockSession = MockSessionObserver()
        let mockTransport = MockDiscordTransport()
        let client = DiscordIpcClient(transport: mockTransport)

        let coordinator = PresenceCoordinator(
            settingsService: settingsService,
            foregroundDetector: mockDetector,
            sessionObserver: mockSession,
            discordClient: client
        )

        coordinator.start()
        defer { coordinator.stop() }
        coordinator.setDiscordClientId("123456789012345678")
        coordinator.toggleSharing()
        mockDetector.simulateAppChange(app: .claude, bundleId: "com.anthropic.claudefordesktop")

        // Simulate session lock or sleep
        mockSession.simulateSessionSuspended(true)

        let exp = expectation(description: "Wait for session suspend")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            XCTAssertTrue(coordinator.state.isSessionSuspended)
            XCTAssertTrue(coordinator.state.outgoingHeadline.contains("Cleared"))
            exp.fulfill()
        }
        await fulfillment(of: [exp], timeout: 1.0)
    }

    @MainActor
    func testCustomCodexBundleMapping() async {
        let settingsService = SettingsService(userDefaults: testDefaults)
        let mockDetector = MockForegroundDetector()
        let mockSession = MockSessionObserver()
        let mockTransport = MockDiscordTransport()
        let client = DiscordIpcClient(transport: mockTransport)

        let coordinator = PresenceCoordinator(
            settingsService: settingsService,
            foregroundDetector: mockDetector,
            sessionObserver: mockSession,
            discordClient: client
        )

        coordinator.start()
        defer { coordinator.stop() }
        coordinator.setDiscordClientId("123456789012345678")
        coordinator.toggleSharing()

        // Add custom Codex mapping
        coordinator.updateBundleMapping(bundleId: "com.openai.codex", app: .codex)
        XCTAssertEqual(coordinator.settings.bundleMappings["com.openai.codex"], .codex)

        mockDetector.simulateAppChange(app: .codex, bundleId: "com.openai.codex")

        let exp = expectation(description: "Wait for custom mapping dispatch")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            XCTAssertEqual(coordinator.state.foregroundApp, .codex)
            XCTAssertEqual(coordinator.state.outgoingHeadline, "Using Codex")
            exp.fulfill()
        }
        await fulfillment(of: [exp], timeout: 1.0)
    }
}
