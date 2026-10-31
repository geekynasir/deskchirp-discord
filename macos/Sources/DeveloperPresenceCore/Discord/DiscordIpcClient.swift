import Foundation

/// Configuration options for DiscordIpcClient timing and retry intervals.
public struct DiscordClientConfiguration: Sendable {
    public var minPublishInterval: TimeInterval
    public var handshakeTimeout: TimeInterval
    public var commandTimeout: TimeInterval
    public var retryInterval: TimeInterval
    public var connectTimeout: TimeInterval

    public init(
        minPublishInterval: TimeInterval = 5.0,
        handshakeTimeout: TimeInterval = 5.0,
        commandTimeout: TimeInterval = 5.0,
        retryInterval: TimeInterval = 4.0,
        connectTimeout: TimeInterval = 2.0
    ) {
        self.minPublishInterval = minPublishInterval
        self.handshakeTimeout = handshakeTimeout
        self.commandTimeout = commandTimeout
        self.retryInterval = retryInterval
        self.connectTimeout = connectTimeout
    }
}

/// Discord Rich Presence IPC Client.
///
/// Protocol and concurrency guarantees:
/// - A dedicated serial background queue owns all transport and protocol state.
/// - Opcode 0 handshake with public client ID (15..20 ASCII digits).
/// - 8-byte LE frames (opcode, length + UTF-8 JSON); whole-frame transmission.
/// - Incremental bounded parser capped at 64 KiB.
/// - Awaits DISPATCH / READY before sending SET_ACTIVITY.
/// - At most 1 in-flight command; tracks actual sent snapshot in ACK.
/// - Duplicate suppression: identical activities are not republished.
/// - Bounded socket scanning & candidate rotation to bypass dead sockets.
/// - Nonce tracking, ack verification, ERROR handling, and command timeouts.
/// - Responds to PING (op 3) with PONG (op 4).
/// - Handles CLOSE (op 2), EOF, errors, and bounded reconnection retries.
/// - 5-second update throttle coalesces updates.
/// - Immediate clear & disconnect on disable, pause, unsupported focus, sleep, or lock.
/// - Generation guards protect against stale asynchronous callbacks.
public final class DiscordIpcClient: @unchecked Sendable {
    private let queue: DispatchQueue
    private let transport: DiscordTransport
    private let parser: DiscordFrameParser
    private let configuration: DiscordClientConfiguration
    private let candidatePathsProvider: @Sendable () -> [String]

    // Thread-safe generation counter to guard asynchronous queue-hops
    private let lock = NSLock()
    private var generation: Int = 0
    private var requestVersion: Int = 0
    private var requestAllowed = false
    private var candidateAttempt: Int = 0
    private var accountId: String?
    public var publishAllowed: (@Sendable (String?) -> Bool)?
    public var onAccountChanged: (@Sendable (String?) -> Void)?

    public var currentUserId: String? {
        lock.lock(); defer { lock.unlock() }
        return accountId
    }

    // State owned strictly by `queue`
    private var isConnected: Bool = false
    private var isReady: Bool = false
    private var isConnecting: Bool = false
    private var currentClientId: String = ""
    private var desiredActivity: DiscordActivity?
    private var lastPublishedActivity: DiscordActivity?
    private var lastWriteTime: Date?

    // In-flight command tracking (at most 1 in-flight)
    private var inFlightNonce: String?
    private var inFlightActivity: DiscordActivity?

    // Candidate socket rotation state
    private var candidateIndex: Int = 0
    private var attemptsInCurrentRound: Int = 0

    // Managed timers & work items
    private var throttleWorkItem: DispatchWorkItem?
    private var retryWorkItem: DispatchWorkItem?
    private var handshakeTimeoutWorkItem: DispatchWorkItem?
    private var commandTimeoutWorkItem: DispatchWorkItem?
    private var connectTimeoutWorkItem: DispatchWorkItem?

