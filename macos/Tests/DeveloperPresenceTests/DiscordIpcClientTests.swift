import XCTest
@testable import DeveloperPresenceCore

final class ScriptedMockDiscordTransport: DiscordTransport, @unchecked Sendable {
    var onDataReceived: (@Sendable (Data) -> Void)?
    var onDisconnected: (@Sendable (Error?) -> Void)?

    private let lock = NSLock()
    private let handshakeSent = DispatchSemaphore(value: 0)
    private let activitySent = DispatchSemaphore(value: 0)
    var isConnected: Bool = false
    var connectedPaths: [String] = []
    var disconnectCount: Int = 0
    var sentFrames: [DiscordIpcFrame] = []
    var rawSentData: [Data] = []

    var connectHandler: (@Sendable (String) -> Result<Void, Error>)?
    var sendHandler: (@Sendable (Data) -> Result<Void, Error>)?
    var onFrameSent: (@Sendable (DiscordIpcFrame) -> Void)?

    func connect(to path: String, completion: @escaping @Sendable (Result<Void, Error>) -> Void) {
        lock.lock()
        connectedPaths.append(path)
        let handler = connectHandler
        lock.unlock()

        let result = handler?(path) ?? .success(())
        lock.lock()
        if case .success = result {
            isConnected = true
        }
        lock.unlock()
        completion(result)
    }

    func send(data: Data, completion: @escaping @Sendable (Result<Void, Error>) -> Void) {
        lock.lock()
        rawSentData.append(data)
        let parser = DiscordFrameParser()
        let frames = (try? parser.feed(data)) ?? []
        sentFrames.append(contentsOf: frames)
        let sHandler = sendHandler
        let frameCallback = onFrameSent
        lock.unlock()

        for frame in frames {
            if frame.opcode == DiscordIpcFrame.opcodeHandshake {
                handshakeSent.signal()
            } else if frame.opcode == DiscordIpcFrame.opcodeFrame,
                      let object = try? JSONSerialization.jsonObject(with: frame.payload) as? [String: Any],
                      let args = object["args"] as? [String: Any],
                      args["activity"] is [String: Any] {
                activitySent.signal()
            }
            frameCallback?(frame)
        }

        let result = sHandler?(data) ?? .success(())
        completion(result)
    }

    func disconnect() {
        lock.lock()
        isConnected = false
        disconnectCount += 1
        lock.unlock()
    }

    func snapshotIsConnected() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return isConnected
    }

    func simulateIncomingData(_ data: Data) {
        onDataReceived?(data)
    }

    func simulateIncomingFrame(opcode: UInt32, json: String) {
        if let frame = try? DiscordIpcFrame(opcode: opcode, jsonString: json) {
            onDataReceived?(frame.encode())
        }
    }

    func simulateReady(waitForHandshake: Bool = true) {
        // A real Discord server cannot send READY before receiving the
        // handshake. Synchronize with the client queue instead of racing it.
        if waitForHandshake {
            XCTAssertEqual(handshakeSent.wait(timeout: .now() + 2), .success)
        }
        simulateIncomingFrame(
            opcode: DiscordIpcFrame.opcodeFrame,
            json: "{\"cmd\":\"DISPATCH\",\"evt\":\"READY\",\"data\":{\"v\":1}}"
        )
    }

    func waitForActivityFrame() {
        XCTAssertEqual(activitySent.wait(timeout: .now() + 2), .success)
    }

    func simulateActivityAck(nonce: String) {
        simulateIncomingFrame(
            opcode: DiscordIpcFrame.opcodeFrame,
            json: "{\"cmd\":\"SET_ACTIVITY\",\"data\":{},\"nonce\":\"\(nonce)\"}"
        )
    }

    func simulateErrorAck(nonce: String, message: String) {
        simulateIncomingFrame(
            opcode: DiscordIpcFrame.opcodeFrame,
            json: "{\"cmd\":\"SET_ACTIVITY\",\"evt\":\"ERROR\",\"data\":{\"message\":\"\(message)\"},\"nonce\":\"\(nonce)\"}"
        )
    }

    func simulateDisconnection(error: Error? = nil) {
        lock.lock()
        isConnected = false
        lock.unlock()
        onDisconnected?(error)
    }
}

