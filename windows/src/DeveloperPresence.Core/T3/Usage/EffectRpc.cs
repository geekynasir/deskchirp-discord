using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DeveloperPresence.Core.T3.Usage;

/// <summary>Decoded inbound Effect-RPC frame. Only the shapes in spec/t3-usage-transport.md are used.</summary>
public abstract record RpcFrame;
/// <summary>Exit for a request. A Failure or Die makes the value unavailable; server text is never kept.</summary>
public sealed record RpcExit(string RequestId, bool Success, JsonElement? Value, bool AuthorizationError) : RpcFrame;
public sealed record RpcChunk(string RequestId) : RpcFrame;
public sealed record RpcPing : RpcFrame;
public sealed record RpcPong : RpcFrame;
/// <summary>A server-side defect or any shape this client does not use. Ends the poll.</summary>
public sealed record RpcUnexpected(string Tag) : RpcFrame;

public static class EffectRpcCodec
{
    public static string EncodeRequest(string id, string tag, object payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("_tag", "Request");
            writer.WriteString("id", id);
            writer.WriteString("tag", tag);
            writer.WriteStartArray("headers");
            writer.WriteEndArray();
            writer.WritePropertyName("payload");
            JsonSerializer.Serialize(writer, payload, payload.GetType());
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string EncodePing() => "{\"_tag\":\"Ping\"}";
    public static string EncodePong() => "{\"_tag\":\"Pong\"}";
    public static string EncodeAck(string requestId) => JsonSerializer.Serialize(new Dictionary<string, string> { ["_tag"] = "Ack", ["requestId"] = requestId });

    /// <summary>Decodes one text message holding one object or an array of objects.</summary>
    public static IReadOnlyList<RpcFrame> Decode(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var frames = new List<RpcFrame>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var item in doc.RootElement.EnumerateArray()) frames.Add(DecodeOne(item));
        else
            frames.Add(DecodeOne(doc.RootElement));
        return frames;
    }

    private static RpcFrame DecodeOne(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("_tag", out var tagElement) || tagElement.ValueKind != JsonValueKind.String)
            return new RpcUnexpected("invalid");
        string tag = tagElement.GetString()!;
        switch (tag)
        {
            case "Exit":
            {
                string? id = RequestId(e);
                if (id == null || !e.TryGetProperty("exit", out var exit) || exit.ValueKind != JsonValueKind.Object ||
                    !exit.TryGetProperty("_tag", out var exitTag) || exitTag.ValueKind != JsonValueKind.String)
                    return new RpcUnexpected("Exit");
                if (exitTag.GetString() == "Success")
                    return new RpcExit(id, true, exit.TryGetProperty("value", out var value) ? value.Clone() : null, false);
                // Failure (including a Die cause) or anything else: unavailable.
                bool auth = exit.TryGetProperty("cause", out var cause) && HasAuthorizationError(cause, 0);
                return new RpcExit(id, false, null, auth);
            }
            case "Chunk":
                return RequestId(e) is { } chunkId ? new RpcChunk(chunkId) : new RpcUnexpected("Chunk");
            case "Ping": return new RpcPing();
            case "Pong": return new RpcPong();
            default: return new RpcUnexpected(tag is "Defect" or "ClientProtocolError" ? tag : "other");
        }
    }

    private static string? RequestId(JsonElement e)
    {
        if (!e.TryGetProperty("requestId", out var id)) return null;
        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null
        };
    }

    private static bool HasAuthorizationError(JsonElement cause, int depth)
    {
        if (depth > 16) return false;
        switch (cause.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in cause.EnumerateArray()) if (HasAuthorizationError(item, depth + 1)) return true;
                return false;
            case JsonValueKind.Object:
                if (cause.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("_tag", out var t) && t.ValueKind == JsonValueKind.String &&
                    t.GetString() == "EnvironmentAuthorizationError") return true;
                foreach (var name in new[] { "left", "right", "cause" })
                    if (cause.TryGetProperty(name, out var inner) && HasAuthorizationError(inner, depth + 1)) return true;
                return false;
            default: return false;
        }
    }
}

public sealed class T3UsageHttpStatusException : Exception
{
    public int StatusCode { get; }
    public T3UsageHttpStatusException(int statusCode) : base("T3 refused the token usage connection.") { StatusCode = statusCode; }
}

