import XCTest
@testable import DeveloperPresenceCore

final class T3McpClientTests: XCTestCase {
    private var mockTransport: MockHTTPTransport!
    private var client: T3McpClient!

    override func setUp() {
        super.setUp()
        mockTransport = MockHTTPTransport()
        client = T3McpClient(
            transport: mockTransport,
            maxResponseBytes: 100_000,
            maxPages: 3,
            maxTotalRows: 250
        )
    }

    func testMcpHeadersAndSessionIdPreserved() async throws {
        let endpoint = try T3UrlValidator.validateAndNormalize("http://localhost:3773")
        var seenHeaders: [[String: String]] = []

        mockTransport.handler = { req in
            seenHeaders.append(req.allHTTPHeaderFields ?? [:])
            guard let bodyData = req.httpBody,
                  let json = try? JSONSerialization.jsonObject(with: bodyData) as? [String: Any],
                  let method = json["method"] as? String else {
                throw HTTPTransportError.invalidResponse
            }

            if method == "initialize" {
                let respJson = """
                {
                  "jsonrpc": "2.0",
                  "id": 1,
                  "result": {
                    "protocolVersion": "2025-03-26",
                    "capabilities": {},
                    "serverInfo": { "name": "t3-server", "version": "1.0.0" }
                  }
                }
                """
                let headers = [
                    "Content-Type": "application/json",
                    "mcp-session-id": "session-xyz-123",
                    "mcp-protocol-version": "2025-03-26"
                ]
                return (respJson.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: headers)!)
            } else if method == "notifications/initialized" {
                return (Data(), HTTPURLResponse(url: req.url!, statusCode: 202, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if method == "tools/call" {
                let params = json["params"] as? [String: Any] ?? [:]
                let toolName = params["name"] as? String ?? ""

                if toolName == "orchestrator_capabilities" {
                    let resp = """
                    {
                      "jsonrpc": "2.0",
                      "id": 2,
                      "result": {
                        "structuredContent": {
                          "providers": [
                            { "providerInstanceId": "claude-inst", "driverKind": "claudeAgent" },
                            { "providerInstanceId": "ag-inst", "driverKind": "antigravity" }
                          ]
                        }
                      }
                    }
                    """
                    return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
                } else if toolName == "t3_thread_list" {
                    let resp = """
                    {
                      "jsonrpc": "2.0",
                      "id": 3,
                      "result": {
                        "threads": [
                          { "threadId": "t1", "providerInstanceId": "claude-inst", "status": "running", "relationshipToParent": null },
                          { "threadId": "t2", "providerInstanceId": "ag-inst", "status": "running", "relationshipToParent": "subagent" }
                        ],
                        "nextCursor": null, "total": 2
                      }
                    }
                    """
                    return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
                }
            }
            throw HTTPTransportError.invalidResponse
        }

        let (providers, threads) = try await client.poll(endpoint: endpoint, token: "test-token")

        XCTAssertEqual(providers.count, 2)
        XCTAssertEqual(threads.count, 2)

        // Verify session header was included after initialize
        let laterCalls = seenHeaders.dropFirst()
        for headers in laterCalls {
            XCTAssertEqual(headers["mcp-session-id"], "session-xyz-123")
            XCTAssertEqual(headers["mcp-protocol-version"], "2025-03-26")
            XCTAssertEqual(headers["Authorization"], "Bearer test-token")
        }
    }

    func testSseResponseStreamParsingWithoutWaitingEOF() async throws {
        let endpoint = try T3UrlValidator.validateAndNormalize("http://localhost:3773")

        mockTransport.handler = { req in
            guard let bodyData = req.httpBody,
                  let json = try? JSONSerialization.jsonObject(with: bodyData) as? [String: Any],
                  let method = json["method"] as? String else {
                throw HTTPTransportError.invalidResponse
            }

            if method == "initialize" {
                let resp = """
                { "jsonrpc": "2.0", "id": 1, "result": { "protocolVersion": "2025-03-26" } }
                """
                return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if method == "notifications/initialized" {
                return (Data(), HTTPURLResponse(url: req.url!, statusCode: 202, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if method == "tools/call" {
                let params = json["params"] as? [String: Any] ?? [:]
                let toolName = params["name"] as? String ?? ""

                if toolName == "orchestrator_capabilities" {
                    // Send as SSE event stream!
                    let sseStream = """
                    event: message
                    data: {"jsonrpc":"2.0","id":2,"result":{"structuredContent":{"providers":[{"providerInstanceId":"inst-1","driverKind":"codex"}]}}}

                    """
                    return (sseStream.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "text/event-stream"])!)
                } else if toolName == "t3_thread_list" {
                    let sseStream = """
                    event: ping
                    data: {"type":"heartbeat"}

                    event: message
                    data: {"jsonrpc":"2.0","id":3,"result":{"threads":[{"threadId":"t-1","providerInstanceId":"inst-1","status":"running","relationshipToParent":null}],"nextCursor":null, "total": 1}}

                    """
                    return (sseStream.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "text/event-stream"])!)
                }
            }
            throw HTTPTransportError.invalidResponse
        }

        let (providers, threads) = try await client.poll(endpoint: endpoint, token: "test-token")
        XCTAssertEqual(providers.count, 1)
        XCTAssertEqual(providers.first?.driverKind, "codex")
        XCTAssertEqual(threads.count, 1)
        XCTAssertEqual(threads.first?.threadId, "t-1")
    }

    func testPaginationAndDeduplication() async throws {
        let endpoint = try T3UrlValidator.validateAndNormalize("http://localhost:3773")
        var threadListCallCount = 0

        mockTransport.handler = { req in
            guard let bodyData = req.httpBody,
                  let json = try? JSONSerialization.jsonObject(with: bodyData) as? [String: Any],
                  let method = json["method"] as? String else {
                throw HTTPTransportError.invalidResponse
            }

            if method == "initialize" {
                let resp = """
                { "jsonrpc": "2.0", "id": 1, "result": { "protocolVersion": "2025-03-26" } }
                """
                return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if method == "notifications/initialized" {
                return (Data(), HTTPURLResponse(url: req.url!, statusCode: 202, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if method == "tools/call" {
                let params = json["params"] as? [String: Any] ?? [:]
                let toolName = params["name"] as? String ?? ""

                if toolName == "orchestrator_capabilities" {
                    let resp = """
                    { "jsonrpc": "2.0", "id": 2, "result": { "providers": [] } }
                    """
                    return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
                } else if toolName == "t3_thread_list" {
                    threadListCallCount += 1
                    let reqId = json["id"] as? Int ?? 3
                    if threadListCallCount == 1 {
                        // Page 1 returns t1 and duplicate t1, with nextCursor 100
                        let resp = """
                        {
                          "jsonrpc": "2.0",
                          "id": \(reqId),
                          "result": {
                            "threads": [
                              { "threadId": "t1", "providerInstanceId": "p1", "status": "running" },
                              { "threadId": "t1", "providerInstanceId": "p1", "status": "running" },
                              { "threadId": "t2", "providerInstanceId": "p1", "status": "running" }
                            ],
                            "nextCursor": 100, "total": 4
                          }
                        }
                        """
                        return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
                    } else {
                        // Page 2 returns t3 and finishes (nextCursor null)
                        let resp = """
                        {
                          "jsonrpc": "2.0",
                          "id": \(reqId),
                          "result": {
                            "threads": [
                              { "threadId": "t3", "providerInstanceId": "p1", "status": "running" }
                            ],
                            "nextCursor": null, "total": 4
                          }
                        }
                        """
                        return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
                    }
                }
            }
            throw HTTPTransportError.invalidResponse
        }

        let (_, threads) = try await client.poll(endpoint: endpoint, token: "tok")
        XCTAssertEqual(threadListCallCount, 2, "Should paginate through 2 pages")
        // t1 was in page 1 twice, deduplicated to 1! So total unique threads = 3 (t1, t2, t3)
        XCTAssertEqual(threads.count, 3)
        XCTAssertEqual(threads.map { $0.threadId }, ["t1", "t2", "t3"])
    }

    func testPaginationCappingRejectsIncompleteSnapshots() async throws {
        let endpoint = try T3UrlValidator.validateAndNormalize("http://localhost:3773")

        mockTransport.handler = { req in
            guard let bodyData = req.httpBody,
                  let json = try? JSONSerialization.jsonObject(with: bodyData) as? [String: Any],
                  let method = json["method"] as? String else {
                throw HTTPTransportError.invalidResponse
            }

            if method == "initialize" {
                return ("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2025-06-18\"}}".data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if method == "notifications/initialized" {
                return (Data(), HTTPURLResponse(url: req.url!, statusCode: 202, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if method == "tools/call" {
                let params = json["params"] as? [String: Any] ?? [:]
                let toolName = params["name"] as? String ?? ""
                let reqId = json["id"] as? Int ?? 2

                if toolName == "orchestrator_capabilities" {
                    return ("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"providers\":[]}}".data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
                } else if toolName == "t3_thread_list" {
                    // Infinite pagination: always returns nextCursor = 999
                    let resp = """
                    {
                      "jsonrpc": "2.0",
                      "id": \(reqId),
                      "result": {
                        "threads": [ { "threadId": "t-\(reqId)", "providerInstanceId": "p", "status": "running" } ],
                        "nextCursor": 999, "total": 1000
                      }
                    }
                    """
                    return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
                }
            }
            throw HTTPTransportError.invalidResponse
        }

        do {
            _ = try await client.poll(endpoint: endpoint, token: "tok")
            XCTFail("Should have thrown snapshotCapped")
        } catch let err as McpClientError {
            XCTAssertEqual(err, .snapshotCapped)
        }
    }

    func testSecondPollUsesExistingSessionWithoutReinitializing() async throws {
        let endpoint = try T3UrlValidator.validateAndNormalize("http://localhost:3773")
        var methodsCalled: [String] = []
        var sessionHeadersSeen: [String?] = []

        mockTransport.handler = { req in
            let sessionHeader = req.value(forHTTPHeaderField: "mcp-session-id")
            sessionHeadersSeen.append(sessionHeader)

            guard let bodyData = req.httpBody,
                  let json = try? JSONSerialization.jsonObject(with: bodyData) as? [String: Any],
                  let method = json["method"] as? String else {
                throw HTTPTransportError.invalidResponse
            }
            methodsCalled.append(method)

            if method == "initialize" {
                XCTAssertNil(sessionHeader, "initialize request must never have mcp-session-id attached")
                let respJson = """
                {
                  "jsonrpc": "2.0",
                  "id": 1,
                  "result": { "protocolVersion": "2025-06-18" }
                }
                """
                return (respJson.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: [
                    "Content-Type": "application/json",
                    "mcp-session-id": "sess-active-456"
                ])!)
            } else if method == "notifications/initialized" {
                return (Data(), HTTPURLResponse(url: req.url!, statusCode: 202, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if method == "tools/call" {
                let params = json["params"] as? [String: Any] ?? [:]
                let toolName = params["name"] as? String ?? ""
                let reqId = json["id"] as? Int ?? 2

                if toolName == "orchestrator_capabilities" {
                    let resp = """
                    { "jsonrpc": "2.0", "id": \(reqId), "result": { "providers": [] } }
                    """
                    return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
                } else if toolName == "t3_thread_list" {
                    let resp = """
                    { "jsonrpc": "2.0", "id": \(reqId), "result": { "threads": [], "nextCursor": null, "total": 0 } }
                    """
                    return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
                }
            }
            throw HTTPTransportError.invalidResponse
        }

        // Poll 1: Initial poll must initialize and capture session
        _ = try await client.poll(endpoint: endpoint, token: "tok-1")
        XCTAssertEqual(methodsCalled, ["initialize", "notifications/initialized", "tools/call", "tools/call"])
        XCTAssertEqual(sessionHeadersSeen[0], nil)
        XCTAssertEqual(sessionHeadersSeen[1], "sess-active-456")
        XCTAssertEqual(sessionHeadersSeen[2], "sess-active-456")
        XCTAssertEqual(sessionHeadersSeen[3], "sess-active-456")

        // Poll 2: Second poll with same endpoint and token must NOT re-initialize!
        methodsCalled.removeAll()
        sessionHeadersSeen.removeAll()

        _ = try await client.poll(endpoint: endpoint, token: "tok-1")
        XCTAssertEqual(methodsCalled, ["tools/call", "tools/call"], "Second poll must not call initialize or notifications/initialized")
        XCTAssertEqual(sessionHeadersSeen, ["sess-active-456", "sess-active-456"], "Tool calls in second poll must attach existing session id")
    }

    func testSessionResetOn404ReinitializesWithoutSessionHeader() async throws {
        let endpoint = try T3UrlValidator.validateAndNormalize("http://localhost:3773")
        var shouldFailWith404 = false

        mockTransport.handler = { req in
            let sessionHeader = req.value(forHTTPHeaderField: "mcp-session-id")
            guard let bodyData = req.httpBody,
                  let json = try? JSONSerialization.jsonObject(with: bodyData) as? [String: Any],
                  let method = json["method"] as? String else {
                throw HTTPTransportError.invalidResponse
            }

            if method == "initialize" {
                XCTAssertNil(sessionHeader, "Initialize must NEVER attach session header, even after re-initialization")
                let resp = """
                { "jsonrpc": "2.0", "id": 1, "result": { "protocolVersion": "2025-06-18" } }
                """
                return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: [
                    "Content-Type": "application/json",
                    "mcp-session-id": "sess-new-789"
                ])!)
            } else if method == "notifications/initialized" {
                return (Data(), HTTPURLResponse(url: req.url!, statusCode: 202, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            } else if method == "tools/call" {
                if shouldFailWith404 {
                    return (Data(), HTTPURLResponse(url: req.url!, statusCode: 404, httpVersion: nil, headerFields: [:])!)
                }
                let resp = """
                { "jsonrpc": "2.0", "id": \(json["id"] as? Int ?? 2), "result": { "providers": [], "threads": [], "nextCursor": null, "total": 0 } }
                """
                return (resp.data(using: .utf8)!, HTTPURLResponse(url: req.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!)
            }
            throw HTTPTransportError.invalidResponse
        }

        // Poll 1 succeeds
        _ = try await client.poll(endpoint: endpoint, token: "tok-1")

        // Next poll fails with 404 (session expired)
        shouldFailWith404 = true
        do {
            _ = try await client.poll(endpoint: endpoint, token: "tok-1")
            XCTFail("Should throw 404 error")
        } catch {
            // Expected
        }

        // Poll 3: Should re-initialize cleanly without session header
        shouldFailWith404 = false
        _ = try await client.poll(endpoint: endpoint, token: "tok-1")
    }

    func testUnauthorizedThrows401Error() async throws {
        let endpoint = try T3UrlValidator.validateAndNormalize("http://localhost:3773")

        mockTransport.handler = { req in
            return (Data(), HTTPURLResponse(url: req.url!, statusCode: 401, httpVersion: nil, headerFields: ["WWW-Authenticate": "Bearer"])!)
        }

        do {
            _ = try await client.poll(endpoint: endpoint, token: "expired-token")
            XCTFail("Should have thrown unauthorized error")
        } catch let err as McpClientError {
            XCTAssertEqual(err, .unauthorized)
        }
    }
}
