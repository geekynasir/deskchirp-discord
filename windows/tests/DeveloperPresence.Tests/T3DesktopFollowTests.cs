using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeveloperPresence.Core.Credentials;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.T3;
using DeveloperPresence.Core.T3.Desktop;
using DeveloperPresence.Core.T3.Usage;
using Xunit;

namespace DeveloperPresence.Tests;

/// <summary>Following the connections saved in the T3 Code desktop app. All tokens here are synthetic.</summary>
public class T3DesktopFollowTests : IDisposable
{
    private const string Vps = "https://vps.example.ts.net:3773/mcp";
    private const string VpsLan = "https://10.0.0.5:3773/mcp";
    private const string Laptop = "https://laptop.example/mcp";
    private readonly string _dir;

    public T3DesktopFollowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "deskchirp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- Catalog: decrypting and parsing ----

    private static JsonObject Catalog(params (string EnvId, string Label, JsonObject Target, JsonObject? Profile, string? CredentialId, string? Token)[] routes)
    {
        var targets = new JsonArray();
        var profiles = new JsonArray();
        var credentials = new JsonArray();
        foreach (var r in routes)
        {
            targets.Add(r.Target);
            if (r.Profile != null) profiles.Add(r.Profile);
            if (r.CredentialId != null)
                credentials.Add(new JsonObject
                {
                    ["connectionId"] = r.CredentialId,
                    ["credential"] = new JsonObject { ["_tag"] = "BearerConnectionCredential", ["token"] = r.Token }
                });
        }
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["targets"] = targets,
            ["profiles"] = profiles,
            ["credentials"] = credentials,
            ["remoteDpopTokens"] = new JsonArray(),
            ["disabledEnvironmentIds"] = new JsonArray()
        };
    }

    private static (string, string, JsonObject, JsonObject?, string?, string?) Bearer(
        string envId, string label, string connectionId, string httpBaseUrl, string? token, string? authorization = null, string? credentialId = null)
    {
        var target = new JsonObject { ["_tag"] = "BearerConnectionTarget", ["environmentId"] = envId, ["label"] = label, ["connectionId"] = connectionId };
        var profile = new JsonObject
        {
            ["_tag"] = "BearerConnectionProfile", ["connectionId"] = connectionId, ["environmentId"] = envId, ["label"] = label,
            ["httpBaseUrl"] = httpBaseUrl, ["wsBaseUrl"] = httpBaseUrl.Replace("http", "ws")
        };
        if (authorization != null) profile["authorization"] = authorization;
        return (envId, label, target, profile, token == null ? null : credentialId ?? connectionId, token);
    }

    private static (string, string, JsonObject, JsonObject?, string?, string?) Relay(string envId, string label) =>
        (envId, label, new JsonObject { ["_tag"] = "RelayConnectionTarget", ["environmentId"] = envId, ["label"] = label }, null, null, null);

    private static (string, string, JsonObject, JsonObject?, string?, string?) Ssh(string envId, string label) =>
        (envId, label, new JsonObject { ["_tag"] = "SshConnectionTarget", ["environmentId"] = envId, ["label"] = label, ["connectionId"] = "ssh-" + envId }, null, null, null);

    /// <summary>Encrypts the catalog the way Electron safeStorage does on Windows, with a fake DPAPI.</summary>
    private T3DesktopCatalogReader WriteCatalog(JsonObject catalog, byte[]? key = null, string[]? extraLocalStates = null)
    {
        key ??= RandomNumberGenerator.GetBytes(32);
        string localState = Path.Combine(_dir, "Local State");
        File.WriteAllText(localState, JsonSerializer.Serialize(new
        {
            os_crypt = new { encrypted_key = Convert.ToBase64String("DPAPI"u8.ToArray().Concat(key).ToArray()) }
        }));

        byte[] plain = Encoding.UTF8.GetBytes(catalog.ToJsonString());
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        byte[] blob = "v10"u8.ToArray().Concat(nonce).Concat(cipher).Concat(tag).ToArray();

        string catalogPath = Path.Combine(_dir, "connection-catalog.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new { version = 1, encryptedCatalog = Convert.ToBase64String(blob) }));
        var states = (extraLocalStates ?? Array.Empty<string>()).Append(localState).ToList();
        return new T3DesktopCatalogReader(catalogPath, states, data => data.ToArray());
    }

    [Fact]
    public void ReadsBearerRoutesInT3sOrderAndSkipsOthers()
    {
        var catalog = Catalog(
            Bearer("env-vps", "Mumbai VPS", "c-vps", "https://vps.example.ts.net:3773/", "synthetic-vps"),
            // A learned route shares the credential of the route it was learned from.
            Bearer("env-vps", "Mumbai VPS", "learned:env-vps:https://10.0.0.5:3773@c-vps", "https://10.0.0.5:3773", null),
            Bearer("env-lan", "Plain LAN", "c-lan", "http://192.168.1.20:3773", "synthetic-lan"),
            Bearer("env-connect", "Through Connect", "c-connect", "https://connect.example", "synthetic-connect", authorization: "t3-connect"),
            Relay("env-relay", "Relay only"),
            Ssh("env-ssh", "SSH box"),
            Bearer("env-local", "Second local", "c-local", "http://127.0.0.1:3900", "synthetic-local"));
        var reader = WriteCatalog(catalog);
        // The learned route's credential lives under c-vps.
        var result = reader.Read();

        Assert.Equal(T3DesktopCatalogStatus.Read, result.Status);
        var vps = result.Environments.Single(e => e.EnvironmentId == "env-vps");
        Assert.Equal("Mumbai VPS", vps.Label);
        Assert.Equal(new[] { Vps, VpsLan }, vps.Routes.Select(r => r.McpAddress));
        Assert.All(vps.Routes, r => Assert.Equal("synthetic-vps", r.DesktopToken));
        Assert.Equal("http://127.0.0.1:3900/mcp", result.Environments.Single(e => e.EnvironmentId == "env-local").Routes.Single().McpAddress);
        Assert.Equal(T3DesktopCatalogReader.MsgInsecureOnly, result.Environments.Single(e => e.EnvironmentId == "env-lan").UnsupportedReason);
        Assert.Equal(T3DesktopCatalogReader.MsgRelayOnly, result.Environments.Single(e => e.EnvironmentId == "env-connect").UnsupportedReason);
        Assert.Equal(T3DesktopCatalogReader.MsgRelayOnly, result.Environments.Single(e => e.EnvironmentId == "env-relay").UnsupportedReason);
        Assert.Equal(T3DesktopCatalogReader.MsgSshOnly, result.Environments.Single(e => e.EnvironmentId == "env-ssh").UnsupportedReason);
        Assert.Equal(new[] { "env-vps", "env-lan", "env-connect", "env-relay", "env-ssh", "env-local" }, result.Environments.Select(e => e.EnvironmentId));
        // Nothing that might be logged carries a token.
        Assert.DoesNotContain("synthetic", string.Join(" ", result.Environments.SelectMany(e => e.Routes.Select(r => r.ToString()).Append(e.ToString()))));
    }

    [Fact]
    public void SkipsDisabledEnvironmentsAndRoutesWithoutSignIn()
    {
        var catalog = Catalog(
            Bearer("env-off", "Turned off", "c-off", "https://off.example", "synthetic-off"),
            Bearer("env-nocred", "No credential", "c-nocred", "https://nocred.example", null));
        catalog["disabledEnvironmentIds"] = new JsonArray("env-off");
        var result = WriteCatalog(catalog).Read();

        Assert.DoesNotContain(result.Environments, e => e.EnvironmentId == "env-off");
        var noCred = Assert.Single(result.Environments);
        Assert.Empty(noCred.Routes);
        Assert.Equal(T3DesktopCatalogReader.MsgNoSignIn, noCred.UnsupportedReason);
    }

    [Fact]
    public void TriesEachProfilesKeyUntilOneDecrypts()
    {
        string stale = Path.Combine(_dir, "stale Local State");
        File.WriteAllText(stale, JsonSerializer.Serialize(new
        {
            os_crypt = new { encrypted_key = Convert.ToBase64String("DPAPI"u8.ToArray().Concat(RandomNumberGenerator.GetBytes(32)).ToArray()) }
        }));
        var reader = WriteCatalog(Catalog(Bearer("env-vps", "VPS", "c", "https://vps.example.ts.net:3773", "synthetic")),
            extraLocalStates: new[] { Path.Combine(_dir, "missing"), stale });
        Assert.Equal(Vps, reader.Read().Environments.Single().Routes.Single().McpAddress);
    }

    [Fact]
    public void MissingOrUndecryptableCatalogsAreReportedNotGuessed()
    {
        Assert.Equal(T3DesktopCatalogStatus.NotFound,
            new T3DesktopCatalogReader(Path.Combine(_dir, "none.json"), Array.Empty<string>(), d => d).Read().Status);

        var reader = WriteCatalog(Catalog(Bearer("env-vps", "VPS", "c", "https://vps.example", "synthetic")));
        // DPAPI refuses (another Windows user, or not Windows at all).
        var refused = new T3DesktopCatalogReader(reader.CatalogPath, new[] { Path.Combine(_dir, "Local State") }, _ => null);
        Assert.Equal(T3DesktopCatalogStatus.Unreadable, refused.Read().Status);

        File.WriteAllText(reader.CatalogPath, "{ not json");
        Assert.Equal(T3DesktopCatalogStatus.Unreadable, reader.Read().Status);
    }

    [Theory]
    [InlineData("learned:env:https://10.0.0.5:3773@primary", "primary")]
    [InlineData("learned:env:https://10.0.0.5:3773", "learned:env:https://10.0.0.5:3773")]
    [InlineData("user@host", "user@host")]
    public void CredentialIdMatchesT3(string connectionId, string expected) =>
        Assert.Equal(expected, T3DesktopCatalogReader.CredentialConnectionId(connectionId));

    // ---- Minting pairing codes with T3 Code's sign-in ----

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

    [Fact]
    public async Task MintsCodeOnTheRoutesOwnOrigin()
    {
        var handler = new Handler { Respond = (_, _) => Json(HttpStatusCode.OK, """{"id":"x","credential":"ABCDEFGH2345","label":"Deskchirp","expiresAt":"2026-11-01T00:05:00Z"}""") };
        var client = new T3DesktopPairingClient(new HttpClient(handler));
        var result = await client.MintCodeAsync(new T3DesktopRoute(Vps, "synthetic-desktop"), new[] { "orchestration:read" });

        Assert.Equal(T3DesktopPairingOutcome.Success, result.Outcome);
        Assert.Equal("ABCDEFGH2345", result.Code);
        Assert.DoesNotContain("ABCDEFGH2345", result.ToString());
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://vps.example.ts.net:3773/api/auth/pairing-token", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer synthetic-desktop", request.Headers.Authorization!.ToString());
        Assert.False(request.Headers.Contains("Origin"));
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Deskchirp", doc.RootElement.GetProperty("label").GetString());
        Assert.Equal(new[] { "orchestration:read" }, doc.RootElement.GetProperty("scopes").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, T3DesktopPairingOutcome.TokenRejected)]
    [InlineData(HttpStatusCode.Forbidden, T3DesktopPairingOutcome.MissingPermission)]
    [InlineData(HttpStatusCode.Found, T3DesktopPairingOutcome.Failed)]
    [InlineData(HttpStatusCode.InternalServerError, T3DesktopPairingOutcome.Failed)]
    public async Task MintFailuresAreNamed(HttpStatusCode status, T3DesktopPairingOutcome outcome)
    {
        var handler = new Handler { Respond = (_, _) => new HttpResponseMessage(status) };
        var result = await new T3DesktopPairingClient(new HttpClient(handler)).MintCodeAsync(new T3DesktopRoute(Vps, "synthetic"), new[] { "orchestration:read" });
        Assert.Equal(outcome, result.Outcome);
        Assert.Null(result.Code);
    }

    [Fact]
    public async Task UnreachableRouteIsReportedAsUnreachable()
    {
        var handler = new Handler { Respond = (_, _) => throw new HttpRequestException("refused") };
        var result = await new T3DesktopPairingClient(new HttpClient(handler)).MintCodeAsync(new T3DesktopRoute(Vps, "synthetic"), new[] { "orchestration:read" });
        Assert.Equal(T3DesktopPairingOutcome.Unreachable, result.Outcome);
    }

    // ---- Signing in without a browser ----

    private static Handler T3Server(Func<JsonDocument, string>? decide = null, string scope = "orchestration:read")
    {
        const string origin = "https://vps.example.ts.net:3773";
        var handler = new Handler();
        handler.Respond = (request, body) => request.RequestUri!.AbsolutePath switch
        {
            "/.well-known/oauth-protected-resource/mcp" => Json(HttpStatusCode.OK,
                $$"""{"resource":"{{origin}}/mcp","authorization_servers":["{{origin}}"],"scopes_supported":["orchestration:read"]}"""),
            "/.well-known/oauth-authorization-server" => Json(HttpStatusCode.OK,
                $$"""{"issuer":"{{origin}}","authorization_endpoint":"{{origin}}/oauth/mcp/authorize","token_endpoint":"{{origin}}/oauth/mcp/token","registration_endpoint":"{{origin}}/oauth/mcp/register","code_challenge_methods_supported":["S256"]}"""),
            "/oauth/mcp/register" => Json(HttpStatusCode.Created, """{"client_id":"client-1"}"""),
            "/oauth/mcp/decision" => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                redirectTo = decide?.Invoke(JsonDocument.Parse(body)) ??
                    $"http://127.0.0.1/deskchirp?code=auth-code&state={Uri.EscapeDataString(JsonDocument.Parse(body).RootElement.GetProperty("authorization").GetProperty("state").GetString()!)}&iss={Uri.EscapeDataString(origin)}"
            })),
            "/oauth/mcp/token" => Json(HttpStatusCode.OK, $$"""{"access_token":"synthetic-mcp","token_type":"Bearer","expires_in":2592000,"scope":"{{scope}}"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
        return handler;
    }

    private sealed class NoBrowser : IBrowserLauncher
    {
        public bool OpenBrowser(Uri uri) => throw new InvalidOperationException("No browser in headless sign-in.");
    }

    [Fact]
    public async Task PairingCodeSignInApprovesItselfAndExchangesTheCode()
    {
        var handler = T3Server();
        var client = new T3OAuthClient(new HttpClient(handler), new NoBrowser());
        var token = await client.SignInWithPairingCodeAsync(new Uri(Vps), "ABCDEFGH2345");

        Assert.Equal("synthetic-mcp", token.AccessToken);
        Assert.Equal(Vps, token.McpUri);
        var decision = JsonDocument.Parse(handler.Requests.Single(r => r.Request.RequestUri!.AbsolutePath == "/oauth/mcp/decision").Body).RootElement;
        Assert.Equal("pairing-code", decision.GetProperty("decision").GetProperty("_tag").GetString());
        Assert.Equal("read-only", decision.GetProperty("decision").GetProperty("access").GetString());
        Assert.Equal("ABCDEFGH2345", decision.GetProperty("decision").GetProperty("code").GetString());
        var authorization = decision.GetProperty("authorization");
        Assert.Equal("client-1", authorization.GetProperty("client_id").GetString());
        Assert.Equal(T3OAuthClient.HeadlessRedirectUri, authorization.GetProperty("redirect_uri").GetString());
        Assert.Equal("S256", authorization.GetProperty("code_challenge_method").GetString());
        Assert.Equal(Vps, authorization.GetProperty("resource").GetString());

        var form = handler.Requests.Single(r => r.Request.RequestUri!.AbsolutePath == "/oauth/mcp/token").Body;
        Assert.Contains("grant_type=authorization_code", form);
        Assert.Contains("code=auth-code", form);
        Assert.Contains("code_verifier=", form);
    }

    [Theory]
    [InlineData("http://127.0.0.1/deskchirp?code=auth-code&state=WRONG&iss=https%3A%2F%2Fvps.example.ts.net%3A3773")]
    [InlineData("http://127.0.0.1/elsewhere?code=auth-code&state={state}&iss=https%3A%2F%2Fvps.example.ts.net%3A3773")]
    [InlineData("http://127.0.0.1/deskchirp?code=auth-code&state={state}&iss=https%3A%2F%2Fevil.example")]
    [InlineData("http://127.0.0.1/deskchirp?error=access_denied&state={state}&iss=https%3A%2F%2Fvps.example.ts.net%3A3773")]
    [InlineData("http://127.0.0.1/deskchirp?code=a&code=b&state={state}&iss=https%3A%2F%2Fvps.example.ts.net%3A3773")]
    public async Task PairingCodeSignInChecksTheRedirect(string redirect)
    {
        var handler = T3Server(doc => redirect.Replace("{state}",
            Uri.EscapeDataString(doc.RootElement.GetProperty("authorization").GetProperty("state").GetString()!)));
        var client = new T3OAuthClient(new HttpClient(handler), new NoBrowser());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SignInWithPairingCodeAsync(new Uri(Vps), "ABCDEFGH2345"));
        Assert.DoesNotContain(handler.Requests, r => r.Request.RequestUri!.AbsolutePath == "/oauth/mcp/token");
    }

    [Fact]
    public async Task PairingCodeSignInRejectsBroaderTokens()
    {
        var client = new T3OAuthClient(new HttpClient(T3Server(scope: "orchestration:read orchestration:write")), new NoBrowser());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SignInWithPairingCodeAsync(new Uri(Vps), "ABCDEFGH2345"));
    }

    // ---- Following ----

    private sealed class FakeCatalog : IT3DesktopCatalogSource
    {
        public T3DesktopCatalogResult Result { get; set; } = T3DesktopCatalogResult.NotFound;
        public DateTime? WriteTime { get; set; } = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        public int Reads;
        public T3DesktopCatalogResult Read() { Reads++; return Result; }
        public DateTime? CatalogWriteTimeUtc() => WriteTime;

        public void Set(params T3DesktopEnvironment[] environments)
        {
            Result = new T3DesktopCatalogResult(T3DesktopCatalogStatus.Read, environments);
            WriteTime = WriteTime?.AddSeconds(1);
        }
    }

    private sealed class FakePairing : IT3DesktopPairingClient
    {
        public Dictionary<string, T3DesktopPairingOutcome> Outcomes { get; } = new();
        public List<(string Address, string Token, string Scopes)> Calls { get; } = new();

        public Task<T3DesktopPairingResult> MintCodeAsync(T3DesktopRoute route, IReadOnlyList<string> scopes, CancellationToken ct = default)
        {
            Calls.Add((route.McpAddress, route.DesktopToken, string.Join(" ", scopes)));
            var outcome = Outcomes.GetValueOrDefault(route.McpAddress, T3DesktopPairingOutcome.Success);
            return Task.FromResult(outcome == T3DesktopPairingOutcome.Success
                ? new T3DesktopPairingResult(outcome, scopes.Count > 1 ? "USAGECODE234" : "READCODE2345")
                : new T3DesktopPairingResult(outcome));
        }
    }

    private sealed class HeadlessOAuth : IT3OAuthClient
    {
        public List<(string Address, string Code)> Calls { get; } = new();
        public HashSet<string> Failing { get; } = new();
        public Task<T3StoredToken> SignInAsync(Uri normalizedMcpUri, CancellationToken ct = default) =>
            throw new InvalidOperationException("Following never opens the browser.");
        public Task<T3StoredToken> SignInWithPairingCodeAsync(Uri normalizedMcpUri, string pairingCode, CancellationToken ct = default)
        {
            Calls.Add((normalizedMcpUri.AbsoluteUri, pairingCode));
            if (Failing.Contains(normalizedMcpUri.AbsoluteUri)) throw new InvalidOperationException("Sign-in didn’t finish. Try connecting again.");
            return Task.FromResult(new T3StoredToken
            {
                AccessToken = "synthetic-mcp-" + normalizedMcpUri.Host, McpUri = normalizedMcpUri.AbsoluteUri,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            });
        }
    }

    private sealed class NullMcp : IT3McpClient
    {
        public Task<T3CapabilitiesResult> GetCapabilitiesAsync(Uri uri, string token, CancellationToken ct = default) => Task.FromResult(new T3CapabilitiesResult());
        public Task<T3ThreadListResult> ListThreadsAsync(Uri uri, string token, int? cursor = null, CancellationToken ct = default) => Task.FromResult(new T3ThreadListResult());
        public Task<IReadOnlyList<T3ProjectListItem>> ListProjectsAsync(Uri uri, string token, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<T3ProjectListItem>>(Array.Empty<T3ProjectListItem>());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeUsage : IT3UsageService
    {
        public HashSet<string> Allowed { get; } = new();
        public List<(string EnvironmentId, string Code)> Paired { get; } = new();
        public List<string> Removed { get; } = new();
        public T3UsageSnapshot? CurrentSnapshot => null;
        public event Action? Changed { add { } remove { } }
        public bool HasPermission(string environmentId) => Allowed.Contains(environmentId);
        public bool HasAnyPermission => Allowed.Count > 0;
        public string? StatusFor(string environmentId) => null;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<T3PairingResult> PairAsync(string environmentId, string rawInput, CancellationToken ct = default)
        {
            Paired.Add((environmentId, rawInput));
            Allowed.Add(environmentId);
            return Task.FromResult(new T3PairingResult(T3PairingOutcome.Success, T3UsageText.Allowed));
        }
        public Task RemoveTokenAccessAsync(string environmentId, CancellationToken ct = default)
        {
            Removed.Add(environmentId);
            Allowed.Remove(environmentId);
            return Task.CompletedTask;
        }
        public Task<T3UsageSnapshot?> PollOnceAsync(T3UsagePollRequest request, CancellationToken ct = default) => Task.FromResult<T3UsageSnapshot?>(null);
        public void Invalidate() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public MockSettingsService Settings { get; } = new();
        public InMemoryCredentialStore Store { get; } = new();
        public FakeCatalog Catalog { get; } = new();
        public FakePairing Pairing { get; } = new();
        public HeadlessOAuth OAuth { get; } = new();
        public FakeUsage Usage { get; } = new();
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        public T3ActivityService Activity { get; }
        public T3DesktopFollower Follower { get; }

        public Rig(Action<PresenceSettings>? setup = null)
        {
            Settings.Settings.ShowT3Activity = true;
            setup?.Invoke(Settings.Settings);
            Activity = new T3ActivityService(Settings, Store, OAuth, new NullMcp());
            Follower = new T3DesktopFollower(Settings, Activity, Usage, Catalog, Pairing, () => Now);
        }

        public T3EnvironmentStatus StatusOf(string address) => Activity.Environments.Single(e => e.Address == address);

        public async ValueTask DisposeAsync()
        {
            await Follower.DisposeAsync();
            await Activity.DisposeAsync();
        }
    }

    private static T3DesktopEnvironment Env(string id, string label, params string[] addresses) => new()
    {
        EnvironmentId = id,
        Label = label,
        Routes = addresses.Select(a => new T3DesktopRoute(a, "synthetic-desktop-" + id)).ToList()
    };

    [Fact]
    public async Task AddsAndConnectsEveryT3CodeConnectionWithoutABrowser()
    {
        await using var rig = new Rig();
        await rig.Activity.InitializeAsync();
        rig.Catalog.Set(Env("env-vps", "Mumbai VPS", Vps, VpsLan), Env("env-laptop", "Laptop", Laptop));

        await rig.Follower.SyncOnceAsync();

        var envs = rig.Settings.Settings.T3Environments;
        Assert.Equal(new[] { Vps, Laptop }, envs.Select(e => e.Address));
        Assert.Equal(new[] { "Mumbai VPS", "Laptop" }, envs.Select(e => e.Label));
        Assert.Equal(new[] { "env-vps", "env-laptop" }, envs.Select(e => e.T3DesktopEnvironmentId));
        Assert.All(rig.Activity.Environments, e => Assert.Equal(T3ConnectionStatus.Connected, e.Status));
        Assert.Equal(new[] { (Vps, "READCODE2345"), (Laptop, "READCODE2345") }, rig.OAuth.Calls);
        // Each desktop token went only to its own environment, asking for read-only access.
        Assert.Equal(new[] { (Vps, "synthetic-desktop-env-vps", "orchestration:read"), (Laptop, "synthetic-desktop-env-laptop", "orchestration:read") }, rig.Pairing.Calls);
        // The new sign-ins are saved like any other.
        Assert.NotNull(await rig.Store.ReadCredentialAsync(T3ActivityService.CredentialTargetFor(Vps), T3ActivityService.CredentialUsername));
        // Card style isn't Usage and nothing has usage yet, so no diagnostics permission was asked for.
        Assert.Empty(rig.Usage.Paired);
    }

    [Fact]
    public async Task DoesNothingUntilT3ActivityIsOnOrWhenFollowingIsOff()
    {
        await using var off = new Rig(s => s.ShowT3Activity = false);
        off.Catalog.Set(Env("env-vps", "VPS", Vps));
        await off.Follower.SyncOnceAsync();
        Assert.Empty(off.Settings.Settings.T3Environments);
        Assert.Equal(0, off.Catalog.Reads);

        await using var notFollowing = new Rig(s => s.FollowT3DesktopConnections = false);
        notFollowing.Catalog.Set(Env("env-vps", "VPS", Vps));
        await notFollowing.Follower.SyncOnceAsync();
        Assert.Empty(notFollowing.Settings.Settings.T3Environments);
        Assert.Equal(0, notFollowing.Catalog.Reads);
    }

    [Fact]
    public async Task AdoptsAnEntryAddedByHandAndKeepsItsNameAndSignIn()
    {
        await using var rig = new Rig(s => s.T3Environments.Add(new T3Environment { Id = "mine", Address = Vps, Label = "My name" }));
        await rig.Store.SaveCredentialAsync(T3ActivityService.CredentialTargetFor(Vps), T3ActivityService.CredentialUsername,
            JsonSerializer.Serialize(new T3StoredToken { AccessToken = "synthetic-existing", McpUri = Vps, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) }));
        await rig.Activity.InitializeAsync();
        rig.Catalog.Set(Env("env-vps", "Mumbai VPS", Vps));

        await rig.Follower.SyncOnceAsync();

        var env = Assert.Single(rig.Settings.Settings.T3Environments);
        Assert.Equal(("mine", "My name", "env-vps"), (env.Id, env.Label, env.T3DesktopEnvironmentId));
        Assert.Empty(rig.Pairing.Calls); // Already connected.
    }

    [Fact]
    public async Task RemovesFollowedEntriesT3CodeNoLongerHasButNotOnesAddedByHand()
    {
        await using var rig = new Rig(s => s.T3Environments.Add(new T3Environment { Id = "local", Address = "http://127.0.0.1:3773/mcp", Label = "This PC" }));
        await rig.Activity.InitializeAsync();
        rig.Catalog.Set(Env("env-vps", "VPS", Vps), Env("env-laptop", "Laptop", Laptop));
        await rig.Follower.SyncOnceAsync();
        string laptopId = rig.Settings.Settings.T3Environments.Single(e => e.Address == Laptop).Id;

        rig.Catalog.Set(Env("env-vps", "VPS", Vps));
        await rig.Follower.SyncOnceAsync();

        Assert.Equal(new[] { "http://127.0.0.1:3773/mcp", Vps }, rig.Settings.Settings.T3Environments.Select(e => e.Address));
        Assert.Null(await rig.Store.ReadCredentialAsync(T3ActivityService.CredentialTargetFor(Laptop), T3ActivityService.CredentialUsername));
        Assert.Contains(laptopId, rig.Usage.Removed);
    }

    [Fact]
    public async Task UnreadableCatalogChangesNothing()
    {
        await using var rig = new Rig();
        await rig.Activity.InitializeAsync();
        rig.Catalog.Set(Env("env-vps", "VPS", Vps));
        await rig.Follower.SyncOnceAsync();

        rig.Catalog.Result = T3DesktopCatalogResult.Unreadable;
        rig.Catalog.WriteTime = rig.Catalog.WriteTime?.AddSeconds(1);
        await rig.Follower.SyncOnceAsync();

        Assert.Single(rig.Settings.Settings.T3Environments);
        Assert.Equal(T3DesktopFollower.MsgUnreadable, rig.Follower.Note);
    }

    [Fact]
    public async Task EnvironmentsRemovedHereStayRemoved()
    {
        await using var rig = new Rig();
        await rig.Activity.InitializeAsync();
        rig.Catalog.Set(Env("env-vps", "VPS", Vps));
        await rig.Follower.SyncOnceAsync();
        string id = rig.Settings.Settings.T3Environments.Single().Id;

        await rig.Settings.UpdateSettingsAsync(s => T3DesktopFollower.MarkRemovedByUser(s, id));
        await rig.Activity.RemoveEnvironmentAsync(id);
        rig.Catalog.Set(Env("env-vps", "VPS", Vps));
        await rig.Follower.SyncOnceAsync();

        Assert.Empty(rig.Settings.Settings.T3Environments);
        Assert.Contains("env-vps", rig.Settings.Settings.IgnoredT3DesktopEnvironments);
    }

    [Fact]
    public async Task FallsBackToT3CodesNextRouteAndMovesTheEntry()
    {
        await using var rig = new Rig();
        await rig.Activity.InitializeAsync();
        rig.Pairing.Outcomes[Vps] = T3DesktopPairingOutcome.Unreachable;
        rig.Catalog.Set(Env("env-vps", "VPS", Vps, VpsLan));

        await rig.Follower.SyncOnceAsync();

        var env = Assert.Single(rig.Settings.Settings.T3Environments);
        Assert.Equal(VpsLan, env.Address);
        Assert.Equal(T3ConnectionStatus.Connected, rig.StatusOf(VpsLan).Status);
        Assert.Equal(new[] { (VpsLan, "READCODE2345") }, rig.OAuth.Calls);
    }

    [Fact]
    public async Task FailuresWaitBeforeTryingAgainUnlessT3CodeChanges()
    {
        await using var rig = new Rig();
        await rig.Activity.InitializeAsync();
        rig.Pairing.Outcomes[Vps] = T3DesktopPairingOutcome.TokenRejected;
        rig.Catalog.Set(Env("env-vps", "VPS", Vps));

        await rig.Follower.SyncOnceAsync();
        string id = rig.Settings.Settings.T3Environments.Single().Id;
        Assert.Equal(T3DesktopFollower.MsgTokenRejected, rig.Follower.ProblemFor(id));
        Assert.Single(rig.Pairing.Calls);

        // Nothing changed and the wait isn't over: the catalog isn't even read again.
        int reads = rig.Catalog.Reads;
        rig.Now += TimeSpan.FromMinutes(1);
        await rig.Follower.SyncOnceAsync();
        Assert.Single(rig.Pairing.Calls);
        Assert.Equal(reads, rig.Catalog.Reads);

        // The user signs in again in T3 Code, which rewrites its catalog: retry right away.
        rig.Pairing.Outcomes.Clear();
        rig.Catalog.Set(Env("env-vps", "VPS", Vps));
        await rig.Follower.SyncOnceAsync();
        Assert.Equal(T3ConnectionStatus.Connected, rig.StatusOf(Vps).Status);
        Assert.Null(rig.Follower.ProblemFor(id));
    }

    [Fact]
    public async Task HeadlessSignInFailureIsRetriedAfterTheWait()
    {
        await using var rig = new Rig();
        await rig.Activity.InitializeAsync();
        rig.OAuth.Failing.Add(Vps);
        rig.Catalog.Set(Env("env-vps", "VPS", Vps));

        await rig.Follower.SyncOnceAsync();
        Assert.Equal(T3ConnectionStatus.CouldNotReach, rig.StatusOf(Vps).Status);

        rig.OAuth.Failing.Clear();
        rig.Now += T3DesktopFollower.FailureBackoff + TimeSpan.FromSeconds(1);
        await rig.Follower.SyncOnceAsync();
        Assert.Equal(T3ConnectionStatus.Connected, rig.StatusOf(Vps).Status);
    }

    [Fact]
    public async Task AllowsTokenUsageWhenTheCardShowsUsageUnlessTheUserRemovedIt()
    {
        await using var rig = new Rig(s => s.CardStyle = CardStyle.Usage);
        await rig.Activity.InitializeAsync();
        rig.Catalog.Set(Env("env-vps", "VPS", Vps), Env("env-laptop", "Laptop", Laptop));
        await rig.Follower.SyncOnceAsync();

        var ids = rig.Settings.Settings.T3Environments.Select(e => e.Id).ToList();
        Assert.Equal(ids.Select(id => (id, "USAGECODE234")), rig.Usage.Paired);
        Assert.Contains(rig.Pairing.Calls, c => c.Address == Vps && c.Scopes == "orchestration:read diagnostics:read");

        // The user removes token access for the laptop; following leaves it removed.
        await rig.Settings.UpdateSettingsAsync(s => T3DesktopFollower.MarkUsageDeclined(s, ids[1]));
        await rig.Usage.RemoveTokenAccessAsync(ids[1]);
        rig.Catalog.Set(Env("env-vps", "VPS", Vps), Env("env-laptop", "Laptop", Laptop));
        await rig.Follower.SyncOnceAsync();
        Assert.Equal(2, rig.Usage.Paired.Count);
        Assert.False(rig.Usage.HasPermission(ids[1]));
    }

    [Fact]
    public async Task NotesConnectionsDeskchirpCantFollow()
    {
        await using var rig = new Rig();
        await rig.Activity.InitializeAsync();
        rig.Catalog.Set(Env("env-vps", "VPS", Vps), new T3DesktopEnvironment
        {
            EnvironmentId = "env-relay", Label = "Phone hotspot box", Routes = Array.Empty<T3DesktopRoute>(),
            UnsupportedReason = T3DesktopCatalogReader.MsgRelayOnly
        });
        await rig.Follower.SyncOnceAsync();

        Assert.Single(rig.Settings.Settings.T3Environments);
        Assert.Equal($"T3 Code also has “Phone hotspot box”, which {T3DesktopCatalogReader.MsgRelayOnly}.", rig.Follower.Note);
    }

    [Fact]
    public void SettingsWithoutTheNewFieldsStillLoad()
    {
        var settings = JsonSerializer.Deserialize<PresenceSettings>("""{"T3Environments":[{"Id":"a","Address":"https://a.example/mcp","Label":"A"}]}""")!;
        Assert.True(settings.FollowT3DesktopConnections);
        Assert.False(settings.T3Environments[0].IsFromT3Desktop);
        settings.IgnoredT3DesktopEnvironments = null!;
        settings.T3DesktopUsageDeclined = null!;
        settings.NormalizeAndMigrate();
        Assert.Empty(settings.IgnoredT3DesktopEnvironments);
        Assert.Empty(settings.T3DesktopUsageDeclined);
        Assert.DoesNotContain("IsFromT3Desktop", JsonSerializer.Serialize(settings));
    }
}