/// <summary>A text-message socket. Tests use an in-memory fake; production uses ClientWebSocket.</summary>
public interface IT3UsageSocket : IAsyncDisposable
{
    Task SendAsync(string text, CancellationToken ct);
    /// <summary>Returns the next complete text message, or null when the socket closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);
}

public interface IT3UsageSocketFactory
{
    /// <summary>Opens /ws with a bearer header. Throws <see cref="T3UsageHttpStatusException"/> on 401/426.</summary>
    Task<IT3UsageSocket> ConnectAsync(Uri uri, string bearerToken, CancellationToken ct);
}

public sealed class ClientWebSocketFactory : IT3UsageSocketFactory
{
    public const int MaxMessageBytes = 2 * 1024 * 1024;

    public async Task<IT3UsageSocket> ConnectAsync(Uri uri, string bearerToken, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + bearerToken);
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        try
        {
            await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
            return new Socket(socket);
        }
        catch (WebSocketException) when (socket.HttpStatusCode is var status && (int)status != 0)
        {
            socket.Dispose();
            throw new T3UsageHttpStatusException((int)status);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class Socket : IT3UsageSocket
    {
        private readonly ClientWebSocket _socket;
        public Socket(ClientWebSocket socket) { _socket = socket; }

        public Task SendAsync(string text, CancellationToken ct) =>
            _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[16 * 1024];
            while (true)
            {
                var result = await _socket.ReceiveAsync(chunk, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (buffer.Length + result.Count > MaxMessageBytes) throw new InvalidDataException("Message too large.");
                buffer.Write(chunk, 0, result.Count);
                if (result.EndOfMessage)
                {
                    if (result.MessageType != WebSocketMessageType.Text) return string.Empty;
                    return new UTF8Encoding(false, true).GetString(buffer.ToArray());
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
                }
            }
            catch { }
            _socket.Dispose();
        }
    }
}

/// <summary>The answer to one request: a value, or unavailable.</summary>
public sealed record RpcResult(bool Ok, JsonElement? Value, bool AuthorizationError = false)
{
    public static readonly RpcResult Unavailable = new(false, null);
}

/// <summary>Runs one usage poll over an open socket: send requests, collect exits, then stop.</summary>
public static class EffectRpcSession
{
    public static async Task<IReadOnlyDictionary<string, RpcResult>> RunAsync(
        IT3UsageSocket socket, IReadOnlyList<(string Tag, object Payload)> requests, CancellationToken ct)
    {
        var results = new Dictionary<string, RpcResult>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < requests.Count; i++)
        {
            string id = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            pending.Add(id);
            await socket.SendAsync(EffectRpcCodec.EncodeRequest(id, requests[i].Tag, requests[i].Payload), ct).ConfigureAwait(false);
        }

        try
        {
            while (pending.Count > 0)
            {
                string? message = await socket.ReceiveAsync(ct).ConfigureAwait(false);
                if (message == null) break;
                if (message.Length == 0) continue;
                IReadOnlyList<RpcFrame> frames;
                try { frames = EffectRpcCodec.Decode(message); }
                catch (JsonException) { break; }
                bool stop = false;
                foreach (var frame in frames)
                {
                    switch (frame)
                    {
                        case RpcExit exit when pending.Remove(exit.RequestId):
                            results[exit.RequestId] = exit.Success ? new RpcResult(true, exit.Value) : new RpcResult(false, null, exit.AuthorizationError);
                            break;
                        case RpcChunk chunk:
                            // This version uses no streams: acknowledge, then end the poll.
                            await socket.SendAsync(EffectRpcCodec.EncodeAck(chunk.RequestId), ct).ConfigureAwait(false);
                            stop = true;
                            break;
                        case RpcPing:
                            await socket.SendAsync(EffectRpcCodec.EncodePong(), ct).ConfigureAwait(false);
                            break;
                        case RpcPong:
                        case RpcExit:
                            break;
                        default:
                            stop = true;
                            break;
                    }
                }
                if (stop) break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

        foreach (var id in pending) results[id] = RpcResult.Unavailable;
        return results;
    }
}
