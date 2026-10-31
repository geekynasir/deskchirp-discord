using System.Text;

namespace DeveloperPresence.Core.Models;

/// <summary>
/// Represents a low-level Discord IPC packet frame with an opcode and binary/JSON payload.
/// Wire format: [4 bytes opcode (LE)][4 bytes length (LE)][payload bytes].
/// </summary>
public sealed class DiscordIpcFrame
{
    public const int OpcodeHandshake = 0;
    public const int OpcodeFrame = 1;
    public const int OpcodeClose = 2;
    public const int OpcodePing = 3;
    public const int OpcodePong = 4;

    /// <summary>
    /// Frame header is 4 bytes opcode + 4 bytes length = 8 bytes.
    /// </summary>
    public const int HeaderSize = 8;

    /// <summary>
    /// Bounded maximum frame size for safety (64 KiB).
    /// Prevents unbounded buffer allocation and denial-of-service from malformed input.
    /// </summary>
    public const int MaxFrameSize = 64 * 1024;

    public int Opcode { get; }
    public byte[] Payload { get; }

    public DiscordIpcFrame(int opcode, byte[] payload)
    {
        if (payload.Length > MaxFrameSize)
        {
            throw new ArgumentException($"Payload size ({payload.Length} bytes) exceeds maximum allowable frame size of {MaxFrameSize} bytes.", nameof(payload));
        }

        Opcode = opcode;
        Payload = payload;
    }

    public DiscordIpcFrame(int opcode, string json)
        : this(opcode, Encoding.UTF8.GetBytes(json))
    {
    }

    /// <summary>
    /// Reads payload as UTF-8 string.
    /// </summary>
    public string GetPayloadAsString() => Encoding.UTF8.GetString(Payload);

    public override string ToString() => $"DiscordIpcFrame(Opcode={Opcode}, Length={Payload.Length})";
}
