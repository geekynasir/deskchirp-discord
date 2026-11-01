using System.Net;
using System.Text;
using System.Text.Json;
using DeveloperPresence.Core.Credentials;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;
using DeveloperPresence.Core.T3;
using DeveloperPresence.Core.T3.Usage;
using Xunit;

namespace DeveloperPresence.Tests;

public class T3UsageTransportTests
{
    private static readonly Uri Origin = new("https://remote.example:8443");

    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, string, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return Respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // ---- Pairing ----

    [Theory]
    [InlineData("AbC123xyz", "AbC123xyz")]
    [InlineData("  AbC123xyz  ", "AbC123xyz")]
    [InlineData("https://remote.example:8443/pair#token=AbC123xyz", "AbC123xyz")]
    public void PairingInputAcceptsCodeOrSameOriginLink(string raw, string code)
    {
        Assert.True(T3UsagePairingInput.TryParse(raw, Origin, out var parsed, out _));
        Assert.Equal(code, parsed);
    }

    [Theory]
    [InlineData("https://evil.example/pair#token=AbC123xyz", T3UsageText.OtherOrigin)]
    [InlineData("https://remote.example/pair#token=AbC123xyz", T3UsageText.OtherOrigin)]
    [InlineData("https://remote.example:8443/pair?token=AbC123xyz", T3UsageText.InvalidInput)]
    [InlineData("abc", T3UsageText.InvalidInput)]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.e30.signature", T3UsageText.InvalidInput)]
    [InlineData("", T3UsageText.InvalidInput)]
    public void PairingInputRejectsOtherOriginsAndTokens(string raw, string error)
    {
        Assert.False(T3UsagePairingInput.TryParse(raw, Origin, out _, out var message));
        Assert.Equal(error, message);
    }

    [Fact]
    public async Task PairingExchangeSucceedsWithExactFormAndNoDpop()
    {
        var handler = new Handler
        {
            Respond = (_, _) => Json(HttpStatusCode.OK,
                "{\"access_token\":\"synthetic-usage\",\"token_type\":\"Bearer\",\"expires_in\":2592000,\"scope\":\"orchestration:read diagnostics:read\"}")
        };
        var now = DateTimeOffset.FromUnixTimeSeconds(1793500000);
        var client = new T3UsagePairingClient(new HttpClient(handler), () => now);
        var result = await client.ExchangeAsync(Origin, "AbC123xyz");
        Assert.Equal(T3PairingOutcome.Success, result.Outcome);
        Assert.Equal("synthetic-usage", result.Credential!.AccessToken);
        Assert.Equal(now.AddDays(30), result.Credential.ExpiresAt);

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://remote.example:8443/oauth/token", request.RequestUri!.AbsoluteUri);
        Assert.False(request.Headers.Contains("DPoP"));
        var form = body.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        Assert.Equal("urn:ietf:params:oauth:grant-type:token-exchange", form["grant_type"]);
        Assert.Equal("AbC123xyz", form["subject_token"]);
        Assert.Equal("urn:t3:params:oauth:token-type:environment-bootstrap", form["subject_token_type"]);
        Assert.Equal("urn:ietf:params:oauth:token-type:access_token", form["requested_token_type"]);
        Assert.Equal("orchestration:read diagnostics:read", form["scope"]);
        Assert.Equal("Deskchirp", form["client_label"]);
        Assert.Equal("desktop", form["client_device_type"]);
        Assert.Equal("Windows", form["client_os"]);
    }

    [Fact]
    public async Task ReusedCodeGets401Message()
    {
        var handler = new Handler { Respond = (_, _) => Json(HttpStatusCode.Unauthorized, "{\"error\":\"PRIVATE server text\"}") };
        var result = await new T3UsagePairingClient(new HttpClient(handler)).ExchangeAsync(Origin, "AbC123xyz");
        Assert.Equal(T3PairingOutcome.CodeRejected, result.Outcome);
        Assert.Equal(T3UsageText.CodeRejected, result.Message);
        Assert.Null(result.Credential);
    }

