import Foundation
#if canImport(Darwin)
import Darwin
#elseif canImport(Glibc)
import Glibc
#endif

public enum OAuthError: LocalizedError, Equatable {
    case timeout
    case cancelled
    case stateMismatch
    case issuerMismatch
    case missingCode
    case duplicateParameters
    case invalidPath
    case socketError(String)
    case browserFailed
    case registrationFailed(String)
    case tokenExchangeFailed(String)
    case discoveryFailed(String)

    public var errorDescription: String? {
        switch self {
        case .timeout:
            return "Sign-in timed out. Try connecting again."
        case .cancelled:
            return "Sign-in was cancelled."
        case .stateMismatch, .issuerMismatch, .missingCode, .duplicateParameters, .invalidPath:
            return "Sign-in didn’t finish. Try connecting again."
        case .socketError:
            return "Couldn’t start local sign-in listener. Try connecting again."
        case .browserFailed:
            return "Couldn’t open your browser. Check that you have a default browser, then try again."
        case .registrationFailed, .tokenExchangeFailed:
            return "Sign-in didn’t finish. Try connecting again."
        case .discoveryFailed:
            return "This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL."
        }
    }
}

public protocol OAuthLoopbackServing: AnyObject, Sendable {
    /// Prepares and binds the loopback socket on an ephemeral port before registration and browser launch.
    func prepare() throws -> (redirectUri: String, port: UInt16)

    /// Waits for incoming GET /callback request with state and issuer verification.
    func waitForCallback(
        state: String,
        issuer: String,
        timeoutSeconds: TimeInterval
    ) async throws -> String

    func cancel()
}

/// Lightweight, secure local loopback HTTP server bound to 127.0.0.1 on an ephemeral port.
/// Handles the one-time OAuth callback from T3 Code approval, serves the confirmation page,
/// and immediately releases the listening socket.
public final class BSDLoopbackServer: OAuthLoopbackServing, @unchecked Sendable {
    private final class Session: @unchecked Sendable {
        let fd: Int32
        var client: Int32 = -1
        var waiting = false
        var cancelled = false
        var closed = false
        init(fd: Int32) { self.fd = fd }
    }
    private let lock = NSLock()
    private var session: Session?

    public init() {}

    public func cancel() {
        lock.lock()
        defer { lock.unlock() }
        if let active = session { cancelLocked(active) }
        session = nil
    }

    // A waiting worker owns its descriptors until cleanup. Cancellation only
    // shuts them down, so an old worker can never close a newly reused descriptor.
    private func cancelLocked(_ active: Session) {
        active.cancelled = true
        if active.waiting {
            _ = Darwin.shutdown(active.fd, SHUT_RDWR)
            if active.client >= 0 { _ = Darwin.shutdown(active.client, SHUT_RDWR) }
        } else if !active.closed {
            Darwin.close(active.fd)
            active.closed = true
        }
    }

