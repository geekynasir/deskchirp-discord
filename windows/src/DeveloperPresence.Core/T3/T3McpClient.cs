using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DeveloperPresence.Core.T3;

/// <summary>Read-only MCP client. Calls are serialized and scoped to one endpoint and sign-in.</summary>
public sealed class T3McpClient : IT3McpClient
{
    private const string InitialProtocolVersion = "2025-06-18";
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _callLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private string? _sessionId;
    private string _protocolVersion = InitialProtocolVersion;
    private Uri? _endpoint;
    private string? _credential;
    private bool _initialized;
    private int _nextId;
    private int _disposeStarted;
    private static readonly JsonSerializerOptions JsonOptions = new();
    public const int MaxResponseBytes = 1024 * 1024;

    public T3McpClient() : this(new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(20) }, true) { }

    public T3McpClient(HttpClient httpClient) : this(httpClient, false) { }

    private T3McpClient(HttpClient httpClient, bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<T3CapabilitiesResult> GetCapabilitiesAsync(Uri mcpUri, string bearerToken, CancellationToken ct = default)
    {
        var payload = await CallToolAsync(mcpUri, bearerToken, "orchestrator_capabilities", new { }, ct).ConfigureAwait(false);
        if (!payload.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Array)
            throw InvalidResponse();
        var result = payload.Deserialize<T3CapabilitiesResult>(JsonOptions) ?? throw InvalidResponse();
        if (result.Providers == null || result.Providers.Any(p => p == null ||
            string.IsNullOrWhiteSpace(p.ProviderInstanceId) || string.IsNullOrWhiteSpace(p.DriverKind)))
            throw InvalidResponse();
        return result;
    }

    public async Task<T3ThreadListResult> ListThreadsAsync(Uri mcpUri, string bearerToken, int? cursor = null, CancellationToken ct = default)
    {
        var args = new Dictionary<string, object>
        {
            ["allProjects"] = true, ["includeSubagents"] = true, ["limit"] = 100
        };
        if (cursor.HasValue) args["cursor"] = cursor.Value;
        var payload = await CallToolAsync(mcpUri, bearerToken, "t3_thread_list", args, ct).ConfigureAwait(false);
        if (!payload.TryGetProperty("threads", out var threads) || threads.ValueKind != JsonValueKind.Array ||
            !payload.TryGetProperty("nextCursor", out _) || !payload.TryGetProperty("total", out _))
            throw InvalidResponse();
        var result = payload.Deserialize<T3ThreadListResult>(JsonOptions) ?? throw InvalidResponse();
        if (result.Threads == null || result.Threads.Count > 100 || result.Total < 0 ||
            result.Total > T3ActivityService.MaxPollThreads ||
            result.NextCursor is < 0 || result.NextCursor <= cursor.GetValueOrDefault() ||
            result.Threads.Any(t => t == null || string.IsNullOrWhiteSpace(t.ThreadId) ||
                string.IsNullOrWhiteSpace(t.ProviderInstanceId) || string.IsNullOrWhiteSpace(t.Status) ||
                t.RelationshipToParent is not (null or "fork" or "subagent")))
            throw InvalidResponse();
        // A last page must not silently omit known rows. Counts may shrink during pagination.
        if (result.NextCursor == null && cursor.GetValueOrDefault() + result.Threads.Count < result.Total)
            throw InvalidResponse();
        return result;
    }

    private async Task<JsonElement> CallToolAsync(Uri endpoint, string credential, string tool, object arguments, CancellationToken ct)
    {
        if (tool is not ("orchestrator_capabilities" or "t3_thread_list")) throw new ArgumentException("Unsupported tool.", nameof(tool));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _callLock.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            if (_endpoint != endpoint || !string.Equals(_credential, credential, StringComparison.Ordinal))
            {
                ResetSession();
                _endpoint = endpoint;
                _credential = credential;
            }
            await EnsureInitializedAsync(endpoint, credential, lifetime.Token).ConfigureAwait(false);
            string id = NextId();
            using var doc = await PostAsync(endpoint, credential,
                new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name = tool, arguments } },
                id, initialize: false, lifetime.Token).ConfigureAwait(false) ?? throw InvalidResponse();
            var result = GetResult(doc.RootElement);
            if (result.TryGetProperty("isError", out var isError) && isError.ValueKind != JsonValueKind.False)
                throw InvalidResponse();
            if (result.TryGetProperty("structuredContent", out var structured))
            {
                if (structured.ValueKind != JsonValueKind.Object) throw InvalidResponse();
                return structured.Clone();
            }
            if (result.TryGetProperty("content", out var contents) && contents.ValueKind == JsonValueKind.Array)
            {
                foreach (var content in contents.EnumerateArray())
                {
                    if (content.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "text" &&
                        content.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        using var inner = JsonDocument.Parse(text.GetString()!);
                        if (inner.RootElement.ValueKind != JsonValueKind.Object) throw InvalidResponse();
                        return inner.RootElement.Clone();
                    }
                }
            }
            throw InvalidResponse();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            ResetSession();
            throw new TimeoutException("Couldn’t reach T3 Code.");
        }
        catch
        {
            ResetSession();
            throw;
        }
        finally { _callLock.Release(); }
    }

    private async Task EnsureInitializedAsync(Uri endpoint, string credential, CancellationToken ct)
    {
        if (_initialized) return;
        string id = NextId();
        using var doc = await PostAsync(endpoint, credential, new
        {
            jsonrpc = "2.0", id, method = "initialize",
            @params = new { protocolVersion = InitialProtocolVersion, capabilities = new { },
                clientInfo = new { name = "Developer Presence", version = "1.0.0" } }
        }, id, initialize: true, ct).ConfigureAwait(false) ?? throw InvalidResponse();
        var result = GetResult(doc.RootElement);
        if (!result.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.String ||
            version.GetString() is not ("2025-03-26" or "2025-06-18" or "2025-11-25")) throw InvalidResponse();
        _protocolVersion = version.GetString()!;
        using var ignored = await PostAsync(endpoint, credential,
            new { jsonrpc = "2.0", method = "notifications/initialized" }, null, initialize: false, ct).ConfigureAwait(false);
        _initialized = true;
    }

    private string NextId() => $"presence-{Interlocked.Increment(ref _nextId)}";

    private async Task<JsonDocument?> PostAsync(Uri endpoint, string credential, object body, string? expectedId, bool initialize, CancellationToken ct)
    {
        // HttpClient's timeout only covers headers with ResponseHeadersRead. This deadline
        // also bounds a stalled body or an SSE stream containing only heartbeats.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        ct = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Add("MCP-Protocol-Version", _protocolVersion);
        if (!initialize && !string.IsNullOrEmpty(_sessionId)) request.Headers.Add("Mcp-Session-Id", _sessionId);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new T3AuthenticationException("Sign in to T3 Code again.");
        if ((int)response.StatusCode is >= 300 and < 400) throw new HttpRequestException("T3 Code redirected this request.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Couldn’t reach T3 Code.");
        if (initialize && response.Headers.TryGetValues("mcp-session-id", out var ids))
        {
            string? session = ids.SingleOrDefault();
            if (string.IsNullOrWhiteSpace(session) || session.Length > 1024) throw InvalidResponse();
            _sessionId = session;
        }
        if (expectedId == null) return null;
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw InvalidResponse();
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        JsonDocument document;
        if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            document = await ReadSseAsync(stream, expectedId, ct).ConfigureAwait(false);
        else
            document = await ReadJsonAsync(stream, ct).ConfigureAwait(false);
        try
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var matches = root.EnumerateArray().Where(e => MatchesId(e, expectedId)).ToArray();
                if (matches.Length != 1) throw InvalidResponse();
                var selected = JsonDocument.Parse(matches[0].GetRawText());
                document.Dispose();
                document = selected;
                root = document.RootElement;
            }
            if (!MatchesId(root, expectedId) || !root.TryGetProperty("jsonrpc", out var rpc) ||
                rpc.ValueKind != JsonValueKind.String || rpc.GetString() != "2.0") throw InvalidResponse();
            _ = GetResult(root);
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private static JsonElement GetResult(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _) ||
            !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        return result;
    }

    private static bool MatchesId(JsonElement root, string expected) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == expected;

    private static async Task<JsonDocument> ReadJsonAsync(Stream stream, CancellationToken ct)
    {
        using var body = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (body.Length + read > MaxResponseBytes) throw InvalidResponse();
            body.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(body.ToArray());
    }

    private static async Task<JsonDocument> ReadSseAsync(Stream stream, string expectedId, CancellationToken ct)
    {
        using var line = new MemoryStream();
        using var data = new MemoryStream();
        byte[] buffer = new byte[4096];
        int total = 0;
        bool previousWasCr = false;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) throw InvalidResponse();
            total += read;
            if (total > MaxResponseBytes) throw InvalidResponse();
            for (int i = 0; i < read; i++)
            {
                byte value = buffer[i];
                if (value == 10 && previousWasCr) { previousWasCr = false; continue; }
                previousWasCr = value == 13;
                if (value is not (10 or 13)) { line.WriteByte(value); continue; }
                if (line.Length == 0)
                {
                    if (data.Length == 0) continue;
                    using var parsed = JsonDocument.Parse(data.ToArray());
                    data.SetLength(0);
                    if (MatchesId(parsed.RootElement, expectedId)) return JsonDocument.Parse(parsed.RootElement.GetRawText());
                }
                else
                {
                    // Decode only a complete line, so fragmented UTF-8 remains intact.
                    string text = new UTF8Encoding(false, true).GetString(line.ToArray());
                    line.SetLength(0);
                    if (!text.StartsWith("data:", StringComparison.Ordinal)) continue;
                    string payload = text[5..];
                    if (payload.StartsWith(' ')) payload = payload[1..];
                    byte[] bytes = Encoding.UTF8.GetBytes(payload);
                    data.Write(bytes);
                    data.WriteByte(10);
                }
            }
        }
    }

    private static InvalidOperationException InvalidResponse() => new("T3 activity isn’t available right now, so only the app name is shown.");

    // Called only while holding _callLock. Endpoint and credential changes automatically
    // reset the session, as do authentication, network, and response-validation failures.
    private void ResetSession()
    {
        _initialized = false;
        _sessionId = null;
        _protocolVersion = InitialProtocolVersion;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _lifetime.Cancel();
        await _callLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ResetSession();
            _credential = null;
            _endpoint = null;
            if (_ownsHttpClient) _httpClient.Dispose();
        }
        finally { _callLock.Release(); }
    }
}
