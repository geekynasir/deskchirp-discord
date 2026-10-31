import XCTest
@testable import DeveloperPresenceCore

final class ComputerLinkTests: XCTestCase {
    func testSecureAddressesAndSafeMigration() throws {
        XCTAssertEqual(try ComputerAddress.normalize("https://link.example/"), "https://link.example")
        XCTAssertEqual(try ComputerAddress.normalize("http://127.0.0.1:4859"), "http://127.0.0.1:4859")
        for text in ["http://link.example", "https://user:secret@link.example", "https://link.example/mcp", "https://link.example?key=secret"] {
            XCTAssertThrowsError(try ComputerAddress.normalize(text))
        }
        let settings = try JSONDecoder().decode(PresenceSettings.self, from: Data("{}".utf8))
        XCTAssertFalse(settings.followComputers)
        XCTAssertEqual(settings.computerLinkAddress, "")
    }

    func testPermissionChecksAccountDeadlineAndOptOut() {
        let permission = ComputerPermission(inputAge: { 0 })
        XCTAssertTrue(permission.allows(account: nil))
        permission.configure(linked: true, account: "123")
        XCTAssertFalse(permission.allows(account: "123"))
        permission.grant(start: .now, seconds: 15)
        XCTAssertTrue(permission.allows(account: "123"))
        XCTAssertFalse(permission.allows(account: "456"))
        permission.grant(start: ContinuousClock.now.advanced(by: .seconds(-13)), seconds: 15)
        XCTAssertFalse(permission.allows(account: "123"))
        permission.grant(start: .now, seconds: 200)
        XCTAssertFalse(permission.allows(account: "123"))
        permission.grant(start: .now, seconds: 15)
        permission.configure(linked: true, account: nil)
        XCTAssertFalse(permission.allows(account: "123"))
    }

    func testIdleAndUnknownInputCannotPublish() {
        for age in [nil, 300, -1] as [Double?] {
            let permission = ComputerPermission(inputAge: { age })
            permission.configure(linked: true, account: "123")
            permission.grant(start: .now, seconds: 15)
            XCTAssertFalse(permission.allows(account: "123"))
        }
    }

    @MainActor
    func testLinkHeartbeatAndSecureUnlink() async throws {
        let transport = MockHTTPTransport { request in
            let path = request.url!.path
            let body: String
            if path == "/v1/join" {
                body = #"{"deviceId":"test_device_123456","groupId":"test_group_123456","token":"test_token_123456789012"}"#
            } else if path == "/v1/heartbeat" {
                let text = String(data: request.httpBody!, encoding: .utf8)!
                XCTAssertFalse(text.contains("123456789012345678"))
                XCTAssertFalse(text.contains("T3"))
                body = #"{"allowed":true,"validFor":15,"generation":1,"selectedName":"My Mac","switching":false}"#
            } else { body = #"{"removed":true}"# }
            return (Data(body.utf8), HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!)
        }
        let keychain = MockKeychain()
        let service = ComputerLinkService(keychain: keychain, transport: transport, inputAge: { 0 })
        service.updateContext(linked: true, available: false, discordUserId: nil)
        let linked = await service.link(address: "https://link.example", code: "test-code", name: "My Mac")
        XCTAssertTrue(linked)
        XCTAssertTrue(service.hasSavedLink)
        XCTAssertFalse(service.permission.allows(account: "123456789012345678"))
        service.updateContext(linked: true, available: true, discordUserId: "123456789012345678")
        await service.pollOnce()
        XCTAssertTrue(service.permission.allows(account: "123456789012345678"))
        service.updateContext(linked: true, available: false, discordUserId: nil)
        XCTAssertFalse(service.permission.allows(account: "123456789012345678"))
        let unlinked = await service.unlink()
        XCTAssertTrue(unlinked)
        XCTAssertFalse(service.hasSavedLink)
        XCTAssertNil(try keychain.loadCredential(forService: "com.developerpresence.computers", account: "device"))
    }

    @MainActor
    func testFailedCredentialSaveDoesNotClaimLinked() async {
        let transport = MockHTTPTransport { request in
            let body = #"{"deviceId":"test_device_123456","groupId":"test_group_123456","token":"test_token_123456789012"}"#
            return (Data(body.utf8), HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!)
        }
        let keychain = MockKeychain()
        keychain.errorToThrowOnSave = KeychainError.saveFailed(status: -1)
        let service = ComputerLinkService(keychain: keychain, transport: transport, inputAge: { 0 })
        service.updateContext(linked: true, available: false, discordUserId: nil)
        let result = await service.link(address: "https://link.example", code: "test-code", name: "My Mac")
        XCTAssertFalse(result)
        XCTAssertFalse(service.hasSavedLink)
        XCTAssertFalse(service.permission.allows(account: nil))
    }

    func testStandbyHandshakeReadsAccountWithoutPublishing() async {
        let transport = ScriptedMockDiscordTransport()
        let client = DiscordIpcClient(transport: transport, candidatePathsProvider: { ["/mock"] })
        let account = expectation(description: "account observed")
        client.onAccountChanged = { id in if id == "123456789012345678" { account.fulfill() } }
        client.observeAccount(clientId: "123456789012345678")
        // Synchronize the handshake before sending READY, as the real client does.
        transport.simulateReady(userId: "123456789012345678")
        await fulfillment(of: [account], timeout: 2)
        XCTAssertEqual(client.currentUserId, "123456789012345678")
        XCTAssertFalse(transport.snapshotSentFrames().contains { frame in
            guard let object = try? JSONSerialization.jsonObject(with: frame.payload) as? [String: Any] else { return false }
            return object["cmd"] as? String == "SET_ACTIVITY"
        })
        client.clearAndCloseImmediately()
    }

    func testTransportWaitsForComputerPermissionBeforeWritingActivity() async {
        let transport = ScriptedMockDiscordTransport()
        let client = DiscordIpcClient(transport: transport, candidatePathsProvider: { ["/mock"] })
        let permission = ComputerPermission(inputAge: { 0 })
        permission.configure(linked: true, account: "123456789012345678")
        client.publishAllowed = { account in permission.allows(account: account) }
        let ready = expectation(description: "ready without permission")
        client.onStatusChanged = { status, _ in if status == .connected { ready.fulfill() } }
        client.updateActivity(DiscordActivity(details: "Using T3 Code"), clientId: "123456789012345678")
        transport.simulateReady(userId: "123456789012345678")
        await fulfillment(of: [ready], timeout: 2)
        XCTAssertFalse(transport.snapshotSentFrames().contains { frame in
            guard let object = try? JSONSerialization.jsonObject(with: frame.payload) as? [String: Any] else { return false }
            return object["cmd"] as? String == "SET_ACTIVITY"
        })
        permission.grant(start: .now, seconds: 15)
        client.updateActivity(DiscordActivity(details: "Using T3 Code"), clientId: "123456789012345678")
        transport.waitForActivityFrame()
        permission.invalidate()
        client.clearAndCloseImmediately()
    }
}
