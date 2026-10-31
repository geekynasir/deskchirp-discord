import XCTest
@testable import DeveloperPresenceCore

final class T3LocalDiscoveryTests: XCTestCase {
    func testRuntimeUsesOnlyLoopbackPort() {
        let runtime = Data(#"{"version":1,"pid":4,"port":4123,"origin":"https://other.example","unexpectedSecret":"ignored"}"#.utf8)
        XCTAssertEqual(T3LocalDiscovery.parseRuntime(runtime), "http://127.0.0.1:4123/mcp")
        for bad in [#"{"version":2,"pid":4,"port":4123}"#, #"{"version":1,"pid":0,"port":4123}"#, #"{"version":1,"pid":4,"port":65536}"#, "[]"] {
            XCTAssertNil(T3LocalDiscovery.parseRuntime(Data(bad.utf8)))
        }
    }

    func testMetadataMustMatchResourceIssuerAndReadScope() throws {
        let endpoint = try T3UrlValidator.validateAndNormalize("http://127.0.0.1:3773/mcp")
        for (resource, issuer, scope, expected) in [
            (endpoint.mcpUrl.absoluteString, endpoint.origin, "orchestration:read", true),
            ("http://127.0.0.1:4000/mcp", endpoint.origin, "orchestration:read", false),
            (endpoint.mcpUrl.absoluteString, "https://other.example", "orchestration:read", false),
            (endpoint.mcpUrl.absoluteString, endpoint.origin, "orchestration:operate", false)
        ] {
            let bytes = try JSONSerialization.data(withJSONObject: ["resource": resource, "authorization_servers": [issuer], "scopes_supported": [scope]])
            XCTAssertEqual(T3LocalDiscovery.matchesMetadata(bytes, endpoint: endpoint), expected)
        }
    }

    func testFindDefaultWithoutRuntimeFile() async throws {
        let transport = MockHTTPTransport()
        transport.handler = { request in
            XCTAssertEqual(request.url?.host, "127.0.0.1")
            XCTAssertNil(request.value(forHTTPHeaderField: "Authorization"))
            let body = Data(#"{"resource":"http://127.0.0.1:3773/mcp","authorization_servers":["http://127.0.0.1:3773"],"scopes_supported":["orchestration:read"]}"#.utf8)
            return (body, HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!)
        }
        let found = try await T3LocalDiscovery(runtimePath: "/missing/\(UUID().uuidString)", transport: transport).find()
        XCTAssertEqual(found, "http://127.0.0.1:3773/mcp")
    }

    func testMultipleEnvironmentsDoNotSilentlyChoose() async throws {
        let path = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try Data(#"{"version":1,"pid":7,"port":4123}"#.utf8).write(to: path)
        defer { try? FileManager.default.removeItem(at: path) }
        let transport = MockHTTPTransport()
        transport.handler = { request in
            let origin = "http://127.0.0.1:\(request.url!.port!)"
            let bytes = try JSONSerialization.data(withJSONObject: ["resource": origin + "/mcp", "authorization_servers": [origin], "scopes_supported": ["orchestration:read"]])
            return (bytes, HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!)
        }
        do {
            _ = try await T3LocalDiscovery(runtimePath: path.path, transport: transport).find()
            XCTFail("Must ask which environment")
        } catch T3DiscoveryError.ambiguous { }
    }
}
