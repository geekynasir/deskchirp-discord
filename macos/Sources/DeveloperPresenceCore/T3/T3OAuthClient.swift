import Foundation

public struct T3AuthMetadata: Sendable {
    public let issuer: String
    public let authorizationEndpoint: URL
    public let tokenEndpoint: URL
    public let registrationEndpoint: URL?
}

/// Real read-only T3 OAuth 2.0 PKCE client per specification:
/// - Discovers .well-known endpoints on the validated T3 origin.
/// - Validates issuer and ensures authorization, token, and registration endpoints share the exact same origin.
/// - Rejects userinfo, queries, and fragments on auth endpoints.
/// - Prepares ephemeral loopback listener first and uses the exact same redirect URI throughout.
/// - Performs dynamic client registration as 'Developer Presence' with `token_endpoint_auth_method: none`.
/// - Requests public OAuth PKCE S256, scope `orchestration:read`, resource exact MCP URL.
/// - Verifies state, issuer, and callback path, then exchanges one-use code via strict form-encoding.
/// - Validates Bearer token type, orchestration:read scope, and positive expiration.
public final class T3OAuthClient: @unchecked Sendable {
    private let transport: HTTPTransporting
    private let injectedLoopback: OAuthLoopbackServing?
    private let authLock = NSLock()
    private var activeLoopback: OAuthLoopbackServing?
    private var generation = 0
    private let browserOpener: BrowserOpening

    public init(
        transport: HTTPTransporting = URLSessionHTTPTransport.shared,
        keychain: KeychainManaging = KeychainService.shared, // Preserved for API compatibility
        loopbackServer: OAuthLoopbackServing? = nil,
        browserOpener: BrowserOpening = WorkspaceBrowserOpener.shared
    ) {
        self.transport = transport
        self.injectedLoopback = loopbackServer
        self.browserOpener = browserOpener
    }

    public func cancel() {
        authLock.lock()
        generation += 1
        let current = activeLoopback
        activeLoopback = nil
        authLock.unlock()
        current?.cancel()
    }

    public func signIn(to endpoint: T3NormalizedEndpoint) async throws -> T3StoredCredential {
        let loopbackServer = injectedLoopback ?? BSDLoopbackServer()
        authLock.lock()
        generation += 1
        let signGeneration = generation
        let previous = activeLoopback
        activeLoopback = loopbackServer
        authLock.unlock()
        previous?.cancel()
        defer {
            loopbackServer.cancel()
            authLock.lock()
            if generation == signGeneration { activeLoopback = nil }
            authLock.unlock()
        }
        func checkActive() throws {
            try Task.checkCancellation()
            authLock.lock()
            let current = generation == signGeneration
            authLock.unlock()
            if !current { throw OAuthError.cancelled }
        }
        try checkActive()
        // 1. Discover and validate metadata on the selected origin
        let metadata = try await discoverMetadata(endpoint: endpoint)
        try checkActive()

        // 2. Prepare ephemeral loopback listener and obtain the exact callback URL BEFORE registration/browser
        let (redirectUri, _) = try loopbackServer.prepare()

        // 3. Register client with the exact same redirect URI
        let registeredClientId = try await registerClient(
            metadata: metadata,
            redirectUri: redirectUri,
            endpoint: endpoint
        )

        try checkActive()
        // 4. Prepare PKCE and State
        let codeVerifier = PKCEHelper.generateCodeVerifier()
        let codeChallenge = PKCEHelper.generateCodeChallenge(for: codeVerifier)
        let state = PKCEHelper.generateState()

        // 5. Build Authorization URL using the exact same redirect URI
        let queryItems: [URLQueryItem] = [
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "client_id", value: registeredClientId),
            URLQueryItem(name: "redirect_uri", value: redirectUri),
            URLQueryItem(name: "state", value: state),
            URLQueryItem(name: "code_challenge", value: codeChallenge),
            URLQueryItem(name: "code_challenge_method", value: "S256"),
            URLQueryItem(name: "scope", value: "orchestration:read"),
            URLQueryItem(name: "resource", value: endpoint.mcpUrl.absoluteString)
        ]

        guard let authUrl = buildAuthURL(base: metadata.authorizationEndpoint, queryItems: queryItems) else {
            loopbackServer.cancel()
            throw OAuthError.browserFailed
        }

        try checkActive()
        // 6. Launch browser
        guard browserOpener.openURL(authUrl) else {
            loopbackServer.cancel()
            throw OAuthError.browserFailed
        }

        // 7. Await callback from loopback listener
        let code = try await loopbackServer.waitForCallback(
            state: state,
            issuer: metadata.issuer,
            timeoutSeconds: 120
        )