    // Public callbacks dispatched from queue (safe to bridge to UI or Coordinator)
    public var onStatusChanged: (@Sendable (PresenceConnectionStatus, String?) -> Void)?
    public var onActivityAcknowledged: (@Sendable (DiscordActivity?) -> Void)?

    public init(
        transport: DiscordTransport? = nil,
        queue: DispatchQueue = DispatchQueue(label: "com.developerpresence.discord.client"),
        configuration: DiscordClientConfiguration = DiscordClientConfiguration(),
        candidatePathsProvider: (@Sendable () -> [String])? = nil
    ) {
        self.queue = queue
        self.configuration = configuration
        self.candidatePathsProvider = candidatePathsProvider ?? {
            DiscordSocketLocator.prioritizedCandidatePaths()
        }
        let assignedTransport = transport ?? NetworkFrameworkTransport(queue: queue)
        self.transport = assignedTransport
        self.parser = DiscordFrameParser()

        self.setupTransportCallbacks()
    }

    private func currentGeneration() -> Int {
        lock.lock()
        defer { lock.unlock() }
        return generation
    }

    private func hasEligibleRequest() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return requestAllowed
    }

    private func setAccount(_ id: String?) {
        lock.lock()
        let changed = accountId != id
        accountId = id
        lock.unlock()
        if changed { onAccountChanged?(id) }
    }

    private func incrementGeneration() -> Int {
        lock.lock()
        defer { lock.unlock() }
        generation += 1
        return generation
    }

    private func setupTransportCallbacks() {
        transport.onDataReceived = { [weak self] data in
            guard let self = self else { return }
            let gen = self.currentGeneration()
            self.queue.async { [weak self] in
                guard let self = self, self.currentGeneration() == gen else { return }
                self.handleIncomingData(data)
            }
        }

        transport.onDisconnected = { [weak self] error in
            guard let self = self else { return }
            let gen = self.currentGeneration()
            self.queue.async { [weak self] in
                guard let self = self, self.currentGeneration() == gen else { return }
                self.handleTransportDisconnection(error: error)
            }
        }
    }

    // MARK: - Public API

    /// Read the account from READY without sending an activity on a standby computer.
    public func observeAccount(clientId: String) {
        let id = clientId.trimmingCharacters(in: .whitespacesAndNewlines)
        guard isValidClientId(id) else { return }
        lock.lock(); requestAllowed = true; lock.unlock()
        queue.async { [weak self] in
            guard let self = self, self.hasEligibleRequest() else { return }
            if self.currentClientId != id || self.desiredActivity != nil {
                self.clearAndCloseImmediatelyInternal()
                self.currentClientId = id
            }
            if !self.isConnected && !self.isConnecting && self.retryWorkItem == nil { self.initiateConnection() }
        }
    }

    /// Requests activity publication or clearing.
    /// If activity is nil, presence is cleared and connection is cleanly closed immediately.
    public func updateActivity(
        _ activity: DiscordActivity?,
        clientId: String,
        immediate: Bool = false
    ) {
        let submittedId = clientId.trimmingCharacters(in: .whitespacesAndNewlines)
        lock.lock()
        requestVersion += 1
        let request = requestVersion
        requestAllowed = activity != nil && isValidClientId(submittedId)
        if !requestAllowed { generation += 1 }
        lock.unlock()
        queue.async { [weak self] in
            guard let self = self else { return }
            self.lock.lock()
            let isCurrentRequest = self.requestVersion == request
            self.lock.unlock()
            guard isCurrentRequest else { return }

            let trimmedId = clientId.trimmingCharacters(in: .whitespacesAndNewlines)

            // Client ID changed: close existing connection
            if self.currentClientId != trimmedId {
                self.clearAndCloseImmediatelyInternal()
                self.currentClientId = trimmedId
            }

            if activity == nil {
                self.clearAndCloseImmediatelyInternal()
                return
            }

            guard self.isValidClientId(trimmedId) else {
                self.desiredActivity = nil
                self.clearAndCloseImmediatelyInternal()
                self.onStatusChanged?(.error, "Invalid Discord Application ID")
                return
            }

            self.desiredActivity = activity

            if !self.isConnected && !self.isConnecting {
                self.initiateConnection()
            } else if self.isReady {
                self.scheduleOrDispatchActivity(immediate: immediate)
            }
        }
    }

    /// Immediately clears Discord presence and shuts down local socket connection.
    public func clearAndCloseImmediately() {
        _ = incrementGeneration()
        lock.lock()
        requestVersion += 1
        requestAllowed = false
        lock.unlock()
        queue.async { [weak self] in
            self?.clearAndCloseImmediatelyInternal()
        }
    }

    private func isValidClientId(_ id: String) -> Bool {
        guard id.count >= 15 && id.count <= 20 else { return false }
        return id.utf8.allSatisfy { $0 >= 48 && $0 <= 57 }
    }

    // MARK: - Internal Lifecycle (Must run on `queue`)

    private func clearAndCloseImmediatelyInternal() {
        _ = incrementGeneration()
        cancelAllTimers()

        let wasReady = isConnected && isReady

        isConnected = false
        isReady = false
        isConnecting = false
        desiredActivity = nil
        lastPublishedActivity = nil
        lastWriteTime = nil
        inFlightActivity = nil
        inFlightNonce = nil
        attemptsInCurrentRound = 0
        parser.reset()
        setAccount(nil)

        if wasReady {
            let clearGeneration = currentGeneration()
            var hasClosed = false
            let closeOnce: @Sendable () -> Void = { [weak self] in
                guard let self = self else { return }
                self.queue.async {
                    guard self.currentGeneration() == clearGeneration, !hasClosed else { return }
                    hasClosed = true
                    self.transport.disconnect()
                }
            }

            // Safety timeout: promptly close socket regardless after 200ms
            queue.asyncAfter(deadline: .now() + 0.2) {
                closeOnce()
            }

            sendClearActivityFrame {
                closeOnce()
            }
        } else {
            transport.disconnect()
        }

        onStatusChanged?(.disconnected, "Cleared and disconnected")
    }

    private func initiateConnection() {
        guard hasEligibleRequest(), !isConnected && !isConnecting else { return }

        _ = incrementGeneration()
        retryWorkItem?.cancel()
        retryWorkItem = nil
        isConnecting = true
        attemptsInCurrentRound = 0

        let gen = currentGeneration()

        // Single timeout covering connection and handshake
        handshakeTimeoutWorkItem?.cancel()
        let handshakeTimeoutItem = DispatchWorkItem { [weak self] in
            guard let self = self, self.currentGeneration() == gen, (!self.isConnected || !self.isReady) else { return }
            self.handshakeTimeoutWorkItem = nil
            let err = TransportError.timeout
            self.onStatusChanged?(.error, "Connection and handshake timed out.")
            self.handleTransportDisconnection(error: err)
        }
        self.handshakeTimeoutWorkItem = handshakeTimeoutItem
        queue.asyncAfter(deadline: .now() + configuration.handshakeTimeout, execute: handshakeTimeoutItem)

        attemptNextCandidate()
    }

    private func attemptNextCandidate() {
        guard isConnecting, !isConnected else { return }

        let candidates = candidatePathsProvider()
        guard !candidates.isEmpty else {
            isConnecting = false
            handshakeTimeoutWorkItem?.cancel()
            handshakeTimeoutWorkItem = nil
            onStatusChanged?(.error, "No Discord IPC candidate sockets found.")
            scheduleReconnectionRetry()
            return
        }

        let maxAttempts = min(candidates.count, 10)
        if attemptsInCurrentRound >= maxAttempts {
            // All candidates in this round probed without success. Back off to periodic retry.
            isConnecting = false
            attemptsInCurrentRound = 0
            handshakeTimeoutWorkItem?.cancel()
            handshakeTimeoutWorkItem = nil
            onStatusChanged?(.connecting, "Connecting to Discord (retrying)...")
            scheduleReconnectionRetry()
            return
        }

        candidateAttempt += 1
        let attempt = candidateAttempt
        let path = candidates[candidateIndex % candidates.count]
        candidateIndex += 1
        attemptsInCurrentRound += 1

        onStatusChanged?(.connecting, "Connecting to Discord at \(path)...")

        let gen = currentGeneration()

        connectTimeoutWorkItem?.cancel()
        let connectTimeout = DispatchWorkItem { [weak self] in
            guard let self = self, self.currentGeneration() == gen, self.candidateAttempt == attempt, self.isConnecting, !self.isConnected else { return }
            self.connectTimeoutWorkItem = nil
            self.transport.disconnect()
            self.attemptNextCandidate()
        }
        self.connectTimeoutWorkItem = connectTimeout
        queue.asyncAfter(deadline: .now() + configuration.connectTimeout, execute: connectTimeout)

        transport.connect(to: path) { [weak self] result in
            guard let self = self else { return }
            self.queue.async { [weak self] in
                guard let self = self, self.currentGeneration() == gen,
                      self.candidateAttempt == attempt, self.isConnecting, !self.isConnected else { return }
                self.connectTimeoutWorkItem?.cancel()
                self.connectTimeoutWorkItem = nil

                switch result {
                case .success:
                    self.isConnected = true
                    self.attemptsInCurrentRound = 0
                    self.sendHandshake(currentGen: gen)
                case .failure:
                    self.transport.disconnect()
                    self.attemptNextCandidate()
                }
            }
        }
    }

    private func sendHandshake(currentGen: Int) {
        let handshake = DiscordHandshakePayload(clientId: currentClientId)
        guard let jsonData = try? JSONEncoder().encode(handshake),
              let frame = try? DiscordIpcFrame(opcode: DiscordIpcFrame.opcodeHandshake, payload: jsonData) else {
            handleTransportDisconnection(error: TransportError.connectionFailed("Failed to encode handshake."))
            return
        }

        transport.send(data: frame.encode()) { [weak self] result in
            guard let self = self else { return }
            self.queue.async { [weak self] in
                guard let self = self, self.currentGeneration() == currentGen else { return }
                if case .failure(let error) = result {
                    self.handleTransportDisconnection(error: error)
                }
            }
        }
    }

    private func handleIncomingData(_ data: Data) {
        do {
            let frames = try parser.feed(data)
            let frameGeneration = currentGeneration()
            for frame in frames {
                guard currentGeneration() == frameGeneration else { break }
                try processFrame(frame)
            }
        } catch {
            onStatusChanged?(.error, "Discord framing error: \(error.localizedDescription)")
            handleTransportDisconnection(error: error)
        }
    }

    private func processFrame(_ frame: DiscordIpcFrame) throws {
        switch frame.opcode {
        case DiscordIpcFrame.opcodePing:
            // Respond with Pong immediately using same payload
            let pongFrame = try DiscordIpcFrame(opcode: DiscordIpcFrame.opcodePong, payload: frame.payload)
            transport.send(data: pongFrame.encode()) { _ in }

        case DiscordIpcFrame.opcodeClose:
            onStatusChanged?(.disconnected, "Discord closed IPC connection.")
            handleTransportDisconnection(error: nil)

        case DiscordIpcFrame.opcodeFrame:
            try handleCommandFrame(frame)

        default:
            break
        }
    }

    private func handleCommandFrame(_ frame: DiscordIpcFrame) throws {
        guard let jsonObject = try JSONSerialization.jsonObject(with: frame.payload) as? [String: Any] else {
            throw FrameError.invalidJson
        }

        let cmd = jsonObject["cmd"] as? String
        let evt = jsonObject["evt"] as? String
        let nonce = jsonObject["nonce"] as? String

        // Handle READY event from handshake
        if cmd == "DISPATCH" && evt == "READY" {
            guard isConnecting, isConnected, hasEligibleRequest() else { return }
            handshakeTimeoutWorkItem?.cancel()
            handshakeTimeoutWorkItem = nil
            isConnecting = false
            isConnected = true
            isReady = true
            let data = jsonObject["data"] as? [String: Any]
            let user = data?["user"] as? [String: Any]
            setAccount(user?["id"] as? String)
            onStatusChanged?(.connected, "Connected to Discord")

            // Dispatch pending activity once READY
            if desiredActivity != nil {
                scheduleOrDispatchActivity(immediate: true)
            }
            return
        }

        if evt == "ERROR", inFlightNonce == nil {
            handleTransportDisconnection(error: TransportError.connectionFailed("Discord rejected the connection."))
            return
        }

        // Handle SET_ACTIVITY response (must match cmd SET_ACTIVITY and in-flight nonce)
        if cmd == "SET_ACTIVITY" {
            guard let nonce = nonce, let inFlight = inFlightNonce, nonce == inFlight else {
                return
            }

            commandTimeoutWorkItem?.cancel()
            commandTimeoutWorkItem = nil

            if evt == "ERROR" {
                let errorData = jsonObject["data"] as? [String: Any]
                let message = errorData?["message"] as? String ?? "Unknown Discord error"
                inFlightNonce = nil
                inFlightActivity = nil
                let err = TransportError.connectionFailed("Discord SET_ACTIVITY error: \(message)")
                onStatusChanged?(.error, err.localizedDescription)
                handleTransportDisconnection(error: err)
                return
            }

            guard evt == nil else { return }
            // Success acknowledgment: acknowledge the snapshot actually sent
            let acknowledged = inFlightActivity
            inFlightNonce = nil
            inFlightActivity = nil
            lastPublishedActivity = acknowledged
            onActivityAcknowledged?(acknowledged)

            // If desiredActivity changed while in-flight, coalesce next dispatch >= minPublishInterval from write
            if let desired = desiredActivity, desired != lastPublishedActivity {
                scheduleOrDispatchActivity(immediate: false)
            }
        }
    }

    private func scheduleOrDispatchActivity(immediate: Bool) {
        guard isConnected, isReady, let activity = desiredActivity else { return }
        guard inFlightNonce == nil else { return }

        // Duplicate suppression: do not republish if already acknowledged as current
        if activity == lastPublishedActivity {
            return
        }

        let now = Date()
        let minInterval = configuration.minPublishInterval
        let elapsed = lastWriteTime.map { now.timeIntervalSince($0) } ?? minInterval

        if elapsed >= minInterval {
            throttleWorkItem?.cancel()
            throttleWorkItem = nil
            dispatchSetActivity(activity)
        } else {
            guard throttleWorkItem == nil else { return }

            let delay = max(0.0, minInterval - elapsed)
            let currentGen = currentGeneration()
            let workItem = DispatchWorkItem { [weak self] in
                guard let self = self, self.currentGeneration() == currentGen else { return }
                self.throttleWorkItem = nil
                if let latest = self.desiredActivity, latest != self.lastPublishedActivity, self.inFlightNonce == nil {
                    self.dispatchSetActivity(latest)
                }
            }
            self.throttleWorkItem = workItem
            queue.asyncAfter(deadline: .now() + delay, execute: workItem)
        }
    }

    private func dispatchSetActivity(_ activity: DiscordActivity) {
        guard hasEligibleRequest(), isConnected, isReady, desiredActivity != nil, inFlightNonce == nil else { return }
        guard publishAllowed?(currentUserId) != false else { return }

        let nonce = UUID().uuidString
        let pid = ProcessInfo.processInfo.processIdentifier
        let payload = DiscordSetActivityPayload(pid: pid, activity: activity, nonce: nonce)

        guard let jsonData = try? JSONEncoder().encode(payload),
              let frame = try? DiscordIpcFrame(opcode: DiscordIpcFrame.opcodeFrame, payload: jsonData) else {
            return
        }

        inFlightNonce = nonce
        inFlightActivity = activity
        lastWriteTime = Date()

        let currentGen = currentGeneration()

        commandTimeoutWorkItem?.cancel()
        let timeoutItem = DispatchWorkItem { [weak self] in
            guard let self = self, self.currentGeneration() == currentGen, self.inFlightNonce == nonce else { return }
            self.commandTimeoutWorkItem = nil
            self.inFlightNonce = nil
            self.inFlightActivity = nil
            let err = TransportError.timeout
            self.onStatusChanged?(.error, "SET_ACTIVITY acknowledgment timed out.")
            self.handleTransportDisconnection(error: err)
        }
        self.commandTimeoutWorkItem = timeoutItem
        queue.asyncAfter(deadline: .now() + configuration.commandTimeout, execute: timeoutItem)

        transport.send(data: frame.encode()) { [weak self] result in
            guard let self = self else { return }
            self.queue.async { [weak self] in
                guard let self = self, self.currentGeneration() == currentGen, self.inFlightNonce == nonce else { return }
                if case .failure(let error) = result {
                    self.commandTimeoutWorkItem?.cancel()
                    self.commandTimeoutWorkItem = nil
                    self.inFlightNonce = nil
                    self.inFlightActivity = nil
                    self.onStatusChanged?(.error, "Failed to send SET_ACTIVITY: \(error.localizedDescription)")
                    self.handleTransportDisconnection(error: error)
                }
            }
        }
    }

    private func sendClearActivityFrame(completion: @escaping @Sendable () -> Void) {
        let nonce = UUID().uuidString
        let pid = ProcessInfo.processInfo.processIdentifier
        let payload = DiscordSetActivityPayload(pid: pid, activity: nil, nonce: nonce)

        guard let jsonData = try? JSONEncoder().encode(payload),
              let frame = try? DiscordIpcFrame(opcode: DiscordIpcFrame.opcodeFrame, payload: jsonData) else {
            completion()
            return
        }

        transport.send(data: frame.encode()) { _ in
            completion()
        }
    }

    private func handleTransportDisconnection(error: Error?) {
        _ = incrementGeneration()
        cancelAllTimers()

        transport.disconnect()

        isConnected = false
        isReady = false
        isConnecting = false
        parser.reset()
        setAccount(nil)

        // Reset duplicate-suppression and publish state so reconnection restores activity
        inFlightActivity = nil
        inFlightNonce = nil
        lastPublishedActivity = nil
        lastWriteTime = nil
        attemptsInCurrentRound = 0

        if let err = error {
            onStatusChanged?(.error, "Discord disconnected: \(err.localizedDescription)")
        } else {
            onStatusChanged?(.disconnected, "Discord IPC disconnected.")
        }

        // If an eligible activity remains desired, schedule reconnection retry
        if hasEligibleRequest() {
            scheduleReconnectionRetry()
        }
    }

    private func scheduleReconnectionRetry() {
        guard retryWorkItem == nil, hasEligibleRequest() else { return }

        let currentGen = currentGeneration()
        let retryItem = DispatchWorkItem { [weak self] in
            guard let self = self, self.currentGeneration() == currentGen, self.hasEligibleRequest() else { return }
            self.retryWorkItem = nil
            self.initiateConnection()
        }
        self.retryWorkItem = retryItem
        queue.asyncAfter(deadline: .now() + configuration.retryInterval, execute: retryItem)
    }

    private func cancelAllTimers() {
        throttleWorkItem?.cancel()
        throttleWorkItem = nil
        retryWorkItem?.cancel()
        retryWorkItem = nil
        handshakeTimeoutWorkItem?.cancel()
        handshakeTimeoutWorkItem = nil
        commandTimeoutWorkItem?.cancel()
        commandTimeoutWorkItem = nil
        connectTimeoutWorkItem?.cancel()
        connectTimeoutWorkItem = nil
    }
}
