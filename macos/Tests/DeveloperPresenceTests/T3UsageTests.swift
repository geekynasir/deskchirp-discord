import XCTest
@testable import DeveloperPresenceCore

/// Scripted socket: each sent request is answered by `responder`, keyed by RPC tag.
final class ScriptedConnection: T3WebSocketConnection, @unchecked Sendable {
    private let lock = NSLock()
    private var queue: [String] = []
    private(set) var sent: [String] = []
    private(set) var closed = false
    private let responder: @Sendable (_ tag: String?, _ id: String?, _ frame: [String: Any]) -> [String]

    init(responder: @escaping @Sendable (_ tag: String?, _ id: String?, _ frame: [String: Any]) -> [String]) {
        self.responder = responder
    }

    func send(_ text: String) async throws {
        let frame = (try? JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any]) ?? [:]
        let replies = responder(frame["tag"] as? String, frame["id"] as? String, frame)
        lock.lock()
        sent.append(text)
        queue.append(contentsOf: replies)
        lock.unlock()
    }

    func receive() async throws -> String {
        lock.lock()
        defer { lock.unlock() }
        guard !queue.isEmpty else { throw T3UsageError.unreachable }
        return queue.removeFirst()
    }

    func close() {
        lock.lock()
        closed = true
        lock.unlock()
    }

    var sentFrames: [[String: Any]] {
        lock.lock()
        defer { lock.unlock() }
        return sent.compactMap { try? JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any] }
    }
}

final class MockConnector: T3WebSocketConnecting, @unchecked Sendable {
    let connection: ScriptedConnection?
    let error: Error?
    private(set) var lastRequest: URLRequest?

    init(connection: ScriptedConnection? = nil, error: Error? = nil) {
        self.connection = connection
        self.error = error
    }

    func connect(request: URLRequest, maxMessageBytes: Int) async throws -> T3WebSocketConnection {
        lastRequest = request
        if let error = error { throw error }
        guard let connection = connection else { throw T3UsageError.unreachable }
        return connection
    }
}

final class T3UsageTests: XCTestCase {
    private let origin = "https://remote.example"

