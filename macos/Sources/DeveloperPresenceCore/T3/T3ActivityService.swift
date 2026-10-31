import Foundation

/// Coordinates T3 Code OAuth sign-in, Keychain storage, MCP streamable HTTP metadata polling,
/// winner selection, snapshot TTL, and privacy cancellation per spec/t3-activity.md.
public final class T3ActivityService: @unchecked Sendable {
    private let transport: HTTPTransporting
    private let keychain: KeychainManaging
    private let oauthClient: T3OAuthClient
    private let mcpClient: T3McpClient
    private let winnerSelector: T3WinnerSelector
    private let pollIntervalSeconds: TimeInterval
    private let snapshotTTLSeconds: TimeInterval

    private let lock = NSLock()
    private let queue = DispatchQueue(label: "com.developerpresence.t3service", qos: .userInitiated)

    private var pollTimer: DispatchSourceTimer?
    private var isPollingInProgress: Bool = false
    private var isEligibleToPoll: Bool = false
    private var authGeneration: Int = 0
    private var pollGeneration: Int = 0
    private var activePollTask: Task<Void, Never>?
    private var activeAuthTask: Task<T3StoredCredential, Error>?

    public private(set) var connectionStatus: T3ConnectionStatus = .notConnected
    public private(set) var currentSnapshot: T3ActivitySnapshot?
    public private(set) var currentEndpoint: T3NormalizedEndpoint?
    public private(set) var winningTool: String?
    public private(set) var lastErrorMessage: String?

    public var onStatusChanged: (@Sendable (T3ConnectionStatus, String?) -> Void)?
    public var onActivityUpdated: (@Sendable (T3ActivitySnapshot?, String?) -> Void)?

    public init(
        transport: HTTPTransporting = URLSessionHTTPTransport.shared,
        keychain: KeychainManaging = KeychainService.shared,
        oauthClient: T3OAuthClient? = nil,
        mcpClient: T3McpClient? = nil,
        winnerSelector: T3WinnerSelector = T3WinnerSelector(),
        pollIntervalSeconds: TimeInterval = 15.0,
        snapshotTTLSeconds: TimeInterval = 45.0
    ) {
        self.transport = transport
        self.keychain = keychain
        self.oauthClient = oauthClient ?? T3OAuthClient(transport: transport, keychain: keychain)
        self.mcpClient = mcpClient ?? T3McpClient(transport: transport)
        self.winnerSelector = winnerSelector
        self.pollIntervalSeconds = pollIntervalSeconds
        self.snapshotTTLSeconds = snapshotTTLSeconds
    }

    // MARK: - Sign In & Disconnect

