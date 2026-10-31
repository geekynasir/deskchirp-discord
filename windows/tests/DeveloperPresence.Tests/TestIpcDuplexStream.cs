using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using DeveloperPresence.Core.Discord;
using DeveloperPresence.Core.Models;

namespace DeveloperPresence.Tests;

public sealed class TestIpcDuplexStream : Stream
{
    private readonly Stream _reader;
    private readonly Stream _writer;
    private readonly Action? _onDispose;
    private int _disposed;

    public TestIpcDuplexStream(Stream reader, Stream writer, Action? onDispose = null)
    {
        _reader = reader;
        _writer = writer;
        _onDispose = onDispose;
    }

    public static (Stream clientStream, TestIpcServer server) CreatePair()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var clientStream = new TestIpcDuplexStream(
            serverToClient.Reader.AsStream(),
            clientToServer.Writer.AsStream(),
            onDispose: () =>
            {
                try { clientToServer.Writer.Complete(); } catch { }
                try { serverToClient.Reader.Complete(); } catch { }
            });

        var serverStream = new TestIpcDuplexStream(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            onDispose: () =>
            {
                try { serverToClient.Writer.Complete(); } catch { }
                try { clientToServer.Reader.Complete(); } catch { }
            });

        return (clientStream, new TestIpcServer(serverStream));
    }

    public override bool CanRead => _reader.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _writer.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _writer.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _writer.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => _reader.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _reader.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _reader.ReadAsync(buffer, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => _writer.Write(buffer, offset, count);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _writer.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _writer.WriteAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && disposing)
        {
            _onDispose?.Invoke();
            _reader.Dispose();
            _writer.Dispose();
        }
        base.Dispose(disposing);
    }
}

public sealed class TestIpcServer : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly DiscordFrameParser _parser = new();
    private readonly byte[] _readBuffer = new byte[4096];

    public Stream Stream => _stream;

    public TestIpcServer(Stream stream)
    {
        _stream = stream;
    }

    public async Task<DiscordIpcFrame> ReadNextFrameAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            int bytesRead = await _stream.ReadAsync(_readBuffer, ct).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                throw new EndOfStreamException("Client closed stream.");
            }

            var frames = _parser.ProcessIncomingBytes(_readBuffer, bytesRead);
            if (frames.Count > 0)
            {
                return frames[0];
            }
        }

        throw new OperationCanceledException();
    }

    public async Task SendFrameAsync(int opcode, string jsonPayload, CancellationToken ct = default)
    {
        byte[] frame = DiscordFrameParser.Pack(opcode, jsonPayload);
        await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task SendRawBytesAsync(byte[] bytes, CancellationToken ct = default)
    {
        await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public Task SendReadyAsync(CancellationToken ct = default)
    {
        string readyJson = "{\"cmd\":\"DISPATCH\",\"evt\":\"READY\",\"data\":{\"v\":1,\"user\":{\"id\":\"42\"}}}";
        return SendFrameAsync(DiscordIpcFrame.OpcodeFrame, readyJson, ct);
    }

    public Task SendActivityAckAsync(string nonce, CancellationToken ct = default)
    {
        string ackJson = $"{{\"cmd\":\"SET_ACTIVITY\",\"data\":{{}},\"evt\":null,\"nonce\":\"{nonce}\"}}";
        return SendFrameAsync(DiscordIpcFrame.OpcodeFrame, ackJson, ct);
    }

    public Task SendErrorAckAsync(string nonce, string message, CancellationToken ct = default)
    {
        string errJson = $"{{\"cmd\":\"SET_ACTIVITY\",\"data\":{{\"message\":\"{message}\"}},\"evt\":\"ERROR\",\"nonce\":\"{nonce}\"}}";
        return SendFrameAsync(DiscordIpcFrame.OpcodeFrame, errJson, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