final class DiscordIpcClientTests: XCTestCase {
    private let testClientId = "123456789012345678"

    private func makeFastConfig() -> DiscordClientConfiguration {
        DiscordClientConfiguration(
            minPublishInterval: 0.05,
            handshakeTimeout: 2,
            commandTimeout: 0.4,
            retryInterval: 0.5,
            connectTimeout: 0.5
        )
    }

    func testOldClearTimeoutCannotDisconnectAReplacementIdentity() {
        let mock = ScriptedMockDiscordTransport()
        let client = DiscordIpcClient(
            transport: mock,
            configuration: makeFastConfig(),
            candidatePathsProvider: { ["/tmp/test-discord-ipc"] }
        )
        let first = expectation(description: "First identity acknowledged")
        let second = expectation(description: "Replacement identity acknowledged")
        mock.onFrameSent = { frame in
            if frame.opcode == DiscordIpcFrame.opcodeHandshake {
                mock.simulateReady()
            } else if let object = try? JSONSerialization.jsonObject(with: frame.payload) as? [String: Any],
                      let nonce = object["nonce"] as? String {
                mock.simulateActivityAck(nonce: nonce)
            }
        }
        client.onActivityAcknowledged = { activity in
            if activity?.details == "Using T3 Code" { first.fulfill() }
            if activity?.details == "Using Codex" { second.fulfill() }
        }
        client.updateActivity(DiscordActivity(details: "Using T3 Code"), clientId: testClientId)
        wait(for: [first], timeout: 1)
        client.updateActivity(DiscordActivity(details: "Using Codex"), clientId: "222222222222222222")
        wait(for: [second], timeout: 1)
        let oldTimeout = expectation(description: "Old clear safety timeout elapsed")
        DispatchQueue.global().asyncAfter(deadline: .now() + 0.3) {
            XCTAssertTrue(mock.snapshotIsConnected())
            oldTimeout.fulfill()
        }
        wait(for: [oldTimeout], timeout: 1)
        client.clearAndCloseImmediately()
    }

    // 1. Handshake occurs, waits for READY before sending SET_ACTIVITY
    func testConnect_sendsHandshake_awaitsReadyBeforeActivity() {
        let mock = ScriptedMockDiscordTransport()
        let client = DiscordIpcClient(
            transport: mock,
            configuration: makeFastConfig(),
            candidatePathsProvider: { ["/tmp/discord-ipc-0"] }
        )

        let handshakeExp = expectation(description: "Handshake sent")
        let readyAndActivityExp = expectation(description: "SET_ACTIVITY sent after READY")

        var handshakeSeen = false
        var activityNonce: String?

        mock.onFrameSent = { frame in
            if frame.opcode == DiscordIpcFrame.opcodeHandshake {
                handshakeSeen = true
                guard let json = frame.payloadString() else { return }
                XCTAssertTrue(json.contains("\"v\":1"))
                XCTAssertTrue(json.contains(self.testClientId))
                handshakeExp.fulfill()
            } else if frame.opcode == DiscordIpcFrame.opcodeFrame {
                XCTAssertTrue(handshakeSeen, "SET_ACTIVITY must not be sent before handshake")
                if let json = frame.payloadString(), json.contains("SET_ACTIVITY") {
                    XCTAssertTrue(json.contains("Using T3 Code"))
                    // Extract nonce
                    if let obj = try? JSONSerialization.jsonObject(with: frame.payload) as? [String: Any],
                       let nonce = obj["nonce"] as? String {
                        activityNonce = nonce
                    }
                    readyAndActivityExp.fulfill()
                }
            }
        }

        client.updateActivity(
            DiscordActivity(details: "Using T3 Code"),
            clientId: testClientId,
            immediate: true
        )

        wait(for: [handshakeExp], timeout: 1.0)

        // Before READY, no activity frame should have been sent
        XCTAssertEqual(mock.sentFrames.filter { $0.opcode == DiscordIpcFrame.opcodeFrame }.count, 0)

        // Server sends READY
        mock.simulateReady()

        wait(for: [readyAndActivityExp], timeout: 1.0)
        XCTAssertNotNil(activityNonce)

        client.clearAndCloseImmediately()
    }

