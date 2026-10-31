using System.Buffers.Binary;
using System.Text.Json;
using DeveloperPresence.Core.Discord;
using DeveloperPresence.Core.Models;
using Xunit;

namespace DeveloperPresence.Tests;

public class DiscordIpcClientTests
{
    private const string ValidClientId = "123456789012345678";

    [Fact]
    public async Task ConnectAsync_SendsHandshake_AndWaitsForReady()
    {
        var (clientStream, server) = TestIpcDuplexStream.CreatePair();
        await using var serverCleanup = server;

        var client = new DiscordIpcClient((id, ct) => Task.FromResult<Stream?>(clientStream));

        var connectTask = client.ConnectAsync(ValidClientId);

        // Server receives Opcode 0 Handshake frame
        var handshakeFrame = await server.ReadNextFrameAsync();
        Assert.Equal(DiscordIpcFrame.OpcodeHandshake, handshakeFrame.Opcode);
        using (var doc = JsonDocument.Parse(handshakeFrame.GetPayloadAsString()))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
            Assert.Equal(ValidClientId, doc.RootElement.GetProperty("client_id").GetString());
        }

        // Before READY, client must NOT be connected
        Assert.False(client.IsConnected);

        // Server emits READY
        await server.SendReadyAsync();

        bool connected = await connectTask;
        Assert.True(connected);
        Assert.True(client.IsConnected);
        Assert.Equal("42", client.CurrentUserId);