        try checkActive()
        // 8. Exchange authorization code for token
        let credential = try await exchangeCodeForToken(
            code: code,
            redirectUri: redirectUri,
            clientId: registeredClientId,
            codeVerifier: codeVerifier,
            endpoint: endpoint,
            metadata: metadata
        )

        try checkActive()
        return credential
    }

    public func discoverMetadata(endpoint: T3NormalizedEndpoint) async throws -> T3AuthMetadata {
        // First check protected resource metadata
        var prReq = URLRequest(url: endpoint.protectedResourceMetadataUrl)
        prReq.httpMethod = "GET"
        prReq.setValue("application/json", forHTTPHeaderField: "Accept")

        var (prData, prResponse) = try await transport.send(request: prReq)
        if prResponse.statusCode == 404 {
            // Fallback to /.well-known/oauth-protected-resource
            var fallbackReq = URLRequest(url: endpoint.protectedResourceMetadataFallbackUrl)
            fallbackReq.httpMethod = "GET"
            fallbackReq.setValue("application/json", forHTTPHeaderField: "Accept")
            (prData, prResponse) = try await transport.send(request: fallbackReq)
        }

        guard prResponse.statusCode == 200,
              let prJson = try? JSONSerialization.jsonObject(with: prData) as? [String: Any] else {
            throw OAuthError.discoveryFailed("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.")
        }

        guard let resource = prJson["resource"] as? String,
              resource == endpoint.mcpUrl.absoluteString,
              let authServers = prJson["authorization_servers"] as? [String],
              authServers.contains(endpoint.origin),
              let scopes = prJson["scopes_supported"] as? [String], scopes.contains("orchestration:read") else {
            throw OAuthError.discoveryFailed("Invalid protected-resource metadata.")
        }

        // Discover authorization server metadata
        var serverReq = URLRequest(url: endpoint.authorizationServerMetadataUrl)
        serverReq.httpMethod = "GET"
        serverReq.setValue("application/json", forHTTPHeaderField: "Accept")

        let (serverData, serverResponse) = try await transport.send(request: serverReq)
        guard serverResponse.statusCode == 200 else {
            throw OAuthError.discoveryFailed("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.")
        }

        guard let json = try? JSONSerialization.jsonObject(with: serverData) as? [String: Any],
              let issuer = json["issuer"] as? String,
              let authEndpointStr = json["authorization_endpoint"] as? String,
              let tokenEndpointStr = json["token_endpoint"] as? String,
              let authEndpoint = URL(string: authEndpointStr),
              let tokenEndpoint = URL(string: tokenEndpointStr) else {
            throw OAuthError.discoveryFailed("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.")
        }

        // Validate issuer matches selected origin
        guard issuer == endpoint.origin,
              (json["code_challenge_methods_supported"] as? [String])?.contains("S256") == true else {
            throw OAuthError.discoveryFailed("Issuer does not match selected T3 origin.")
        }

        // Validate endpoints stay on the exact same selected origin and have no userinfo, query, or fragment
        guard authEndpoint.isSameOrigin(as: endpoint.origin), authEndpoint.hasNoUserinfoQueryOrFragment else {
            throw OAuthError.discoveryFailed("Authorization endpoint must stay on selected origin without query or fragment.")
        }

        guard tokenEndpoint.isSameOrigin(as: endpoint.origin), tokenEndpoint.hasNoUserinfoQueryOrFragment else {
            throw OAuthError.discoveryFailed("Token endpoint must stay on selected origin without query or fragment.")
        }

        let regEndpointStr = json["registration_endpoint"] as? String
        let regEndpoint = regEndpointStr.flatMap { URL(string: $0) }

        if let reg = regEndpoint {
            guard reg.isSameOrigin(as: endpoint.origin), reg.hasNoUserinfoQueryOrFragment else {
                throw OAuthError.discoveryFailed("Registration endpoint must stay on selected origin without query or fragment.")
            }
        }

        return T3AuthMetadata(
            issuer: issuer,
            authorizationEndpoint: authEndpoint,
            tokenEndpoint: tokenEndpoint,
            registrationEndpoint: regEndpoint
        )
    }

    private func registerClient(
        metadata: T3AuthMetadata,
        redirectUri: String,
        endpoint: T3NormalizedEndpoint
    ) async throws -> String {
        guard let regUrl = metadata.registrationEndpoint else {
            throw OAuthError.registrationFailed("Registration endpoint not available.")
        }

        var req = URLRequest(url: regUrl)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue("application/json", forHTTPHeaderField: "Accept")

        let registrationPayload: [String: Any] = [
            "client_name": "Developer Presence",
            "redirect_uris": [redirectUri],
            "grant_types": ["authorization_code"],
            "response_types": ["code"],
            "token_endpoint_auth_method": "none"
        ]

        req.httpBody = try? JSONSerialization.data(withJSONObject: registrationPayload)

        let (data, response) = try await transport.send(request: req)
        guard response.statusCode == 200 || response.statusCode == 201 else {
            throw OAuthError.registrationFailed("Client registration failed with status \(response.statusCode).")
        }

        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let clientId = json["client_id"] as? String, !clientId.isEmpty else {
            throw OAuthError.registrationFailed("Client registration did not return a valid client_id.")
        }

        return clientId
    }

    private func exchangeCodeForToken(
        code: String,
        redirectUri: String,
        clientId: String,
        codeVerifier: String,
        endpoint: T3NormalizedEndpoint,
        metadata: T3AuthMetadata
    ) async throws -> T3StoredCredential {
        var req = URLRequest(url: metadata.tokenEndpoint)
        req.httpMethod = "POST"
        req.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        req.setValue("application/json", forHTTPHeaderField: "Accept")
        req.setValue("no-store", forHTTPHeaderField: "Cache-Control")

        let params: [String: String] = [
            "grant_type": "authorization_code",
            "code": code,
            "redirect_uri": redirectUri,
            "client_id": clientId,
            "code_verifier": codeVerifier,
            "resource": endpoint.mcpUrl.absoluteString
        ]

        req.httpBody = urlFormEncode(params)

        let (data, response) = try await transport.send(request: req)
        guard response.statusCode == 200 else {
            throw OAuthError.tokenExchangeFailed("Token exchange failed with status \(response.statusCode).")
        }

        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw OAuthError.tokenExchangeFailed("Invalid token response from server.")
        }

        // Validate Bearer token type
        guard let tokenType = json["token_type"] as? String,
              tokenType.caseInsensitiveCompare("Bearer") == .orderedSame else {
            throw OAuthError.tokenExchangeFailed("Invalid token_type; must be Bearer.")
        }

        // Validate non-empty access token
        guard let accessToken = json["access_token"] as? String, !accessToken.isEmpty else {
            throw OAuthError.tokenExchangeFailed("Missing access_token in token response.")
        }

        // Validate scope contains orchestration:read
        guard let scope = json["scope"] as? String,
              scope.split(separator: " ") == ["orchestration:read"] else {
            throw OAuthError.tokenExchangeFailed("Token missing orchestration:read scope.")
        }

        // Validate positive expiry
        guard let expiresIn = json["expires_in"] as? Int, expiresIn > 0, expiresIn <= 31_536_000 else {
            throw OAuthError.tokenExchangeFailed("expires_in must be a positive integer.")
        }

        let expiresAt = Date().addingTimeInterval(TimeInterval(expiresIn))
        return T3StoredCredential(accessToken: accessToken, expiresAt: expiresAt)
    }

    /// RFC 6749 application/x-www-form-urlencoded escaping
    private func urlFormEncode(_ parameters: [String: String]) -> Data {
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-_.~"))
        let encoded = parameters.map { key, value in
            let k = key.addingPercentEncoding(withAllowedCharacters: allowed) ?? key
            let v = value.addingPercentEncoding(withAllowedCharacters: allowed) ?? value
            return "\(k)=\(v)"
        }.joined(separator: "&")
        return encoded.data(using: .utf8)!
    }

    private func buildAuthURL(base: URL, queryItems: [URLQueryItem]) -> URL? {
        guard var components = URLComponents(url: base, resolvingAgainstBaseURL: false) else { return nil }
        components.queryItems = queryItems
        return components.url
    }
}

extension URL {
    func isSameOrigin(as originString: String) -> Bool {
        guard let expected = URL(string: originString) else { return false }
        let sameScheme = self.scheme?.lowercased() == expected.scheme?.lowercased()
        let sameHost = self.host?.lowercased() == expected.host?.lowercased()
        let selfPort = self.port ?? (self.scheme == "https" ? 443 : 80)
        let expectedPort = expected.port ?? (expected.scheme == "https" ? 443 : 80)
        return sameScheme && sameHost && (selfPort == expectedPort)
    }

    var hasNoUserinfoQueryOrFragment: Bool {
        guard let components = URLComponents(url: self, resolvingAgainstBaseURL: false) else { return false }
        return components.user == nil &&
               components.password == nil &&
               components.query == nil &&
               components.fragment == nil
    }
}