    // 2. Duplicate suppression: duplicate calls do not send repeated commands
    func testDuplicateSuppression_doesNotResendIdenticalActivity() {
        let mock = ScriptedMockDiscordTransport()
        let client = DiscordIpcClient(
            transport: mock,
            configuration: makeFastConfig(),
            candidatePathsProvider: { ["/tmp/discord-ipc-0"] }
        )

        let activityExp = expectation(description: "First activity sent")
        var recordedNonce: String?

        mock.onFrameSent = { frame in
            if frame.opcode == DiscordIpcFrame.opcodeFrame {
                if let obj = try? JSONSerialization.jsonObject(with: frame.payload) as? [String: Any],
                   let cmd = obj["cmd"] as? String, cmd == "SET_ACTIVITY" {
                    recordedNonce = obj["nonce"] as? String
                    activityExp.fulfill()
                }
            }
        }

        client.updateActivity(
            DiscordActivity(details: "Using T3 Code"),
            clientId: testClientId,
            immediate: true
        )

        mock.simulateReady()
        wait(for: [activityExp], timeout: 1.0)

        guard let nonce = recordedNonce else {
            XCTFail("Missing nonce")
            return
        }

        // Acknowledge the first activity
        mock.simulateActivityAck(nonce: nonce)

        let initialActivityFrameCount = mock.sentFrames.filter { $0.opcode == DiscordIpcFrame.opcodeFrame }.count

        // Now call updateActivity with identical activity repeatedly
        client.updateActivity(
            DiscordActivity(details: "Using T3 Code"),
            clientId: testClientId,
            immediate: true
        )
        client.updateActivity(
            DiscordActivity(details: "Using T3 Code"),
            clientId: testClientId,
            immediate: true
        )

        let waitExp = expectation(description: "Short wait for queue processing")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            let currentActivityFrameCount = mock.sentFrames.filter { $0.opcode == DiscordIpcFrame.opcodeFrame }.count
            XCTAssertEqual(initialActivityFrameCount, currentActivityFrameCount, "Duplicate activity must be suppressed")
            waitExp.fulfill()
        }
        wait(for: [waitExp], timeout: 1.0)

