import Foundation

// Token usage transport (spec/t3-usage-transport.md): Effect-RPC frames over a WebSocket.
// Every piece is injectable so tests need no server.

public enum T3UsageError: LocalizedError, Equatable {
    case invalidPairingCode
    case pairingCodeRejected
    case missingDiagnosticsScope
    case pairAgain
    case unsupportedVersion
    case unreachable
    case invalidResponse
    case messageTooLarge
    case timedOut
    case unexpectedChunk
    case valueUnavailable

    public var errorDescription: String? {
        switch self {
        case .invalidPairingCode:
            return "Paste the pairing code or pairing link from that computer."
        case .pairingCodeRejected:
            return "That code didn’t work. Codes work once and expire after 5 minutes."
        case .missingDiagnosticsScope:
            return "That code didn’t include token usage. Create a new code with the command below."
        case .pairAgain:
            return "Pair again to keep showing tokens"
        case .unsupportedVersion:
            return "This T3 version isn’t supported for token usage."
        case .unreachable, .invalidResponse, .messageTooLarge, .timedOut, .unexpectedChunk, .valueUnavailable:
            return "Couldn’t read token usage from T3 Code right now."
        }
    }
}

// MARK: - Frames

/// Frames Deskchirp sends. Request IDs are increasing decimal strings and `headers` is always `[]`.
public enum T3RpcClientFrame: Equatable {
    case request(id: String, tag: String, payloadJSON: Data)
    case ack(requestId: String)
    case ping
    case pong

    public func encodedText() throws -> String {
        let object: [String: Any]
        switch self {
        case .request(let id, let tag, let payloadJSON):
            let payload = try JSONSerialization.jsonObject(with: payloadJSON, options: [.fragmentsAllowed])
            object = ["_tag": "Request", "id": id, "tag": tag, "headers": [Any](), "payload": payload]
        case .ack(let requestId):
            object = ["_tag": "Ack", "requestId": requestId]
        case .ping:
            object = ["_tag": "Ping"]
        case .pong:
            object = ["_tag": "Pong"]
        }
        let data = try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])
        guard let text = String(data: data, encoding: .utf8) else { throw T3UsageError.invalidResponse }
        return text
    }

    public static func makeRequest(id: String, tag: String, payload: [String: Any]) throws -> T3RpcClientFrame {
        .request(id: id, tag: tag, payloadJSON: try JSONSerialization.data(withJSONObject: payload, options: [.sortedKeys]))
    }
}

/// Frames Deskchirp understands. Server text inside failures is never kept.
public enum T3RpcServerFrame: Equatable {
    /// `value` is the re-serialized success value, decoded and dropped by the caller.
    case success(requestId: String, value: Data)
    case failure(requestId: String)
    case die(requestId: String)
    case chunk(requestId: String)
    case ping
    case pong
    case defect
    case other

    /// Decodes one text message holding one object or an array of objects.
    public static func decode(_ text: String, maxBytes: Int = 2 * 1024 * 1024) throws -> [T3RpcServerFrame] {
        guard let data = text.data(using: .utf8) else { throw T3UsageError.invalidResponse }
        guard data.count <= maxBytes else { throw T3UsageError.messageTooLarge }
        let json = try JSONSerialization.jsonObject(with: data, options: [])
        let objects: [[String: Any]]
        if let array = json as? [[String: Any]] {
            objects = array
        } else if let object = json as? [String: Any] {
            objects = [object]
        } else {
            throw T3UsageError.invalidResponse
        }
        return objects.map(decodeObject)
    }

    private static func decodeObject(_ object: [String: Any]) -> T3RpcServerFrame {
        guard let tag = object["_tag"] as? String else { return .other }
        switch tag {
        case "Exit":
            guard let requestId = requestIdString(object["requestId"]),
                  let exit = object["exit"] as? [String: Any],
                  let exitTag = exit["_tag"] as? String else { return .other }
            switch exitTag {
            case "Success":
                let value = exit["value"] ?? NSNull()
                guard let data = try? JSONSerialization.data(withJSONObject: [value], options: []) else {
                    return .failure(requestId: requestId)
                }
                return .success(requestId: requestId, value: data)
            case "Failure":
                let causes = causeList(exit["cause"])
                if causes.contains(where: { ($0["_tag"] as? String) == "Die" }) { return .die(requestId: requestId) }
                return .failure(requestId: requestId)
            case "Die":
                return .die(requestId: requestId)
            default:
                return .failure(requestId: requestId)
            }
        case "Chunk":
            return .chunk(requestId: requestIdString(object["requestId"]) ?? "")
        case "Ping":
            return .ping
        case "Pong":
            return .pong
        case "Defect", "ClientProtocolError":
            return .defect
        default:
            return .other
        }
    }

    private static func requestIdString(_ value: Any?) -> String? {
        if let string = value as? String { return string }
        if let number = value as? NSNumber { return number.stringValue }
        return nil
    }

    private static func causeList(_ value: Any?) -> [[String: Any]] {
        if let list = value as? [[String: Any]] { return list }
        if let single = value as? [String: Any] {
            // Effect may nest causes (Sequential/Parallel with left/right).
            var result = [single]
            for key in ["left", "right", "cause"] {
                result.append(contentsOf: causeList(single[key]))
            }
            return result
        }
        return []
    }

    /// Unwraps the success value written by `decode`.
    public static func successObject(_ value: Data) -> Any? {
        guard let wrapper = try? JSONSerialization.jsonObject(with: value, options: []) as? [Any] else { return nil }
        return wrapper.first
    }
}

// MARK: - WebSocket abstraction

