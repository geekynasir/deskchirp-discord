using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeveloperPresence.Core.T3;
using Xunit;

namespace DeveloperPresence.Tests;

public sealed class T3McpTransportTests
{
    private static readonly Uri Endpoint = new("https://presence.test/mcp");

    [Fact]
    public async Task ReusesSessionButReinitializesForNewEndpointOrSignIn()
    {
        var handler = new ProtocolHandler();
        using var http = new HttpClient(handler);
        await using var client = new T3McpClient(http);
        await client.GetCapabilitiesAsync(Endpoint, "synthetic-one");
        await client.GetCapabilitiesAsync(Endpoint, "synthetic-one");
        Assert.Equal(1, handler.Initializations);
        await client.GetCapabilitiesAsync(new Uri("https://second.test/mcp"), "synthetic-one");
        await client.GetCapabilitiesAsync(Endpoint, "synthetic-two");
        Assert.Equal(3, handler.Initializations);
        Assert.Equal(3, handler.InitializedNotifications);
    }

    [Fact]
    public async Task RejectsMismatchedJsonReplyAndStartsANewSessionNextTime()
    {
        var handler = new ProtocolHandler { WrongReplyId = true };
        using var http = new HttpClient(handler);
        await using var client = new T3McpClient(http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCapabilitiesAsync(Endpoint, "synthetic"));
        handler.WrongReplyId = false;
        Assert.Single((await client.GetCapabilitiesAsync(Endpoint, "synthetic")).Providers);
        Assert.Equal(2, handler.Initializations);
    }

    [Fact]
    public async Task CompletesMatchingSseEventWithoutWaitingForEof()
    {
        PrefixThenOpenStream? stream = null;
        var handler = new ProtocolHandler
        {
            ToolResponse = id =>
            {
                var bytes = Encoding.UTF8.GetBytes(
                    "data: {\"jsonrpc\":\"2.0\",\"id\":99,\"result\":{}}\r\n\r\n" +
                    $"data: {{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\r\ndata: \"result\":{{\"structuredContent\":{{\"providers\":[{{\"providerInstanceId\":\"p\",\"driverKind\":\"claudeAgent\"}}]}}}}}}\r\n\r\n");
                stream = new PrefixThenOpenStream(bytes, maxChunk: 1);
                return StreamResponse(stream, "text/event-stream");
            }
        };
        using var http = new HttpClient(handler);
        await using var client = new T3McpClient(http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await client.GetCapabilitiesAsync(Endpoint, "synthetic", timeout.Token);
        Assert.Single(result.Providers);
        Assert.True(stream!.WasDisposed);
    }

    [Fact]
    public async Task RejectsOversizedSseLineBeforeEof()
    {
        var handler = new ProtocolHandler
        {
            ToolResponse = _ => StreamResponse(new PrefixThenOpenStream(
                Encoding.UTF8.GetBytes("data:" + new string('x', T3McpClient.MaxResponseBytes))), "text/event-stream")
        };
        using var http = new HttpClient(handler);
        await using var client = new T3McpClient(http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCapabilitiesAsync(Endpoint, "synthetic", timeout.Token));
    }

    [Fact]
    public async Task CancellationInterruptsOpenBodyRead()
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new PrefixThenOpenStream(Encoding.UTF8.GetBytes(": heartbeat\n\n"), onWait: () => reading.TrySetResult());
        var handler = new ProtocolHandler { ToolResponse = _ => StreamResponse(stream, "text/event-stream") };
        using var http = new HttpClient(handler);
        await using var client = new T3McpClient(http);
        using var cancel = new CancellationTokenSource();
        var work = client.GetCapabilitiesAsync(Endpoint, "synthetic", cancel.Token);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task RejectsToolErrorEvenWhenItHasStructuredData()
    {
        var handler = new ProtocolHandler { ToolError = true };
        using var http = new HttpClient(handler);
        await using var client = new T3McpClient(http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCapabilitiesAsync(Endpoint, "synthetic"));
    }

    [Theory]
    [InlineData("{\"threads\":[],\"nextCursor\":null,\"total\":2}")]
    [InlineData("{\"threads\":[],\"nextCursor\":0,\"total\":0}")]
    [InlineData("{\"threads\":[{\"threadId\":\"a\",\"status\":\"running\"}],\"nextCursor\":null,\"total\":1}")]
    public async Task RejectsIncompleteOrMalformedThreadPage(string page)
    {
        var handler = new ProtocolHandler { Payload = JsonSerializer.Deserialize<JsonElement>(page) };
        using var http = new HttpClient(handler);
        await using var client = new T3McpClient(http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListThreadsAsync(Endpoint, "synthetic"));
    }

    private static HttpResponseMessage StreamResponse(Stream stream, string type)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return response;
    }

    private sealed class ProtocolHandler : HttpMessageHandler
    {
        public int Initializations { get; private set; }
        public int InitializedNotifications { get; private set; }
        public bool WrongReplyId { get; set; }
        public bool ToolError { get; set; }
        public Func<string, HttpResponseMessage>? ToolResponse { get; init; }
        public JsonElement? Payload { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Contains(request.Headers.Accept, v => v.MediaType == "application/json");
            Assert.Contains(request.Headers.Accept, v => v.MediaType == "text/event-stream");
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            string? method = root.GetProperty("method").GetString();
            if (method == "initialize")
            {
                Assert.False(request.Headers.Contains("Mcp-Session-Id"));
                Initializations++;
                var response = Rpc(root.GetProperty("id").GetString()!, new { protocolVersion = "2025-06-18" });
                response.Headers.Add("mcp-session-id", $"session-{Initializations}");
                return response;
            }
            Assert.Equal($"session-{Initializations}", request.Headers.GetValues("Mcp-Session-Id").Single());
            Assert.Equal("2025-06-18", request.Headers.GetValues("MCP-Protocol-Version").Single());
            if (method == "notifications/initialized")
            {
                InitializedNotifications++;
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            Assert.Equal("tools/call", method);
            var parameters = root.GetProperty("params");
            string? tool = parameters.GetProperty("name").GetString();
            Assert.True(tool is "orchestrator_capabilities" or "t3_thread_list");
            if (tool == "t3_thread_list")
            {
                var args = parameters.GetProperty("arguments");
                Assert.True(args.GetProperty("allProjects").GetBoolean());
                Assert.True(args.GetProperty("includeSubagents").GetBoolean());
                Assert.Equal(100, args.GetProperty("limit").GetInt32());
            }
            string id = root.GetProperty("id").GetString()!;
            if (ToolResponse != null) return ToolResponse(id);
            return Rpc(WrongReplyId ? "unrelated" : id, new
            {
                isError = ToolError,
                structuredContent = Payload ?? JsonSerializer.SerializeToElement(new
                {
                    providers = new[] { new { providerInstanceId = "p", driverKind = "claudeAgent", displayName = "PRIVATE" } }
                })
            });
        }

        private static HttpResponseMessage Rpc(string id, object result) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }), Encoding.UTF8, "application/json")
        };
    }

    private sealed class PrefixThenOpenStream(byte[] prefix, int maxChunk = 4096, Action? onWait = null) : Stream
    {
        private int _position;
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                int count = Math.Min(Math.Min(buffer.Length, maxChunk), prefix.Length - _position);
                prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }
            onWait?.Invoke();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
}
