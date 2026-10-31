import XCTest
@testable import DeveloperPresenceCore

final class OAuthLoopbackTests: XCTestCase {
    func testRealLoopbackReceivesApproval() async throws {
        let server = BSDLoopbackServer()
        let (redirect, _) = try server.prepare()
        defer { server.cancel() }
        let pending = Task { try await server.waitForCallback(state: "synthetic-state", issuer: "https://t3.example", timeoutSeconds: 5) }
        var url = URLComponents(string: redirect)!
        url.queryItems = [URLQueryItem(name: "state", value: "synthetic-state"), URLQueryItem(name: "iss", value: "https://t3.example"), URLQueryItem(name: "code", value: "synthetic-code")]
        let (data, response) = try await URLSession.shared.data(from: url.url!)
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 200)
        XCTAssertTrue(String(decoding: data, as: UTF8.self).contains("Approval received"))
        let code = try await pending.value
        XCTAssertEqual(code, "synthetic-code")
    }

    func testMissingIssuerIsRejectedOnRealLoopback() async throws {
        let server = BSDLoopbackServer()
        let (redirect, _) = try server.prepare()
        defer { server.cancel() }
        let pending = Task { try await server.waitForCallback(state: "state", issuer: "https://t3.example", timeoutSeconds: 5) }
        let (_, response) = try await URLSession.shared.data(from: URL(string: redirect + "?state=state&code=code")!)
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 400)
        do { _ = try await pending.value; XCTFail("Missing issuer must fail") }
        catch { XCTAssertEqual(error as? OAuthError, .issuerMismatch) }
    }
}