        await client.CloseAsync();
        Assert.Null(client.CurrentUserId);
    }

    [Fact]
    public async Task ExpiredComputerPermissionIsCheckedAtTheFinalWrite()
    {
        var (stream, server) = TestIpcDuplexStream.CreatePair();
        await using var serverCleanup = server;
        await using var client = new DiscordIpcClient((_, _) => Task.FromResult<Stream?>(stream));
        var connecting = client.ConnectAsync(ValidClientId);
        await server.ReadNextFrameAsync(); await server.SendReadyAsync();
        Assert.True(await connecting);
        client.PublishAllowed = () => false;
        await Assert.ThrowsAnyAsync<Exception>(() => client.SendActivityAsync(new DiscordActivity { Details = "Using T3 Code" }));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_Fails_WhenReadyNotReceivedBeforeCancellation()
    {
        var (clientStream, server) = TestIpcDuplexStream.CreatePair();
        await using var serverCleanup = server;

        var client = new DiscordIpcClient((id, ct) => Task.FromResult<Stream?>(clientStream));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var connectTask = client.ConnectAsync(ValidClientId, cts.Token);

        var handshakeFrame = await server.ReadNextFrameAsync();
        Assert.Equal(DiscordIpcFrame.OpcodeHandshake, handshakeFrame.Opcode);

        // Server does NOT send ready; connection must fail
        bool connected = await connectTask;
        Assert.False(connected);
        Assert.False(client.IsConnected);

        await client.CloseAsync();
    }

    [Fact]
    public async Task SendActivityAsync_TracksNonce_AndAwaitsAck()
    {
        var (clientStream, server) = TestIpcDuplexStream.CreatePair();
        await using var serverCleanup = server;

        var client = new DiscordIpcClient((id, ct) => Task.FromResult<Stream?>(clientStream));
        var connectTask = client.ConnectAsync(ValidClientId);

        await server.ReadNextFrameAsync(); // Handshake
        await server.SendReadyAsync();
        Assert.True(await connectTask);

        // Send activity
        var sendTask = client.SendActivityAsync(SupportedApp.T3Code);

        var activityFrame = await server.ReadNextFrameAsync();
        Assert.Equal(DiscordIpcFrame.OpcodeFrame, activityFrame.Opcode);

        string nonce;
        using (var doc = JsonDocument.Parse(activityFrame.GetPayloadAsString()))
        {
            var root = doc.RootElement;
            Assert.Equal("SET_ACTIVITY", root.GetProperty("cmd").GetString());
            nonce = root.GetProperty("nonce").GetString()!;
            Assert.False(string.IsNullOrEmpty(nonce));

            var act = root.GetProperty("args").GetProperty("activity");
            Assert.Equal("Using T3 Code", act.GetProperty("details").GetString());
            // Strict details-only: no timestamps, assets, or fake state
            Assert.False(act.TryGetProperty("timestamps", out _));
            Assert.False(act.TryGetProperty("assets", out _));
        }

        // Server acks the command
        await server.SendActivityAckAsync(nonce);
        await sendTask; // Must complete cleanly

        await client.CloseAsync();
    }

    [Fact]
    public async Task MatchingNonceWithAnotherCommandCannotAcknowledgeActivity()
    {
        var (stream, server) = TestIpcDuplexStream.CreatePair();
        await using var cleanup = server;
        await using var client = new DiscordIpcClient((id, ct) => Task.FromResult<Stream?>(stream));
        var connect = client.ConnectAsync(ValidClientId);
        await server.ReadNextFrameAsync();
        await server.SendReadyAsync();
        Assert.True(await connect);
        var send = client.SendActivityAsync(SupportedApp.T3Code);
        var frame = await server.ReadNextFrameAsync();
        using var payload = JsonDocument.Parse(frame.GetPayloadAsString());
        var nonce = payload.RootElement.GetProperty("nonce").GetString()!;
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameReceived += incoming =>
        {
            if (incoming.GetPayloadAsString().Contains("OTHER_COMMAND")) observed.TrySetResult();
        };
        await server.SendFrameAsync(DiscordIpcFrame.OpcodeFrame, JsonSerializer.Serialize(new {cmd = "OTHER_COMMAND", nonce}));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(send.IsCompleted);
        await server.SendActivityAckAsync(nonce);
        await send.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SendActivityAsync_Throws_WhenErrorAckReceived()
    {
        var (clientStream, server) = TestIpcDuplexStream.CreatePair();
        await using var serverCleanup = server;

        var client = new DiscordIpcClient((id, ct) => Task.FromResult<Stream?>(clientStream));
        var connectTask = client.ConnectAsync(ValidClientId);

        await server.ReadNextFrameAsync();
        await server.SendReadyAsync();
        Assert.True(await connectTask);

        var sendTask = client.SendActivityAsync(SupportedApp.Claude);
        var activityFrame = await server.ReadNextFrameAsync();

        string nonce;
        using (var doc = JsonDocument.Parse(activityFrame.GetPayloadAsString()))
        {
            nonce = doc.RootElement.GetProperty("nonce").GetString()!;
        }

        // Server sends error ack
        await server.SendErrorAckAsync(nonce, "Rate limit exceeded or invalid payload");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sendTask);
        Assert.Contains("Rate limit exceeded", ex.Message);

        await client.CloseAsync();
    }

    [Theory]
    [InlineData("{ malformed json not closed")]
    [InlineData("[]")]
    public async Task IncomingFrame_WithInvalidJson_SetsErrorAndDropsConnection(string invalidPayload)
    {
        var (clientStream, server) = TestIpcDuplexStream.CreatePair();
        await using var serverCleanup = server;

        var client = new DiscordIpcClient((id, ct) => Task.FromResult<Stream?>(clientStream));
        var connectTask = client.ConnectAsync(ValidClientId);

        await server.ReadNextFrameAsync();
        await server.SendReadyAsync();
        Assert.True(await connectTask);

        PresenceConnectionStatus lastStatus = PresenceConnectionStatus.Connected;
        client.StatusChanged += (status, msg) => lastStatus = status;

        // Server sends invalid JSON
        await server.SendFrameAsync(DiscordIpcFrame.OpcodeFrame, invalidPayload);

        // Wait a moment for read loop to process
        await Task.Delay(100);

        Assert.Equal(PresenceConnectionStatus.Error, lastStatus);
        Assert.False(client.IsConnected);

        await client.CloseAsync();
    }

    [Fact]
    public async Task ReadLoop_HandlesEof_DisconnectsAndFailsPendingCommands()
    {
        var (clientStream, server) = TestIpcDuplexStream.CreatePair();

        var client = new DiscordIpcClient((id, ct) => Task.FromResult<Stream?>(clientStream));
        var connectTask = client.ConnectAsync(ValidClientId);

        await server.ReadNextFrameAsync();
        await server.SendReadyAsync();
        Assert.True(await connectTask);

        var sendTask = client.SendActivityAsync(SupportedApp.Codex);
        await server.ReadNextFrameAsync(); // Server receives activity

        // Close server stream (simulates Discord crash or exit causing EOF)
        await server.DisposeAsync();

        // Pending send must fail with IOException
        await Assert.ThrowsAnyAsync<Exception>(() => sendTask);
        Assert.False(client.IsConnected);

        await client.CloseAsync();
    }

    [Fact]
    public async Task ReadLoop_HandlesOpcode2Close_DisconnectsCleanly()
    {
        var (clientStream, server) = TestIpcDuplexStream.CreatePair();
        await using var serverCleanup = server;

        var client = new DiscordIpcClient((id, ct) => Task.FromResult<Stream?>(clientStream));
        var connectTask = client.ConnectAsync(ValidClientId);

        await server.ReadNextFrameAsync();
        await server.SendReadyAsync();
        Assert.True(await connectTask);

        // Server sends Opcode 2 Close
        await server.SendFrameAsync(DiscordIpcFrame.OpcodeClose, "{}");

        await Task.Delay(100);

        Assert.False(client.IsConnected);

        await client.CloseAsync();
    }

    [Fact]
    public async Task ReadLoop_HandlesOpcode3Ping_EchoesOpcode4Pong()
    {
        var (clientStream, server) = TestIpcDuplexStream.CreatePair();
        await using var serverCleanup = server;

        var client = new DiscordIpcClient((id, ct) => Task.FromResult<Stream?>(clientStream));
        var connectTask = client.ConnectAsync(ValidClientId);

        await server.ReadNextFrameAsync();
        await server.SendReadyAsync();
        Assert.True(await connectTask);

        string pingPayload = "{\"message\":\"hello 👋\"}";
        await server.SendFrameAsync(DiscordIpcFrame.OpcodePing, pingPayload);

        var pongFrame = await server.ReadNextFrameAsync();
        Assert.Equal(DiscordIpcFrame.OpcodePong, pongFrame.Opcode);
        Assert.Equal(pingPayload, pongFrame.GetPayloadAsString());

        await client.CloseAsync();
    }

    [Fact]
    public async Task ReadLoop_OversizedFrame_RejectsWithFramingError()
    {
        var (clientStream, server) = TestIpcDuplexStream.CreatePair();
        await using var serverCleanup = server;

        var client = new DiscordIpcClient((id, ct) => Task.FromResult<Stream?>(clientStream));
        var connectTask = client.ConnectAsync(ValidClientId);

        await server.ReadNextFrameAsync();
        await server.SendReadyAsync();
        Assert.True(await connectTask);

        PresenceConnectionStatus lastStatus = PresenceConnectionStatus.Connected;
        client.StatusChanged += (status, msg) => lastStatus = status;

        // Construct frame with header claiming 65537 bytes (> 64 KiB cap)
        byte[] oversizedHeader = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(oversizedHeader.AsSpan(0, 4), DiscordIpcFrame.OpcodeFrame);
        BinaryPrimitives.WriteInt32LittleEndian(oversizedHeader.AsSpan(4, 4), 65537);

        await server.SendRawBytesAsync(oversizedHeader);

        await Task.Delay(100);

        Assert.Equal(PresenceConnectionStatus.Error, lastStatus);
        Assert.False(client.IsConnected);

        await client.CloseAsync();
    }

    [Fact]
    public async Task ConnectAndSend_WithoutStream_NeverReportSuccess()
    {
        // Connector returns null (simulating Discord not running)
        var client = new DiscordIpcClient((id, ct) => Task.FromResult<Stream?>(null));

        bool connected = await client.ConnectAsync(ValidClientId);
        Assert.False(connected);
        Assert.False(client.IsConnected);

        // Send activity without stream must throw, not silently succeed
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendActivityAsync(SupportedApp.T3Code));
    }
}