        client.clearAndCloseImmediately()
    }

    // 3. ACK snapshot coalescing: setting B before ACK of A acknowledges A not B, then publishes B
    func testAckSnapshotCoalescing_desiredBBeforeAckOfA_acknowledgesANotB_thenPublishesB() {
        let mock = ScriptedMockDiscordTransport()
        let client = DiscordIpcClient(
            transport: mock,
            configuration: makeFastConfig(),
            candidatePathsProvider: { ["/tmp/discord-ipc-0"] }
        )

        let firstFrameExp = expectation(description: "Activity A sent")
        let secondFrameExp = expectation(description: "Activity B sent")
        var nonceA: String?
        var nonceB: String?

        mock.onFrameSent = { frame in
            guard frame.opcode == DiscordIpcFrame.opcodeFrame,
                  let obj = try? JSONSerialization.jsonObject(with: frame.payload) as? [String: Any],
                  let cmd = obj["cmd"] as? String, cmd == "SET_ACTIVITY",
                  let nonce = obj["nonce"] as? String else { return }

            if nonceA == nil {
                nonceA = nonce
                firstFrameExp.fulfill()
            } else if nonceB == nil {
                nonceB = nonce
                secondFrameExp.fulfill()
            }
        }

        var acknowledgedActivities: [DiscordActivity] = []
        client.onActivityAcknowledged = { activity in
            if let act = activity {
                acknowledgedActivities.append(act)
            }
        }

        // Initial activity A
        client.updateActivity(
            DiscordActivity(details: "Using T3 Code"),
            clientId: testClientId,
            immediate: true
        )
        mock.simulateReady()

        wait(for: [firstFrameExp], timeout: 1.0)
        guard let firstNonce = nonceA else {
            XCTFail("Missing nonceA")
            return
        }

        // While A is in-flight, coordinator updates desired activity to B
        client.updateActivity(
            DiscordActivity(details: "Using Claude"),
            clientId: testClientId,
            immediate: true
        )

        // Server acknowledges A
        mock.simulateActivityAck(nonce: firstNonce)

        // Verify that A was acknowledged (snapshot actually sent), NOT B
        let ackWaitExp = expectation(description: "Wait for ACK handling")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.02) {
            XCTAssertEqual(acknowledgedActivities.count, 1)
            XCTAssertEqual(acknowledgedActivities.first?.details, "Using T3 Code")
            ackWaitExp.fulfill()
        }
        wait(for: [ackWaitExp], timeout: 1.0)

        // Client then automatically dispatches B
        wait(for: [secondFrameExp], timeout: 1.0)
        guard let secondNonce = nonceB else {
            XCTFail("Missing nonceB")
            return
        }

        // Server acknowledges B
        mock.simulateActivityAck(nonce: secondNonce)

        let finalWaitExp = expectation(description: "Wait for final ACK")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.02) {
            XCTAssertEqual(acknowledgedActivities.count, 2)
            XCTAssertEqual(acknowledgedActivities.last?.details, "Using Claude")
            finalWaitExp.fulfill()
        }
        wait(for: [finalWaitExp], timeout: 1.0)

        client.clearAndCloseImmediately()
    }

    // 4. Stale callbacks / clear pending connection invalidates callbacks
    func testStaleCallbacks_clearPendingConnection_doesNotRestoreOldActivity() {
        let mock = ScriptedMockDiscordTransport()
        let client = DiscordIpcClient(
            transport: mock,
            configuration: makeFastConfig(),
            candidatePathsProvider: { ["/tmp/discord-ipc-0"] }
        )

        // Initiate connection with desired activity
        client.updateActivity(
            DiscordActivity(details: "Using T3 Code"),
            clientId: testClientId,
            immediate: true
        )

        // Clear immediately while connection is pending
        client.clearAndCloseImmediately()

        // Stale READY frame arrives from old connection attempt
        mock.simulateReady(waitForHandshake: false)

        let waitExp = expectation(description: "Wait to ensure stale callback ignored")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            // Verify no activity frame was published
            let activityFrames = mock.sentFrames.filter {
                guard $0.opcode == DiscordIpcFrame.opcodeFrame,
                      let str = $0.payloadString() else { return false }
                return str.contains("Using T3 Code")
            }
            XCTAssertEqual(activityFrames.count, 0, "Stale callbacks must not publish activity after clear")
            waitExp.fulfill()
        }
        wait(for: [waitExp], timeout: 1.0)
    }

    // 5. Reconnection restores unchanged activity without requiring coordinator re-evaluation
    func testReconnectRestoresUnchangedActivity() {
        let mock = ScriptedMockDiscordTransport()
        let client = DiscordIpcClient(
            transport: mock,
            configuration: makeFastConfig(),
            candidatePathsProvider: { ["/tmp/discord-ipc-0"] }
        )

        let firstFrameExp = expectation(description: "First activity frame")
        let secondFrameExp = expectation(description: "Reconnected activity frame")
        var frameCount = 0

        mock.onFrameSent = { frame in
            if frame.opcode == DiscordIpcFrame.opcodeFrame,
               let str = frame.payloadString(), str.contains("Using Codex") {
                frameCount += 1
                if frameCount == 1 {
                    firstFrameExp.fulfill()
                } else if frameCount == 2 {
                    secondFrameExp.fulfill()
                }
            }
        }

        client.updateActivity(
            DiscordActivity(details: "Using Codex"),
            clientId: testClientId,
            immediate: true
        )
        mock.simulateReady()
        wait(for: [firstFrameExp], timeout: 1.0)

        // Simulate unexpected Discord disconnect
        mock.simulateDisconnection(error: TransportError.connectionFailed("Discord killed"))

        // Client reconnects automatically
        let readyExp = expectation(description: "Wait for reconnect ready")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.08) {
            mock.simulateReady()
            readyExp.fulfill()
        }
        wait(for: [readyExp], timeout: 1.0)

        // Verify unchanged activity is restored on the new connection
        wait(for: [secondFrameExp], timeout: 1.0)
        XCTAssertEqual(frameCount, 2)

        client.clearAndCloseImmediately()
    }

    // 6. Command ERROR drops connection and retries
    func testCommandError_dropsConnectionAndRetries() {
        let mock = ScriptedMockDiscordTransport()
        let client = DiscordIpcClient(
            transport: mock,
            configuration: makeFastConfig(),
            candidatePathsProvider: { ["/tmp/discord-ipc-0"] }
        )

        let frameExp = expectation(description: "Activity frame sent")
        var sentNonce: String?

        mock.onFrameSent = { frame in
            if frame.opcode == DiscordIpcFrame.opcodeFrame,
               let obj = try? JSONSerialization.jsonObject(with: frame.payload) as? [String: Any],
               let nonce = obj["nonce"] as? String {
                sentNonce = nonce
                frameExp.fulfill()
            }
        }

        var errorReported = false
        client.onStatusChanged = { status, msg in
            if status == .error {
                errorReported = true
            }
        }

        client.updateActivity(
            DiscordActivity(details: "Using T3 Code"),
            clientId: testClientId,
            immediate: true
        )
        mock.simulateReady()
        wait(for: [frameExp], timeout: 1.0)

        guard let nonce = sentNonce else {
            XCTFail("Missing nonce")
            return
        }

        // Simulate Discord returning ERROR response
        mock.simulateErrorAck(nonce: nonce, message: "Rate limit exceeded")

        let waitExp = expectation(description: "Wait for error handling and disconnect")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
            XCTAssertTrue(errorReported)
            XCTAssertFalse(mock.isConnected)
            waitExp.fulfill()
        }
        wait(for: [waitExp], timeout: 1.0)

        client.clearAndCloseImmediately()
    }

    // 7. Command timeout drops connection and retries
    func testCommandTimeout_dropsConnectionAndRetries() {
        let mock = ScriptedMockDiscordTransport()
        let client = DiscordIpcClient(
            transport: mock,
            configuration: makeFastConfig(),
            candidatePathsProvider: { ["/tmp/discord-ipc-0"] }
        )

        let frameExp = expectation(description: "Activity frame sent")
        mock.onFrameSent = { frame in
            if frame.opcode == DiscordIpcFrame.opcodeFrame,
               let str = frame.payloadString(), str.contains("Using Claude") {
                frameExp.fulfill()
            }
        }

        var timeoutReported = false
        client.onStatusChanged = { status, msg in
            if status == .error, let msg = msg, msg.contains("timed out") {
                timeoutReported = true
            }
        }

        client.updateActivity(
            DiscordActivity(details: "Using Claude"),
            clientId: testClientId,
            immediate: true
        )
        mock.simulateReady()
        wait(for: [frameExp], timeout: 1.0)

        // Never send ACK; observe after the command deadline and before retry.
        let timeoutExp = expectation(description: "Wait for command timeout")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) {
            XCTAssertTrue(timeoutReported)
            XCTAssertFalse(mock.isConnected)
            timeoutExp.fulfill()
        }
        wait(for: [timeoutExp], timeout: 1.0)

        client.clearAndCloseImmediately()
    }

    // 8. Invalid JSON drops connection
    func testInvalidJson_dropsConnection() {
        let mock = ScriptedMockDiscordTransport()
        let client = DiscordIpcClient(
            transport: mock,
            configuration: makeFastConfig(),
            candidatePathsProvider: { ["/tmp/discord-ipc-0"] }
        )

        client.updateActivity(
            DiscordActivity(details: "Using T3 Code"),
            clientId: testClientId,
            immediate: true
        )
        mock.simulateReady()

        var errorReported = false
        client.onStatusChanged = { status, msg in
            if status == .error {
                errorReported = true
            }
        }

        // Send corrupt JSON frame
        let badJson = "{ invalid json payload not terminated"
        let badFrame = try! DiscordIpcFrame(opcode: DiscordIpcFrame.opcodeFrame, jsonString: badJson)
        mock.simulateIncomingData(badFrame.encode())

        let waitExp = expectation(description: "Wait for invalid JSON drop")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
            XCTAssertTrue(errorReported)
            XCTAssertFalse(mock.isConnected)
            waitExp.fulfill()
        }
        wait(for: [waitExp], timeout: 1.0)

        client.clearAndCloseImmediately()
    }

    // 9. Stale socket rotation: dead socket 0 does not block connecting to socket 1
    func testStaleSocketRotation_bypassesDeadSocket0() {
        let mock = ScriptedMockDiscordTransport()

        // Fail connection to socket 0; succeed on socket 1
        mock.connectHandler = { path in
            if path.contains("discord-ipc-0") {
                return .failure(TransportError.connectionFailed("Connection refused (stale socket)"))
            }
            return .success(())
        }

        let client = DiscordIpcClient(
            transport: mock,
            configuration: makeFastConfig(),
            candidatePathsProvider: {
                ["/tmp/discord-ipc-0", "/tmp/discord-ipc-1"]
            }
        )

        let connectedExp = expectation(description: "Connected to socket 1")
        client.onStatusChanged = { status, _ in
            if status == .connected {
                connectedExp.fulfill()
            }
        }

        client.updateActivity(
            DiscordActivity(details: "Using T3 Code"),
            clientId: testClientId,
            immediate: true
        )

        let waitToReadyExp = expectation(description: "Wait for rotation and send ready")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.08) {
            XCTAssertTrue(mock.connectedPaths.contains("/tmp/discord-ipc-0"))
            XCTAssertTrue(mock.connectedPaths.contains("/tmp/discord-ipc-1"))
            mock.simulateReady()
            waitToReadyExp.fulfill()
        }
        wait(for: [waitToReadyExp], timeout: 1.0)
        wait(for: [connectedExp], timeout: 1.0)

        client.clearAndCloseImmediately()
    }

    // 10. Ping opcode 3 responds with Pong opcode 4
    func testPingPong() {
        let mock = ScriptedMockDiscordTransport()
        let client = DiscordIpcClient(
            transport: mock,
            configuration: makeFastConfig(),
            candidatePathsProvider: { ["/tmp/discord-ipc-0"] }
        )

        let pongExp = expectation(description: "Pong received")
        mock.onFrameSent = { frame in
            if frame.opcode == DiscordIpcFrame.opcodePong {
                XCTAssertEqual(frame.payloadString(), "{\"test\":\"ping\"}")
                pongExp.fulfill()
            }
        }

        client.updateActivity(
            DiscordActivity(details: "Using T3 Code"),
            clientId: testClientId,
            immediate: true
        )
        mock.simulateReady()

        // Simulate Discord server sending Ping
        mock.simulateIncomingFrame(opcode: DiscordIpcFrame.opcodePing, json: "{\"test\":\"ping\"}")

        wait(for: [pongExp], timeout: 1.0)
        client.clearAndCloseImmediately()
    }

    // 11. Clear presence sends activity: null frame and closes transport promptly
    func testClearActivity_sendsNullActivityAndDisconnectsPromptly() {
        let mock = ScriptedMockDiscordTransport()
        let client = DiscordIpcClient(
            transport: mock,
            configuration: makeFastConfig(),
            candidatePathsProvider: { ["/tmp/discord-ipc-0"] }
        )

        let clearFrameExp = expectation(description: "Clear activity frame sent")
        mock.onFrameSent = { frame in
            if frame.opcode == DiscordIpcFrame.opcodeFrame,
               let str = frame.payloadString(), str.contains("\"activity\":null") {
                clearFrameExp.fulfill()
            }
        }

        client.updateActivity(
            DiscordActivity(details: "Using T3 Code"),
            clientId: testClientId,
            immediate: true
        )
        mock.simulateReady()

        // Now request clear
        mock.waitForActivityFrame()
        client.updateActivity(nil, clientId: testClientId)

        wait(for: [clearFrameExp], timeout: 1.0)

        let disconnectExp = expectation(description: "Wait for prompt transport disconnect")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            XCTAssertFalse(mock.isConnected)
            disconnectExp.fulfill()
        }
        wait(for: [disconnectExp], timeout: 1.0)
    }
}