    public func connect(address: String) async throws {
        // Validate and normalize address
        let endpoint: T3NormalizedEndpoint
        do { endpoint = try T3UrlValidator.validateAndNormalize(address) }
        catch {
            notifyStatusChanged(.notConnected, message: error.localizedDescription)
            throw error
        }

        lock.lock()
        authGeneration += 1
        let signAuthGen = authGeneration
        self.currentEndpoint = endpoint
        self.connectionStatus = .openingSignIn
        self.lastErrorMessage = nil
        activeAuthTask?.cancel()
        let authTask = Task { try await oauthClient.signIn(to: endpoint) }
        activeAuthTask = authTask
        lock.unlock()

        notifyStatusChanged(.openingSignIn, message: nil)

        do {
            let credential = try await authTask.value

            var finalStatus: T3ConnectionStatus?
            var finalMsg: String?
            var saveError: Error?
            var shouldPoll = false

            lock.lock()
            if self.authGeneration == signAuthGen {
                do {
                    // Service owns atomic, generation-guarded Keychain save
                    try keychain.saveCredential(
                        credential,
                        forService: KeychainService.defaultService,
                        account: endpoint.origin
                    )
                    self.connectionStatus = .connected
                    self.lastErrorMessage = nil
                    finalStatus = .connected
                    finalMsg = nil
                    shouldPoll = self.isEligibleToPoll
                } catch {
                    saveError = error
                    self.connectionStatus = .couldNotReach
                    self.lastErrorMessage = "Couldn’t save your sign-in to Keychain. Try connecting again."
                    finalStatus = .couldNotReach
                    finalMsg = self.lastErrorMessage
                }
            }
            lock.unlock()

            if let status = finalStatus {
                notifyStatusChanged(status, message: finalMsg)
            }

            if let error = saveError {
                throw error
            }

            if shouldPoll {
                queue.async { [weak self] in
                    self?.pollOnce()
                }
            }
        } catch let error as OAuthError {
            var finalStatus = T3ConnectionStatus.notConnected
            var finalMessage: String?
            var shouldNotify = false

            lock.lock()
            if self.authGeneration == signAuthGen {
                if let origin = currentEndpoint?.origin,
                   let existing = try? keychain.loadCredential(forService: KeychainService.defaultService, account: origin),
                   !existing.isExpired() {
                    self.connectionStatus = .connected
                    self.lastErrorMessage = error == .cancelled
                        ? "Sign-in was cancelled. Your existing connection wasn’t changed."
                        : "Sign-in didn’t finish. Your existing connection wasn’t changed."
                } else {
                    self.connectionStatus = .notConnected
                    self.lastErrorMessage = error.errorDescription
                }
                finalStatus = self.connectionStatus
                finalMessage = self.lastErrorMessage
                shouldNotify = true
            }
            lock.unlock()

            if shouldNotify {
                notifyStatusChanged(finalStatus, message: finalMessage)
            }
            throw error
        } catch {
            var shouldNotify = false
            lock.lock()
            if self.authGeneration == signAuthGen {
                self.connectionStatus = .couldNotReach
                if self.lastErrorMessage?.contains("Keychain") != true {
                    self.lastErrorMessage = "Check that T3 Code is running and the address is right."
                }
                shouldNotify = true
            }
            lock.unlock()

            if shouldNotify {
                notifyStatusChanged(.couldNotReach, message: self.lastErrorMessage)
            }
            throw error
        }
    }

    public func cancelSignIn() {
        lock.lock()
        authGeneration += 1
        activeAuthTask?.cancel()
        activeAuthTask = nil
        lock.unlock()

        oauthClient.cancel()

        lock.lock()
        if let origin = currentEndpoint?.origin,
           let existing = try? keychain.loadCredential(forService: KeychainService.defaultService, account: origin),
           !existing.isExpired() {
            self.connectionStatus = .connected
            self.lastErrorMessage = nil
        } else {
            self.connectionStatus = .notConnected
            self.lastErrorMessage = "Sign-in was cancelled."
        }
        let status = self.connectionStatus
        let msg = self.lastErrorMessage
        lock.unlock()

        notifyStatusChanged(status, message: msg)
    }

    public func disconnect() {
        lock.lock()
        authGeneration += 1
        activeAuthTask?.cancel()
        activeAuthTask = nil
        pollGeneration += 1
        isEligibleToPoll = false
        stopTimerLocked()
        activePollTask?.cancel()
        activePollTask = nil
        oauthClient.cancel()

        if let origin = currentEndpoint?.origin {
            do {
                try keychain.deleteCredential(forService: KeychainService.defaultService, account: origin)
            } catch {
                currentSnapshot = nil
                winningTool = nil
                winnerSelector.reset()
                connectionStatus = .connected
                lastErrorMessage = "Couldn’t remove your saved sign-in. Try disconnecting again."
                lock.unlock()
                notifyStatusChanged(.connected, message: "Couldn’t remove your saved sign-in. Try disconnecting again.")
                notifyActivityUpdated(nil, winner: nil)
                return
            }
        }

        mcpClient.resetSession()
        winnerSelector.reset()
        currentSnapshot = nil
        winningTool = nil
        connectionStatus = .notConnected
        lastErrorMessage = nil
        lock.unlock()

        notifyStatusChanged(.notConnected, message: "To revoke access in T3 too, open T3 Code Settings → Sharing & access.")
        notifyActivityUpdated(nil, winner: nil)
    }

    public func restoreSavedConnection(address: String) {
        guard let endpoint = try? T3UrlValidator.validateAndNormalize(address) else {
            return
        }

        lock.lock()
        self.currentEndpoint = endpoint
        if let credential = try? keychain.loadCredential(forService: KeychainService.defaultService, account: endpoint.origin) {
            if credential.isExpired() {
                self.connectionStatus = .signInAgain
                self.lastErrorMessage = "Your T3 Code sign-in expired. Connect again to keep showing activity."
            } else {
                self.connectionStatus = .connected
                self.lastErrorMessage = nil
            }
        } else {
            self.connectionStatus = .notConnected
            self.lastErrorMessage = nil
        }
        let status = self.connectionStatus
        let msg = self.lastErrorMessage
        lock.unlock()

        notifyStatusChanged(status, message: msg)
    }

