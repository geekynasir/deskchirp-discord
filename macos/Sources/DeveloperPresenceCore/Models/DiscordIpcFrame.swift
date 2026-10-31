import Foundation

/// Represents a low-level Discord IPC packet frame with an opcode and payload data.
/// Wire format: [4 bytes opcode (LE)][4 bytes length (LE)][payload bytes].
public struct DiscordIpcFrame: Equatable, Sendable {
    public static let opcodeHandshake: UInt32 = 0
    public static let opcodeFrame: UInt32 = 1
    public static let opcodeClose: UInt32 = 2
    public static let opcodePing: UInt32 = 3
    public static let opcodePong: UInt32 = 4

    public static let headerSize: Int = 8
    public static let maxPayloadBytes: Int = 65536 // 64 KiB

    public let opcode: UInt32
    public let payload: Data

    public init(opcode: UInt32, payload: Data) throws {
        guard payload.count <= Self.maxPayloadBytes else {
            throw FrameError.oversizedPayload(payload.count)
        }
        self.opcode = opcode
        self.payload = payload
    }

    public init(opcode: UInt32, jsonString: String) throws {
        guard let data = jsonString.data(using: .utf8) else {
            throw FrameError.invalidEncoding
        }
        try self.init(opcode: opcode, payload: data)
    }

    /// Encodes frame into wire format: [4 bytes opcode LE][4 bytes length LE][payload bytes].
    public func encode() -> Data {
        var data = Data(capacity: Self.headerSize + payload.count)
        var leOpcode = opcode.littleEndian
        var leLength = UInt32(payload.count).littleEndian

        withUnsafeBytes(of: &leOpcode) { data.append(contentsOf: $0) }
        withUnsafeBytes(of: &leLength) { data.append(contentsOf: $0) }
        data.append(payload)
        return data
    }

    /// Helper to read payload as UTF-8 string.
    public func payloadString() -> String? {
        String(data: payload, encoding: .utf8)
    }
}

public enum FrameError: Error, Equatable, LocalizedError {
    case oversizedPayload(Int)
    case invalidEncoding
    case malformedHeader
    case invalidJson

    public var errorDescription: String? {
        switch self {
        case .oversizedPayload(let size):
            return "Frame payload size (\(size) bytes) exceeds maximum allowable 64 KiB."
        case .invalidEncoding:
            return "Failed to encode string as UTF-8."
        case .malformedHeader:
            return "Header could not be parsed."
        case .invalidJson:
            return "Payload contains invalid JSON."
        }
    }
}
