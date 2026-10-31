import XCTest
@testable import DeveloperPresenceCore

final class T3OAuthClientTests: XCTestCase {
    private var mockTransport: MockHTTPTransport!
    private var mockKeychain: MockKeychain!
    private var mockLoopback: MockOAuthLoopbackServer!
    private var mockBrowser: MockBrowserOpener!
    private var client: T3OAuthClient!

    override func setUp() {
        super.setUp()
        mockTransport = MockHTTPTransport()
        mockKeychain = MockKeychain()
        mockLoopback = MockOAuthLoopbackServer()
        mockBrowser = MockBrowserOpener()
        client = T3OAuthClient(
            transport: mockTransport,
            keychain: mockKeychain,
            loopbackServer: mockLoopback,
            browserOpener: mockBrowser
        )
    }

    func testDiscoverySuccessWithProtectedResourceValidation() async throws {
        let endpoint = try T3UrlValidator.validateAndNormalize("http://localhost:3773")

        mockTransport.handler = { req in
            let path = req.url?.path ?? ""
            if path == "/.well-known/oauth-protected-resource/mcp" {
                let json = """
                {
                  "resource": "http://localhost:3773/mcp",
                  "authorization_servers": ["http://localhost:3773"], "scopes_supported": ["orchestration:read"]
                }
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
            }
            throw HTTPTransportError.invalidResponse
        }

        let metadata = try await client.discoverMetadata(endpoint: endpoint)
        XCTAssertEqual(metadata.issuer, "http://localhost:3773")
        XCTAssertEqual(metadata.authorizationEndpoint.absoluteString, "http://localhost:3773/oauth/mcp/authorize")
        XCTAssertEqual(metadata.tokenEndpoint.absoluteString, "http://localhost:3773/oauth/mcp/token")
        XCTAssertEqual(metadata.registrationEndpoint?.absoluteString, "http://localhost:3773/oauth/mcp/register")
    }

    func testDiscoveryCrossOriginMetadataRejected() async throws {
        let endpoint = try T3UrlValidator.validateAndNormalize("http://localhost:3773")

        mockTransport.handler = { req in
            let path = req.url?.path ?? ""
            if path == "/.well-known/oauth-protected-resource/mcp" {
                let json = """
                {
                  "resource": "http://localhost:3773/mcp",
                  "authorization_servers": ["http://localhost:3773"], "scopes_supported": ["orchestration:read"]
                }
                """
                return (json.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if path == "/.well-known/oauth-authorization-server" {
                let json = """
                {
                  "issuer": "http://localhost:3773",
                  "code_challenge_methods_supported": ["S256"],
                  "authorization_endpoint": "https://malicious.attacker.com/oauth/authorize",
                  "token_endpoint": "https://malicious.attacker.com/oauth/token"
                }
                """
                return (json.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            }
            throw HTTPTransportError.invalidResponse
        }

        do {
            _ = try await client.discoverMetadata(endpoint: endpoint)
            XCTFail("Should have thrown error on cross-origin metadata")
        } catch {
            // Success: threw error
        }
    }

    func testDiscoveryRejectsEndpointsWithQueryOrFragment() async throws {
        let endpoint = try T3UrlValidator.validateAndNormalize("http://localhost:3773")

        mockTransport.handler = { req in
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
                  "authorization_endpoint": "http://localhost:3773/oauth/mcp/authorize?injected=true",
                  "token_endpoint": "http://localhost:3773/oauth/mcp/token"
                }
                """
                return (json.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            }
            throw HTTPTransportError.invalidResponse
        }

        do {
            _ = try await client.discoverMetadata(endpoint: endpoint)
            XCTFail("Should have thrown error on endpoint containing query parameter")
        } catch {
            // Success
        }
    }

    func testFullOAuthSignInFlowWithExactRedirectConsistency() async throws {
        let endpoint = try T3UrlValidator.validateAndNormalize("http://localhost:3773")
        let assignedRedirect = "http://127.0.0.1:54321/callback"
        mockLoopback.redirectUriToReturn = assignedRedirect
        mockLoopback.codeToReturn = "auth-code-123"

        mockTransport.handler = { req in
            let path = req.url?.path ?? ""
            if path == "/.well-known/oauth-protected-resource/mcp" {
                let json = """
                {
                  "resource": "http://localhost:3773/mcp",
                  "authorization_servers": ["http://localhost:3773"], "scopes_supported": ["orchestration:read"]
                }
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
                // Verify that registration received the EXACT same ephemeral redirect URI
                if let bodyData = req.httpBody,
                   let json = try? JSONSerialization.jsonObject(with: bodyData) as? [String: Any] {
                    XCTAssertEqual(json["client_name"] as? String, "Developer Presence")
                    XCTAssertEqual(json["token_endpoint_auth_method"] as? String, "none")
                    let uris = json["redirect_uris"] as? [String]
                    XCTAssertEqual(uris, [assignedRedirect], "Registration must use the exact ephemeral redirect URI")
                }
                let respJson = """
                {
                  "client_id": "client-test-id",
                  "client_name": "Developer Presence"
                }
                """
                return (respJson.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 201, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if path == "/oauth/mcp/token" {
                // Verify token request parameters and strict form escaping
                if let bodyData = req.httpBody, let bodyString = String(data: bodyData, encoding: .utf8) {
                    XCTAssertTrue(bodyString.contains("grant_type=authorization_code"))
                    XCTAssertTrue(bodyString.contains("code=auth-code-123"))
                    XCTAssertTrue(bodyString.contains("client_id=client-test-id"))
                    XCTAssertTrue(bodyString.contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A54321%2Fcallback"))
                    XCTAssertTrue(bodyString.contains("code_verifier="))
                    XCTAssertTrue(bodyString.contains("resource=http%3A%2F%2Flocalhost%3A3773%2Fmcp"))
                }
                let tokenJson = """
                {
                  "access_token": "bearer-token-abc",
                  "token_type": "Bearer",
                  "expires_in": 3600,
                  "scope": "orchestration:read"
                }
                """
                return (tokenJson.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            }
            throw HTTPTransportError.invalidResponse
        }

        let credential = try await client.signIn(to: endpoint)

        XCTAssertEqual(credential.accessToken, "bearer-token-abc")
        XCTAssertNotNil(credential.expiresAt)
        XCTAssertFalse(credential.isExpired())

        // Verify loopback server was prepared BEFORE browser opened
        XCTAssertTrue(mockLoopback.wasPrepared)

        // Verify browser was opened with the exact ephemeral redirect URI
        XCTAssertNotNil(mockBrowser.lastOpenedURL)
        let openedURL = mockBrowser.lastOpenedURL!
        XCTAssertTrue(openedURL.absoluteString.contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A54321%2Fcallback"))
        XCTAssertTrue(openedURL.absoluteString.contains("scope=orchestration%3Aread") || openedURL.absoluteString.contains("scope=orchestration:read"))
        XCTAssertTrue(openedURL.absoluteString.contains("code_challenge_method=S256"))
        XCTAssertTrue(openedURL.absoluteString.contains("client_id=client-test-id"))

        // Note: T3ActivityService owns the guarded save to Keychain
    }

    func testTokenResponseValidationRejectsInvalidTokens() async throws {
        let endpoint = try T3UrlValidator.validateAndNormalize("http://localhost:3773")

        let makeHandler: (String) -> (@Sendable (URLRequest) throws -> (Data, HTTPURLResponse)) = { tokenPayload in
            return { req in
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
                    { "client_id": "client-1" }
                    """
                    return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 201, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
                } else if path == "/oauth/mcp/token" {
                    return (tokenPayload.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
                }
                throw HTTPTransportError.invalidResponse
            }
        }

        // Test 1: Wrong token_type (must be Bearer)
        mockTransport.handler = makeHandler("""
        {
          "access_token": "token-1",
          "token_type": "MAC",
          "expires_in": 3600,
          "scope": "orchestration:read"
        }
        """)
        do {
            _ = try await client.signIn(to: endpoint)
            XCTFail("Should reject non-Bearer token type")
        } catch {
            // Expected
        }

        // Test 2: Missing orchestration:read scope
        mockTransport.handler = makeHandler("""
        {
          "access_token": "token-1",
          "token_type": "Bearer",
          "expires_in": 3600,
          "scope": "other:scope"
        }
        """)
        do {
            _ = try await client.signIn(to: endpoint)
            XCTFail("Should reject token without orchestration:read scope")
        } catch {
            // Expected
        }

        // Test 3: Non-positive expires_in
        mockTransport.handler = makeHandler("""
        {
          "access_token": "token-1",
          "token_type": "Bearer",
          "expires_in": 0,
          "scope": "orchestration:read"
        }
        """)
        do {
            _ = try await client.signIn(to: endpoint)
            XCTFail("Should reject token with non-positive expires_in")
        } catch {
            // Expected
        }
    }

    func testCallbackRequiresMatchingIssParameter() async throws {
        let server = BSDLoopbackServer()
        let (_, port) = try server.prepare()
        defer { server.cancel() }

        let expectedState = "csrf-test-state"
        let expectedIssuer = "http://localhost:3773"

        // 1. Missing iss parameter rejected with issuerMismatch
        let taskMissingIss = Task { () -> String in
            return try await server.waitForCallback(state: expectedState, issuer: expectedIssuer, timeoutSeconds: 2.0)
        }

        // Send HTTP GET request missing iss
        let urlMissingIss = URL(string: "http://127.0.0.1:\(port)/callback?state=\(expectedState)&code=auth-code-1")!
        var (data1, resp1) = try await URLSession.shared.data(from: urlMissingIss)
        _ = (data1, resp1)

        do {
            _ = try await taskMissingIss.value
            XCTFail("Should reject callback when iss is missing")
        } catch let err as OAuthError {
            XCTAssertEqual(err, .issuerMismatch)
        }

        // 2. Mismatched iss parameter rejected with issuerMismatch
        let (_, port2) = try server.prepare()
        let taskMismatchedIss = Task { () -> String in
            return try await server.waitForCallback(state: expectedState, issuer: expectedIssuer, timeoutSeconds: 2.0)
        }

        let urlWrongIss = URL(string: "http://127.0.0.1:\(port2)/callback?state=\(expectedState)&code=auth-code-1&iss=http%3A%2F%2Fwrong-server.com")!
        var (data2, resp2) = try await URLSession.shared.data(from: urlWrongIss)
        _ = (data2, resp2)

        do {
            _ = try await taskMismatchedIss.value
            XCTFail("Should reject callback when iss does not match expected issuer")
        } catch let err as OAuthError {
            XCTAssertEqual(err, .issuerMismatch)
        }

        // 3. Matching iss parameter accepted
        let (_, port3) = try server.prepare()
        let taskValid = Task { () -> String in
            return try await server.waitForCallback(state: expectedState, issuer: expectedIssuer, timeoutSeconds: 2.0)
        }

        let urlValid = URL(string: "http://127.0.0.1:\(port3)/callback?state=\(expectedState)&code=auth-code-success&iss=http%3A%2F%2Flocalhost%3A3773")!
        var (data3, resp3) = try await URLSession.shared.data(from: urlValid)
        _ = (data3, resp3)

        let receivedCode = try await taskValid.value
        XCTAssertEqual(receivedCode, "auth-code-success")
    }
}