    public func handleEndpointChange(address: String) {
        lock.lock()
        authGeneration += 1
        activeAuthTask?.cancel()
        activeAuthTask = nil
        pollGeneration += 1
        stopTimerLocked()
        activePollTask?.cancel()
        activePollTask = nil
        oauthClient.cancel()

        currentSnapshot = nil
        winningTool = nil
        winnerSelector.reset()
        currentEndpoint = try? T3UrlValidator.validateAndNormalize(address)

        if let endpoint = currentEndpoint,
           let credential = try? keychain.loadCredential(forService: KeychainService.defaultService, account: endpoint.origin),
           !credential.accessToken.isEmpty {
            if credential.isExpired() {
                connectionStatus = .signInAgain
                lastErrorMessage = "Your T3 Code sign-in expired. Connect again to keep showing activity."
            } else {
                connectionStatus = .connected
                lastErrorMessage = nil
            }
        } else {
            connectionStatus = .notConnected
            lastErrorMessage = nil
        }
        let status = connectionStatus
        let msg = lastErrorMessage
        lock.unlock()

        notifyStatusChanged(status, message: msg)
        notifyActivityUpdated(nil, winner: nil)
    }

    // MARK: - Polling Control

    public func updateEligibility(isEligibleToPoll: Bool) {
        var shouldPoll = false
        var shouldClearSnapshot = false

        lock.lock()
        self.isEligibleToPoll = isEligibleToPoll

        if isEligibleToPoll && (connectionStatus == .connected || connectionStatus == .couldNotReach) {
            if pollTimer == nil {
                startTimerLocked()
                shouldPoll = true
            }
        } else {
            // Cancel poll task and stop timer without affecting auth generation
            pollGeneration += 1
            stopTimerLocked()
            activePollTask?.cancel()
            activePollTask = nil

            if currentSnapshot != nil {
                currentSnapshot = nil
                winningTool = nil
                winnerSelector.reset()
                shouldClearSnapshot = true
            }
        }
        lock.unlock()

        if shouldClearSnapshot {
            notifyActivityUpdated(nil, winner: nil)
        }

        if shouldPoll {
            queue.async { [weak self] in
                self?.pollOnce()
            }
        }
    }

    private func startTimerLocked() {
        stopTimerLocked()

        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(
            deadline: .now() + pollIntervalSeconds,
            repeating: pollIntervalSeconds,
            leeway: .milliseconds(500)
        )
        timer.setEventHandler { [weak self] in
            self?.pollOnce()
        }
        timer.resume()
        self.pollTimer = timer
    }

    private func stopTimerLocked() {
        pollTimer?.cancel()
        pollTimer = nil
    }

