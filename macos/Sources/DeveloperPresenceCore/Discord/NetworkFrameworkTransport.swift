import Foundation
import Network

/// macOS Network.framework NWConnection UNIX domain socket transport.
/// Uses NWEndpoint.unix(path:) and NWParameters.tcp.
public final class NetworkFrameworkTransport: DiscordTransport, @unchecked Sendable {
    private var connection: NWConnection?
    private let queue: DispatchQueue
    private let queueKey = DispatchSpecificKey<Void>()

    public var onDataReceived: (@Sendable (Data) -> Void)?
    public var onDisconnected: (@Sendable (Error?) -> Void)?

    public init(queue: DispatchQueue = DispatchQueue(label: "com.developerpresence.transport.network")) {
        self.queue = queue
        queue.setSpecific(key: queueKey, value: ())
    }

    private var isOnQueue: Bool {
        DispatchQueue.getSpecific(key: queueKey) != nil
    }

    private func runOnQueue(_ block: @escaping @Sendable () -> Void) {
        if isOnQueue {
            block()
        } else {
            queue.async(execute: block)
        }
    }

    public func connect(to path: String, completion: @escaping @Sendable (Result<Void, Error>) -> Void) {
        runOnQueue { [weak self] in
            guard let self = self else { return }
            self.disconnectInternal()

            let endpoint = NWEndpoint.unix(path: path)
            // Valid standard stream UNIX domain socket parameters (TLS disabled, standard stream transport)
            let parameters = NWParameters.tcp

            let conn = NWConnection(to: endpoint, using: parameters)
            self.connection = conn

            var connectionCompleted = false

            conn.stateUpdateHandler = { [weak self, weak conn] state in
                guard let self = self, let conn = conn else { return }
                self.runOnQueue {
                    // Guard against stale callbacks from a superseded connection
                    guard self.connection === conn else { return }

                    switch state {
                    case .ready:
                        if !connectionCompleted {
                            connectionCompleted = true
                            completion(.success(()))
                        }
                        self.startReceiving(conn: conn)

                    case .failed(let error):
                        if !connectionCompleted {
                            connectionCompleted = true
                            self.disconnectInternal()
                            completion(.failure(error))
                        } else {
                            self.handleDisconnection(conn: conn, error: error)
                        }

                    case .cancelled:
                        self.handleDisconnection(conn: conn, error: nil)

                    case .waiting(let error):
                        if !connectionCompleted {
                            connectionCompleted = true
                            self.disconnectInternal()
                            completion(.failure(error))
                        }

                    default:
                        break
                    }
                }
            }

            conn.start(queue: self.queue)
        }
    }

    private func startReceiving(conn: NWConnection) {
        conn.receive(minimumIncompleteLength: 1, maximumLength: DiscordIpcFrame.maxPayloadBytes) { [weak self, weak conn] content, _, isComplete, error in
            guard let self = self, let conn = conn else { return }
            self.runOnQueue {
                // Stale callback guard: ignore data or errors from old connection
                guard self.connection === conn else { return }

                if let data = content, !data.isEmpty {
                    self.onDataReceived?(data)
                }

                if isComplete {
                    self.handleDisconnection(conn: conn, error: nil)
                } else if let error = error {
                    self.handleDisconnection(conn: conn, error: error)
                } else {
                    self.startReceiving(conn: conn)
                }
            }
        }
    }

    public func send(data: Data, completion: @escaping @Sendable (Result<Void, Error>) -> Void) {
        runOnQueue { [weak self] in
            guard let self = self, let conn = self.connection else {
                completion(.failure(TransportError.notConnected))
                return
            }

            conn.send(content: data, completion: .contentProcessed { error in
                if let error = error {
                    completion(.failure(error))
                } else {
                    completion(.success(()))
                }
            })
        }
    }

    public func disconnect() {
        runOnQueue { [weak self] in
            self?.disconnectInternal()
        }
    }

    private func disconnectInternal() {
        if let conn = connection {
            conn.stateUpdateHandler = nil
            conn.cancel()
            connection = nil
        }
    }

    private func handleDisconnection(conn: NWConnection, error: Error?) {
        guard connection === conn else { return }
        disconnectInternal()
        onDisconnected?(error)
    }
}
