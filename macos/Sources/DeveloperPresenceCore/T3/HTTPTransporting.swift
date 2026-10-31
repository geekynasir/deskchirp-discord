import Foundation

public enum HTTPTransportError: LocalizedError, Equatable {
    case redirectRefused
    case invalidResponse
    case responseTooLarge
    case httpError(statusCode: Int, message: String?)

    public var errorDescription: String? {
        switch self {
        case .redirectRefused:
            return "T3 Code sent Developer Presence to a different address. Copy the address again from T3 Code."
        case .invalidResponse, .responseTooLarge:
            return "This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL."
        case .httpError:
            return "Couldn’t reach T3 Code. Check that it’s running and try again."
        }
    }
}

public protocol HTTPTransporting: Sendable {
    func send(request: URLRequest) async throws -> (Data, HTTPURLResponse)
}

/// Each exchange owns its session so cancellation also stops an open SSE response.
/// Bounds apply as bytes arrive, including streams that never send an EOF.
public final class URLSessionHTTPTransport: HTTPTransporting, @unchecked Sendable {
    public static let shared = URLSessionHTTPTransport()
    private let configuration: URLSessionConfiguration
    private let maxResponseBytes: Int
    private let maxMetadataBytes: Int

    public init(
        configuration: URLSessionConfiguration = .ephemeral,
        maxResponseBytes: Int = 1_048_576,
        maxMetadataBytes: Int = 65_536,
        requestTimeout: TimeInterval = 15,
        resourceTimeout: TimeInterval = 30
    ) {
        let settings = configuration.copy() as! URLSessionConfiguration
        settings.timeoutIntervalForRequest = requestTimeout
        settings.timeoutIntervalForResource = resourceTimeout
        settings.requestCachePolicy = .reloadIgnoringLocalCacheData
        settings.urlCache = nil
        settings.httpCookieStorage = nil
        settings.httpShouldSetCookies = false
        settings.urlCredentialStorage = nil
        self.configuration = settings
        self.maxResponseBytes = max(1, maxResponseBytes)
        self.maxMetadataBytes = max(1, maxMetadataBytes)
    }

    public func send(request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let limit = request.url?.path == "/mcp" ? maxResponseBytes : maxMetadataBytes
        let exchange = BoundedHTTPExchange(configuration: configuration, request: request, limit: limit)
        return try await withTaskCancellationHandler(operation: {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { continuation in
                exchange.start(continuation)
            }
        }, onCancel: {
            exchange.cancel()
        })
    }
}

