using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DeveloperPresence.Core.Credentials;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;
using DeveloperPresence.Core.Services;

namespace DeveloperPresence.Core.T3.Usage;

/// <summary>What a usage poll should compute, captured by the coordinator at poll time.</summary>
public sealed class T3UsagePollRequest
{
    public required PresenceSettings Settings { get; init; }
    public T3Snapshot? Snapshot { get; init; }
    public DateTimeOffset? WorkSessionStart { get; init; }
}

public interface IT3UsageService : IAsyncDisposable
{
    T3UsageSnapshot? CurrentSnapshot { get; }
    event Action? Changed;
    bool HasPermission(string environmentId);
    bool HasAnyPermission { get; }
    /// <summary>Plain status for one environment's token usage, or null when nothing to say.</summary>
    string? StatusFor(string environmentId);
    Task InitializeAsync(CancellationToken ct = default);
    Task<T3PairingResult> PairAsync(string environmentId, string rawInput, CancellationToken ct = default);
    /// <summary>Remove token access: deletes the stored usage credential.</summary>
    Task RemoveTokenAccessAsync(string environmentId, CancellationToken ct = default);
    Task<T3UsageSnapshot?> PollOnceAsync(T3UsagePollRequest request, CancellationToken ct = default);
    void Invalidate();
}

/// <summary>
/// Reads token totals, context percentage and estimated cost through T3's app interface
/// (spec/t3-usage-transport.md). One socket per environment per poll, closed after the answers.
/// Projection responses carry conversation text; only usage numbers are decoded and kept.
/// </summary>
public sealed class T3UsageService : IT3UsageService
{
    public const string CredentialPrefix = "deskchirp-usage:";
    public const string CredentialUsername = "UsageAccessToken";
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(20);
    public const int MaxMetadataBytes = 16 * 1024;

    private readonly ISettingsService _settings;
    private readonly ICredentialStore _store;
    private readonly T3UsagePairingClient _pairingClient;
    private readonly HttpClient _http;
    private readonly IT3UsageSocketFactory _sockets;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeZoneInfo _timeZone;
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly object _lock = new();
    private readonly Dictionary<string, T3UsageCredential> _credentials = new(StringComparer.Ordinal); // keyed by origin
    private readonly Dictionary<string, string> _status = new(StringComparer.Ordinal); // keyed by origin
    private T3UsageSnapshot? _current;
    private long _generation;
    // Per-origin credential generation. Removal bumps it and cancels any pairing in flight, so a
    // late exchange can never recreate a removed credential. Save/delete are serialized.
    private readonly Dictionary<string, long> _credentialGeneration = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _pairing = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _credentialLock = new(1, 1);

    private long CredentialGeneration(string origin)
    {
        lock (_lock) { return _credentialGeneration.TryGetValue(origin, out long g) ? g : 0; }
    }

    public static string CredentialTargetFor(string origin) => CredentialPrefix + origin;

