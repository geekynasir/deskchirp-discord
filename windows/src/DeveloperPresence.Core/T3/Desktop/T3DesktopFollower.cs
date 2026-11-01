using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Services;
using DeveloperPresence.Core.T3.Usage;

namespace DeveloperPresence.Core.T3.Desktop;

/// <summary>Where the follower reads T3 Code's saved connections. The real one is <see cref="T3DesktopCatalogReader"/>.</summary>
public interface IT3DesktopCatalogSource
{
    T3DesktopCatalogResult Read();
    DateTime? CatalogWriteTimeUtc();
}

public sealed class T3DesktopCatalogSource : IT3DesktopCatalogSource
{
    private readonly T3DesktopCatalogReader _reader;
    public T3DesktopCatalogSource(T3DesktopCatalogReader? reader = null) => _reader = reader ?? new T3DesktopCatalogReader();
    public T3DesktopCatalogResult Read() => _reader.Read();
    public DateTime? CatalogWriteTimeUtc() => _reader.CatalogWriteTimeUtc();
}

/// <summary>
/// Keeps the environment list in step with the connections saved in the T3 Code desktop app.
/// New ones are added and signed in to read-only without a browser: T3 Code's own sign-in for
/// that environment asks it for a one-time pairing code, which Deskchirp then approves itself.
/// T3 Code's tokens stay in memory only for that request and go only to their own environment.
/// Runs only while T3 activity and <see cref="PresenceSettings.FollowT3DesktopConnections"/> are on.
/// </summary>
public sealed class T3DesktopFollower : IAsyncDisposable
{
    public const string MsgUnreadable = "Couldn’t read the connections saved in T3 Code. You can still add environments below.";
    public const string MsgUnreachable = "Couldn’t reach this environment. Deskchirp tries again in a few minutes.";
    public const string MsgTokenRejected = "T3 Code’s sign-in for this environment has expired. Open the environment in T3 Code, and Deskchirp connects again after it.";
    public const string MsgMissingPermission = "T3 Code’s sign-in for this environment can’t create pairing codes. Use Allow instead.";
    public const string MsgSignInFailed = "Couldn’t connect through T3 Code. Deskchirp tries again in a few minutes, or use Allow.";

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(5);
    private static readonly string[] ReadScopes = { T3DesktopPairingClient.ReadScope };
    private static readonly string[] UsageScopes = { T3DesktopPairingClient.ReadScope, T3DesktopPairingClient.DiagnosticsScope };

    private readonly ISettingsService _settingsService;
    private readonly IT3ActivityService _activityService;
    private readonly IT3UsageService? _usageService;
    private readonly IT3DesktopCatalogSource _catalog;
    private readonly IT3DesktopPairingClient _pairing;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _retryAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _usageRetryAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _problems = new(StringComparer.Ordinal);
    private Task? _loop;
    private DateTime? _lastCatalogWrite;
    private bool _wasEnabled;
    private bool _forceRead = true;
    private string _note = string.Empty;
    private bool _disposed;

