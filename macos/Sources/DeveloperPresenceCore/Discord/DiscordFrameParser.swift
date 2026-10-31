import Foundation

/// Incremental bounded parser for Discord IPC frames.
/// Wire format: [4 bytes opcode LE][4 bytes length LE][payload bytes].
///
/// Guarantees:
/// - Maximum frame payload size is capped at 64 KiB (65,536 bytes).
/// - Gracefully handles split headers, split payloads, and coalesced frames.
/// - Does not allocate arbitrary memory from untrusted lengths.
public final class DiscordFrameParser {
    public static let headerSize = DiscordIpcFrame.headerSize
    public static let maxPayloadBytes = DiscordIpcFrame.maxPayloadBytes

    private var buffer = Data()

    public init() {}

    /// Clears the internal parse buffer.
    public func reset() {
        buffer.removeAll(keepingCapacity: false)
    }

    /// Current buffered byte count awaiting completion.
    public var bufferedByteCount: Int {
        buffer.count
    }

    /// Feeds incoming data chunk into the parser and extracts all complete frames.
    /// Returns an array of successfully parsed frames (which may be empty if data is partial).
    /// Throws FrameError if an oversized payload length is encountered.
    public func feed(_ data: Data) throws -> [DiscordIpcFrame] {
        buffer.append(data)
        var frames: [DiscordIpcFrame] = []

        while buffer.count >= Self.headerSize {
            // Read 4-byte LE opcode and 4-byte LE length
            let opcode: UInt32 = buffer.withUnsafeBytes { ptr in
                ptr.loadUnaligned(fromByteOffset: 0, as: UInt32.self).littleEndian
            }
            let length: UInt32 = buffer.withUnsafeBytes { ptr in
                ptr.loadUnaligned(fromByteOffset: 4, as: UInt32.self).littleEndian
            }

            // Enforce 64 KiB bound immediately
            guard length <= UInt32(Self.maxPayloadBytes) else {
                reset()
                throw FrameError.oversizedPayload(Int(length))
            }

            let frameTotalSize = Self.headerSize + Int(length)
            guard buffer.count >= frameTotalSize else {
                // Not enough bytes yet for the full payload; await more data
                break
            }

            let payload = buffer.subdata(in: Self.headerSize..<frameTotalSize)
            buffer.removeSubrange(0..<frameTotalSize)

            let frame = try DiscordIpcFrame(opcode: opcode, payload: payload)
            frames.append(frame)
        }

        return frames
    }
}
