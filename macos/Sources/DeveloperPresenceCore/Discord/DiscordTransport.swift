import Foundation

/// Low-level transport abstraction for Discord local IPC.
public protocol DiscordTransport: AnyObject, Sendable {
    var onDataReceived: (@Sendable (Data) -> Void)? { get set }
    var onDisconnected: (@Sendable (Error?) -> Void)? { get set }

    func connect(to path: String, completion: @escaping @Sendable (Result<Void, Error>) -> Void)
    func send(data: Data, completion: @escaping @Sendable (Result<Void, Error>) -> Void)
    func disconnect()
}

public enum TransportError: Error, LocalizedError, Equatable {
    case notConnected
    case socketCreationFailed
    case connectionFailed(String)
    case writeFailed
    case timeout

    public var errorDescription: String? {
        switch self {
        case .notConnected:
            return "Not connected to Discord socket."
        case .socketCreationFailed:
            return "Failed to allocate local socket."
        case .connectionFailed(let reason):
            return "Socket connection failed: \(reason)"
        case .writeFailed:
            return "Failed writing frame to socket."
        case .timeout:
            return "Operation timed out."
        }
    }
}
