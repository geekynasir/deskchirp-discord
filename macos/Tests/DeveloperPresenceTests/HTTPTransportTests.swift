import XCTest
@testable import DeveloperPresenceCore

final class HTTPTransportTests: XCTestCase {
    private func configuredTransport(limit: Int = 1024) -> URLSessionHTTPTransport {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [PresenceURLProtocol.self]
        return URLSessionHTTPTransport(configuration: config, maxResponseBytes: limit)
    }

    private func request(script: @escaping (PresenceURLProtocol) -> Void) -> URLRequest {
        let id = UUID().uuidString
        PresenceURLProtocol.register(id: id, script: script)
        var request = URLRequest(url: URL(string: "https://presence.test/mcp")!)
        request.httpMethod = "POST"
        request.httpBody = Data("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\"}".utf8)
        request.setValue(id, forHTTPHeaderField: "X-Presence-Test")
        return request
    }

    func testStreamingReplyFinishesBeforeEOFAndSkipsOtherIDs() async throws {
        let done = expectation(description: "Matching SSE reply completes without EOF")
        let transport = configuredTransport()
        let req = request { source in
            source.headers(type: "text/event-stream")
            // Deliberately split CRLF and a UTF-8 code point across callbacks.
            let bytes = Data(("data: {\"id\":6,\"result\":{\"wrong\":true}}\r\n\r\n" +
                "data: {\"id\":7,\r\ndata: \"result\":{\"label\":\"résumé\"}}\r\n\r\n").utf8)
            for byte in bytes { source.chunk(Data([byte])) }
            // Do not call urlProtocolDidFinishLoading: a real MCP stream may remain open.
        }
        let work = Task {
            let output = try await transport.send(request: req)
            done.fulfill()
            return output
        }
        await fulfillment(of: [done], timeout: 3)
        work.cancel()
        let (data, response) = try await work.value
        XCTAssertEqual(response.statusCode, 200)
        let text = String(decoding: data, as: UTF8.self)
        XCTAssertTrue(text.contains("résumé"))
        XCTAssertFalse(text.contains("wrong"))
    }

    func testRejectsOversizedBodyWhileStreamIsStillOpen() async {
        let done = expectation(description: "Size limit rejects before EOF")
        let transport = configuredTransport(limit: 8)
        let req = request { source in
            source.headers(type: "application/json")
            source.chunk(Data(repeating: 32, count: 9))
        }
        let work = Task {
            defer { done.fulfill() }
            do {
                _ = try await transport.send(request: req)
                XCTFail("An oversized body must fail")
            } catch {
                XCTAssertEqual(error as? HTTPTransportError, .responseTooLarge)
            }
        }
        await fulfillment(of: [done], timeout: 3)
        work.cancel()
        await work.value
    }

    func testCancellationStopsOpenResponse() async {
        let started = expectation(description: "Response started")
        let done = expectation(description: "Cancelled response returns")
        let transport = configuredTransport()
        let req = request { source in
            source.headers(type: "text/event-stream")
            source.chunk(Data(": keepalive\n\n".utf8))
            started.fulfill()
        }
        let work = Task {
            defer { done.fulfill() }
            do {
                _ = try await transport.send(request: req)
                XCTFail("Cancelled request must fail")
            } catch {
                XCTAssertTrue(error is CancellationError || (error as? URLError)?.code == .cancelled)
            }
        }
        await fulfillment(of: [started], timeout: 3)
        work.cancel()
        await fulfillment(of: [done], timeout: 3)
        await work.value
    }
}

private final class PresenceURLProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private static var scripts: [String: (PresenceURLProtocol) -> Void] = [:]

    static func register(id: String, script: @escaping (PresenceURLProtocol) -> Void) {
        lock.lock()
        scripts[id] = script
        lock.unlock()
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let id = request.value(forHTTPHeaderField: "X-Presence-Test") ?? ""
        Self.lock.lock()
        let script = Self.scripts.removeValue(forKey: id)
        Self.lock.unlock()
        script?(self)
    }

    override func stopLoading() {}

    func headers(type: String) {
        let response = HTTPURLResponse(url: request.url!, statusCode: 200,
            httpVersion: "HTTP/1.1", headerFields: ["Content-Type": type])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
    }

    func chunk(_ data: Data) { client?.urlProtocol(self, didLoad: data) }
}
