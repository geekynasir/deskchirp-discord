using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;

namespace DeveloperPresence.Core.Discord;

/// <summary>
/// Native Named Pipe client for Discord IPC (discord-ipc-0 .. discord-ipc-9).
/// Implements atomic whole-frame writes, 64 KiB framing enforcement, automatic Ping-Pong echoing,
/// nonce-based command ack tracking, READY handshake synchronization, and per-connection lifecycle management.
/// </summary>
public sealed class DiscordIpcClient : IDiscordIpcClient
{
    private readonly Func<string, CancellationToken, Task<Stream?>>? _streamConnector;
    private readonly object _stateLock = new();

    private ConnectionSession? _currentSession;
    private CancellationTokenSource? _scanCts;
    private bool _isDisposed;

    public event Action<PresenceConnectionStatus, string?>? StatusChanged;
    public event Action<DiscordIpcFrame>? FrameReceived;

    public bool IsConnected
    {
        get
        {
            var session = _currentSession;
            return session != null && session.IsReady && !session.IsDisposed;
        }
    }

    public DiscordIpcClient() : this(null)
    {
    }

    public DiscordIpcClient(Func<string, CancellationToken, Task<Stream?>>? streamConnector)
    {
        _streamConnector = streamConnector;
    }

    public async Task<bool> ConnectAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || !PresenceSettings.IsValidClientId(clientId))
        {
            SetStatus(PresenceConnectionStatus.Error, "Missing or invalid Discord Application ID.");
            return false;
        }

        // Cleanly close any existing connection before starting a new one
        await CloseAsync().ConfigureAwait(false);

        CancellationTokenSource scanCts;
        lock (_stateLock)
        {
            if (_isDisposed)
            {
                return false;
            }
            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            scanCts = _scanCts;
        }

        var token = scanCts.Token;
        SetStatus(PresenceConnectionStatus.Connecting, "Scanning Discord named pipes (0..9)...");

        Stream? connectedStream = null;
        try
        {
            if (_streamConnector != null)
            {
                connectedStream = await _streamConnector(clientId, token).ConfigureAwait(false);
            }
            else
            {
                connectedStream = await ScanWindowsPipesAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(PresenceConnectionStatus.Disconnected, "Connection canceled.");
            return false;
        }
        catch (Exception ex)
        {
            SetStatus(PresenceConnectionStatus.Error, $"Connection attempt failed: {ex.Message}");
            return false;
        }

        if (connectedStream == null)
        {
            SetStatus(PresenceConnectionStatus.Disconnected, "Discord desktop client not found on local named pipes.");
            return false;
        }

        ConnectionSession session;
        lock (_stateLock)
        {
            if (token.IsCancellationRequested || _isDisposed)
            {
                try { connectedStream.Dispose(); } catch { }
                return false;
            }

            session = new ConnectionSession(connectedStream, clientId, token);
            _currentSession = session;
        }

        // Start background reading loop with local stream and parser snapshots
        session.ReadLoopTask = Task.Run(() => ReadLoopAsync(session), session.Cts.Token);

        // Perform Opcode 0 Handshake and await READY frame
        try
        {
            var handshake = new DiscordHandshakePayload
            {
                Version = 1,
                ClientId = clientId
            };

            string handshakeJson = JsonSerializer.Serialize(handshake);
            byte[] handshakeFrame = DiscordFrameParser.Pack(DiscordIpcFrame.OpcodeHandshake, handshakeJson);

            await WriteRawFrameAsync(session, handshakeFrame, session.Cts.Token).ConfigureAwait(false);

            // Await READY frame with 5-second bounded timeout
            using var readyTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedReadyCts = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token, readyTimeoutCts.Token);
            using (linkedReadyCts.Token.Register(() => session.ReadyTcs.TrySetCanceled(linkedReadyCts.Token)))
            {
                bool isReady = await session.ReadyTcs.Task.ConfigureAwait(false);
                if (isReady && !session.IsDisposed)
                {
                    session.IsReady = true;
                    SetStatus(PresenceConnectionStatus.Connected, "Connected to Discord IPC.");
                    return true;
                }
            }

            SetStatus(PresenceConnectionStatus.Error, "Handshake failed: READY not received.");
            await CloseSessionAsync(session).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            SetStatus(PresenceConnectionStatus.Error, "Handshake timed out waiting for READY.");
            await CloseSessionAsync(session).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            SetStatus(PresenceConnectionStatus.Error, $"Handshake failed: {ex.Message}");
            await CloseSessionAsync(session).ConfigureAwait(false);
            return false;
        }
    }

    private static async Task<Stream?> ScanWindowsPipesAsync(CancellationToken token)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        for (int i = 0; i <= 9; i++)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            string pipeName = $"discord-ipc-{i}";
            var candidate = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

                await candidate.ConnectAsync(linkedCts.Token).ConfigureAwait(false);

                if (candidate.IsConnected)
                {
                    return candidate;
                }
            }
            catch
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }
        }

        return null;
    }

    public async Task SendActivityAsync(DiscordActivity activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var session = _currentSession;
        if (session == null || !session.IsReady || session.IsDisposed)
        {
            throw new InvalidOperationException("Not connected to Discord IPC.");
        }

        await SendSetActivityPayloadAsync(session, activity, ct).ConfigureAwait(false);
    }

    public async Task SendActivityAsync(SupportedApp app, CancellationToken ct = default)
    {
        var session = _currentSession;
        if (session == null || !session.IsReady || session.IsDisposed)
        {
            throw new InvalidOperationException("Not connected to Discord IPC.");
        }

        var activity = ActivityFormatter.FormatActivity(app, enableSharing: true, isPaused: false);
        await SendSetActivityPayloadAsync(session, activity, ct).ConfigureAwait(false);
    }

    public async Task ClearActivityAsync(CancellationToken ct = default)
    {
        var session = _currentSession;
        if (session == null || !session.IsReady || session.IsDisposed)
        {
            throw new InvalidOperationException("Not connected to Discord IPC.");
        }

        await SendSetActivityPayloadAsync(session, activity: null, ct).ConfigureAwait(false);
    }

    private async Task SendSetActivityPayloadAsync(ConnectionSession session, DiscordActivity? activity, CancellationToken ct)
    {
        if (session.IsDisposed)
        {
            throw new InvalidOperationException("Discord IPC session is closed.");
        }

        string nonce = Guid.NewGuid().ToString("N");
        var payload = new DiscordSetActivityPayload
        {
            Command = "SET_ACTIVITY",
            Args = new DiscordSetActivityArgs
            {
                ProcessId = Environment.ProcessId,
                Activity = activity
            },
            Nonce = nonce
        };

        string json = JsonSerializer.Serialize(payload);
        byte[] frame = DiscordFrameParser.Pack(DiscordIpcFrame.OpcodeFrame, json);

        var tcs = new TaskCompletionSource<DiscordIpcFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PendingCommands[nonce] = tcs;

        try
        {
            await WriteRawFrameAsync(session, frame, ct).ConfigureAwait(false);

            // Bounded wait for command ack (5 seconds)
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cts.Token, timeoutCts.Token);
            using (linkedCts.Token.Register(() => tcs.TrySetCanceled(linkedCts.Token)))
            {
                await tcs.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            session.PendingCommands.TryRemove(nonce, out _);
            await CloseSessionAsync(session).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            throw new TimeoutException("Timed out awaiting SET_ACTIVITY acknowledgment from Discord.");
        }
        catch (Exception)
        {
            session.PendingCommands.TryRemove(nonce, out _);
            await CloseSessionAsync(session).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WriteRawFrameAsync(ConnectionSession session, byte[] frame, CancellationToken ct)
    {
        if (session.IsDisposed || !session.Stream.CanWrite)
        {
            throw new InvalidOperationException("Cannot write to closed or disposed Discord stream.");
        }

        using var linkedWrite = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cts.Token);
        ct = linkedWrite.Token;
        await session.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (session.IsDisposed || !session.Stream.CanWrite)
            {
                throw new InvalidOperationException("Cannot write to closed or disposed Discord stream.");
            }

            ct.ThrowIfCancellationRequested();
            await session.Stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await session.Stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            session.WriteLock.Release();
        }
    }

    private async Task ReadLoopAsync(ConnectionSession session)
    {
        Stream stream = session.Stream;
        DiscordFrameParser parser = session.Parser;
        var token = session.Cts.Token;
        byte[] buffer = new byte[4096];
        bool errorOccurred = false;

        try
        {
            while (!token.IsCancellationRequested && !session.IsDisposed)
            {
                int bytesRead;
                try
                {
                    bytesRead = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested && !session.IsDisposed)
                    {
                        SetStatus(PresenceConnectionStatus.Error, $"Connection error: {ex.Message}");
                    }
                    break;
                }

                if (bytesRead <= 0)
                {
                    // EOF encountered: Discord closed pipe/socket
                    break;
                }

                List<DiscordIpcFrame> frames;
                try
                {
                    frames = parser.ProcessIncomingBytes(buffer, bytesRead);
                }
                catch (InvalidDataException ex)
                {
                    errorOccurred = true;
                    SetStatus(PresenceConnectionStatus.Error, $"Protocol framing error: {ex.Message}");
                    break;
                }

                foreach (var frame in frames)
                {
                    FrameReceived?.Invoke(frame);

                    if (frame.Opcode == DiscordIpcFrame.OpcodePing)
                    {
                        byte[] pongFrame = DiscordFrameParser.CreatePongResponse(frame);
                        try
                        {
                            await WriteRawFrameAsync(session, pongFrame, token).ConfigureAwait(false);
                        }
                        catch { }
                    }
                    else if (frame.Opcode == DiscordIpcFrame.OpcodeClose)
                    {
                        SetStatus(PresenceConnectionStatus.Disconnected, "Discord closed connection.");
                        return;
                    }
                    else if (frame.Opcode == DiscordIpcFrame.OpcodeFrame)
                    {
                        HandleIncomingFrame(session, frame);
                    }
                }
            }
        }
        finally
        {
            session.IsReady = false;

            // Fail any commands awaiting ack on this session
            foreach (var kvp in session.PendingCommands)
            {
                kvp.Value.TrySetException(new IOException("Discord connection lost before acknowledgment."));
            }
            session.PendingCommands.Clear();
            session.ReadyTcs.TrySetResult(false);

            if (!token.IsCancellationRequested && !session.IsDisposed && !errorOccurred)
            {
                SetStatus(PresenceConnectionStatus.Disconnected, "Disconnected from Discord.");
            }

            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch { }
        }
    }

    private void HandleIncomingFrame(ConnectionSession session, DiscordIpcFrame frame)
    {
        string json;
        JsonDocument doc;
        try
        {
            json = frame.GetPayloadAsString();
            doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                throw new InvalidDataException("Discord IPC message must be a JSON object.");
            }
        }
        catch (Exception ex)
        {
            // Reject malformed JSON from wire and terminate session
            SetStatus(PresenceConnectionStatus.Error, $"Invalid JSON received from Discord IPC: {ex.Message}");
            session.Cts.Cancel();
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            string? cmd = root.TryGetProperty("cmd", out var cmdProp) ? cmdProp.GetString() : null;
            string? evt = root.TryGetProperty("evt", out var evtProp) ? evtProp.GetString() : null;
            string? nonce = root.TryGetProperty("nonce", out var nonceProp) ? nonceProp.GetString() : null;

            if (cmd == "DISPATCH" && evt == "READY")
            {
                session.ReadyTcs.TrySetResult(true);
                return;
            }

            if (string.Equals(evt, "ERROR", StringComparison.OrdinalIgnoreCase))
            {
                string msg = root.TryGetProperty("data", out var dataProp) &&
                             dataProp.TryGetProperty("message", out var msgProp)
                    ? msgProp.GetString() ?? "Unknown error"
                    : "Unknown Discord error";
                SetStatus(PresenceConnectionStatus.Error, $"Discord error: {msg}");

                if (!string.IsNullOrEmpty(nonce) && session.PendingCommands.TryRemove(nonce, out var failedCmd))
                {
                    failedCmd.TrySetException(new InvalidOperationException($"Discord error: {msg}"));
                }
                return;
            }

            // Route command acknowledgment to pending command
            if (cmd == "SET_ACTIVITY" && evt == null && !string.IsNullOrEmpty(nonce) &&
                session.PendingCommands.TryRemove(nonce, out var pendingCmd))
            {
                pendingCmd.TrySetResult(frame);
            }
        }
    }

    private void SetStatus(PresenceConnectionStatus status, string? message)
    {
        StatusChanged?.Invoke(status, message);
    }

    public async Task CloseAsync()
    {
        CancellationTokenSource? scanCts;
        ConnectionSession? session;

        lock (_stateLock)
        {
            scanCts = _scanCts;
            _scanCts = null;
            session = _currentSession;
            _currentSession = null;
        }

        if (scanCts != null)
        {
            try
            {
                scanCts.Cancel();
                scanCts.Dispose();
            }
            catch { }
        }

        if (session != null)
        {
            session.IsReady = false;

            await session.DisposeAsync().ConfigureAwait(false);

            if (session.ReadLoopTask != null)
            {
                try
                {
                    await session.ReadLoopTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    private async Task CloseSessionAsync(ConnectionSession session)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(_currentSession, session)) _currentSession = null;
        }
        await session.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_isDisposed)
            {
                return;
            }
            _isDisposed = true;
        }

        await CloseAsync().ConfigureAwait(false);
    }

    private sealed class ConnectionSession : IAsyncDisposable
    {
        public Stream Stream { get; }
        public string ClientId { get; }
        public DiscordFrameParser Parser { get; } = new();
        public CancellationTokenSource Cts { get; }
        public TaskCompletionSource<bool> ReadyTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<string, TaskCompletionSource<DiscordIpcFrame>> PendingCommands { get; } = new();
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public Task? ReadLoopTask { get; set; }
        public bool IsReady { get; set; }

        private int _disposed;
        public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

        public ConnectionSession(Stream stream, string clientId, CancellationToken externalToken)
        {
            Stream = stream;
            ClientId = clientId;
            Cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try { Cts.Cancel(); } catch { }
            ReadyTcs.TrySetResult(false);

            foreach (var kvp in PendingCommands)
            {
                kvp.Value.TrySetException(new IOException("Discord IPC connection closed."));
            }
            PendingCommands.Clear();

            try
            {
                await Stream.DisposeAsync().ConfigureAwait(false);
            }
            catch { }

            // Active operations may still unwind and release the semaphore. Let these
            // managed objects be collected after their final users leave the session.
        }
    }
}