    public func prepare() throws -> (redirectUri: String, port: UInt16) {
        lock.lock()
        defer { lock.unlock() }
        if let active = session { cancelLocked(active) }
        session = nil
        let fd = Darwin.socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { throw OAuthError.socketError("socket") }
        var prepared = false
        defer { if !prepared { Darwin.close(fd) } }
        _ = fcntl(fd, F_SETFL, O_NONBLOCK)
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        let bound = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bound == 0, Darwin.listen(fd, 1) == 0 else { throw OAuthError.socketError("bind/listen") }
        var size = socklen_t(MemoryLayout<sockaddr_in>.size)
        let named = withUnsafeMutablePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.getsockname(fd, $0, &size)
            }
        }
        guard named == 0 else { throw OAuthError.socketError("getsockname") }
        let port = UInt16(bigEndian: address.sin_port)
        session = Session(fd: fd)
        prepared = true
        return ("http://127.0.0.1:\(port)/callback", port)
    }

    public func waitForCallback(state: String, issuer: String, timeoutSeconds: TimeInterval) async throws -> String {
        lock.lock()
        guard let active = session, !active.cancelled, !active.waiting else {
            lock.unlock()
            throw OAuthError.cancelled
        }
        active.waiting = true
        lock.unlock()
        return try await withTaskCancellationHandler(operation: {
            try await withCheckedThrowingContinuation { continuation in
                DispatchQueue.global(qos: .userInitiated).async {
                    defer { self.cleanup(active) }
                    do {
                        let deadline = ProcessInfo.processInfo.systemUptime + max(0, min(timeoutSeconds, 120))
                        try self.waitUntilReady(active.fd, events: Int16(POLLIN), active: active, deadline: deadline)
                        let client = Darwin.accept(active.fd, nil, nil)
                        guard client >= 0 else { throw OAuthError.socketError("accept") }
                        self.lock.lock()
                        active.client = client
                        let cancelled = active.cancelled
                        self.lock.unlock()
                        guard !cancelled else { throw OAuthError.cancelled }
                        _ = fcntl(client, F_SETFL, O_NONBLOCK)
                        var noSigpipe: Int32 = 1
                        _ = setsockopt(client, SOL_SOCKET, SO_NOSIGPIPE, &noSigpipe, socklen_t(MemoryLayout<Int32>.size))
                        let requestDeadline = min(deadline, ProcessInfo.processInfo.systemUptime + 5)
                        var data = Data()
                        var buffer = [UInt8](repeating: 0, count: 1024)
                        while data.range(of: Data("\r\n\r\n".utf8)) == nil && data.range(of: Data("\n\n".utf8)) == nil {
                            guard data.count < 8192 else { throw OAuthError.socketError("header limit") }
                            try self.waitUntilReady(client, events: Int16(POLLIN), active: active, deadline: requestDeadline)
                            let n = Darwin.read(client, &buffer, min(buffer.count, 8192 - data.count))
                            if n < 0 && (errno == EAGAIN || errno == EINTR) { continue }
                            guard n > 0 else { throw OAuthError.socketError("incomplete request") }
                            data.append(contentsOf: buffer.prefix(n))
                        }
                        do {
                            let code = try Self.authorizationCode(data, state: state, issuer: issuer)
                            self.writeResponse(client: client, success: true, active: active, deadline: requestDeadline)
                            continuation.resume(returning: code)
                        } catch {
                            self.writeResponse(client: client, success: false, active: active, deadline: requestDeadline)
                            throw error
                        }
                    } catch { continuation.resume(throwing: error) }
                }
            }
        }, onCancel: {
            self.lock.lock()
            self.cancelLocked(active)
            self.lock.unlock()
        })
    }

    private func cleanup(_ active: Session) {
        lock.lock()
        defer { lock.unlock() }
        if active.client >= 0 { Darwin.close(active.client); active.client = -1 }
        if !active.closed { Darwin.close(active.fd); active.closed = true }
        if session === active { session = nil }
    }

    private func waitUntilReady(_ fd: Int32, events: Int16, active: Session, deadline: TimeInterval) throws {
        while true {
            lock.lock()
            let cancelled = active.cancelled
            lock.unlock()
            if cancelled { throw OAuthError.cancelled }
            let remaining = deadline - ProcessInfo.processInfo.systemUptime
            guard remaining > 0 else { throw OAuthError.timeout }
            var descriptor = pollfd(fd: fd, events: events, revents: 0)
            let result = Darwin.poll(&descriptor, 1, Int32(min(remaining * 1000 + 1, 100)))
            if result > 0 { return }
            if result < 0 && errno != EINTR { throw OAuthError.socketError("poll") }
        }
    }

    private static func authorizationCode(_ data: Data, state: String, issuer: String) throws -> String {
        guard let request = String(data: data, encoding: .utf8),
              let first = request.components(separatedBy: "\n").first else { throw OAuthError.invalidPath }
        let parts = first.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: " ")
        guard parts.count == 3, parts[0] == "GET", parts[2].hasPrefix("HTTP/1."),
              parts[1].hasPrefix("/callback?"),
              let url = URLComponents(string: "http://127.0.0.1\(parts[1])"),
              url.path == "/callback", url.fragment == nil else { throw OAuthError.invalidPath }
        let items = url.queryItems ?? []
        guard Set(items.map { $0.name }).count == items.count else { throw OAuthError.duplicateParameters }
        let values = Dictionary(uniqueKeysWithValues: items.compactMap { item in item.value.map { (item.name, $0) } })
        guard values["state"] == state else { throw OAuthError.stateMismatch }
        guard values["iss"] == issuer else { throw OAuthError.issuerMismatch }
        if let error = values["error"] {
            throw error == "access_denied" ? OAuthError.cancelled : OAuthError.tokenExchangeFailed("denied")
        }
        guard let code = values["code"], !code.isEmpty else { throw OAuthError.missingCode }
        return code
    }

    private func writeResponse(client: Int32, success: Bool, active: Session, deadline: TimeInterval) {
        let title = success ? "Approval received" : "Sign-in didn’t finish"
        let message = success ? "Return to Deskchirp to finish connecting. You can close this tab." : "Return to Deskchirp and try connecting again."
        let html = "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>\(title)</title><body style=\"font:16px system-ui;padding:32px\"><h1>\(title)</h1><p>\(message)</p></body></html>"
        let body = Data(html.utf8)
        let status = success ? "200 OK" : "400 Bad Request"
        var response = Data("HTTP/1.1 \(status)\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: \(body.count)\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n".utf8)
        response.append(body)
        var written = 0
        while written < response.count {
            guard (try? waitUntilReady(client, events: Int16(POLLOUT), active: active, deadline: deadline)) != nil else { return }
            let n = response.withUnsafeBytes { bytes in
                Darwin.write(client, bytes.baseAddress!.advanced(by: written), response.count - written)
            }
            if n < 0 && (errno == EAGAIN || errno == EINTR) { continue }
            guard n > 0 else { return }
            written += n
        }
    }
}

/// Mock loopback server for testing without socket allocation.
public final class MockOAuthLoopbackServer: OAuthLoopbackServing, @unchecked Sendable {
    private let lock = NSLock()
    public var redirectUriToReturn: String = "http://127.0.0.1:54321/callback"
    public var portToReturn: UInt16 = 54321
    public var codeToReturn: String = "mock-auth-code"
    public var errorToThrow: Error?
    public private(set) var wasPrepared: Bool = false
    public private(set) var wasCancelled: Bool = false

    public init() {}

    public func prepare() throws -> (redirectUri: String, port: UInt16) {
        lock.lock()
        defer { lock.unlock() }
        wasPrepared = true
        return (redirectUriToReturn, portToReturn)
    }

    public func waitForCallback(state: String, issuer: String, timeoutSeconds: TimeInterval) async throws -> String {
        lock.lock()
        let err = errorToThrow
        let code = codeToReturn
        lock.unlock()

        if let error = err {
            throw error
        }
        return code
    }

    public func cancel() {
        lock.lock()
        defer { lock.unlock() }
        wasCancelled = true
    }
}
