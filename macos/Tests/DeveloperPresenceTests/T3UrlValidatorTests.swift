import XCTest
@testable import DeveloperPresenceCore

final class T3UrlValidatorTests: XCTestCase {
    func testEmptyAddressThrows() {
        XCTAssertThrowsError(try T3UrlValidator.validateAndNormalize("")) { error in
            XCTAssertEqual(error as? T3UrlValidationError, .empty)
            XCTAssertEqual((error as? LocalizedError)?.errorDescription, "Paste the address from T3 Code first.")
        }

        XCTAssertThrowsError(try T3UrlValidator.validateAndNormalize("   \n\t")) { error in
            XCTAssertEqual(error as? T3UrlValidationError, .empty)
        }
    }

    func testPlainHttpLoopbackAllowedAndNormalized() throws {
        let localhost = try T3UrlValidator.validateAndNormalize("http://localhost:3773")
        XCTAssertEqual(localhost.origin, "http://localhost:3773")
        XCTAssertEqual(localhost.mcpUrl.absoluteString, "http://localhost:3773/mcp")

        let localhostMcp = try T3UrlValidator.validateAndNormalize("http://localhost:3773/mcp")
        XCTAssertEqual(localhostMcp.origin, "http://localhost:3773")
        XCTAssertEqual(localhostMcp.mcpUrl.absoluteString, "http://localhost:3773/mcp")

        let loopbackIp = try T3UrlValidator.validateAndNormalize("http://127.0.0.1:8080/mcp/")
        XCTAssertEqual(loopbackIp.origin, "http://127.0.0.1:8080")
        XCTAssertEqual(loopbackIp.mcpUrl.absoluteString, "http://127.0.0.1:8080/mcp")

        let ipv6Loopback = try T3UrlValidator.validateAndNormalize("http://[::1]:3773")
        XCTAssertEqual(ipv6Loopback.origin, "http://[::1]:3773")
        XCTAssertEqual(ipv6Loopback.mcpUrl.absoluteString, "http://[::1]:3773/mcp")
    }

    func testPlainHttpNonLoopbackThrows() {
        XCTAssertThrowsError(try T3UrlValidator.validateAndNormalize("http://remote-server.com:3773")) { error in
            XCTAssertEqual(error as? T3UrlValidationError, .plainHttpNonLocalhost)
            XCTAssertEqual(
                (error as? LocalizedError)?.errorDescription,
                "Use an address that starts with https://. Plain http:// works only for T3 Code on this computer."
            )
        }

        // Suffix trick: attacker domain ending in localhost or 127.0.0.1
        XCTAssertThrowsError(try T3UrlValidator.validateAndNormalize("http://notlocalhost.com:3773")) { error in
            XCTAssertEqual(error as? T3UrlValidationError, .plainHttpNonLocalhost)
        }

        XCTAssertThrowsError(try T3UrlValidator.validateAndNormalize("http://127.0.0.1.attacker.com:3773")) { error in
            XCTAssertEqual(error as? T3UrlValidationError, .plainHttpNonLocalhost)
        }
    }

    func testHttpsNonLoopbackAllowed() throws {
        let tailscale = try T3UrlValidator.validateAndNormalize("https://box.example.ts.net:3773/mcp")
        XCTAssertEqual(tailscale.origin, "https://box.example.ts.net:3773")
        XCTAssertEqual(tailscale.mcpUrl.absoluteString, "https://box.example.ts.net:3773/mcp")
        XCTAssertEqual(tailscale.authorizationServerMetadataUrl.absoluteString, "https://box.example.ts.net:3773/.well-known/oauth-authorization-server")
    }

    func testRejectUserinfoQueryFragmentAndWrongPaths() {
        // Userinfo
        XCTAssertThrowsError(try T3UrlValidator.validateAndNormalize("http://user:pass@localhost:3773/mcp")) { error in
            XCTAssertEqual(error as? T3UrlValidationError, .invalidFormat)
        }

        // Query
        XCTAssertThrowsError(try T3UrlValidator.validateAndNormalize("http://localhost:3773/mcp?key=val")) { error in
            XCTAssertEqual(error as? T3UrlValidationError, .invalidFormat)
        }

        // Fragment
        XCTAssertThrowsError(try T3UrlValidator.validateAndNormalize("http://localhost:3773/mcp#anchor")) { error in
            XCTAssertEqual(error as? T3UrlValidationError, .invalidFormat)
        }

        // Wrong path
        XCTAssertThrowsError(try T3UrlValidator.validateAndNormalize("http://localhost:3773/api/v1")) { error in
            XCTAssertEqual(error as? T3UrlValidationError, .invalidFormat)
        }

        // Not a URL
        XCTAssertThrowsError(try T3UrlValidator.validateAndNormalize("not a url at all")) { error in
            XCTAssertEqual(error as? T3UrlValidationError, .invalidFormat)
        }
    }
}