    public T3DesktopFollower(
        ISettingsService settingsService,
        IT3ActivityService activityService,
        IT3UsageService? usageService = null,
        IT3DesktopCatalogSource? catalog = null,
        IT3DesktopPairingClient? pairing = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? interval = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _activityService = activityService ?? throw new ArgumentNullException(nameof(activityService));
        _usageService = usageService;
        _catalog = catalog ?? new T3DesktopCatalogSource();
        _pairing = pairing ?? new T3DesktopPairingClient();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _interval = interval ?? DefaultInterval;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>Raised when <see cref="Note"/> or a per-environment problem changes.</summary>
    public event Action? Changed;

    /// <summary>T3 Code connections Deskchirp can't follow, in plain words. Empty when there are none.</summary>
    public string Note { get { lock (_lock) return _note; } }

    /// <summary>Why following couldn't connect this environment, or null.</summary>
    public string? ProblemFor(string environmentId)
    {
        lock (_lock) return _problems.TryGetValue(environmentId, out var problem) ? problem : null;
    }

    public static bool IsEnabled(PresenceSettings settings) => settings.FollowT3DesktopConnections && settings.ShowT3Activity;

    /// <summary>Records that the user removed a followed environment here, so it isn't added back.</summary>
    public static void MarkRemovedByUser(PresenceSettings settings, string environmentId)
    {
        var env = settings.T3Environments.FirstOrDefault(e => e.Id == environmentId);
        if (env is { IsFromT3Desktop: true }) settings.IgnoredT3DesktopEnvironments.Add(env.T3DesktopEnvironmentId);
    }

    /// <summary>Records that the user removed token access here, so following doesn't pair it again.</summary>
    public static void MarkUsageDeclined(PresenceSettings settings, string environmentId)
    {
        var env = settings.T3Environments.FirstOrDefault(e => e.Id == environmentId);
        if (env is { IsFromT3Desktop: true }) settings.T3DesktopUsageDeclined.Add(env.T3DesktopEnvironmentId);
    }

    /// <summary>Clears a previous decline once the user pairs token usage again themselves.</summary>
    public static void ClearUsageDeclined(PresenceSettings settings, string environmentId)
    {
        var env = settings.T3Environments.FirstOrDefault(e => e.Id == environmentId);
        if (env is { IsFromT3Desktop: true }) settings.T3DesktopUsageDeclined.Remove(env.T3DesktopEnvironmentId);
    }

    public void Start()
    {
        if (_disposed || _loop != null) return;
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Forgets failures and checks again now, for example after the user signs in again in T3 Code.</summary>
    public void RetryNow()
    {
        lock (_lock)
        {
            _retryAt.Clear();
            _usageRetryAt.Clear();
            _forceRead = true;
        }
        Wake();
    }

    private void OnSettingsChanged(PresenceSettings settings) => Wake();

    private void Wake()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await SyncOnceAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { /* Following is best effort; the next pass tries again. */ }
            try { await _wake.WaitAsync(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass: read T3 Code's connections if anything could have changed, then add, remove and connect.</summary>
    public async Task SyncOnceAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try { await SyncCoreAsync(ct).ConfigureAwait(false); }
        finally { _syncLock.Release(); }
    }

    private async Task SyncCoreAsync(CancellationToken ct)
    {
        var settings = _settingsService.Settings;
        bool enabled = IsEnabled(settings);
        if (!enabled)
        {
            _wasEnabled = false;
            ClearProblems();
            SetNote(string.Empty);
            return;
        }

        var writeTime = _catalog.CatalogWriteTimeUtc();
        bool catalogChanged = writeTime != _lastCatalogWrite;
        bool force;
        lock (_lock)
        {
            force = _forceRead || !_wasEnabled || catalogChanged;
            _forceRead = false;
            // T3 Code changed its connections, maybe after signing in again: retry failures now.
            if (catalogChanged) { _retryAt.Clear(); _usageRetryAt.Clear(); }
        }
        _wasEnabled = true;
        if (!force && !HasWork(settings)) return;

        var catalog = _catalog.Read();
        // An unreadable catalog, for example one T3 Code is still writing, is read again next pass.
        if (catalog.Status == T3DesktopCatalogStatus.Unreadable) { SetNote(MsgUnreadable); return; }
        _lastCatalogWrite = writeTime;
        if (catalog.Status == T3DesktopCatalogStatus.NotFound) { SetNote(string.Empty); return; }

        var byId = catalog.Environments.ToDictionary(e => e.EnvironmentId, StringComparer.Ordinal);
        await AddAndAdoptAsync(catalog.Environments, ct).ConfigureAwait(false);
        await RemoveGoneAsync(byId, ct).ConfigureAwait(false);
        await ConnectAsync(byId, ct).ConfigureAwait(false);
        await PairUsageAsync(byId, ct).ConfigureAwait(false);
        SetNote(BuildNote(catalog.Environments));
    }

    /// <summary>True when a followed environment still needs a sign-in or token access and isn't waiting out a failure.</summary>
    private bool HasWork(PresenceSettings settings)
    {
        var now = _clock();
        var statuses = _activityService.Environments;
        bool wantsUsage = WantsUsage(settings);
        lock (_lock)
        {
            foreach (var env in settings.T3Environments.Where(e => e.IsFromT3Desktop))
            {
                var envStatus = statuses.FirstOrDefault(s => s.EnvironmentId == env.Id);
                var status = envStatus?.Status;
                if ((NeedsSignIn(status) || envStatus?.Unreachable == true) && !(_retryAt.TryGetValue(env.Id, out var at) && now < at)) return true;
                if (status == T3ConnectionStatus.Connected && wantsUsage && _usageService?.HasPermission(env.Id) == false &&
                    !settings.T3DesktopUsageDeclined.Contains(env.T3DesktopEnvironmentId) &&
                    !(_usageRetryAt.TryGetValue(env.Id, out var usageAt) && now < usageAt)) return true;
            }
        }
        return false;
    }

    private static bool NeedsSignIn(T3ConnectionStatus? status) =>
        status is null or T3ConnectionStatus.NotConnected or T3ConnectionStatus.SignInAgain or T3ConnectionStatus.CouldNotReach;

    private bool WantsUsage(PresenceSettings settings) =>
        _usageService != null && (settings.CardStyle == CardStyle.Usage || _usageService.HasAnyPermission);

    private async Task AddAndAdoptAsync(IReadOnlyList<T3DesktopEnvironment> environments, CancellationToken ct)
    {
        var settings = _settingsService.Settings;
        var usable = environments
            .Where(d => d.Routes.Count > 0 && !settings.IgnoredT3DesktopEnvironments.Contains(d.EnvironmentId))
            .ToList();
        bool NeedsChange(PresenceSettings s) => usable.Any(d => !s.T3Environments.Any(e => e.T3DesktopEnvironmentId == d.EnvironmentId));
        if (!NeedsChange(settings)) return;

        ct.ThrowIfCancellationRequested();
        await _settingsService.UpdateSettingsAsync(s =>
        {
            foreach (var d in usable)
            {
                if (s.T3Environments.Any(e => e.T3DesktopEnvironmentId == d.EnvironmentId)) continue;
                // An entry added by hand for the same environment is adopted, keeping its name and sign-in.
                var match = s.T3Environments.FirstOrDefault(e => !e.IsFromT3Desktop && d.Routes.Any(r => r.McpAddress == e.Address));
                if (match != null)
                {
                    match.T3DesktopEnvironmentId = d.EnvironmentId;
                    continue;
                }
                s.T3Environments.Add(new T3Environment
                {
                    Address = d.Routes[0].McpAddress,
                    Label = d.Label,
                    T3DesktopEnvironmentId = d.EnvironmentId
                });
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Removes followed entries T3 Code no longer has, with their sign-ins and token access.</summary>
    private async Task RemoveGoneAsync(Dictionary<string, T3DesktopEnvironment> byId, CancellationToken ct)
    {
        var gone = _settingsService.Settings.T3Environments
            .Where(e => e.IsFromT3Desktop && !byId.ContainsKey(e.T3DesktopEnvironmentId))
            .Select(e => e.Id)
            .ToList();
        foreach (string id in gone)
        {
            ct.ThrowIfCancellationRequested();
            if (_usageService != null)
            {
                try { await _usageService.RemoveTokenAccessAsync(id, ct).ConfigureAwait(false); } catch { }
            }
            await _activityService.RemoveEnvironmentAsync(id, ct).ConfigureAwait(false);
            lock (_lock) { _retryAt.Remove(id); _usageRetryAt.Remove(id); _problems.Remove(id); }
        }
    }

    private async Task ConnectAsync(Dictionary<string, T3DesktopEnvironment> byId, CancellationToken ct)
    {
        var followed = _settingsService.Settings.T3Environments
            .Where(e => e.IsFromT3Desktop && byId.ContainsKey(e.T3DesktopEnvironmentId))
            .Select(e => (e.Id, e.Address, Desktop: byId[e.T3DesktopEnvironmentId]))
            .ToList();
        foreach (var (id, address, desktop) in followed)
        {
            ct.ThrowIfCancellationRequested();
            var envStatus = _activityService.Environments.FirstOrDefault(s => s.EnvironmentId == id);
            var status = envStatus?.Status;
            // Signed in but the address stopped answering, for example after leaving the LAN: try T3 Code's other routes.
            bool moveOnly = !NeedsSignIn(status) && envStatus?.Unreachable == true && desktop.Routes.Any(r => r.McpAddress != address);
            if (!NeedsSignIn(status) && !moveOnly)
            {
                if (status == T3ConnectionStatus.Connected && envStatus?.Unreachable != true) SetProblem(id, null);
                continue;
            }
            lock (_lock) { if (_retryAt.TryGetValue(id, out var at) && _clock() < at) continue; }

            string? problem = await ConnectOneAsync(id, address, desktop, moveOnly, ct).ConfigureAwait(false);
            lock (_lock)
            {
                if (problem == null) _retryAt.Remove(id);
                else _retryAt[id] = _clock() + FailureBackoff;
            }
            SetProblem(id, problem);
        }
    }

    /// <summary>
    /// Tries T3 Code's routes for this environment, the current address first, or only the others when
    /// <paramref name="otherRoutesOnly"/>. Returns a problem, or null once connected.
    /// </summary>
    private async Task<string?> ConnectOneAsync(string id, string address, T3DesktopEnvironment desktop, bool otherRoutesOnly, CancellationToken ct)
    {
        var routes = desktop.Routes
            .Where(r => !otherRoutesOnly || r.McpAddress != address)
            .OrderBy(r => r.McpAddress == address ? 0 : 1).ToList();
        string problem = MsgUnreachable;
        foreach (var route in routes)
        {
            var minted = await _pairing.MintCodeAsync(route, ReadScopes, ct).ConfigureAwait(false);
            if (minted.Outcome != T3DesktopPairingOutcome.Success || minted.Code == null)
            {
                problem = minted.Outcome switch
                {
                    T3DesktopPairingOutcome.TokenRejected => MsgTokenRejected,
                    T3DesktopPairingOutcome.MissingPermission => MsgMissingPermission,
                    T3DesktopPairingOutcome.Unreachable => problem,
                    _ => MsgSignInFailed
                };
                continue;
            }
            // The user may have pressed Disconnect while the code was being minted.
            if (!IsEnabled(_settingsService.Settings)) return null;
            if (route.McpAddress != address)
            {
                // Token access is saved per address; drop it while the old address is still known.
                // Following pairs it again on the new address unless the user declined it.
                if (_usageService?.HasPermission(id) == true)
                {
                    try { await _usageService.RemoveTokenAccessAsync(id, ct).ConfigureAwait(false); } catch { }
                }
                if (!await _activityService.ChangeEnvironmentAddressAsync(id, route.McpAddress, ct).ConfigureAwait(false))
                    continue;
            }
            if (await _activityService.ConnectEnvironmentWithPairingCodeAsync(id, minted.Code, ct).ConfigureAwait(false)) return null;
            address = route.McpAddress;
            problem = MsgSignInFailed;
        }
        return problem;
    }

    /// <summary>Allows token usage for followed environments once the user has chosen to show usage anywhere.</summary>
    private async Task PairUsageAsync(Dictionary<string, T3DesktopEnvironment> byId, CancellationToken ct)
    {
        var settings = _settingsService.Settings;
        if (_usageService == null || !WantsUsage(settings)) return;
        var candidates = settings.T3Environments
            .Where(e => e.IsFromT3Desktop && byId.ContainsKey(e.T3DesktopEnvironmentId) &&
                        !settings.T3DesktopUsageDeclined.Contains(e.T3DesktopEnvironmentId))
            .Select(e => (e.Id, e.Address, Desktop: byId[e.T3DesktopEnvironmentId]))
            .ToList();
        foreach (var (id, address, desktop) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (UsageDeclined(id) || _usageService.HasPermission(id)) continue;
            if (_activityService.Environments.FirstOrDefault(s => s.EnvironmentId == id)?.Status != T3ConnectionStatus.Connected) continue;
            lock (_lock) { if (_usageRetryAt.TryGetValue(id, out var at) && _clock() < at) continue; }
            var route = desktop.Routes.FirstOrDefault(r => r.McpAddress == address);
            if (route == null) continue;

            bool paired = false;
            var minted = await _pairing.MintCodeAsync(route, UsageScopes, ct).ConfigureAwait(false);
            // The user may have removed token access, or turned following off, while the code was minted.
            if (UsageDeclined(id) || !IsEnabled(_settingsService.Settings)) continue;
            if (minted.Outcome == T3DesktopPairingOutcome.Success && minted.Code != null)
            {
                var result = await _usageService.PairAsync(id, minted.Code, ct).ConfigureAwait(false);
                paired = result.Outcome == T3PairingOutcome.Success;
            }
            lock (_lock)
            {
                if (paired) _usageRetryAt.Remove(id);
                else _usageRetryAt[id] = _clock() + FailureBackoff;
            }
        }
    }

    /// <summary>Checks current settings, not a snapshot taken before an await. A removed environment counts as declined.</summary>
    private bool UsageDeclined(string environmentId)
    {
        var settings = _settingsService.Settings;
        var env = settings.T3Environments.FirstOrDefault(e => e.Id == environmentId);
        return env == null || settings.T3DesktopUsageDeclined.Contains(env.T3DesktopEnvironmentId);
    }

    private string BuildNote(IReadOnlyList<T3DesktopEnvironment> environments)
    {
        var ignored = _settingsService.Settings.IgnoredT3DesktopEnvironments;
        var unsupported = environments.Where(d => d.Routes.Count == 0 && !ignored.Contains(d.EnvironmentId)).ToList();
        return string.Join(" ", unsupported.Select(d => $"T3 Code also has “{d.Label}”, which {d.UnsupportedReason}."));
    }

    private void SetNote(string note)
    {
        bool changed;
        lock (_lock)
        {
            changed = !string.Equals(_note, note, StringComparison.Ordinal);
            _note = note;
        }
        if (changed) Changed?.Invoke();
    }

    private void SetProblem(string environmentId, string? problem)
    {
        bool changed;
        lock (_lock)
        {
            _problems.TryGetValue(environmentId, out var previous);
            changed = !string.Equals(previous, problem, StringComparison.Ordinal);
            if (problem == null) _problems.Remove(environmentId);
            else _problems[environmentId] = problem;
        }
        if (changed) Changed?.Invoke();
    }

    private void ClearProblems()
    {
        bool changed;
        lock (_lock)
        {
            changed = _problems.Count > 0;
            _problems.Clear();
        }
        if (changed) Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _stop.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _stop.Dispose();
    }
}