    public func pollOnce() {
        lock.lock()
        if let snapshot = currentSnapshot, !snapshot.isFresh(maxAgeSeconds: snapshotTTLSeconds) {
            currentSnapshot = nil
            winningTool = nil
            winnerSelector.reset()
            notifyActivityUpdated(nil, winner: nil)
        }
        guard isEligibleToPoll, (connectionStatus == .connected || connectionStatus == .couldNotReach), let endpoint = currentEndpoint else {
            lock.unlock()
            return
        }
        guard !isPollingInProgress else {
            lock.unlock()
            return
        }

        // Check credential in Keychain
        guard let credential = try? keychain.loadCredential(forService: KeychainService.defaultService, account: endpoint.origin),
              !credential.accessToken.isEmpty else {
            self.connectionStatus = .notConnected
            self.currentSnapshot = nil
            self.winningTool = nil
            self.winnerSelector.reset()
            let status = self.connectionStatus
            lock.unlock()
            notifyStatusChanged(status, message: "Paste the T3 address, then connect.")
            notifyActivityUpdated(nil, winner: nil)
            return
        }

        if credential.isExpired() {
            self.connectionStatus = .signInAgain
            self.currentSnapshot = nil
            self.winningTool = nil
            self.winnerSelector.reset()
            self.lastErrorMessage = "Your T3 Code sign-in expired. Connect again to keep showing activity."
            let status = self.connectionStatus
            let msg = self.lastErrorMessage
            lock.unlock()
            notifyStatusChanged(status, message: msg)
            notifyActivityUpdated(nil, winner: nil)
            return
        }

        isPollingInProgress = true
        let gen = pollGeneration
        let token = credential.accessToken
        lock.unlock()

        let task = Task {
            do {
                let (providers, threads) = try await mcpClient.poll(endpoint: endpoint, token: token)

                var shouldNotify = false
                var newSnapshot: T3ActivitySnapshot?
                var newWinner: String?

                lock.lock()
                defer {
                    self.isPollingInProgress = false
                    self.activePollTask = nil
                    self.lock.unlock()
                }

                guard !Task.isCancelled, self.pollGeneration == gen, self.isEligibleToPoll else {
                    return
                }

                let (snapshot, winner) = self.winnerSelector.evaluate(
                    providers: providers,
                    threads: threads,
                    now: Date()
                )

                self.currentSnapshot = snapshot
                self.winningTool = winner
                let recovered = self.connectionStatus != .connected
                self.connectionStatus = .connected
                self.lastErrorMessage = nil
                newSnapshot = snapshot
                newWinner = winner
                shouldNotify = true

                if recovered { self.notifyStatusChanged(.connected, message: nil) }
                if shouldNotify {
                    self.notifyActivityUpdated(newSnapshot, winner: newWinner)
                }
            } catch let error as McpClientError {
                var newStatus = T3ConnectionStatus.couldNotReach
                var newMsg: String?
                var shouldNotifyStatus = false

                lock.lock()
                defer {
                    self.isPollingInProgress = false
                    self.activePollTask = nil
                    self.lock.unlock()
                }

                guard !Task.isCancelled, self.pollGeneration == gen else { return }

                self.currentSnapshot = nil
                self.winningTool = nil
                self.winnerSelector.reset()

                if error == .unauthorized {
                    self.connectionStatus = .signInAgain
                    self.lastErrorMessage = "Your T3 Code sign-in expired. Connect again to keep showing activity."
                } else {
                    self.connectionStatus = .couldNotReach
                    self.lastErrorMessage = "Check that T3 Code is running and the address is right."
                }

                newStatus = self.connectionStatus
                newMsg = self.lastErrorMessage
                shouldNotifyStatus = true

                if shouldNotifyStatus {
                    self.notifyStatusChanged(newStatus, message: newMsg)
                    self.notifyActivityUpdated(nil, winner: nil)
                }
            } catch {
                lock.lock()
                defer {
                    self.isPollingInProgress = false
                    self.activePollTask = nil
                    self.lock.unlock()
                }

                guard !Task.isCancelled, self.pollGeneration == gen else { return }

                self.currentSnapshot = nil
                self.winningTool = nil
                self.winnerSelector.reset()
                self.connectionStatus = .couldNotReach
                self.lastErrorMessage = "Check that T3 Code is running and the address is right."

                let status = self.connectionStatus
                let msg = self.lastErrorMessage

                self.notifyStatusChanged(status, message: msg)
                self.notifyActivityUpdated(nil, winner: nil)
            }
        }

        lock.lock()
        self.activePollTask = task
        lock.unlock()
    }

    public func currentFormattedActivity(showAppIcons: Bool, now: Date = Date()) -> T3FormattedActivity {
        lock.lock()
        defer { lock.unlock() }

        return T3ActivityFormatter.format(
            snapshot: currentSnapshot,
            winningTool: winningTool,
            showAppIcons: showAppIcons,
            now: now
        )
    }

    private func notifyStatusChanged(_ status: T3ConnectionStatus, message: String?) {
        DispatchQueue.main.async { [weak self, onStatusChanged] in
            guard self != nil else { return }
            onStatusChanged?(status, message)
        }
    }

    private func notifyActivityUpdated(_ snapshot: T3ActivitySnapshot?, winner: String?) {
        DispatchQueue.main.async { [weak self, onActivityUpdated] in
            guard self != nil else { return }
            onActivityUpdated?(snapshot, winner)
        }
    }
}
