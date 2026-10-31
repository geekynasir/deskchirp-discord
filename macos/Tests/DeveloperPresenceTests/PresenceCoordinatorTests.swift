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
    @MainActor
    func testCoordinatorSignInWhileSharingOffRegression() async throws {
        let settingsService = SettingsService(userDefaults: testDefaults)
        let mockDetector = MockForegroundDetector()
        let mockSession = MockSessionObserver()
        let mockDiscordTransport = MockDiscordTransport()
        let discordClient = DiscordIpcClient(transport: mockDiscordTransport)

        let mockHttpTransport = MockHTTPTransport()
        let mockKeychain = MockKeychain()
        let mockLoopback = MockOAuthLoopbackServer()
        let mockBrowser = MockBrowserOpener()

        let oauthClient = T3OAuthClient(
            transport: mockHttpTransport,
            keychain: mockKeychain,
            loopbackServer: mockLoopback,
            browserOpener: mockBrowser
        )
        let mcpClient = T3McpClient(transport: mockHttpTransport)

        let t3Service = T3ActivityService(
            transport: mockHttpTransport,
            keychain: mockKeychain,
            oauthClient: oauthClient,
            mcpClient: mcpClient
        )

        let coordinator = PresenceCoordinator(
            settingsService: settingsService,
            foregroundDetector: mockDetector,
            sessionObserver: mockSession,
            discordClient: discordClient,
            t3Service: t3Service
        )

        coordinator.start()
        defer { coordinator.stop() }

        // Sharing is OFF by default:
        XCTAssertFalse(coordinator.settings.enableSharing)
        coordinator.toggleT3Activity() // Enable T3 activity
        XCTAssertTrue(coordinator.settings.showT3Activity)

        coordinator.setT3Address("http://localhost:3773")

        // Setup mock HTTP handler for discovery, register, token, and MCP tools
        mockHttpTransport.handler = { req in
            let path = req.url?.path ?? ""
            if path == "/.well-known/oauth-protected-resource/mcp" {
                let json = """
                { "resource": "http://localhost:3773/mcp", "authorization_servers": ["http://localhost:3773"], "scopes_supported": ["orchestration:read"] }
                """
                return (json.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if path == "/.well-known/oauth-authorization-server" {
                let json = """
                {
                  "issuer": "http://localhost:3773",
                  "code_challenge_methods_supported": ["S256"],
                  "authorization_endpoint": "http://localhost:3773/oauth/mcp/authorize",
                  "token_endpoint": "http://localhost:3773/oauth/mcp/token",
                  "registration_endpoint": "http://localhost:3773/oauth/mcp/register"
                }
                """
                return (json.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if path == "/oauth/mcp/register" {
                let resp = """
                { "client_id": "client-123" }
                """
                return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 201, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if path == "/oauth/mcp/token" {
                let tokenJson = """
                {
                  "access_token": "token-sharing-off",
                  "token_type": "Bearer",
                  "expires_in": 3600,
                  "scope": "orchestration:read"
                }
                """
                return (tokenJson.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if path == "/mcp" {
                guard let bodyData = req.httpBody,
                      let json = try? JSONSerialization.jsonObject(with: bodyData) as? [String: Any],
                      let method = json["method"] as? String else {
                    throw HTTPTransportError.invalidResponse
                }
                if method == "initialize" {
                    let resp = """
                    { "jsonrpc": "2.0", "id": 1, "result": { "protocolVersion": "2025-06-18" } }
                    """
                    return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json", "mcp-session-id": "sess-reg"])!)
                } else if method == "notifications/initialized" {
                    return (Data(), HTTPURLResponse(url: req.url!, statusCode: 202, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
                } else if method == "tools/call" {
                    let params = json["params"] as? [String: Any] ?? [:]
                    let name = params["name"] as? String ?? ""
                    let reqId = json["id"] as? Int ?? 2
                    if name == "orchestrator_capabilities" {
                        let resp = """
                        {
                          "jsonrpc": "2.0",
                          "id": \(reqId),
                          "result": {
                            "structuredContent": {
                              "providers": [
                                { "providerInstanceId": "p-ag", "driverKind": "antigravity" }
                              ]
                            }
                          }
                        }
                        """
                        return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
                    } else if name == "t3_thread_list" {
                        let resp = """
                        {
                          "jsonrpc": "2.0",
                          "id": \(reqId),
                          "result": {
                            "threads": [
                              { "threadId": "t-1", "providerInstanceId": "p-ag", "status": "running" }
                            ],
                            "nextCursor": null, "total": 1
                          }
                        }
                        """
                        return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
                    }
                }
            }
            throw HTTPTransportError.invalidResponse
        }

        // Perform sign-in while sharing is off
        try await coordinator.t3Service.connect(address: coordinator.settings.t3Address)

        // Wait a beat for async onStatusChanged to dispatch
        let exp1 = expectation(description: "Wait for sign-in completion dispatch")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            // Verify that connect succeeded and status is connected:
            XCTAssertEqual(coordinator.state.t3ConnectionStatus, .connected)
            // Verify credential is saved in Keychain:
            let saved = try? mockKeychain.loadCredential(forService: KeychainService.defaultService, account: "http://localhost:3773")
            XCTAssertEqual(saved?.accessToken, "token-sharing-off")
            // Verify snapshot is NIL because sharing was OFF (no poll while sharing off):
            XCTAssertNil(coordinator.t3Service.currentSnapshot)
            XCTAssertNil(coordinator.state.t3ActiveWinner)
            exp1.fulfill()
        }
        await fulfillment(of: [exp1], timeout: 1.0)

        // Now turn sharing ON and focus T3 Code
        coordinator.setDiscordClientId("123456789012345678")
        coordinator.toggleSharing()
        XCTAssertTrue(coordinator.settings.enableSharing)

        mockDetector.simulateAppChange(app: .t3Code, bundleId: "com.t3tools.t3code")

        // Trigger poll now that sharing and foreground eligibility are true
        coordinator.t3Service.pollOnce()

        let exp2 = expectation(description: "Wait for poll and activity update")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
            XCTAssertEqual(coordinator.state.t3ActiveWinner, "Antigravity")
            XCTAssertEqual(coordinator.state.outgoingHeadline, "T3 Code · Antigravity")
            XCTAssertEqual(coordinator.state.outgoingState, "1 task")
            exp2.fulfill()
        }
        await fulfillment(of: [exp2], timeout: 2.0)
    }

    @MainActor
    func testKeychainSaveFailurePreventsConnectedStatus() async throws {
        let settingsService = SettingsService(userDefaults: testDefaults)
        let mockDetector = MockForegroundDetector()
        let mockSession = MockSessionObserver()
        let mockDiscordTransport = MockDiscordTransport()
        let discordClient = DiscordIpcClient(transport: mockDiscordTransport)

        let mockHttpTransport = MockHTTPTransport()
        let mockKeychain = MockKeychain()
        // Inject failure on save
        mockKeychain.errorToThrowOnSave = KeychainError.saveFailed(status: -1)

        let mockLoopback = MockOAuthLoopbackServer()
        let mockBrowser = MockBrowserOpener()

        let oauthClient = T3OAuthClient(
            transport: mockHttpTransport,
            keychain: mockKeychain,
            loopbackServer: mockLoopback,
            browserOpener: mockBrowser
        )
        let mcpClient = T3McpClient(transport: mockHttpTransport)

        let t3Service = T3ActivityService(
            transport: mockHttpTransport,
            keychain: mockKeychain,
            oauthClient: oauthClient,
            mcpClient: mcpClient
        )

        let coordinator = PresenceCoordinator(
            settingsService: settingsService,
            foregroundDetector: mockDetector,
            sessionObserver: mockSession,
            discordClient: discordClient,
            t3Service: t3Service
        )

        coordinator.start()
        defer { coordinator.stop() }

        mockHttpTransport.handler = { req in
            let path = req.url?.path ?? ""
            if path == "/.well-known/oauth-protected-resource/mcp" {
                let json = """
                { "resource": "http://localhost:3773/mcp", "authorization_servers": ["http://localhost:3773"], "scopes_supported": ["orchestration:read"] }
                """
                return (json.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if path == "/.well-known/oauth-authorization-server" {
                let json = """
                {
                  "issuer": "http://localhost:3773",
                  "code_challenge_methods_supported": ["S256"],
                  "authorization_endpoint": "http://localhost:3773/oauth/mcp/authorize",
                  "token_endpoint": "http://localhost:3773/oauth/mcp/token",
                  "registration_endpoint": "http://localhost:3773/oauth/mcp/register"
                }
                """
                return (json.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if path == "/oauth/mcp/register" {
                return (#"{"client_id":"c1"}"#.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 201, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if path == "/oauth/mcp/token" {
                let tokenJson = """
                {
                  "access_token": "token-1",
                  "token_type": "Bearer",
                  "expires_in": 3600,
                  "scope": "orchestration:read"
                }
                """
                return (tokenJson.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            }
            throw HTTPTransportError.invalidResponse
        }

        do {
            try await coordinator.t3Service.connect(address: "http://localhost:3773")
            XCTFail("Should have thrown Keychain error")
        } catch {
            // Expected
        }

        XCTAssertNotEqual(coordinator.t3Service.connectionStatus, .connected, "Must not claim connected if save fails")
        XCTAssertEqual(coordinator.t3Service.connectionStatus, .couldNotReach)
    }

    @MainActor
    func testEndpointChangeClearsSnapshotAndWinner() async throws {
        let settingsService = SettingsService(userDefaults: testDefaults)
        let mockDetector = MockForegroundDetector()
        let mockSession = MockSessionObserver()
        let mockDiscordTransport = MockDiscordTransport()
        let discordClient = DiscordIpcClient(transport: mockDiscordTransport)
        let t3Service = T3ActivityService(keychain: MockKeychain())

        let coordinator = PresenceCoordinator(
            settingsService: settingsService,
            foregroundDetector: mockDetector,
            sessionObserver: mockSession,
            discordClient: discordClient,
            t3Service: t3Service
        )
        coordinator.start()
        defer { coordinator.stop() }

        coordinator.setT3Address("http://localhost:3773")
        XCTAssertEqual(coordinator.settings.t3Address, "http://localhost:3773")

        // Changing address resets snapshot and resets winner
        coordinator.setT3Address("http://localhost:4444")
        XCTAssertEqual(coordinator.settings.t3Address, "http://localhost:4444")
        XCTAssertNil(coordinator.t3Service.currentSnapshot)
        XCTAssertNil(coordinator.state.t3ActiveWinner)
    }
}
