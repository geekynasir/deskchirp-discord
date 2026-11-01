import XCTest
@testable import DeveloperPresenceCore

final class PresenceV3CoordinatorTests: XCTestCase {
    private let suiteName = "com.developerpresence.tests.v3coordinator"
    private var defaults: UserDefaults!

    override func setUp() {
        super.setUp()
        defaults = UserDefaults(suiteName: suiteName)
        defaults.removePersistentDomain(forName: suiteName)
    }

    override func tearDown() {
        defaults.removePersistentDomain(forName: suiteName)
        defaults = nil
        super.tearDown()
    }

    @MainActor
    private func makeCoordinator(keychain: MockKeychain) -> PresenceCoordinator {
        let transport = MockHTTPTransport()
        let service = T3ActivityService(transport: transport, keychain: keychain, mcpClient: T3McpClient(transport: transport))
        let usage = T3UsageService(client: T3UsageClient(transport: transport, connector: MockConnector(), keychain: keychain))
        return PresenceCoordinator(
            settingsService: SettingsService(userDefaults: defaults),
            foregroundDetector: MockForegroundDetector(),
            sessionObserver: MockSessionObserver(),
            discordClient: DiscordIpcClient(transport: MockDiscordTransport()),
            t3Service: service,
            usageService: usage)
    }

    @MainActor
    func testLegacyAddressBecomesFirstEnvironmentAndKeepsItsSignIn() throws {
        defaults.set("http://localhost:3773", forKey: SettingsService.Keys.t3Address)
        let keychain = MockKeychain()
        try keychain.saveCredential(T3StoredCredential(accessToken: "legacy", expiresAt: Date().addingTimeInterval(3600)),
                                    forService: KeychainService.defaultService, account: "http://localhost:3773")
        let coordinator = makeCoordinator(keychain: keychain)
        XCTAssertEqual(coordinator.settings.t3Environments.count, 1)
        XCTAssertEqual(coordinator.t3Service.connectionStatus, .connected, "The preview 3 sign-in keeps working")
        XCTAssertEqual(try keychain.loadCredential(forService: KeychainService.defaultService, account: "http://localhost:3773/mcp")?.accessToken,
                       "legacy", "The legacy credential moves to the per-environment key")
        XCTAssertNil(try keychain.loadCredential(forService: KeychainService.defaultService, account: "http://localhost:3773"))
    }

    @MainActor
    func testAddAndRemoveEnvironmentsDeletesCredentialsAndPins() throws {
        let keychain = MockKeychain()
        let coordinator = makeCoordinator(keychain: keychain)
        coordinator.start()
        defer { coordinator.stop() }

        XCTAssertTrue(coordinator.addEnvironment(address: "https://remote.example"))
        XCTAssertFalse(coordinator.addEnvironment(address: "https://remote.example/mcp"), "Duplicate origins are refused")
        XCTAssertFalse(coordinator.addEnvironment(address: "http://remote.example"), "Plain http is only for this computer")
        XCTAssertTrue(coordinator.addEnvironment(address: "http://localhost:3773", label: "This Mac"))
        XCTAssertEqual(coordinator.settings.t3Environments.map { $0.label }, ["remote.example", "This Mac"])

        let remote = coordinator.settings.t3Environments[0]
        try keychain.saveCredential(T3StoredCredential(accessToken: "mcp"), forService: KeychainService.defaultService, account: "https://remote.example/mcp")
        try keychain.saveCredential(T3StoredCredential(accessToken: "old"), forService: KeychainService.defaultService, account: "https://remote.example")
        try keychain.saveCredential(T3StoredCredential(accessToken: "usage"), forService: KeychainService.defaultService,
                                    account: T3UsagePairing.keychainAccount(origin: "https://remote.example"))
        coordinator.updateControls {
            $0.pinnedTask = "\(remote.id)/t1"
            $0.pinnedProject = "\(remote.id)/p1"
            $0.excludedProjects = ["\(remote.id)/p2"]
            $0.projectAliases = ["\(remote.id)/p1": "Orbit"]
        }

        coordinator.removeEnvironment(id: remote.id)
        XCTAssertEqual(coordinator.settings.t3Environments.count, 1)
        XCTAssertNil(try keychain.loadCredential(forService: KeychainService.defaultService, account: "https://remote.example/mcp"))
        XCTAssertNil(try keychain.loadCredential(forService: KeychainService.defaultService, account: "https://remote.example"))
        XCTAssertNil(try keychain.loadCredential(forService: KeychainService.defaultService,
                                                 account: T3UsagePairing.keychainAccount(origin: "https://remote.example")))
        XCTAssertEqual(coordinator.settings.pinnedTask, "")
        XCTAssertEqual(coordinator.settings.pinnedProject, "")
        XCTAssertTrue(coordinator.settings.excludedProjects.isEmpty)
        XCTAssertTrue(coordinator.settings.projectAliases.isEmpty)

        let reloaded = SettingsService(userDefaults: defaults).loadSettings()
        XCTAssertEqual(reloaded.t3Environments.map { $0.label }, ["This Mac"])
    }

    @MainActor
    func testContextMetricForcesPinnedTaskScope() {
        let coordinator = makeCoordinator(keychain: MockKeychain())
        coordinator.updateControls {
            $0.usageScope = .today
            $0.usageMetric = .context
        }
        XCTAssertEqual(coordinator.settings.usageScope, .pinnedTask)
    }
}