public protocol T3WebSocketConnection: AnyObject, Sendable {
    func send(_ text: String) async throws
    func receive() async throws -> String
    func close()
}

public protocol T3WebSocketConnecting: Sendable {
    /// Opens a socket. Must refuse redirects; HTTP 426 throws `T3UsageError.unsupportedVersion`,
    /// HTTP 401 throws `T3UsageError.pairAgain`.
    func connect(request: URLRequest, maxMessageBytes: Int) async throws -> T3WebSocketConnection
}

/// URLSessionWebSocketTask transport (macOS 12+ async API; package target is macOS 13).
public final class URLSessionWebSocketConnector: T3WebSocketConnecting, @unchecked Sendable {
    public static let shared = URLSessionWebSocketConnector()

    public init() {}

    public func connect(request: URLRequest, maxMessageBytes: Int) async throws -> T3WebSocketConnection {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 20
        configuration.timeoutIntervalForResource = 30
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.urlCredentialStorage = nil
        let delegate = NoRedirectWebSocketDelegate()
        let session = URLSession(configuration: configuration, delegate: delegate, delegateQueue: nil)
        let task = session.webSocketTask(with: request)
        task.maximumMessageSize = maxMessageBytes
        task.resume()
        let connection = URLSessionWebSocketConnection(session: session, task: task)
        // Surface the handshake result before the first request: a ping only completes once open.
        do {
            try await connection.waitUntilOpen()
        } catch {
            let status = (task.response as? HTTPURLResponse)?.statusCode
            connection.close()
            if status == 426 { throw T3UsageError.unsupportedVersion }
            if status == 401 || status == 403 { throw T3UsageError.pairAgain }
            throw T3UsageError.unreachable
        }
        return connection
    }
}

private final class NoRedirectWebSocketDelegate: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(
        _ session: URLSession, task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
        completionHandler: @escaping (URLRequest?) -> Void
    ) {
        completionHandler(nil)
    }
}

private final class URLSessionWebSocketConnection: T3WebSocketConnection, @unchecked Sendable {
    private let session: URLSession
    private let task: URLSessionWebSocketTask

    init(session: URLSession, task: URLSessionWebSocketTask) {
        self.session = session
        self.task = task
    }

    func waitUntilOpen() async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            task.sendPing { error in
                if let error = error { continuation.resume(throwing: error) } else { continuation.resume() }
            }
        }
    }

    func send(_ text: String) async throws {
        try await task.send(.string(text))
    }

    func receive() async throws -> String {
        let message = try await task.receive()
        switch message {
        case .string(let text):
            return text
        case .data(let data):
            guard let text = String(data: data, encoding: .utf8) else { throw T3UsageError.invalidResponse }
            return text
        @unknown default:
            throw T3UsageError.invalidResponse
        }
    }

    func close() {
        task.cancel(with: .normalClosure, reason: nil)
        session.invalidateAndCancel()
    }
}

// MARK: - RPC session

/// One request at a time over one socket. Answers Ping and Chunk; a Chunk ends the poll.
public final class T3RpcSession: @unchecked Sendable {
    private let connection: T3WebSocketConnection
    private let maxMessageBytes: Int
    private var nextId = 1

    public init(connection: T3WebSocketConnection, maxMessageBytes: Int = 2 * 1024 * 1024) {
        self.connection = connection
        self.maxMessageBytes = maxMessageBytes
    }

    /// Returns the success value. Failure, Die and Defect throw `valueUnavailable` without server text.
    public func call(tag: String, payload: [String: Any]) async throws -> Any {
        let id = String(nextId)
        nextId += 1
        let frame = try T3RpcClientFrame.makeRequest(id: id, tag: tag, payload: payload)
        try await connection.send(try frame.encodedText())
        // Bounded by the poll timeout; also cap the number of unrelated frames.
        for _ in 0..<64 {
            let text = try await connection.receive()
            for incoming in try T3RpcServerFrame.decode(text, maxBytes: maxMessageBytes) {
                switch incoming {
                case .success(let requestId, let value) where requestId == id:
                    guard let object = T3RpcServerFrame.successObject(value) else { throw T3UsageError.invalidResponse }
                    return object
                case .failure(let requestId) where requestId == id:
                    throw T3UsageError.valueUnavailable
                case .die(let requestId) where requestId == id:
                    throw T3UsageError.valueUnavailable
                case .chunk(let requestId):
                    try? await connection.send(try T3RpcClientFrame.ack(requestId: requestId).encodedText())
                    throw T3UsageError.unexpectedChunk
                case .ping:
                    try await connection.send(try T3RpcClientFrame.pong.encodedText())
                case .defect:
                    throw T3UsageError.valueUnavailable
                case .success, .failure, .die, .pong, .other:
                    continue
                }
            }
        }
        throw T3UsageError.invalidResponse
    }

    public func close() {
        connection.close()
    }
}

/// Runs `operation` with a deadline. The poll is bounded to 20 seconds.
/// `onTimeout` must unblock the operation (for example by closing the socket), because
/// a task group waits for every child before returning.
func withUsageDeadline<T: Sendable>(
    seconds: TimeInterval,
    onTimeout: @escaping @Sendable () -> Void = {},
    _ operation: @escaping @Sendable () async throws -> T
) async throws -> T {
    try await withThrowingTaskGroup(of: T.self) { group in
        group.addTask { try await operation() }
        group.addTask {
            try await Task.sleep(nanoseconds: UInt64(max(0, seconds) * 1_000_000_000))
            onTimeout()
            throw T3UsageError.timedOut
        }
        guard let first = try await group.next() else { throw T3UsageError.timedOut }
        group.cancelAll()
        return first
    }
}