    [Fact]
    public async Task MissingDiagnosticsScopeIsRejected()
    {
        var handler = new Handler
        {
            Respond = (_, _) => Json(HttpStatusCode.OK, "{\"access_token\":\"t\",\"token_type\":\"Bearer\",\"expires_in\":60,\"scope\":\"orchestration:read\"}")
        };
        var result = await new T3UsagePairingClient(new HttpClient(handler)).ExchangeAsync(Origin, "AbC123xyz");
        Assert.Equal(T3PairingOutcome.MissingPermission, result.Outcome);
        Assert.Null(result.Credential);
    }

    [Fact]
    public async Task RedirectAndOversizedResponsesFail()
    {
        var redirect = new Handler { Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example/") } } };
        Assert.Equal(T3PairingOutcome.Failed, (await new T3UsagePairingClient(new HttpClient(redirect)).ExchangeAsync(Origin, "AbC123xyz")).Outcome);
        var big = new Handler { Respond = (_, _) => Json(HttpStatusCode.OK, "{\"x\":\"" + new string('a', 70_000) + "\"}") };
        Assert.Equal(T3PairingOutcome.Failed, (await new T3UsagePairingClient(new HttpClient(big)).ExchangeAsync(Origin, "AbC123xyz")).Outcome);
    }

    // ---- Effect-RPC frames ----

    [Fact]
    public void RequestFramesMatchTransportSpec()
    {
        string frame = EffectRpcCodec.EncodeRequest("1", "server.getUsageSummary", new Dictionary<string, object>
            { ["sinceDay"] = "2026-11-01", ["untilDay"] = "2026-11-01", ["timeZone"] = "Asia/Karachi", ["awaitRefresh"] = true });
        Assert.Equal("{\"_tag\":\"Request\",\"id\":\"1\",\"tag\":\"server.getUsageSummary\",\"headers\":[],\"payload\":{\"sinceDay\":\"2026-11-01\",\"untilDay\":\"2026-11-01\",\"timeZone\":\"Asia/Karachi\",\"awaitRefresh\":true}}", frame);
        Assert.Equal("{\"_tag\":\"Ack\",\"requestId\":\"7\"}", EffectRpcCodec.EncodeAck("7"));
        Assert.Equal("{\"_tag\":\"Ping\"}", EffectRpcCodec.EncodePing());
    }

    [Fact]
    public void DecodesSuccessFailureDieChunkAndArrays()
    {
        var frames = EffectRpcCodec.Decode(
            "[{\"_tag\":\"Exit\",\"requestId\":\"1\",\"exit\":{\"_tag\":\"Success\",\"value\":{\"n\":1}}}," +
            "{\"_tag\":\"Exit\",\"requestId\":\"2\",\"exit\":{\"_tag\":\"Failure\",\"cause\":[{\"_tag\":\"Fail\",\"error\":{\"_tag\":\"EnvironmentAuthorizationError\",\"requiredPermission\":\"diagnostics:read\"}}]}}," +
            "{\"_tag\":\"Exit\",\"requestId\":3,\"exit\":{\"_tag\":\"Failure\",\"cause\":{\"_tag\":\"Die\",\"defect\":\"PRIVATE stack\"}}}," +
            "{\"_tag\":\"Chunk\",\"requestId\":\"4\",\"values\":[]},{\"_tag\":\"Pong\"},{\"_tag\":\"Defect\",\"defect\":\"x\"}]");
        var ok = Assert.IsType<RpcExit>(frames[0]);
        Assert.True(ok.Success);
        Assert.Equal(1, ok.Value!.Value.GetProperty("n").GetInt32());
        var auth = Assert.IsType<RpcExit>(frames[1]);
        Assert.False(auth.Success);
        Assert.True(auth.AuthorizationError);
        var die = Assert.IsType<RpcExit>(frames[2]);
        Assert.Equal("3", die.RequestId);
        Assert.False(die.Success);
        Assert.Null(die.Value);
        Assert.Equal("4", Assert.IsType<RpcChunk>(frames[3]).RequestId);
        Assert.IsType<RpcPong>(frames[4]);
        Assert.IsType<RpcUnexpected>(frames[5]);
        Assert.DoesNotContain("PRIVATE", string.Join(",", frames.Select(f => f.ToString())));
    }

    private sealed class ScriptedSocket : IT3UsageSocket
    {
        public List<string> Sent { get; } = new();
        public Func<string, IEnumerable<string>> Reply { get; set; } = _ => Array.Empty<string>();
        private readonly Queue<string> _inbox = new();
        public bool Disposed { get; private set; }

        public Task SendAsync(string text, CancellationToken ct)
        {
            Sent.Add(text);
            foreach (var reply in Reply(text)) _inbox.Enqueue(reply);
            return Task.CompletedTask;
        }

        public Task<string?> ReceiveAsync(CancellationToken ct) => Task.FromResult(_inbox.Count > 0 ? _inbox.Dequeue() : null);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task ChunkIsAcknowledgedAndEndsThePollWithUnavailableValues()
    {
        var socket = new ScriptedSocket
        {
            Reply = text => text.Contains("\"id\":\"2\"")
                ? new[] { "{\"_tag\":\"Ping\"}", "{\"_tag\":\"Exit\",\"requestId\":\"1\",\"exit\":{\"_tag\":\"Success\",\"value\":1}}", "{\"_tag\":\"Chunk\",\"requestId\":\"2\",\"values\":[]}" }
                : Array.Empty<string>()
        };
        var results = await EffectRpcSession.RunAsync(socket, new (string, object)[] { ("a", new { }), ("b", new { }) }, CancellationToken.None);
        Assert.True(results["1"].Ok);
        Assert.False(results["2"].Ok);
        Assert.Contains("{\"_tag\":\"Pong\"}", socket.Sent);
        Assert.Contains("{\"_tag\":\"Ack\",\"requestId\":\"2\"}", socket.Sent);
    }

    // ---- Usage calculations ----

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string Summary = "{\"contractVersion\":6,\"sources\":[{\"status\":\"ok\",\"sourcePath\":\"PRIVATE/home\"}],\"pricing\":{\"status\":\"fresh\"},\"buckets\":[" +
        "{\"provider\":\"claude\",\"totals\":{\"uncachedInputTokens\":400000,\"cachedInputTokens\":300000,\"cacheCreationTokens\":42000,\"outputTokens\":100000,\"reasoningTokens\":90000},\"costUsd\":1.5,\"unpricedRecords\":0}," +
        "{\"provider\":\"codex\",\"totals\":{\"uncachedInputTokens\":\"bad\"}}," +
        "{\"provider\":\"codex\",\"totals\":{\"uncachedInputTokens\":0,\"outputTokens\":0},\"costUsd\":0.3413}]}";

    [Fact]
    public void TodaySumsDecodableBucketsWithoutDoubleCountingReasoning()
    {
        var value = T3UsageCalculator.ParseUsageSummary(Parse(Summary))!;
        Assert.False(value.Partial);
        Assert.False(value.CostPartial);
        Assert.Equal(842000, value.Tokens!.Total);
        Assert.Equal(742000, value.Tokens.Input);
        Assert.Equal(100000, value.Tokens.Output);
        Assert.Equal(1.8413m, value.CostUsd);
    }

    [Fact]
    public void TodayPartialRules()
    {
        Assert.Null(T3UsageCalculator.ParseUsageSummary(Parse(Summary.Replace("\"contractVersion\":6", "\"contractVersion\":7"))));
        var sourcePartial = T3UsageCalculator.ParseUsageSummary(Parse(Summary.Replace("\"status\":\"ok\"", "\"status\":\"refreshing\"")))!;
        Assert.True(sourcePartial.Partial);
        var unpriced = T3UsageCalculator.ParseUsageSummary(Parse(Summary.Replace("\"unpricedRecords\":0", "\"unpricedRecords\":2")))!;
        Assert.False(unpriced.Partial);
        Assert.True(unpriced.CostPartial);
        var pricing = T3UsageCalculator.ParseUsageSummary(Parse(Summary.Replace("\"fresh\"", "\"unavailable\"")))!;
        Assert.False(pricing.Partial);
        Assert.True(pricing.CostPartial);

        // One failed environment makes the combined value partial.
        var ok = T3UsageCalculator.ParseUsageSummary(Parse(Summary));
        Assert.True(T3UsageCalculator.CombineToday(new[] { ok, null }).Partial);
        var both = T3UsageCalculator.CombineToday(new[] { ok, ok });
        Assert.False(both.Partial);
        Assert.Equal(1684000, both.Tokens!.Total);
    }

    [Fact]
    public void CostSuffixHidesWhenOnlyCostIsPartial()
    {
        var settings = new PresenceSettings { CardStyle = CardStyle.Usage, ShareEstimatedCost = true };
        var usage = new T3UsageSnapshot { Today = new T3UsageValue { Tokens = new T3TokenCounts { UncachedInputTokens = 1234 }, CostUsd = 1m, CostPartial = true } };
        Assert.Equal("1.2K tokens today", PresenceCardFormatter.UsageLine(settings, usage, default, DateTimeOffset.UtcNow).Text);
        usage.Today.CostPartial = false;
        Assert.Equal("1.2K tokens today · ~$1.00 API est.", PresenceCardFormatter.UsageLine(settings, usage, default, DateTimeOffset.UtcNow).Text);
        // Stale usage (beyond the 180-second TTL) is never shown.
        usage.Timestamp = DateTimeOffset.UtcNow.AddSeconds(-181);
        Assert.Null(PresenceCardFormatter.UsageLine(settings, usage, default, DateTimeOffset.UtcNow).Text);
        // Today's aggregate can't honor exclusions.
        usage.Timestamp = null;
        settings.ExcludedProjects.Add("e/p");
        Assert.Null(PresenceCardFormatter.UsageLine(settings, usage, default, DateTimeOffset.UtcNow).Text);
    }

    private const string Projection = "{\"activeProviderThreadId\":\"pt1\",\"messages\":[{\"text\":\"PRIVATE prompt\"}]," +
        "\"providerThreads\":[{\"id\":\"pt1\",\"contextUsage\":{\"usedTokens\":1000,\"maxTokens\":200000}}]," +
        "\"providerTurns\":[" +
        "{\"providerThreadId\":\"pt1\",\"startedAt\":\"2026-11-01T10:00:00Z\",\"tokenUsage\":{\"usedTokens\":124999,\"maxTokens\":200000,\"updatedAt\":\"2026-11-01T10:05:00Z\"}," +
        "\"turnTokenUsage\":{\"usageStatus\":\"complete\",\"inputTokens\":1000,\"cachedInputTokens\":600,\"cacheCreationTokens\":100,\"outputTokens\":50}}," +
        "{\"providerThreadId\":\"pt1\",\"startedAt\":\"2026-11-01T09:00:00Z\",\"tokenUsage\":{\"usedTokens\":5,\"maxTokens\":200000,\"updatedAt\":\"2026-11-01T09:05:00Z\"}," +
        "\"turnTokenUsage\":{\"usageStatus\":\"complete\",\"inputTokens\":10,\"outputTokens\":20}}]}";

    [Fact]
    public void PinnedTaskAndSessionTokensFromTurns()
    {
        var projection = T3UsageCalculator.ParseProjection(Parse(Projection));
        var pinned = T3UsageCalculator.SumTurns(new[] { projection }, null);
        Assert.False(pinned.Partial);
        Assert.Equal(1010, pinned.Tokens!.Input);
        Assert.Equal(310, pinned.Tokens.UncachedInputTokens); // 1000 − 600 cached − 100 cache creation, plus 10
        Assert.Equal(600, pinned.Tokens.CachedInputTokens);
        Assert.Equal(100, pinned.Tokens.CacheCreationTokens);
        Assert.Equal(70, pinned.Tokens.Output);

        var session = T3UsageCalculator.SumTurns(new[] { projection }, DateTimeOffset.Parse("2026-11-01T09:30:00Z"));
        Assert.Equal(1050, session.Tokens!.Total);

        var partial = T3UsageCalculator.ParseProjection(Parse(Projection.Replace("\"usageStatus\":\"complete\",\"inputTokens\":10,\"outputTokens\":20", "\"usageStatus\":\"partial\",\"inputTokens\":10,\"outputTokens\":20")));
        Assert.True(T3UsageCalculator.SumTurns(new[] { partial }, null).Partial);
        Assert.False(T3UsageCalculator.SumTurns(new[] { partial }, DateTimeOffset.Parse("2026-11-01T09:30:00Z")).Partial);
        Assert.True(T3UsageCalculator.SumTurns(new T3ProjectionUsage?[] { null }, null).Partial);
    }

    [Fact]
    public void ContextUsesNewestTurnThenFallsBack()
    {
        var projection = T3UsageCalculator.ParseProjection(Parse(Projection));
        Assert.Equal((124999L, 200000L), T3UsageCalculator.Context(projection));
        var fallback = T3UsageCalculator.ParseProjection(Parse(Projection.Replace("\"tokenUsage\"", "\"ignoredUsage\"")));
        Assert.Equal((1000L, 200000L), T3UsageCalculator.Context(fallback));
        var missing = T3UsageCalculator.ParseProjection(Parse(Projection.Replace("\"tokenUsage\"", "\"ignoredUsage\"").Replace("\"maxTokens\":200000}}]", "\"maxTokens\":0}}]")));
        Assert.Null(T3UsageCalculator.Context(missing));
    }

    // ---- Usage service end to end with fake transports ----

    private sealed class FakeSockets : IT3UsageSocketFactory
    {
        public Func<string, IEnumerable<string>> Reply { get; set; } = _ => Array.Empty<string>();
        public int StatusCode { get; set; }
        public List<(Uri Uri, string Token)> Connections { get; } = new();
        public List<ScriptedSocket> Sockets { get; } = new();

        public Task<IT3UsageSocket> ConnectAsync(Uri uri, string bearerToken, CancellationToken ct)
        {
            Connections.Add((uri, bearerToken));
            if (StatusCode != 0) throw new T3UsageHttpStatusException(StatusCode);
            var socket = new ScriptedSocket { Reply = Reply };
            Sockets.Add(socket);
            return Task.FromResult<IT3UsageSocket>(socket);
        }
    }

    private static async Task<(MockSettingsService, InMemoryCredentialStore, FakeSockets, Handler, T3UsageService)> UsageSetup()
    {
        var settings = new MockSettingsService();
        settings.Settings.T3Environments.Add(new T3Environment { Id = "env-a", Address = "https://remote.example:8443/mcp" });
        var store = new InMemoryCredentialStore();
        var handler = new Handler
        {
            Respond = (req, _) => req.RequestUri!.AbsolutePath switch
            {
                "/.well-known/t3/environment" => Json(HttpStatusCode.OK, "{\"orchestrationProtocolVersion\":2}"),
                "/oauth/token" => Json(HttpStatusCode.OK, "{\"access_token\":\"synthetic-usage\",\"token_type\":\"Bearer\",\"expires_in\":2592000,\"scope\":\"orchestration:read diagnostics:read\"}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            }
        };
        var sockets = new FakeSockets();
        var service = new T3UsageService(settings, store, new HttpClient(handler), sockets, null,
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi"));
        await service.InitializeAsync();
        return (settings, store, sockets, handler, service);
    }

    [Fact]
    public async Task PairStoresCredentialUnderUsageOriginKeyAndRemoveDeletesIt()
    {
        var (_, store, _, _, service) = await UsageSetup();
        Assert.False(service.HasPermission("env-a"));
        var result = await service.PairAsync("env-a", "https://remote.example:8443/pair#token=AbC123xyz");
        Assert.Equal(T3PairingOutcome.Success, result.Outcome);
        Assert.Null(result.Credential);
        Assert.True(service.HasPermission("env-a"));
        string? saved = await store.ReadCredentialAsync("deskchirp-usage:https://remote.example:8443", T3UsageService.CredentialUsername);
        Assert.Contains("synthetic-usage", saved);
        await service.RemoveTokenAccessAsync("env-a");
        Assert.False(service.HasPermission("env-a"));
        Assert.Null(await store.ReadCredentialAsync("deskchirp-usage:https://remote.example:8443", T3UsageService.CredentialUsername));
    }

    [Fact]
    public async Task TodayPollSendsIanaZoneAndClosesSocket()
    {
        var (settings, _, sockets, _, service) = await UsageSetup();
        await service.PairAsync("env-a", "AbC123xyz");
        sockets.Reply = text => text.Contains("server.getUsageSummary")
            ? new[] { "{\"_tag\":\"Exit\",\"requestId\":\"1\",\"exit\":{\"_tag\":\"Success\",\"value\":" + Summary + "}}" }
            : Array.Empty<string>();
        settings.Settings.CardStyle = CardStyle.Usage;
        var snapshot = await service.PollOnceAsync(new T3UsagePollRequest { Settings = settings.Settings });
        Assert.Equal(842000, snapshot!.Today!.Tokens!.Total);
        var (uri, token) = Assert.Single(sockets.Connections);
        Assert.Equal("wss://remote.example:8443/ws?orchestrationProtocol=2&clientSurface=deskchirp&clientAppVersion=" + Uri.EscapeDataString(T3UsageService.ClientAppVersion), uri.AbsoluteUri);
        Assert.Equal("synthetic-usage", token);
        string request = Assert.Single(sockets.Sockets.Single().Sent);
        Assert.Contains("\"timeZone\":\"Asia/Karachi\"", request);
        Assert.Contains("\"awaitRefresh\":true", request);
        Assert.True(sockets.Sockets.Single().Disposed);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public async Task FailureFrameMakesTodayPartialAndExclusionsSkipTheRequest()
    {
        var (settings, _, sockets, _, service) = await UsageSetup();
        await service.PairAsync("env-a", "AbC123xyz");
        sockets.Reply = _ => new[] { "{\"_tag\":\"Exit\",\"requestId\":\"1\",\"exit\":{\"_tag\":\"Failure\",\"cause\":[{\"_tag\":\"Fail\",\"error\":{\"_tag\":\"EnvironmentAuthorizationError\"}}]}}" };
        var snapshot = await service.PollOnceAsync(new T3UsagePollRequest { Settings = settings.Settings });
        Assert.True(snapshot!.Today!.Partial);
        Assert.Equal(T3UsageText.PairAgain, service.StatusFor("env-a"));

        settings.Settings.ExcludedProjects.Add("env-a/p");
        sockets.Connections.Clear();
        Assert.Null(await service.PollOnceAsync(new T3UsagePollRequest { Settings = settings.Settings }));
        Assert.Empty(sockets.Connections);
    }

    [Fact]
    public async Task Unauthorized401DeletesCredentialAndAsksToPairAgain()
    {
        var (settings, store, sockets, _, service) = await UsageSetup();
        await service.PairAsync("env-a", "AbC123xyz");
        sockets.StatusCode = 401;
        var snapshot = await service.PollOnceAsync(new T3UsagePollRequest { Settings = settings.Settings });
        Assert.True(snapshot!.Today!.Partial);
        Assert.False(service.HasPermission("env-a"));
        Assert.Equal(T3UsageText.PairAgain, service.StatusFor("env-a"));
        Assert.Null(await store.ReadCredentialAsync("deskchirp-usage:https://remote.example:8443", T3UsageService.CredentialUsername));

        await service.PairAsync("env-a", "AbC123xyz");
        sockets.StatusCode = 426;
        await service.PollOnceAsync(new T3UsagePollRequest { Settings = settings.Settings });
        Assert.Equal(T3UsageText.Unsupported, service.StatusFor("env-a"));
        Assert.True(service.HasPermission("env-a"));
    }

    [Fact]
    public async Task SessionScopeAsksOnlyForUnfinishedThreadsAfterExclusions()
    {
        var (settings, _, sockets, _, service) = await UsageSetup();
        await service.PairAsync("env-a", "AbC123xyz");
        var now = DateTimeOffset.UtcNow;
        var snapshot = new T3Snapshot
        {
            Timestamp = now,
            Environments = new[] { new T3EnvironmentSnapshot {
                EnvironmentId = "env-a", Timestamp = now,
                Drivers = new Dictionary<string, string> { ["c"] = "claudeAgent" },
                Threads = new[] {
                    new T3ThreadListItem { ThreadId = "run", ProviderInstanceId = "c", Status = "running", ProjectId = "p" },
                    new T3ThreadListItem { ThreadId = "helper", ProviderInstanceId = "c", Status = "completed", ProjectId = "p", ParentThreadId = "run", RelationshipToParent = "subagent" },
                    new T3ThreadListItem { ThreadId = "done", ProviderInstanceId = "c", Status = "completed", ProjectId = "p" },
                    new T3ThreadListItem { ThreadId = "secret", ProviderInstanceId = "c", Status = "running", ProjectId = "x" } } } }
        };
        settings.Settings.UsageScope = UsageScope.Session;
        settings.Settings.ExcludedProjects.Add("env-a/x");
        sockets.Reply = text => text.Contains("\"id\":\"2\"")
            ? new[] {
                "{\"_tag\":\"Exit\",\"requestId\":\"1\",\"exit\":{\"_tag\":\"Success\",\"value\":" + Projection + "}}",
                "{\"_tag\":\"Exit\",\"requestId\":\"2\",\"exit\":{\"_tag\":\"Success\",\"value\":{\"providerTurns\":[]}}}" }
            : Array.Empty<string>();
        var start = DateTimeOffset.Parse("2026-11-01T09:30:00Z");
        var usage = await service.PollOnceAsync(new T3UsagePollRequest { Settings = settings.Settings, Snapshot = snapshot, WorkSessionStart = start });
        var sent = sockets.Sockets.Single().Sent;
        Assert.Equal(2, sent.Count);
        Assert.Contains(sent, s => s.Contains("\"threadId\":\"run\""));
        Assert.Contains(sent, s => s.Contains("\"threadId\":\"helper\""));
        Assert.DoesNotContain(sent, s => s.Contains("secret") || s.Contains("\"done\""));
        Assert.False(usage!.Session!.Partial);
        Assert.Equal(1050, usage.Session.Tokens!.Total);
        Assert.Equal(start, usage.SessionStart);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(usage));
    }

    [Fact]
    public void WindowsZonesConvertToIana()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
        Assert.True(T3UsageService.TryIanaZone(zone, out var iana));
        Assert.Equal("Asia/Karachi", iana);
        Assert.True(TimeZoneInfo.TryConvertWindowsIdToIanaId("Pakistan Standard Time", out var converted));
        Assert.Equal("Asia/Karachi", converted);
    }
}