private final class BoundedHTTPExchange: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    typealias Output = (Data, HTTPURLResponse)
    private let configuration: URLSessionConfiguration
    private let request: URLRequest
    private let limit: Int
    private let expectedID: Data?
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Output, Error>?
    private var outcome: Result<Output, Error>?
    private var session: URLSession?
    private var task: URLSessionDataTask?
    // The delegate queue alone owns these response buffers.
    private var response: HTTPURLResponse?
    private var body = Data()
    private var receivedBytes = 0
    private var isSSE = false
    private var sse = StreamingRPCEventParser()

    init(configuration: URLSessionConfiguration, request: URLRequest, limit: Int) {
        self.configuration = configuration
        self.request = request
        self.limit = limit
        if let bytes = request.httpBody,
           let object = try? JSONSerialization.jsonObject(with: bytes) as? [String: Any],
           let id = object["id"] {
            self.expectedID = try? JSONSerialization.data(withJSONObject: [id])
        } else {
            self.expectedID = nil
        }
        super.init()
    }

    func start(_ continuation: CheckedContinuation<(Data, HTTPURLResponse), Error>) {
        lock.lock()
        if let result = outcome {
            lock.unlock()
            continuation.resume(with: result)
            return
        }
        self.continuation = continuation
        let queue = OperationQueue()
        queue.maxConcurrentOperationCount = 1
        let session = URLSession(configuration: configuration, delegate: self, delegateQueue: queue)
        let task = session.dataTask(with: request)
        self.session = session
        self.task = task
        lock.unlock()
        task.resume()
    }

    func cancel() { finish(.failure(CancellationError())) }

    private var isFinished: Bool {
        lock.lock()
        defer { lock.unlock() }
        return outcome != nil
    }

    private func finish(_ result: Result<Output, Error>) {
        lock.lock()
        guard outcome == nil else { lock.unlock(); return }
        outcome = result
        let pending = continuation
        let activeSession = session
        continuation = nil
        session = nil
        task = nil
        lock.unlock()
        activeSession?.invalidateAndCancel()
        pending?.resume(with: result)
    }

    func urlSession(
        _ session: URLSession, task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
        completionHandler: @escaping (URLRequest?) -> Void
    ) {
        completionHandler(nil)
        finish(.failure(HTTPTransportError.redirectRefused))
    }

    func urlSession(
        _ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
        completionHandler: @escaping (URLSession.ResponseDisposition) -> Void
    ) {
        guard !isFinished, let http = response as? HTTPURLResponse else {
            completionHandler(.cancel)
            finish(.failure(HTTPTransportError.invalidResponse))
            return
        }
        self.response = http
        if (300..<400).contains(http.statusCode) {
            completionHandler(.cancel)
            finish(.failure(HTTPTransportError.redirectRefused))
        } else if !(200..<300).contains(http.statusCode) || http.statusCode == 202 || http.statusCode == 204 {
            completionHandler(.cancel)
            finish(.success((Data(), http)))
        } else if http.expectedContentLength > Int64(limit) {
            completionHandler(.cancel)
            finish(.failure(HTTPTransportError.responseTooLarge))
        } else {
            isSSE = http.mimeType?.lowercased() == "text/event-stream"
            completionHandler(.allow)
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard !isFinished, let response = response else { return }
        guard data.count <= limit - receivedBytes else {
            finish(.failure(HTTPTransportError.responseTooLarge))
            return
        }
        receivedBytes += data.count
        if isSSE {
            if let matched = sse.append(data, expectedID: expectedID) {
                // A canonical single-line event also handles multi-line SSE data correctly
                // for callers that consume a complete bounded response.
                var event = Data("data: ".utf8)
                event.append(matched)
                event.append(Data("\n\n".utf8))
                finish(.success((event, response)))
            }
        } else {
            body.append(data)
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard !isFinished else { return }
        if let error = error {
            finish(.failure(error))
        } else if let response = response, !isSSE {
            finish(.success((body, response)))
        } else {
            finish(.failure(HTTPTransportError.invalidResponse))
        }
    }
}

/// Incremental SSE framing. Only a complete event with the requested JSON-RPC ID
/// finishes an exchange; notifications and other IDs cannot satisfy a request.
private struct StreamingRPCEventParser {
    private var line = Data()
    private var event = Data()
    private var previousWasCR = false

    mutating func append(_ bytes: Data, expectedID: Data?) -> Data? {
        for byte in bytes {
            if byte == 10 && previousWasCR {
                previousWasCR = false
                continue
            }
            previousWasCR = byte == 13
            if byte == 10 || byte == 13 {
                if let result = endLine(expectedID: expectedID) { return result }
            } else {
                line.append(byte)
            }
        }
        return nil
    }

    private mutating func endLine(expectedID: Data?) -> Data? {
        defer { line.removeAll(keepingCapacity: true) }
        if line.isEmpty {
            defer { event.removeAll(keepingCapacity: true) }
            guard !event.isEmpty,
                  let json = try? JSONSerialization.jsonObject(with: event) as? [String: Any],
                  let responseID = json["id"],
                  let encodedID = try? JSONSerialization.data(withJSONObject: [responseID]),
                  expectedID == nil || encodedID == expectedID else { return nil }
            return try? JSONSerialization.data(withJSONObject: json)
        }
        let prefix = Data("data:".utf8)
        if line.starts(with: prefix) {
            var value = line.dropFirst(prefix.count)
            if value.first == 32 { value = value.dropFirst() }
            event.append(contentsOf: value)
            event.append(10)
        }
        return nil
    }
}

/// Explicitly injected test transport; the production transport always uses URLSession.
public final class MockHTTPTransport: HTTPTransporting, @unchecked Sendable {
    private let lock = NSLock()
    public var handler: (@Sendable (URLRequest) throws -> (Data, HTTPURLResponse))?

    public init(handler: (@Sendable (URLRequest) throws -> (Data, HTTPURLResponse))? = nil) {
        self.handler = handler
    }

    public func send(request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        lock.lock()
        let currentHandler = self.handler
        lock.unlock()
        guard let handler = currentHandler else { throw HTTPTransportError.invalidResponse }
        return try handler(request)
    }
}