    private func response(_ request: URLRequest, status: Int, body: String) -> (Data, HTTPURLResponse) {
        (Data(body.utf8), HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil,
                                          headerFields: ["Content-Type": "application/json"])!)
    }

    // MARK: Pairing

    func testPairingCodeParsing() throws {
        XCTAssertEqual(try T3UsagePairing.parseCode("  AbC123xyz ", environmentOrigin: origin), "AbC123xyz")
        XCTAssertEqual(try T3UsagePairing.parseCode("https://remote.example/pair#token=ZXCV9876", environmentOrigin: origin), "ZXCV9876")
        XCTAssertEqual(try T3UsagePairing.parseCode("https://remote.example:443/pair#token=ZXCV9876", environmentOrigin: origin), "ZXCV9876")
        XCTAssertThrowsError(try T3UsagePairing.parseCode("https://evil.example/pair#token=ZXCV9876", environmentOrigin: origin))
        XCTAssertThrowsError(try T3UsagePairing.parseCode("https://remote.example/pair?token=ZXCV9876", environmentOrigin: origin))
        XCTAssertThrowsError(try T3UsagePairing.parseCode("abc", environmentOrigin: origin))
        XCTAssertThrowsError(try T3UsagePairing.parseCode("eyJhbGciOi.access.token", environmentOrigin: origin))
        XCTAssertEqual(T3UsagePairing.keychainAccount(origin: origin), "deskchirp-usage:https://remote.example")
    }

    func testPairingExchangeStoresCredentialUnderUsageKey() async throws {
        let keychain = MockKeychain()
        let captured = RequestBox()
        let transport = MockHTTPTransport(handler: { request in
            captured.set(request)
            return self.response(request, status: 200, body: """
            {"access_token":"usage-token","token_type":"Bearer","expires_in":2592000,"scope":"orchestration:read diagnostics:read"}
            """)
        })
        let client = T3UsageClient(transport: transport, connector: MockConnector(), keychain: keychain)
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let credential = try await client.pair(origin: origin, input: "PAIRCODE42", now: now)

        XCTAssertEqual(credential.accessToken, "usage-token")
        XCTAssertEqual(credential.expiresAt, now.addingTimeInterval(2_592_000))
        let saved = try keychain.loadCredential(forService: KeychainService.defaultService, account: "deskchirp-usage:\(origin)")
        XCTAssertEqual(saved?.accessToken, "usage-token")
        XCTAssertNil(try keychain.loadCredential(forService: KeychainService.defaultService, account: origin),
                     "The usage credential never overwrites the MCP sign-in")

        let request = try XCTUnwrap(captured.request)
        XCTAssertEqual(request.url?.absoluteString, "\(origin)/oauth/token")
        XCTAssertEqual(request.httpMethod, "POST")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Content-Type"), "application/x-www-form-urlencoded")
        XCTAssertNil(request.value(forHTTPHeaderField: "DPoP"))
        XCTAssertEqual(request.timeoutInterval, 10)
        let body = String(data: request.httpBody ?? Data(), encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Atoken-exchange"))
        XCTAssertTrue(body.contains("subject_token=PAIRCODE42"))
        XCTAssertTrue(body.contains("subject_token_type=urn%3At3%3Aparams%3Aoauth%3Atoken-type%3Aenvironment-bootstrap"))
        XCTAssertTrue(body.contains("requested_token_type=urn%3Aietf%3Aparams%3Aoauth%3Atoken-type%3Aaccess_token"))
        XCTAssertTrue(body.contains("scope=orchestration%3Aread%20diagnostics%3Aread"))
        XCTAssertTrue(body.contains("client_label=Deskchirp"))
        XCTAssertTrue(body.contains("client_device_type=desktop"))
        XCTAssertTrue(body.contains("client_os=macOS"))
        XCTAssertEqual(client.permissionStatus(origin: origin, now: now), .allowed)
    }

    func testReusedCodeReturns401WithPlainMessage() async {
        let keychain = MockKeychain()
        let transport = MockHTTPTransport(handler: { request in
            self.response(request, status: 401, body: #"{"error":"invalid_grant","error_description":"PRIVATE server text"}"#)
        })
        let client = T3UsageClient(transport: transport, connector: MockConnector(), keychain: keychain)
        do {
            try await client.pair(origin: origin, input: "PAIRCODE42")
            XCTFail("A used code must fail")
        } catch let error as T3UsageError {
            XCTAssertEqual(error, .pairingCodeRejected)
            XCTAssertEqual(error.errorDescription, "That code didn’t work. Codes work once and expire after 5 minutes.")
            XCTAssertFalse(error.errorDescription?.contains("PRIVATE") ?? true)
        } catch {
            XCTFail("Unexpected error \(error)")
        }
        XCTAssertEqual(client.permissionStatus(origin: origin), .notAllowed)
    }

    func testMissingDiagnosticsScopeIsRefusedAndNotStored() async {
        let keychain = MockKeychain()
        let transport = MockHTTPTransport(handler: { request in
            self.response(request, status: 200, body: """
            {"access_token":"narrow","token_type":"Bearer","expires_in":2592000,"scope":"orchestration:read"}
            """)
        })
        let client = T3UsageClient(transport: transport, connector: MockConnector(), keychain: keychain)
        do {
            try await client.pair(origin: origin, input: "PAIRCODE42")
            XCTFail("A token without diagnostics:read must be refused")
        } catch let error as T3UsageError {
            XCTAssertEqual(error, .missingDiagnosticsScope)
        } catch {
            XCTFail("Unexpected error \(error)")
        }
        XCTAssertNil(try keychain.loadCredential(forService: KeychainService.defaultService, account: "deskchirp-usage:\(origin)"))
    }

    // MARK: Frames

    func testRequestFrameEncoding() throws {
        let frame = try T3RpcClientFrame.makeRequest(id: "1", tag: "server.getUsageSummary", payload: [
            "sinceDay": "2026-11-01", "untilDay": "2026-11-01", "timeZone": "Asia/Karachi", "awaitRefresh": true
        ])
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(try frame.encodedText().utf8)) as? [String: Any])
        XCTAssertEqual(object["_tag"] as? String, "Request")
        XCTAssertEqual(object["id"] as? String, "1")
        XCTAssertEqual(object["tag"] as? String, "server.getUsageSummary")
        XCTAssertEqual((object["headers"] as? [Any])?.count, 0)
        let payload = try XCTUnwrap(object["payload"] as? [String: Any])
        XCTAssertEqual(payload["timeZone"] as? String, "Asia/Karachi")
        XCTAssertEqual(payload["awaitRefresh"] as? Bool, true)

        let ack = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(try T3RpcClientFrame.ack(requestId: "7").encodedText().utf8)) as? [String: Any])
        XCTAssertEqual(ack["_tag"] as? String, "Ack")
        XCTAssertEqual(ack["requestId"] as? String, "7")
        XCTAssertEqual(try T3RpcClientFrame.ping.encodedText(), #"{"_tag":"Ping"}"#)
    }

    func testServerFrameDecodingIncludingFailureDieAndChunk() throws {
        let success = try T3RpcServerFrame.decode(#"{"_tag":"Exit","requestId":"1","exit":{"_tag":"Success","value":{"contractVersion":6}}}"#)
        guard case .success(let id, let value)? = success.first else { return XCTFail("Expected success") }
        XCTAssertEqual(id, "1")
        XCTAssertEqual((T3RpcServerFrame.successObject(value) as? [String: Any])?["contractVersion"] as? Int, 6)

        let failure = try T3RpcServerFrame.decode(#"{"_tag":"Exit","requestId":"1","exit":{"_tag":"Failure","cause":[{"_tag":"Fail","error":{"_tag":"EnvironmentAuthorizationError","requiredPermission":"diagnostics:read"}}]}}"#)
        XCTAssertEqual(failure, [.failure(requestId: "1")])

        let die = try T3RpcServerFrame.decode(#"{"_tag":"Exit","requestId":"2","exit":{"_tag":"Failure","cause":{"_tag":"Die","defect":"PRIVATE stack"}}}"#)
        XCTAssertEqual(die, [.die(requestId: "2")])

        let batch = try T3RpcServerFrame.decode(#"[{"_tag":"Chunk","requestId":"3","values":[]},{"_tag":"Pong"},{"_tag":"Ping"},{"_tag":"Something"}]"#)
        XCTAssertEqual(batch, [.chunk(requestId: "3"), .pong, .ping, .other])

        XCTAssertThrowsError(try T3RpcServerFrame.decode(String(repeating: " ", count: 64) + "{}", maxBytes: 16))
        XCTAssertThrowsError(try T3RpcServerFrame.decode("not json"))
    }

    func testRpcSessionAnswersPingAndEndsOnChunk() async throws {
        let connection = ScriptedConnection { tag, id, _ in
            guard let id = id else { return [] }
            if tag == "first" {
                return [#"{"_tag":"Ping"}"#, #"{"_tag":"Exit","requestId":"\#(id)","exit":{"_tag":"Success","value":42}}"#]
            }
            return [#"{"_tag":"Chunk","requestId":"\#(id)","values":[1]}"#]
        }
        let session = T3RpcSession(connection: connection)
        let value = try await session.call(tag: "first", payload: [:])
        XCTAssertEqual(value as? Int, 42)
        do {
            _ = try await session.call(tag: "second", payload: [:])
            XCTFail("A Chunk must end the poll")
        } catch let error as T3UsageError {
            XCTAssertEqual(error, .unexpectedChunk)
        }
        let tags = connection.sentFrames.map { $0["_tag"] as? String ?? "" }
        XCTAssertEqual(tags, ["Request", "Pong", "Request", "Ack"])
        XCTAssertEqual(connection.sentFrames[2]["id"] as? String, "2", "Request IDs are increasing decimal strings")
        XCTAssertEqual(connection.sentFrames[3]["requestId"] as? String, "2")
    }

    func testFailureMakesValueUnavailableWithoutServerText() async {
        let connection = ScriptedConnection { _, id, _ in
            guard let id = id else { return [] }
            return [#"{"_tag":"Exit","requestId":"\#(id)","exit":{"_tag":"Failure","cause":[{"_tag":"Fail","error":{"message":"PRIVATE"}}]}}"#]
        }
        let session = T3RpcSession(connection: connection)
        do {
            _ = try await session.call(tag: "server.getUsageSummary", payload: [:])
            XCTFail("Failure must throw")
        } catch let error as T3UsageError {
            XCTAssertEqual(error, .valueUnavailable)
            XCTAssertFalse(error.errorDescription?.contains("PRIVATE") ?? true)
        } catch {
            XCTFail("Unexpected error \(error)")
        }
    }

    // MARK: Usage rules

    private let summaryJSON = """
    {"contractVersion":6,
     "buckets":[
       {"totals":{"uncachedInputTokens":400000,"cachedInputTokens":300000,"cacheCreationTokens":42000,"outputTokens":100000,"reasoningTokens":90000},"costUsd":1.25,"unpricedRecords":0},
       {"totals":{"uncachedInputTokens":1,"cachedInputTokens":0,"cacheCreationTokens":0,"outputTokens":0,"reasoningTokens":0},"costUsd":0.5,"unpricedRecords":0},
       {"totals":"broken"}
     ],
     "sources":[{"status":"ok","sourcePath":"/Users/PRIVATE/.claude"}],
     "pricing":{"status":"fresh"}}
    """

    private func summary(_ text: String) throws -> T3UsageValue {
        try T3UsageDecoding.summary(try XCTUnwrap(JSONSerialization.jsonObject(with: Data(text.utf8))))
    }

    func testSummarySumsBucketsAndSkipsBrokenOnes() throws {
        let value = try summary(summaryJSON)
        XCTAssertEqual(value.tokens?.input, 742_001)
        XCTAssertEqual(value.tokens?.output, 100_000, "Reasoning is part of output and never added")
        XCTAssertEqual(value.tokens?.total, 842_001)
        XCTAssertFalse(value.tokensPartial)
        XCTAssertFalse(value.costPartial)
        XCTAssertEqual(value.costUsd ?? 0, 1.75, accuracy: 0.0001)
    }

    func testSummaryPartialRules() throws {
        let sourcePartial = try summary(summaryJSON.replacingOccurrences(of: #""status":"ok""#, with: #""status":"refreshing""#))
        XCTAssertTrue(sourcePartial.tokensPartial, "A source not ok makes tokens partial")
        XCTAssertTrue(sourcePartial.costPartial)

        let unpriced = try summary(summaryJSON.replacingOccurrences(of: #""unpricedRecords":0},"#, with: #""unpricedRecords":3},"#))
        XCTAssertFalse(unpriced.tokensPartial, "Unpriced records affect cost only")
        XCTAssertTrue(unpriced.costPartial)

        let pricing = try summary(summaryJSON.replacingOccurrences(of: #""status":"fresh""#, with: #""status":"unavailable""#))
        XCTAssertFalse(pricing.tokensPartial)
        XCTAssertTrue(pricing.costPartial)

        let cached = try summary(summaryJSON.replacingOccurrences(of: #""status":"fresh""#, with: #""status":"cached""#))
        XCTAssertFalse(cached.costPartial)

        let missingCost = try summary(summaryJSON.replacingOccurrences(of: #""costUsd":0.5,"#, with: ""))
        XCTAssertFalse(missingCost.tokensPartial, "A bucket without a cost still counts tokens")
        XCTAssertTrue(missingCost.costPartial, "A bucket without a cost makes the estimate partial")

        XCTAssertThrowsError(try summary(summaryJSON.replacingOccurrences(of: #""contractVersion":6"#, with: #""contractVersion":7"#)))
    }

    func testPartialUsageNeverReachesTheCard() throws {
        var settings = PresenceSettings(enableSharing: true, showT3Activity: true, t3Environments: [])
        settings.cardStyle = .usage
        settings.shareEstimatedCost = true
        let now = Date()
        let snapshot = T3CombinedSnapshot(environments: [T3EnvironmentSnapshot(
            environmentId: "e", observedAt: now, providers: [T3ProviderInfo(providerInstanceId: "c", driverKind: "codex")],
            threads: [T3ThreadItem(threadId: "t", providerInstanceId: "c", status: "running")])])
        func state(_ value: T3UsageValue) -> String? {
            PresenceCardFormatter.format(settings: settings, snapshot: snapshot,
                                         usage: T3UsageSnapshot(observedAt: now, today: value),
                                         timer: PresenceTimerState(), shownApp: .t3Code, now: now).activity?.state
        }
        let unpriced = try summary(summaryJSON.replacingOccurrences(of: #""unpricedRecords":0},"#, with: #""unpricedRecords":3},"#))
        XCTAssertEqual(state(unpriced), "842K tokens today", "Partial cost hides only the cost suffix")
        XCTAssertEqual(state(try summary(summaryJSON)), "842K tokens today · ~$1.75 API est.")
        let partial = try summary(summaryJSON.replacingOccurrences(of: #""status":"ok""#, with: #""status":"failed""#))
        XCTAssertEqual(state(partial), "1 task")
    }

    func testProjectionTokensSessionAndContext() throws {
        let projectionJSON = """
        {"activeProviderThreadId":"pt-2",
         "messages":[{"text":"PRIVATE conversation"}],
         "providerThreads":[{"id":"pt-1","contextUsage":{"usedTokens":10,"maxTokens":100}},
                            {"id":"pt-2","contextUsage":{"usedTokens":50000,"maxTokens":200000}}],
         "providerTurns":[
           {"providerThreadId":"pt-2","startedAt":"2026-11-01T10:00:00Z",
            "tokenUsage":{"usedTokens":90000,"maxTokens":200000,"updatedAt":"2026-11-01T10:05:00Z"},
            "turnTokenUsage":{"usageStatus":"complete","inputTokens":1000,"cachedInputTokens":600,"cacheCreationTokens":100,"outputTokens":300,"reasoningTokens":200}},
           {"providerThreadId":"pt-2","startedAt":"2026-11-01T11:00:00Z",
            "tokenUsage":{"usedTokens":124999,"maxTokens":200000,"updatedAt":"2026-11-01T11:05:00Z"},
            "turnTokenUsage":{"usageStatus":"complete","inputTokens":2000,"cachedInputTokens":0,"outputTokens":500}},
           {"providerThreadId":"pt-1","startedAt":"2026-11-01T12:00:00Z",
            "tokenUsage":{"usedTokens":99,"maxTokens":100,"updatedAt":"2026-11-01T12:30:00Z"},
            "turnTokenUsage":{"usageStatus":"complete","inputTokens":10,"outputTokens":5}}
         ]}
        """
        let projection = try T3UsageDecoding.projection(try XCTUnwrap(JSONSerialization.jsonObject(with: Data(projectionJSON.utf8))))
        let pinned = T3UsageDecoding.tokens(projection)
        XCTAssertFalse(pinned.tokensPartial)
        XCTAssertEqual(pinned.tokens?.input, 3010, "Input already includes cache reads and writes")
        XCTAssertEqual(pinned.tokens?.uncachedInputTokens, 300 + 2000 + 10)
        XCTAssertEqual(pinned.tokens?.output, 805)

        let sessionStart = PresenceCardFormatter.parseDate("2026-11-01T10:30:00Z")
        let session = T3UsageDecoding.tokens(projection, since: sessionStart)
        XCTAssertEqual(session.tokens?.total, 2500 + 15)

        let context = try XCTUnwrap(T3UsageDecoding.context(projection))
        XCTAssertEqual(context.contextPercent, 62, "Newest tokenUsage on the active provider thread wins")

        let partialJSON = projectionJSON.replacingOccurrences(of: #""usageStatus":"complete","inputTokens":10"#, with: #""usageStatus":"partial","inputTokens":10"#)
        let partialProjection = try T3UsageDecoding.projection(try XCTUnwrap(JSONSerialization.jsonObject(with: Data(partialJSON.utf8))))
        XCTAssertTrue(T3UsageDecoding.tokens(partialProjection).tokensPartial)

        let noMax = """
        {"activeProviderThreadId":"pt","providerThreads":[{"id":"pt","contextUsage":{"usedTokens":5,"maxTokens":0}}],"providerTurns":[]}
        """
        let noMaxProjection = try T3UsageDecoding.projection(try XCTUnwrap(JSONSerialization.jsonObject(with: Data(noMax.utf8))))
        XCTAssertNil(T3UsageDecoding.context(noMaxProjection))
    }

    func testLocalDayAndWebSocketUrl() throws {
        let karachi = try XCTUnwrap(TimeZone(identifier: "Asia/Karachi"))
        // 2026-10-31T20:30:00Z is already 1 November in Karachi.
        let date = try XCTUnwrap(PresenceCardFormatter.parseDate("2026-10-31T20:30:00Z"))
        XCTAssertEqual(T3UsageDecoding.localDay(date, timeZone: karachi), "2026-11-01")
        XCTAssertEqual(T3UsageClient.webSocketURL(origin: "https://remote.example:3773", appVersion: "1.2")?.absoluteString,
                       "wss://remote.example:3773/ws?orchestrationProtocol=2&clientSurface=deskchirp&clientAppVersion=1.2")
        XCTAssertEqual(T3UsageClient.webSocketURL(origin: "http://localhost:3773", appVersion: "1.2")?.scheme, "ws")
    }

    // MARK: Full poll

    private func pollingClient(keychain: MockKeychain, connector: MockConnector, environmentVersion: Int = 2) throws -> T3UsageClient {
        try keychain.saveCredential(T3StoredCredential(accessToken: "usage-token", expiresAt: Date().addingTimeInterval(3600)),
                                    forService: KeychainService.defaultService, account: "deskchirp-usage:\(origin)")
        let transport = MockHTTPTransport(handler: { request in
            guard request.url?.path == "/.well-known/t3/environment" else { throw HTTPTransportError.invalidResponse }
            XCTAssertNil(request.value(forHTTPHeaderField: "Authorization"))
            return self.response(request, status: 200, body: #"{"orchestrationProtocolVersion":\#(environmentVersion)}"#)
        })
        return T3UsageClient(transport: transport, connector: connector, keychain: keychain,
                             timeZone: TimeZone(identifier: "Asia/Karachi")!)
    }

    func testPollReadsTodayOverOneSocketWithBearerToken() async throws {
        let summary = summaryJSON
        let connection = ScriptedConnection { tag, id, _ in
            guard let id = id, tag == "server.getUsageSummary" else { return [] }
            return [#"{"_tag":"Exit","requestId":"\#(id)","exit":{"_tag":"Success","value":\#(summary)}}"#]
        }
        let connector = MockConnector(connection: connection)
        let keychain = MockKeychain()
        let client = try pollingClient(keychain: keychain, connector: connector)
        let result = try await client.poll(origin: origin, plan: T3UsagePollPlan(needsToday: true))

        XCTAssertEqual(result.today?.tokens?.total, 842_001)
        XCTAssertTrue(connection.closed, "The socket is closed after the answers arrive")
        let request = try XCTUnwrap(connector.lastRequest)
        XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer usage-token")
        XCTAssertEqual(request.url?.path, "/ws")
        XCTAssertNil(request.value(forHTTPHeaderField: "Sec-WebSocket-Protocol"))
        let payload = try XCTUnwrap(connection.sentFrames.first?["payload"] as? [String: Any])
        XCTAssertEqual(payload["timeZone"] as? String, "Asia/Karachi")
        XCTAssertEqual(payload["sinceDay"] as? String, payload["untilDay"] as? String)
    }

    func testIncompatibleVersionsAndExpiredTokens() async throws {
        let keychain = MockKeychain()
        let oldServer = try pollingClient(keychain: keychain, connector: MockConnector(), environmentVersion: 1)
        do {
            _ = try await oldServer.poll(origin: origin, plan: T3UsagePollPlan(needsToday: true))
            XCTFail("Protocol 1 must be refused")
        } catch let error as T3UsageError {
            XCTAssertEqual(error, .unsupportedVersion)
        }

        let upgrade = try pollingClient(keychain: keychain, connector: MockConnector(error: T3UsageError.unsupportedVersion))
        do {
            _ = try await upgrade.poll(origin: origin, plan: T3UsagePollPlan(needsToday: true))
            XCTFail("HTTP 426 must be refused")
        } catch let error as T3UsageError {
            XCTAssertEqual(error.errorDescription, "This T3 version isn’t supported for token usage.")
        }

        let rejected = try pollingClient(keychain: keychain, connector: MockConnector(error: T3UsageError.pairAgain))
        do {
            _ = try await rejected.poll(origin: origin, plan: T3UsagePollPlan(needsToday: true))
            XCTFail("401 must ask to pair again")
        } catch let error as T3UsageError {
            XCTAssertEqual(error, .pairAgain)
        }
        XCTAssertEqual(rejected.permissionStatus(origin: origin), .notAllowed, "A rejected token is deleted")
    }

    func testCombinedTodayIsPartialWhenAnEnvironmentFails() async throws {
        let summary = summaryJSON
        let connection = ScriptedConnection { tag, id, _ in
            guard let id = id, tag == "server.getUsageSummary" else { return [] }
            return [#"{"_tag":"Exit","requestId":"\#(id)","exit":{"_tag":"Success","value":\#(summary)}}"#]
        }
        let keychain = MockKeychain()
        let client = try pollingClient(keychain: keychain, connector: MockConnector(connection: connection))
        let service = T3UsageService(client: client)
        let requests = [
            T3UsageEnvironmentRequest(environmentId: "a", origin: origin, plan: T3UsagePollPlan(needsToday: true)),
            // No credential for this origin: the environment fails.
            T3UsageEnvironmentRequest(environmentId: "b", origin: "https://other.example", plan: T3UsagePollPlan(needsToday: true))
        ]
        let snapshot = await service.poll(requests: requests, pinnedTaskKey: nil, sessionStart: nil)
        let today = try XCTUnwrap(snapshot?.today)
        XCTAssertTrue(today.tokensPartial)
        XCTAssertTrue(today.costPartial)
        XCTAssertEqual(service.status(environmentId: "b", origin: "https://other.example"), .pairAgain)
    }

    /// Review fix 6: a slow environment must not hold up a healthy one. The slow environment only answers
    /// after the fast one has connected, which can only happen when the polls run concurrently.
    func testUsagePollsEnvironmentsConcurrently() async throws {
        let summary = summaryJSON
        func answering() -> ScriptedConnection {
            ScriptedConnection { tag, id, _ in
                guard let id = id, tag == "server.getUsageSummary" else { return [] }
                return [#"{"_tag":"Exit","requestId":"\#(id)","exit":{"_tag":"Success","value":\#(summary)}}"#]
            }
        }
        let connector = HostGatedConnector(slowHost: "slow.example", fast: answering(), slow: answering())
        let keychain = MockKeychain()
        for host in ["slow.example", "fast.example"] {
            try keychain.saveCredential(T3StoredCredential(accessToken: "t", expiresAt: Date().addingTimeInterval(3600)),
                                        forService: KeychainService.defaultService, account: "deskchirp-usage:https://\(host)")
        }
        let transport = MockHTTPTransport(handler: { request in
            (Data(#"{"orchestrationProtocolVersion":2}"#.utf8),
             HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!)
        })
        let service = T3UsageService(client: T3UsageClient(transport: transport, connector: connector, keychain: keychain))
        let requests = [
            T3UsageEnvironmentRequest(environmentId: "slow", origin: "https://slow.example", plan: T3UsagePollPlan(needsToday: true)),
            T3UsageEnvironmentRequest(environmentId: "fast", origin: "https://fast.example", plan: T3UsagePollPlan(needsToday: true))
        ]
        let snapshot = await service.poll(requests: requests, pinnedTaskKey: nil, sessionStart: nil)
        let today = try XCTUnwrap(snapshot?.today)
        XCTAssertFalse(today.tokensPartial, "Both environments answered")
        XCTAssertEqual(today.tokens?.total, 2 * 842_001)
    }

    /// Review fix 7: removing an environment while its pairing exchange is pending must not let the
    /// exchange store the credential afterwards.
    func testRemovalDuringPendingPairingWins() async throws {
        let keychain = MockKeychain()
        let entered = Flag()
        let gate = DispatchSemaphore(value: 0)
        let transport = MockHTTPTransport(handler: { request in
            entered.set()
            _ = gate.wait(timeout: .now() + 5)
            return (Data(#"{"access_token":"late","token_type":"Bearer","expires_in":600,"scope":"orchestration:read diagnostics:read"}"#.utf8),
                    HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!)
        })
        let service = T3UsageService(client: T3UsageClient(transport: transport, connector: MockConnector(), keychain: keychain))
        let origin = self.origin
        let pairing = Task { try await service.pair(environmentId: "e", origin: origin, input: "PAIRCODE42") }

        for _ in 0..<250 where !entered.isSet { try await Task.sleep(nanoseconds: 20_000_000) }
        XCTAssertTrue(entered.isSet)
        try service.removeAccess(environmentId: "e", origin: origin)
        gate.signal()

        do {
            try await pairing.value
            XCTFail("The superseded pairing must not succeed")
        } catch let error as T3UsageError {
            XCTAssertEqual(error, .pairingCancelled)
        }
        XCTAssertNil(try keychain.loadCredential(forService: KeychainService.defaultService, account: "deskchirp-usage:\(origin)"))
        XCTAssertEqual(service.status(environmentId: "e", origin: origin), .notAllowed)

        // A fresh pairing after removal works again.
        gate.signal()
        try await service.pair(environmentId: "e", origin: origin, input: "PAIRCODE43")
        XCTAssertEqual(service.status(environmentId: "e", origin: origin), .allowed)
    }

    func testPlansCapProjectionsAndApplyScope() {
        var settings = PresenceSettings(t3Environments: [])
        settings.cardStyle = .usage
        settings.usageScope = .session
        let env = T3Environment(id: "e", address: "https://remote.example/mcp", label: "Remote")
        var work: [T3WorkThread] = []
        for index in 0..<30 {
            work.append(T3WorkThread(environmentId: "e", thread: T3ThreadItem(threadId: "t\(index)", providerInstanceId: "c", status: "running"), toolName: "Codex"))
        }
        work.append(T3WorkThread(environmentId: "e", thread: T3ThreadItem(threadId: "done", providerInstanceId: "c", status: "completed"), toolName: "Codex"))
        let requests = T3UsageService.plans(settings: settings, work: work, allowedEnvironments: [env], sessionStart: Date())
        XCTAssertEqual(requests.first?.plan.sessionThreadIds.count, 24)
        XCTAssertEqual(requests.first?.plan.sessionOverflow, true)
        XCTAssertFalse(requests.first?.plan.sessionThreadIds.contains("done") ?? true)

        settings.usageMetric = .context
        settings.pinnedTask = "e/t3"
        let pinned = T3UsageService.plans(settings: settings, work: work, allowedEnvironments: [env], sessionStart: nil)
        XCTAssertEqual(pinned.first?.plan.pinnedThreadId, "t3")
        XCTAssertEqual(pinned.first?.plan.needsToday, false)
    }
}

final class RequestBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: URLRequest?
    func set(_ request: URLRequest) { lock.lock(); stored = request; lock.unlock() }
    var request: URLRequest? { lock.lock(); defer { lock.unlock() }; return stored }
}

/// Connects `slowHost` only once the other host has connected (or gives up after 2 s).
final class HostGatedConnector: T3WebSocketConnecting, @unchecked Sendable {
    private let slowHost: String
    private let fast: ScriptedConnection
    private let slow: ScriptedConnection
    private let fastConnected = Flag()

    init(slowHost: String, fast: ScriptedConnection, slow: ScriptedConnection) {
        self.slowHost = slowHost
        self.fast = fast
        self.slow = slow
    }

    func connect(request: URLRequest, maxMessageBytes: Int) async throws -> T3WebSocketConnection {
        guard request.url?.host == slowHost else {
            fastConnected.set()
            return fast
        }
        for _ in 0..<100 where !fastConnected.isSet {
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        guard fastConnected.isSet else { throw T3UsageError.unreachable }
        return slow
    }
}