    public static string ClientAppVersion { get; } =
        typeof(T3UsageService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.0.0";

    public T3UsageService(ISettingsService settings, ICredentialStore store, HttpClient? http = null,
        IT3UsageSocketFactory? sockets = null, Func<DateTimeOffset>? clock = null, TimeZoneInfo? timeZone = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _pairingClient = new T3UsagePairingClient(_http, _clock);
        _sockets = sockets ?? new ClientWebSocketFactory();
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public T3UsageSnapshot? CurrentSnapshot => _current;
    public event Action? Changed;

    public bool HasAnyPermission => _settings.Settings.T3Environments.Any(e => HasPermission(e.Id));

    private string? OriginFor(string environmentId)
    {
        var env = _settings.Settings.T3Environments.FirstOrDefault(e => e.Id == environmentId);
        return env != null && Uri.TryCreate(env.Address, UriKind.Absolute, out var uri) ? T3UsagePairingInput.OriginOf(uri) : null;
    }

    public bool HasPermission(string environmentId)
    {
        string? origin = OriginFor(environmentId);
        if (origin != null) ExpireIfNeeded(origin);
        lock (_lock) { return origin != null && _credentials.TryGetValue(origin, out var c) && !c.IsExpired(_clock()); }
    }

    public string? StatusFor(string environmentId)
    {
        string? origin = OriginFor(environmentId);
        if (origin == null) return null;
        ExpireIfNeeded(origin);
        lock (_lock)
        {
            if (_status.TryGetValue(origin, out var status)) return status;
            return _credentials.ContainsKey(origin) ? T3UsageText.Allowed : null;
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        foreach (var env in _settings.Settings.T3Environments.ToList())
        {
            string? origin = OriginFor(env.Id);
            if (origin == null) continue;
            try
            {
                string? secret = await _store.ReadCredentialAsync(CredentialTargetFor(origin), CredentialUsername, ct).ConfigureAwait(false);
                var credential = string.IsNullOrWhiteSpace(secret) ? null : JsonSerializer.Deserialize<T3UsageCredential>(secret);
                if (credential == null || string.IsNullOrWhiteSpace(credential.AccessToken)) continue;
                if (credential.IsExpired(_clock()))
                {
                    await ForgetAsync(origin, T3UsageText.PairAgain).ConfigureAwait(false);
                    continue;
                }
                lock (_lock) { _credentials[origin] = credential; }
            }
            catch { }
        }
        Changed?.Invoke();
    }

    public async Task<T3PairingResult> PairAsync(string environmentId, string rawInput, CancellationToken ct = default)
    {
        string? origin = OriginFor(environmentId);
        if (origin == null) return new(T3PairingOutcome.Failed, T3UsageText.Failed);
        if (!T3UsagePairingInput.TryParse(rawInput, new Uri(origin), out string code, out string error))
            return new(T3PairingOutcome.InvalidInput, error);

        long generation;
        var pairingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_lock)
        {
            generation = _credentialGeneration.TryGetValue(origin, out long g) ? g : 0;
            if (_pairing.Remove(origin, out var previous)) previous.Cancel();
            _pairing[origin] = pairingCts;
        }
        try
        {
            T3PairingResult result;
            try { result = await ExchangePairingAsync(origin, code, pairingCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(T3PairingOutcome.Failed, T3UsageText.PairingDiscarded); }
            if (result.Outcome != T3PairingOutcome.Success || result.Credential == null) return result;

            await _credentialLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                // Removed (or re-paired) while the exchange was in flight: discard this credential.
                if (pairingCts.IsCancellationRequested || CredentialGeneration(origin) != generation)
                    return new(T3PairingOutcome.Failed, T3UsageText.PairingDiscarded);
                try
                {
                    await _store.SaveCredentialAsync(CredentialTargetFor(origin), CredentialUsername, JsonSerializer.Serialize(result.Credential), CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    return new(T3PairingOutcome.Failed, "Couldn’t save token access to Windows Credential Manager. Try pairing again.");
                }
                lock (_lock)
                {
                    _credentials[origin] = result.Credential;
                    _status.Remove(origin);
                }
            }
            finally { _credentialLock.Release(); }
            Changed?.Invoke();
            return result with { Credential = null }; // Callers never need the token itself.
        }
        finally
        {
            lock (_lock) { if (_pairing.TryGetValue(origin, out var current) && ReferenceEquals(current, pairingCts)) _pairing.Remove(origin); }
            pairingCts.Dispose();
        }
    }

    private Task<T3PairingResult> ExchangePairingAsync(string origin, string code, CancellationToken ct) =>
        _pairingClient.ExchangeAsync(new Uri(origin), code, ct);

    public async Task RemoveTokenAccessAsync(string environmentId, CancellationToken ct = default)
    {
        string? origin = OriginFor(environmentId);
        if (origin == null) return;
        await ForgetAsync(origin, null).ConfigureAwait(false);
        Invalidate();
    }

    /// <summary>Deletes the credential for an origin that is no longer in the environment list.</summary>
    public async Task RemoveOriginAsync(string origin)
    {
        await ForgetAsync(origin, null).ConfigureAwait(false);
        Invalidate();
    }

    /// <summary>
    /// A credential that expires while Deskchirp runs is dropped right away, the environment says to pair
    /// again, and the stored copy is deleted unless a newer pairing has started or replaced it.
    /// </summary>
    private void ExpireIfNeeded(string origin)
    {
        lock (_lock)
        {
            if (!_credentials.TryGetValue(origin, out var credential) || !credential.IsExpired(_clock())) return;
            _credentials.Remove(origin);
            _status[origin] = T3UsageText.PairAgain;
        }
        _ = Task.Run(async () =>
        {
            await _credentialLock.WaitAsync().ConfigureAwait(false);
            try
            {
                bool replaced;
                lock (_lock) { replaced = _credentials.ContainsKey(origin) || _pairing.ContainsKey(origin); }
                if (!replaced) await _store.DeleteCredentialAsync(CredentialTargetFor(origin), CredentialUsername).ConfigureAwait(false);
            }
            catch { }
            finally { _credentialLock.Release(); }
            Changed?.Invoke();
        });
    }

    private async Task ForgetAsync(string origin, string? status)
    {
        lock (_lock)
        {
            _credentialGeneration[origin] = (_credentialGeneration.TryGetValue(origin, out long g) ? g : 0) + 1;
            if (_pairing.Remove(origin, out var pending)) pending.Cancel();
            _credentials.Remove(origin);
            if (status == null) _status.Remove(origin); else _status[origin] = status;
        }
        await _credentialLock.WaitAsync().ConfigureAwait(false);
        try { await _store.DeleteCredentialAsync(CredentialTargetFor(origin), CredentialUsername).ConfigureAwait(false); }
        catch { }
        finally { _credentialLock.Release(); }
        Changed?.Invoke();
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        bool had;
        lock (_lock) { had = _current != null; _current = null; }
        if (had) Changed?.Invoke();
    }

    private sealed class EnvPlan
    {
        public required string EnvironmentId { get; init; }
        public required string Origin { get; init; }
        public required T3UsageCredential Credential { get; init; }
        public bool Today { get; set; }
        public List<string> Threads { get; } = new();
    }

    public async Task<T3UsageSnapshot?> PollOnceAsync(T3UsagePollRequest request, CancellationToken ct = default)
    {
        if (!await _pollLock.WaitAsync(0, ct).ConfigureAwait(false)) return _current;
        long generation = Interlocked.Read(ref _generation);
        try
        {
            var settings = request.Settings;
            var now = _clock();
            var scope = PresenceCardFormatter.EffectiveScope(settings);
            var plans = new Dictionary<string, EnvPlan>(StringComparer.Ordinal);
            EnvPlan? PlanFor(string environmentId)
            {
                if (plans.TryGetValue(environmentId, out var existing)) return existing;
                string? origin = OriginFor(environmentId);
                T3UsageCredential? credential = null;
                lock (_lock) { if (origin != null) _credentials.TryGetValue(origin, out credential); }
                if (origin == null || credential == null || credential.IsExpired(now)) return null;
                var plan = new EnvPlan { EnvironmentId = environmentId, Origin = origin, Credential = credential };
                plans[environmentId] = plan;
                return plan;
            }

            bool missing = false; // An eligible part couldn't be asked: the value is partial.
            string? pinnedKey = null;
            List<string> sessionKeys = new();
            switch (scope)
            {
                case UsageScope.Today:
                    // The provider aggregate can't honor exclusions, so it is never requested with any.
                    if (settings.UsageMetric != UsageMetric.Tokens || settings.ExcludedProjects.Count > 0) break;
                    foreach (var env in settings.T3Environments)
                        if (PlanFor(env.Id) is { } plan) plan.Today = true;
                    break;
                case UsageScope.PinnedTask:
                {
                    int slash = settings.PinnedTask.IndexOf('/');
                    if (slash <= 0 || slash == settings.PinnedTask.Length - 1) break;
                    string envId = settings.PinnedTask[..slash], threadId = settings.PinnedTask[(slash + 1)..];
                    // Exclusions apply first: a pinned task that is excluded (directly or through its parent)
                    // or missing from the filtered snapshot is never read.
                    if (!PresenceCardFormatter.IsPinnedTaskVisible(settings, request.Snapshot, now)) break;
                    if (PlanFor(envId) is { } plan) { plan.Threads.Add(threadId); pinnedKey = settings.PinnedTask; }
                    break;
                }
                case UsageScope.Session:
                {
                    if (request.WorkSessionStart == null || request.Snapshot == null) break;
                    sessionKeys = SessionThreads(settings, request.Snapshot, now);
                    if (sessionKeys.Count > T3UsageCalculator.MaxProjectionsPerPoll) missing = true;
                    foreach (var key in sessionKeys.Take(T3UsageCalculator.MaxProjectionsPerPoll))
                    {
                        int slash = key.IndexOf('/');
                        if (PlanFor(key[..slash]) is { } plan) plan.Threads.Add(key[(slash + 1)..]);
                        else missing = true;
                    }
                    break;
                }
            }

            if (plans.Count == 0)
            {
                Publish(generation, null);
                return null;
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(PollBudget);
            var outcomes = await Task.WhenAll(plans.Values.Select(p => RunEnvironmentAsync(p, now, budget.Token))).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var snapshot = new T3UsageSnapshot { Timestamp = _clock(), SessionStart = request.WorkSessionStart, PinnedTaskKey = pinnedKey };
            if (scope == UsageScope.Today && plans.Values.Any(p => p.Today))
            {
                snapshot.Today = T3UsageCalculator.CombineToday(outcomes.Where(o => o.Plan.Today).Select(o => o.Today));
            }
            else if (scope == UsageScope.PinnedTask && pinnedKey != null)
            {
                var projection = outcomes.SelectMany(o => o.Projections).FirstOrDefault();
                var value = T3UsageCalculator.SumTurns(new[] { projection }, null);
                if (T3UsageCalculator.Context(projection) is { } context)
                {
                    value.ContextUsedTokens = context.Used;
                    value.ContextMaxTokens = context.Max;
                }
                if (projection == null) value.Partial = true;
                snapshot.PinnedTask = value;
            }
            else if (scope == UsageScope.Session && request.WorkSessionStart is { } since)
            {
                var projections = outcomes.SelectMany(o => o.Projections).ToList();
                var value = T3UsageCalculator.SumTurns(projections, since);
                if (missing || outcomes.Any(o => o.Failed)) value.Partial = true;
                snapshot.Session = value;
            }

            Publish(generation, snapshot);
            return generation == Interlocked.Read(ref _generation) ? snapshot : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        finally { _pollLock.Release(); }
    }

    /// <summary>Running, waiting and starting top-level threads plus their helpers, after exclusions.</summary>
    public static List<string> SessionThreads(PresenceSettings settings, T3Snapshot snapshot, DateTimeOffset now)
    {
        var entries = PresenceCardFormatter.VisibleEntries(settings, snapshot, now);
        var tops = entries.Where(e => !e.IsHelper && e.Phase != T3Phase.None).Select(e => e.ThreadKey).ToHashSet(StringComparer.Ordinal);
        var keys = new List<string>(tops);
        foreach (var helper in entries.Where(e => e.IsHelper && !string.IsNullOrEmpty(e.Thread.ParentThreadId)))
            if (tops.Contains(T3WorkloadView.Key(helper.EnvironmentId, helper.Thread.ParentThreadId!))) keys.Add(helper.ThreadKey);
        return keys.Distinct(StringComparer.Ordinal).ToList();
    }

    private sealed record EnvOutcome(EnvPlan Plan, bool Failed, T3UsageValue? Today, IReadOnlyList<T3ProjectionUsage?> Projections);

    private async Task<EnvOutcome> RunEnvironmentAsync(EnvPlan plan, DateTimeOffset now, CancellationToken ct)
    {
        var failed = new EnvOutcome(plan, true, null, plan.Threads.Select(_ => (T3ProjectionUsage?)null).ToList());
        try
        {
            if (!await IsSupportedAsync(plan.Origin, ct).ConfigureAwait(false))
            {
                SetStatus(plan.Origin, T3UsageText.Unsupported);
                return failed;
            }
            var requests = new List<(string Tag, object Payload)>();
            if (plan.Today)
            {
                if (!TryIanaZone(_timeZone, out string zone)) return failed; // Never send an offset.
                string day = TimeZoneInfo.ConvertTime(now, _timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                requests.Add(("server.getUsageSummary", new Dictionary<string, object>
                {
                    ["sinceDay"] = day, ["untilDay"] = day, ["timeZone"] = zone, ["awaitRefresh"] = true
                }));
            }
            foreach (var thread in plan.Threads)
                requests.Add(("orchestration.getThreadProjection", new Dictionary<string, object> { ["threadId"] = thread }));

            var ws = new UriBuilder(plan.Origin) { Scheme = plan.Origin.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws", Path = "/ws",
                Query = "orchestrationProtocol=2&clientSurface=deskchirp&clientAppVersion=" + Uri.EscapeDataString(ClientAppVersion) }.Uri;
            IReadOnlyDictionary<string, RpcResult> results;
            await using (var socket = await _sockets.ConnectAsync(ws, plan.Credential.AccessToken, ct).ConfigureAwait(false))
            {
                results = await EffectRpcSession.RunAsync(socket, requests, ct).ConfigureAwait(false);
            }

            int index = 1;
            T3UsageValue? today = null;
            bool anyFailed = false, authError = false;
            if (plan.Today)
            {
                var r = results[(index++).ToString(CultureInfo.InvariantCulture)];
                today = r.Ok && r.Value is { } v ? T3UsageCalculator.ParseUsageSummary(v) : null;
                anyFailed |= today == null;
                authError |= r.AuthorizationError;
            }
            var projections = new List<T3ProjectionUsage?>();
            foreach (var _ in plan.Threads)
            {
                var r = results[(index++).ToString(CultureInfo.InvariantCulture)];
                var projection = r.Ok && r.Value is { } v ? T3UsageCalculator.ParseProjection(v) : null;
                anyFailed |= projection == null;
                authError |= r.AuthorizationError;
                projections.Add(projection);
            }
            SetStatus(plan.Origin, authError ? T3UsageText.PairAgain : anyFailed ? T3UsageText.Unavailable : null);
            return new EnvOutcome(plan, anyFailed, today, projections);
        }
        catch (T3UsageHttpStatusException ex) when (ex.StatusCode == 401)
        {
            await ForgetAsync(plan.Origin, T3UsageText.PairAgain).ConfigureAwait(false);
            return failed;
        }
        catch (T3UsageHttpStatusException ex) when (ex.StatusCode == 426)
        {
            SetStatus(plan.Origin, T3UsageText.Unsupported);
            return failed;
        }
        catch (Exception)
        {
            SetStatus(plan.Origin, T3UsageText.Unavailable);
            return failed;
        }
    }

    private async Task<bool> IsSupportedAsync(string origin, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, origin + "/.well-known/t3/environment");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return false;
        string body = await T3UsagePairingClient.ReadBoundedAsync(response, MaxMetadataBytes, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        return T3UsageCalculator.ReadLong(doc.RootElement, "orchestrationProtocolVersion") == 2;
    }

    public static bool TryIanaZone(TimeZoneInfo zone, out string iana)
    {
        if (zone.HasIanaId) { iana = zone.Id; return true; }
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var converted) && !string.IsNullOrEmpty(converted))
        {
            iana = converted;
            return true;
        }
        iana = string.Empty;
        return false;
    }

    private void SetStatus(string origin, string? status)
    {
        lock (_lock)
        {
            if (status == null) _status.Remove(origin); else _status[origin] = status;
        }
    }

    private void Publish(long generation, T3UsageSnapshot? snapshot)
    {
        lock (_lock)
        {
            if (generation != Interlocked.Read(ref _generation)) return;
            _current = snapshot;
        }
        Changed?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        Invalidate();
        lock (_lock) { foreach (var cts in _pairing.Values) cts.Cancel(); _pairing.Clear(); }
        _pollLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
