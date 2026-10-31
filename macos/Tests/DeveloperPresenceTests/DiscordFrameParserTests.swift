import XCTest
@testable import DeveloperPresenceCore

final class DiscordFrameParserTests: XCTestCase {
    struct IpcFramesFixtureFile: Codable {
        let version: Int
        let maxPayloadBytes: Int
        let frames: [IpcFrameCase]
    }

    struct IpcFrameCase: Codable {
        let id: String
        let opcode: UInt32
        let json: String
        let wireHex: String
    }

    private func dataFromHex(_ hex: String) -> Data {
        var data = Data()
        var index = hex.startIndex
        while index < hex.endIndex {
            let nextIndex = hex.index(index, offsetBy: 2)
            let byteString = hex[index..<nextIndex]
            if let byte = UInt8(byteString, radix: 16) {
                data.append(byte)
            }
            index = nextIndex
        }
        return data
    }

    private func hexFromData(_ data: Data) -> String {
        data.map { String(format: "%02x", $0) }.joined()
    }

    func testFrameEncodingMatchesWireHex() throws {
        let data = try TestFixtureLoader.loadResource(named: "ipc-frames")
        let fixture = try JSONDecoder().decode(IpcFramesFixtureFile.self, from: data)

        for frameCase in fixture.frames {
            let frame = try DiscordIpcFrame(opcode: frameCase.opcode, jsonString: frameCase.json)
            let encodedData = frame.encode()
            let hex = hexFromData(encodedData)

            XCTAssertEqual(hex.lowercased(), frameCase.wireHex.lowercased(), "Frame '\(frameCase.id)' encoding mismatch")
        }
    }

    func testFrameDecodingMatchesFixture() throws {
        let data = try TestFixtureLoader.loadResource(named: "ipc-frames")
        let fixture = try JSONDecoder().decode(IpcFramesFixtureFile.self, from: data)

        for frameCase in fixture.frames {
            let wireData = dataFromHex(frameCase.wireHex)
            let parser = DiscordFrameParser()
            let parsedFrames = try parser.feed(wireData)

            XCTAssertEqual(parsedFrames.count, 1, "Expected 1 parsed frame for '\(frameCase.id)'")
            let frame = parsedFrames[0]
            XCTAssertEqual(frame.opcode, frameCase.opcode)
            XCTAssertEqual(frame.payloadString(), frameCase.json)
        }
    }

    func testByteByByteIncrementalFeeds() throws {
        let data = try TestFixtureLoader.loadResource(named: "ipc-frames")
        let fixture = try JSONDecoder().decode(IpcFramesFixtureFile.self, from: data)

        for frameCase in fixture.frames {
            let wireData = dataFromHex(frameCase.wireHex)
            let parser = DiscordFrameParser()
            var collectedFrames: [DiscordIpcFrame] = []

            for byte in wireData {
                let singleByteData = Data([byte])
                let frames = try parser.feed(singleByteData)
                collectedFrames.append(contentsOf: frames)
            }

            XCTAssertEqual(collectedFrames.count, 1, "Case '\(frameCase.id)' should yield exactly 1 frame when fed byte by byte")
            if let frame = collectedFrames.first {
                XCTAssertEqual(frame.opcode, frameCase.opcode)
                XCTAssertEqual(frame.payloadString(), frameCase.json)
            }
        }
    }

    func testCoalescedFrames() throws {
        let data = try TestFixtureLoader.loadResource(named: "ipc-frames")
        let fixture = try JSONDecoder().decode(IpcFramesFixtureFile.self, from: data)

        // Concatenate all wire frames together
        var coalescedData = Data()
        for frameCase in fixture.frames {
            coalescedData.append(dataFromHex(frameCase.wireHex))
        }

        let parser = DiscordFrameParser()
        let parsed = try parser.feed(coalescedData)

        XCTAssertEqual(parsed.count, fixture.frames.count, "All coalesced frames should be parsed")
        for (index, expectedCase) in fixture.frames.enumerated() {
            XCTAssertEqual(parsed[index].opcode, expectedCase.opcode)
            XCTAssertEqual(parsed[index].payloadString(), expectedCase.json)
        }
    }

    func testOversizedFrameRejection() {
        let parser = DiscordFrameParser()

        // Craft header declaring 65537 bytes (> 64 KiB)
        var oversizedHeader = Data()
        var opcode: UInt32 = 1
        var length: UInt32 = 65537 // Exceeds 65536
        withUnsafeBytes(of: &opcode) { oversizedHeader.append(contentsOf: $0) }
        withUnsafeBytes(of: &length) { oversizedHeader.append(contentsOf: $0) }

        XCTAssertThrowsError(try parser.feed(oversizedHeader)) { error in
            guard case FrameError.oversizedPayload(let size) = error else {
                XCTFail("Expected oversizedPayload error, got: \(error)")
                return
            }
            XCTAssertEqual(size, 65537)
        }

        // Buffer should be reset after error
        XCTAssertEqual(parser.bufferedByteCount, 0)
    }

    func testPartialHeaderBuffering() throws {
        let parser = DiscordFrameParser()

        // Feed 3 bytes
        let partial1 = Data([0x01, 0x00, 0x00])
        let frames1 = try parser.feed(partial1)
        XCTAssertTrue(frames1.isEmpty)
        XCTAssertEqual(parser.bufferedByteCount, 3)

        // Feed next 4 bytes (total 7 bytes)
        let partial2 = Data([0x00, 0x05, 0x00, 0x00])
        let frames2 = try parser.feed(partial2)
        XCTAssertTrue(frames2.isEmpty)
        XCTAssertEqual(parser.bufferedByteCount, 7)

        // Feed 1 more header byte + 5 bytes payload
        let rest = Data([0x00, 0x68, 0x65, 0x6c, 0x6c, 0x6f])
        let frames3 = try parser.feed(rest)
        XCTAssertEqual(frames3.count, 1)
        XCTAssertEqual(frames3[0].opcode, 1)
        XCTAssertEqual(frames3[0].payloadString(), "hello")
        XCTAssertEqual(parser.bufferedByteCount, 0)
    }
}
