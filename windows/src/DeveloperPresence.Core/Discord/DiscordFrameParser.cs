using System.Buffers.Binary;
using System.Text;
using DeveloperPresence.Core.Models;

namespace DeveloperPresence.Core.Discord;

/// <summary>
/// Handles packing and parsing of low-level Discord IPC frames.
/// Format: 8-byte Little-Endian Header (4-byte Opcode + 4-byte Length) + UTF-8 Payload.
/// Strictly enforces the 64 KiB maximum frame boundary and handles stream chunk coalescing.
/// </summary>
public sealed class DiscordFrameParser
{
    private readonly MemoryStream _buffer = new();

    /// <summary>
    /// Serializes an opcode and string payload into an atomic wire packet.
    /// Packed into a single contiguous byte array to ensure whole-frame atomic writes.
    /// </summary>
    public static byte[] Pack(int opcode, string json)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(json);
        return Pack(opcode, payloadBytes);
    }

    /// <summary>
    /// Serializes an opcode and raw payload bytes into an atomic wire packet.
    /// </summary>
    public static byte[] Pack(int opcode, byte[] payload)
    {
        if (payload.Length > DiscordIpcFrame.MaxFrameSize)
        {
            throw new ArgumentException(
                $"Payload length ({payload.Length} bytes) exceeds maximum allowable frame bound of {DiscordIpcFrame.MaxFrameSize} bytes.",
                nameof(payload));
        }

        byte[] frame = new byte[DiscordIpcFrame.HeaderSize + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame, DiscordIpcFrame.HeaderSize);
        return frame;
    }

    /// <summary>
    /// Constructs a Pong (Opcode 4) frame in response to an incoming Ping (Opcode 3) frame.
    /// Echoes back the payload provided by the ping.
    /// </summary>
    public static byte[] CreatePongResponse(DiscordIpcFrame pingFrame)
    {
        return Pack(DiscordIpcFrame.OpcodePong, pingFrame.Payload);
    }

    /// <summary>
    /// Appends incoming stream bytes and parses all available complete frames.
    /// Safely buffers partial frames until remaining bytes arrive.
    /// Throws InvalidDataException if frame length exceeds bounded 64 KiB limit.
    /// </summary>
    public List<DiscordIpcFrame> ProcessIncomingBytes(byte[] chunk, int count)
    {
        if (count <= 0)
        {
            return new List<DiscordIpcFrame>();
        }

        _buffer.Write(chunk, 0, count);
        byte[] bufferBytes = _buffer.ToArray();
        int offset = 0;
        int remaining = bufferBytes.Length;

        List<DiscordIpcFrame> frames = new();

        while (remaining >= DiscordIpcFrame.HeaderSize)
        {
            int opcode = BinaryPrimitives.ReadInt32LittleEndian(bufferBytes.AsSpan(offset, 4));
            int length = BinaryPrimitives.ReadInt32LittleEndian(bufferBytes.AsSpan(offset + 4, 4));

            if (length < 0 || length > DiscordIpcFrame.MaxFrameSize)
            {
                // Reset buffer on corruption or security violation
                _buffer.SetLength(0);
                throw new InvalidDataException(
                    $"Discord frame length ({length} bytes) is invalid or exceeds maximum safe bound of {DiscordIpcFrame.MaxFrameSize} bytes.");
            }

            int totalFrameSize = DiscordIpcFrame.HeaderSize + length;

            if (remaining < totalFrameSize)
            {
                // Frame payload is partial, wait for more data from stream
                break;
            }

            byte[] payload = new byte[length];
            if (length > 0)
            {
                Array.Copy(bufferBytes, offset + DiscordIpcFrame.HeaderSize, payload, 0, length);
            }

            frames.Add(new DiscordIpcFrame(opcode, payload));

            offset += totalFrameSize;
            remaining -= totalFrameSize;
        }

        // Compact buffer by retaining only unconsumed partial bytes
        _buffer.SetLength(0);
        if (remaining > 0)
        {
            _buffer.Write(bufferBytes, offset, remaining);
        }

        return frames;
    }

    /// <summary>
    /// Clears any cached partial frame bytes in the internal buffer.
    /// </summary>
    public void Reset()
    {
        _buffer.SetLength(0);
    }
}
