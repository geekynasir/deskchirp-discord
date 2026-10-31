using System.Buffers.Binary;
using System.Text;
using DeveloperPresence.Core.Discord;
using DeveloperPresence.Core.Models;
using Xunit;

namespace DeveloperPresence.Tests;

public class DiscordFrameParserTests
{
    [Fact]
    public void Pack_Produces8ByteLittleEndianHeaderAndPayload()
    {
        string json = "{\"cmd\":\"PING\"}";
        byte[] expectedPayload = Encoding.UTF8.GetBytes(json);

        byte[] packed = DiscordFrameParser.Pack(DiscordIpcFrame.OpcodePing, json);

        Assert.Equal(8 + expectedPayload.Length, packed.Length);

        int opcode = BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(0, 4));
        int length = BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(4, 4));

        Assert.Equal(DiscordIpcFrame.OpcodePing, opcode);
        Assert.Equal(expectedPayload.Length, length);

        string payloadStr = Encoding.UTF8.GetString(packed.AsSpan(8, length));
        Assert.Equal(json, payloadStr);
    }

    [Fact]
    public void Pack_ThrowsArgumentException_WhenPayloadExceeds64KiB()
    {
        byte[] oversized = new byte[DiscordIpcFrame.MaxFrameSize + 1];

        var ex = Assert.Throws<ArgumentException>(() =>
            DiscordFrameParser.Pack(DiscordIpcFrame.OpcodeFrame, oversized));

        Assert.Contains("exceeds maximum allowable frame bound", ex.Message);
    }

    [Fact]
    public void ProcessIncomingBytes_ParsesCompleteSingleFrame()
    {
        var parser = new DiscordFrameParser();
        string json = "{\"v\":1,\"client_id\":\"123456789012345678\"}";
        byte[] packet = DiscordFrameParser.Pack(DiscordIpcFrame.OpcodeHandshake, json);

        var frames = parser.ProcessIncomingBytes(packet, packet.Length);

        Assert.Single(frames);
        Assert.Equal(DiscordIpcFrame.OpcodeHandshake, frames[0].Opcode);
        Assert.Equal(json, frames[0].GetPayloadAsString());
    }

    [Fact]
    public void ProcessIncomingBytes_BuffersAndCoalescesPartialFrames()
    {
        var parser = new DiscordFrameParser();
        string json = "{\"cmd\":\"SET_ACTIVITY\"}";
        byte[] packet = DiscordFrameParser.Pack(DiscordIpcFrame.OpcodeFrame, json);

        // Split packet into 3 unequal chunks
        int chunk1Size = 4;
        int chunk2Size = 6;
        int chunk3Size = packet.Length - (chunk1Size + chunk2Size);

        byte[] chunk1 = packet[0..chunk1Size];
        byte[] chunk2 = packet[chunk1Size..(chunk1Size + chunk2Size)];
        byte[] chunk3 = packet[(chunk1Size + chunk2Size)..];

        var frames1 = parser.ProcessIncomingBytes(chunk1, chunk1.Length);
        Assert.Empty(frames1); // Incomplete header

        var frames2 = parser.ProcessIncomingBytes(chunk2, chunk2.Length);
        Assert.Empty(frames2); // Incomplete payload

        var frames3 = parser.ProcessIncomingBytes(chunk3, chunk3.Length);
        Assert.Single(frames3); // Complete frame assembled
        Assert.Equal(DiscordIpcFrame.OpcodeFrame, frames3[0].Opcode);
        Assert.Equal(json, frames3[0].GetPayloadAsString());
    }

    [Fact]
    public void ProcessIncomingBytes_ParsesMultipleCoalescedFramesInSingleBuffer()
    {
        var parser = new DiscordFrameParser();
        byte[] frame1 = DiscordFrameParser.Pack(DiscordIpcFrame.OpcodePing, "ping1");
        byte[] frame2 = DiscordFrameParser.Pack(DiscordIpcFrame.OpcodePing, "ping2");
        byte[] combined = new byte[frame1.Length + frame2.Length];
        frame1.CopyTo(combined, 0);
        frame2.CopyTo(combined, frame1.Length);

        var frames = parser.ProcessIncomingBytes(combined, combined.Length);

        Assert.Equal(2, frames.Count);
        Assert.Equal("ping1", frames[0].GetPayloadAsString());
        Assert.Equal("ping2", frames[1].GetPayloadAsString());
    }

    [Fact]
    public void ProcessIncomingBytes_ThrowsInvalidDataException_WhenHeaderClaimsLengthGreaterThan64KiB()
    {
        var parser = new DiscordFrameParser();
        byte[] corruptHeader = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(corruptHeader.AsSpan(0, 4), DiscordIpcFrame.OpcodeFrame);
        BinaryPrimitives.WriteInt32LittleEndian(corruptHeader.AsSpan(4, 4), 65537); // > 64 KiB

        var ex = Assert.Throws<InvalidDataException>(() =>
            parser.ProcessIncomingBytes(corruptHeader, corruptHeader.Length));

        Assert.Contains("exceeds maximum safe bound", ex.Message);
    }

    [Fact]
    public void ProcessIncomingBytes_ThrowsInvalidDataException_WhenLengthIsNegative()
    {
        var parser = new DiscordFrameParser();
        byte[] corruptHeader = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(corruptHeader.AsSpan(0, 4), DiscordIpcFrame.OpcodeFrame);
        BinaryPrimitives.WriteInt32LittleEndian(corruptHeader.AsSpan(4, 4), -1);

        Assert.Throws<InvalidDataException>(() =>
            parser.ProcessIncomingBytes(corruptHeader, corruptHeader.Length));
    }

    [Fact]
    public void CreatePongResponse_EchoesPayloadWithOpcodePong()
    {
        string pingPayload = "{\"nonce\":\"abc-123\"}";
        var pingFrame = new DiscordIpcFrame(DiscordIpcFrame.OpcodePing, pingPayload);

        byte[] pongBytes = DiscordFrameParser.CreatePongResponse(pingFrame);

        int opcode = BinaryPrimitives.ReadInt32LittleEndian(pongBytes.AsSpan(0, 4));
        int length = BinaryPrimitives.ReadInt32LittleEndian(pongBytes.AsSpan(4, 4));
        string payloadStr = Encoding.UTF8.GetString(pongBytes.AsSpan(8, length));

        Assert.Equal(DiscordIpcFrame.OpcodePong, opcode);
        Assert.Equal(pingPayload, payloadStr);
    }
}
