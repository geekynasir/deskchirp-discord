using System.Text.Json;
using DeveloperPresence.Core.Credentials;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;
using DeveloperPresence.Core.Services;

namespace DeveloperPresence.Core.T3;

/// <summary>
/// Reads read-only T3 activity from every configured environment (spec/presence-v3.md §1).
/// Each environment has its own sign-in, poll lock, generation and snapshot. A failing
/// environment contributes nothing and never invalidates the others.
/// </summary>
public sealed class T3ActivityService : IT3ActivityService
{
    private const string MsgNotConnected = "Deskchirp finds T3 Code on this computer. Allow access once to share task and helper counts.";
    private const string MsgConnected = "T3 work stays visible while the app is minimized.";
    private const string MsgExpired = "Your T3 Code sign-in expired. Connect again to keep showing activity.";
    private const string MsgOpening = "Approve Read only access in your browser, then come back here. If T3 asks for a pairing code, create one in T3 Code under Settings → Sharing & access.";
    private const string MsgUnavailable = "T3 activity isn’t available right now, so only the app name is shown.";
    private const string MsgConnectingThroughT3 = "Connecting through T3 Code…";
    public const string MsgNoWorkHere = "Connected. No tasks are running here right now.";
    public const string MsgAlreadyAdded = "This environment is already in the list.";

    private readonly ISettingsService _settingsService;
    private readonly ICredentialStore _credentialStore;
    private readonly IT3OAuthClient _oauthClient;
    private readonly Func<IT3McpClient> _mcpClientFactory;
    private readonly IT3McpClient? _sharedMcpClient;
    private readonly T3LeaderTracker _leaderTracker = new();
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<string, EnvState> _environments = new(StringComparer.Ordinal);

    private T3ConnectionStatus _connectionStatus = T3ConnectionStatus.NotConnected;
    private string _statusDetail = MsgNotConnected;
    private T3Snapshot? _currentSnapshot;
    private long _pollGeneration;
    private bool _isDisposed;

    /// <summary>Legacy single-environment credential (preview 3). Migrated to the per-address key.</summary>
    public const string CredentialTarget = "DeveloperPresence:T3Code";
    public const string CredentialUsername = "OAuthBearerToken";
    public const int MaxPollPages = 10;
    public const int MaxPollThreads = 1000;
    public static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan ProjectRefresh = TimeSpan.FromMinutes(5);

    /// <summary>Per-environment credential key: the legacy target plus the normalized MCP URL.</summary>
    public static string CredentialTargetFor(string normalizedMcpUri) => $"{CredentialTarget}:{normalizedMcpUri}";

    private sealed class EnvState
    {
        public required string Id { get; init; }
        public string Address { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public T3StoredToken? Token { get; set; }
        public T3ConnectionStatus Status { get; set; } = T3ConnectionStatus.NotConnected;
        public string Detail { get; set; } = MsgNotConnected;
        public SemaphoreSlim PollLock { get; } = new(1, 1);
        public long Generation;
        public long AuthGeneration;
        public CancellationTokenSource? SignInCts { get; set; }
        public T3EnvironmentSnapshot? Snapshot { get; set; }
        public bool HasReturnedWork { get; set; }
        public bool HasPolled { get; set; }
        /// <summary>Signed in, but the last poll couldn't reach the address.</summary>
        public bool Unreachable { get; set; }
        public required IT3McpClient Client { get; init; }
        public DateTimeOffset ProjectsFetchedAt { get; set; } = DateTimeOffset.MinValue;
        public IReadOnlyList<T3PickerProject> Projects { get; set; } = Array.Empty<T3PickerProject>();
        public IReadOnlyList<T3PickerTask> Tasks { get; set; } = Array.Empty<T3PickerTask>();
        /// <summary>Project ids from the last t3_project_list answer, or null before the first one.</summary>
        public IReadOnlyList<T3ProjectListItem>? ProjectCatalog { get; set; }
    }

    public T3ConnectionStatus ConnectionStatus => _connectionStatus;
    public string StatusDetail => _statusDetail;
    public T3Snapshot? CurrentSnapshot => _currentSnapshot;

    public bool IsSnapshotValid
    {
        get
        {
            var s = _currentSnapshot;
            return s != null && s.IsValid && (DateTimeOffset.UtcNow - s.Timestamp) <= SnapshotTtl;
        }
    }

    public event Action<T3ConnectionStatus, string>? StatusChanged;
    public event Action<T3Snapshot?>? SnapshotUpdated;
    public event Action? EnvironmentsChanged;

    public IReadOnlyList<T3EnvironmentStatus> Environments
    {
        get
        {
            lock (_stateLock)
            {
                return OrderedStates().Select(e => new T3EnvironmentStatus
                {
                    EnvironmentId = e.Id, Address = e.Address, Label = e.Label, Status = e.Status, Detail = e.Detail,
                    HasReturnedWork = e.HasReturnedWork, ProjectCatalogLoaded = e.ProjectCatalog != null,
                    Unreachable = e.Unreachable
                }).ToList();
            }
        }
    }

    public bool AnyEnvironmentConnected
    {
        get { lock (_stateLock) { return _environments.Values.Any(e => e.Status == T3ConnectionStatus.Connected && e.Token != null); } }
    }

    public IReadOnlyList<T3PickerTask> PickerTasks
    {
        get { lock (_stateLock) { return OrderedStates().SelectMany(e => e.Tasks).ToList(); } }
    }

    public IReadOnlyList<T3PickerProject> PickerProjects
    {
        get { lock (_stateLock) { return OrderedStates().SelectMany(e => e.Projects).ToList(); } }
    }

    public T3ActivityService(
        ISettingsService settingsService,
        ICredentialStore credentialStore,
        IT3OAuthClient? oauthClient = null,
        IT3McpClient? mcpClient = null,
        Func<IT3McpClient>? mcpClientFactory = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _oauthClient = oauthClient ?? new T3OAuthClient();
        _sharedMcpClient = mcpClient;
        _mcpClientFactory = mcpClient != null ? () => mcpClient : mcpClientFactory ?? (() => new T3McpClient());
        _settingsService.Settings.NormalizeAndMigrate();
        SyncStructure(_settingsService.Settings);
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    private IEnumerable<EnvState> OrderedStates()
    {
        foreach (var env in _settingsService.Settings.T3Environments)
            if (_environments.TryGetValue(env.Id, out var state)) yield return state;
    }

    /// <summary>Aligns per-environment state with settings. Returns environments that need their sign-in loaded.</summary>
    private List<EnvState> SyncStructure(PresenceSettings settings)
    {
        var needsLoad = new List<EnvState>();
        var removed = new List<EnvState>();
        lock (_stateLock)
        {
            var ids = new HashSet<string>(settings.T3Environments.Select(e => e.Id), StringComparer.Ordinal);
            foreach (var id in _environments.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                removed.Add(_environments[id]);
                _environments.Remove(id);
            }
            foreach (var env in settings.T3Environments)
            {
                string address = T3UrlValidator.TryNormalizeT3Address(env.Address, out var uri, out _) ? uri!.AbsoluteUri : env.Address;
                if (!_environments.TryGetValue(env.Id, out var state))
                {
                    state = new EnvState { Id = env.Id, Address = address, Label = env.Label, Client = _mcpClientFactory() };
                    _environments[env.Id] = state;
                    needsLoad.Add(state);
                }
                else if (!string.Equals(state.Address, address, StringComparison.Ordinal))
                {
                    // Editing the address stops using the previous environment immediately.
                    Interlocked.Increment(ref state.Generation);
                    Interlocked.Increment(ref state.AuthGeneration);
                    state.SignInCts?.Cancel();
                    state.Address = address;
                    state.Token = null;
                    state.Snapshot = null;
                    state.HasPolled = false;
                    state.HasReturnedWork = false;
                    state.Status = T3ConnectionStatus.NotConnected;
                    state.Detail = MsgNotConnected;
                    needsLoad.Add(state);
                }
                state.Label = env.Label;
            }
        }
        foreach (var state in removed)
        {
            Interlocked.Increment(ref state.Generation);
            Interlocked.Increment(ref state.AuthGeneration);
            state.SignInCts?.Cancel();
            if (_sharedMcpClient == null) _ = state.Client.DisposeAsync().AsTask();
        }
        if (removed.Count > 0 || needsLoad.Count > 0) RebuildSnapshot();
        return needsLoad;
    }

    private void OnSettingsChanged(PresenceSettings settings)
    {
        if (settings.NormalizeAndMigrate()) { /* Saved with the next settings write. */ }
        var needsLoad = SyncStructure(settings);
        if (needsLoad.Count > 0) _ = LoadCredentialsAsync(needsLoad, CancellationToken.None);
        PublishStatus();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _authLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await MigrateLegacyCredentialAsync(ct).ConfigureAwait(false);
        }
        catch { }
        finally { _authLock.Release(); }

        List<EnvState> all;
        lock (_stateLock) { all = _environments.Values.ToList(); }
        await LoadCredentialsAsync(all, ct).ConfigureAwait(false);
        PublishStatus();
    }

    private async Task MigrateLegacyCredentialAsync(CancellationToken ct)
    {
        string? legacy = await _credentialStore.ReadCredentialAsync(CredentialTarget, CredentialUsername, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(legacy)) return;
        var token = JsonSerializer.Deserialize<T3StoredToken>(legacy);
        if (token == null || string.IsNullOrWhiteSpace(token.AccessToken)) return;
        EnvState? owner;
        lock (_stateLock) { owner = _environments.Values.FirstOrDefault(e => string.Equals(e.Address, token.McpUri, StringComparison.Ordinal)); }
        if (owner == null) return;
        string target = CredentialTargetFor(owner.Address);
        if (string.IsNullOrWhiteSpace(await _credentialStore.ReadCredentialAsync(target, CredentialUsername, ct).ConfigureAwait(false)))
            await _credentialStore.SaveCredentialAsync(target, CredentialUsername, legacy, ct).ConfigureAwait(false);
        await _credentialStore.DeleteCredentialAsync(CredentialTarget, CredentialUsername, ct).ConfigureAwait(false);
    }

    private async Task LoadCredentialsAsync(IEnumerable<EnvState> states, CancellationToken ct)
    {
        foreach (var state in states)
        {
            long authGen = Interlocked.Read(ref state.AuthGeneration);
            string address = state.Address;
            T3StoredToken? token = null;
            try
            {
                string? secret = await _credentialStore.ReadCredentialAsync(CredentialTargetFor(address), CredentialUsername, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(secret)) token = JsonSerializer.Deserialize<T3StoredToken>(secret);
            }
            catch { token = null; }
            lock (_stateLock)
            {
                if (authGen != Interlocked.Read(ref state.AuthGeneration) || state.Address != address) continue;
                if (state.Status == T3ConnectionStatus.OpeningSignIn) continue;
                if (token != null && !string.IsNullOrWhiteSpace(token.AccessToken) &&
                    string.Equals(token.McpUri, address, StringComparison.Ordinal))
                {
                    state.Token = token;
                    SetEnvStatus(state, token.IsExpired ? T3ConnectionStatus.SignInAgain : T3ConnectionStatus.Connected,
                        token.IsExpired ? MsgExpired : MsgConnected);
                }
                else
                {
                    state.Token = null;
                    SetEnvStatus(state, T3ConnectionStatus.NotConnected, MsgNotConnected);
                }
            }
        }
        PublishStatus();
    }

    public async Task<string?> AddEnvironmentAsync(string rawAddress, CancellationToken ct = default)
    {
        if (!T3UrlValidator.TryNormalizeT3Address(rawAddress, out var uri, out var error)) return error ?? T3UrlValidator.MsgInvalidUrl;
        string address = uri!.AbsoluteUri;
        if (_settingsService.Settings.T3Environments.Any(e => string.Equals(e.Address, address, StringComparison.Ordinal)))
            return MsgAlreadyAdded;
        await _settingsService.UpdateSettingsAsync(s => s.T3Environments.Add(new T3Environment
        {
            Address = address, Label = PresenceSettings.DefaultLabel(address)
        })).ConfigureAwait(false);
        return null;
    }

    public async Task<bool> ConnectAsync(string rawAddress, CancellationToken ct = default)
    {
        if (!T3UrlValidator.TryNormalizeT3Address(rawAddress, out var normalized, out var urlError))
        {
            SetStatus(_connectionStatus, urlError ?? T3UrlValidator.MsgInvalidUrl);
            return false;
        }
        string address = normalized!.AbsoluteUri;
        var env = _settingsService.Settings.T3Environments.FirstOrDefault(e => string.Equals(e.Address, address, StringComparison.Ordinal));
        if (env == null)
        {
            env = new T3Environment { Address = address, Label = PresenceSettings.DefaultLabel(address) };
            var added = env;
            await _settingsService.UpdateSettingsAsync(s =>
            {
                if (!s.T3Environments.Any(e => e.Address == address)) s.T3Environments.Add(added);
            }).ConfigureAwait(false);
            env = _settingsService.Settings.T3Environments.First(e => e.Address == address);
        }
        SyncStructure(_settingsService.Settings);
        return await ConnectEnvironmentAsync(env.Id, ct).ConfigureAwait(false);
    }

    public Task<bool> ConnectEnvironmentAsync(string environmentId, CancellationToken ct = default) =>
        ConnectEnvironmentCoreAsync(environmentId, MsgOpening, (uri, token) => _oauthClient.SignInAsync(uri, token), ct);

    public Task<bool> ConnectEnvironmentWithPairingCodeAsync(string environmentId, string pairingCode, CancellationToken ct = default) =>
        ConnectEnvironmentCoreAsync(environmentId, MsgConnectingThroughT3,
            (uri, token) => _oauthClient.SignInWithPairingCodeAsync(uri, pairingCode, token), ct);

    private async Task<bool> ConnectEnvironmentCoreAsync(
        string environmentId,
        string openingMessage,
        Func<Uri, CancellationToken, Task<T3StoredToken>> signIn,
        CancellationToken ct)
    {
        EnvState? state;
        lock (_stateLock) { _environments.TryGetValue(environmentId, out state); }
        if (state == null || !Uri.TryCreate(state.Address, UriKind.Absolute, out var mcpUri)) return false;

        var priorStatus = state.Status;
        var priorToken = state.Token;
        long authGen = Interlocked.Increment(ref state.AuthGeneration);
        state.SignInCts?.Cancel();
        var signInCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        state.SignInCts = signInCts;
        var token = signInCts.Token;
        SetEnvStatus(state, T3ConnectionStatus.OpeningSignIn, openingMessage);
        PublishStatus();

        bool StillCurrent() => authGen == Interlocked.Read(ref state.AuthGeneration) && IsTracked(state);

        try
        {
            var storedToken = await signIn(mcpUri, token).ConfigureAwait(false);
            await _authLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!StillCurrent() || token.IsCancellationRequested) return false;
                string target = CredentialTargetFor(state.Address);
                await _credentialStore.SaveCredentialAsync(target, CredentialUsername, JsonSerializer.Serialize(storedToken), CancellationToken.None).ConfigureAwait(false);
                if (!StillCurrent())
                {
                    if (priorToken != null && IsTracked(state))
                        await _credentialStore.SaveCredentialAsync(target, CredentialUsername, JsonSerializer.Serialize(priorToken), CancellationToken.None).ConfigureAwait(false);
                    else
                        await _credentialStore.DeleteCredentialAsync(target, CredentialUsername, CancellationToken.None).ConfigureAwait(false);
                    return false;
                }
                lock (_stateLock)
                {
                    state.Token = storedToken;
                    Interlocked.Increment(ref state.Generation);
                    state.Snapshot = null;
                    state.HasPolled = false;
                    SetEnvStatus(state, T3ConnectionStatus.Connected, MsgConnected);
                }
                _leaderTracker.Reset();
                RebuildSnapshot();
                PublishStatus();
                return true;
            }
            finally { _authLock.Release(); }
        }
        catch (OperationCanceledException)
        {
            if (!StillCurrent()) { PublishStatus(); return false; }
            bool keep = priorStatus == T3ConnectionStatus.Connected && priorToken is { IsExpired: false };
            SetEnvStatus(state, keep ? T3ConnectionStatus.Connected : T3ConnectionStatus.NotConnected,
                keep ? "Sign-in was cancelled. Your existing connection wasn’t changed." : "Sign-in was cancelled.");
            PublishStatus();
            return false;
        }
        catch (Exception ex)
        {
            if (!StillCurrent()) { PublishStatus(); return false; }
            string msg = ex.Message.Contains("Check that you copied it with Copy MCP URL", StringComparison.OrdinalIgnoreCase) ||
                         ex.Message.Contains("didn’t respond like T3 Code", StringComparison.OrdinalIgnoreCase) ||
                         ex.Message.Contains("sent Deskchirp to a different address", StringComparison.OrdinalIgnoreCase)
                ? ex.Message
                : "Check that T3 Code is running and the address is right.";
            if (priorStatus == T3ConnectionStatus.Connected && priorToken is { IsExpired: false })
                SetEnvStatus(state, T3ConnectionStatus.Connected, "Sign-in didn’t finish. Your existing connection wasn’t changed.");
            else
                SetEnvStatus(state, T3ConnectionStatus.CouldNotReach, msg);
            PublishStatus();
            return false;
        }
    }

    private bool IsTracked(EnvState state)
    {
        lock (_stateLock) { return _environments.TryGetValue(state.Id, out var current) && ReferenceEquals(current, state); }
    }

    public Task CancelSignInAsync() => CancelSignInsAsync(null);

    public Task CancelSignInAsync(string environmentId) => CancelSignInsAsync(environmentId);

    private Task CancelSignInsAsync(string? environmentId)
    {
        List<EnvState> opening;
        lock (_stateLock)
        {
            opening = _environments.Values.Where(e => e.Status == T3ConnectionStatus.OpeningSignIn &&
                (environmentId == null || e.Id == environmentId)).ToList();
        }
        foreach (var state in opening)
        {
            Interlocked.Increment(ref state.AuthGeneration);
            state.SignInCts?.Cancel();
            bool keep = state.Token is { IsExpired: false };
            SetEnvStatus(state, keep ? T3ConnectionStatus.Connected : T3ConnectionStatus.NotConnected,
                keep ? "Sign-in was cancelled. Your existing connection wasn’t changed." : "Sign-in was cancelled.");
        }
        PublishStatus();
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        List<EnvState> all;
        lock (_stateLock) { all = _environments.Values.ToList(); }
        foreach (var state in all)
        {
            Interlocked.Increment(ref state.AuthGeneration);
            state.SignInCts?.Cancel();
        }
        Invalidate();

        await _authLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool failed = false;
            try { await _credentialStore.DeleteCredentialAsync(CredentialTarget, CredentialUsername, ct).ConfigureAwait(false); }
            catch { failed = true; }
            foreach (var state in all)
            {
                try
                {
                    await _credentialStore.DeleteCredentialAsync(CredentialTargetFor(state.Address), CredentialUsername, ct).ConfigureAwait(false);
                    state.Token = null;
                    state.HasPolled = false;
                    state.HasReturnedWork = false;
                    SetEnvStatus(state, T3ConnectionStatus.NotConnected, "To revoke access in T3 too, open T3 Code Settings → Sharing & access.");
                }
                catch
                {
                    failed = true;
                    SetEnvStatus(state, state.Token is { IsExpired: false } ? T3ConnectionStatus.Connected : T3ConnectionStatus.NotConnected,
                        "Couldn’t remove your saved sign-in. Try disconnecting again.");
                }
            }
            ClearSnapshot();
            _leaderTracker.Reset();
            PublishStatus(failed ? "Couldn’t remove your saved sign-in. Try disconnecting again." : null);
        }
        finally { _authLock.Release(); }
    }

    public async Task RemoveEnvironmentAsync(string environmentId, CancellationToken ct = default)
    {
        EnvState? state;
        lock (_stateLock) { _environments.TryGetValue(environmentId, out state); }
        string? address = state?.Address ?? _settingsService.Settings.T3Environments.FirstOrDefault(e => e.Id == environmentId)?.Address;
        if (state != null)
        {
            Interlocked.Increment(ref state.AuthGeneration);
            Interlocked.Increment(ref state.Generation);
            state.SignInCts?.Cancel();
        }
        if (address != null)
        {
            await _credentialStore.DeleteCredentialAsync(CredentialTargetFor(address), CredentialUsername, ct).ConfigureAwait(false);
        }
        string prefix = environmentId + "/";
        await _settingsService.UpdateSettingsAsync(s =>
        {
            s.T3Environments.RemoveAll(e => e.Id == environmentId);
            foreach (var key in s.ProjectAliases.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) s.ProjectAliases.Remove(key);
            s.ExcludedProjects.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
            if (s.PinnedTask.StartsWith(prefix, StringComparison.Ordinal)) s.PinnedTask = string.Empty;
            if (s.PinnedProject.StartsWith(prefix, StringComparison.Ordinal)) s.PinnedProject = string.Empty;
        }).ConfigureAwait(false);
        SyncStructure(_settingsService.Settings);
        PublishStatus();
    }

    public async Task<bool> ChangeEnvironmentAddressAsync(string environmentId, string rawAddress, CancellationToken ct = default)
    {
        if (!T3UrlValidator.TryNormalizeT3Address(rawAddress, out var uri, out _)) return false;
        string address = uri!.AbsoluteUri;
        var env = _settingsService.Settings.T3Environments.FirstOrDefault(e => e.Id == environmentId);
        if (env == null) return false;
        if (string.Equals(env.Address, address, StringComparison.Ordinal)) return true;
        if (_settingsService.Settings.T3Environments.Any(e => e.Id != environmentId && string.Equals(e.Address, address, StringComparison.Ordinal)))
            return false;
        // The old address's sign-in is useless once the entry moves; don't leave it behind.
        try { await _credentialStore.DeleteCredentialAsync(CredentialTargetFor(env.Address), CredentialUsername, ct).ConfigureAwait(false); }
        catch { }
        await _settingsService.UpdateSettingsAsync(s =>
        {
            var current = s.T3Environments.FirstOrDefault(e => e.Id == environmentId);
            if (current != null) current.Address = address;
        }).ConfigureAwait(false);
        SyncStructure(_settingsService.Settings);
        PublishStatus();
        return true;
    }

    public async Task<T3Snapshot?> PollOnceAsync(CancellationToken ct = default)
    {
        List<EnvState> targets;
        lock (_stateLock)
        {
            targets = OrderedStates().Where(e => e.Status == T3ConnectionStatus.Connected && e.Token != null).ToList();
        }
        if (targets.Count == 0) return null;
        long pollGen = Interlocked.Read(ref _pollGeneration);
        await Task.WhenAll(targets.Select(t => PollEnvironmentAsync(t, pollGen, ct))).ConfigureAwait(false);
        if (pollGen != Interlocked.Read(ref _pollGeneration) || ct.IsCancellationRequested) return null;
        return _currentSnapshot;
    }

    private async Task PollEnvironmentAsync(EnvState state, long pollGen, CancellationToken ct)
    {
        var token = state.Token;
        if (token == null) return;
        if (token.IsExpired)
        {
            lock (_stateLock) { state.Snapshot = null; SetEnvStatus(state, T3ConnectionStatus.SignInAgain, MsgExpired); }
            RebuildSnapshot();
            PublishStatus();
            return;
        }
        if (!Uri.TryCreate(token.McpUri, UriKind.Absolute, out var mcpUri) ||
            !string.Equals(token.McpUri, state.Address, StringComparison.Ordinal)) return;
        if (!await state.PollLock.WaitAsync(0, ct).ConfigureAwait(false)) return; // no overlap per environment

        long envGen = Interlocked.Read(ref state.Generation);
        bool Current() => pollGen == Interlocked.Read(ref _pollGeneration) && envGen == Interlocked.Read(ref state.Generation) &&
                          !ct.IsCancellationRequested && IsTracked(state);
        try
        {
            var capabilities = await state.Client.GetCapabilitiesAsync(mcpUri, token.AccessToken, ct).ConfigureAwait(false);
            var threads = new List<T3ThreadListItem>();
            int? cursor = null;
            int pages = 0;
            do
            {
                ct.ThrowIfCancellationRequested();
                if (++pages > MaxPollPages) throw new InvalidOperationException(MsgUnavailable);
                var page = await state.Client.ListThreadsAsync(mcpUri, token.AccessToken, cursor, ct).ConfigureAwait(false);
                if (page.Threads != null) threads.AddRange(page.Threads);
                if (threads.Count > MaxPollThreads) throw new InvalidOperationException(MsgUnavailable);
                cursor = page.NextCursor;
            } while (cursor.HasValue);

            IReadOnlyList<T3ProjectListItem>? projects = null;
            if (DateTimeOffset.UtcNow - state.ProjectsFetchedAt >= ProjectRefresh)
            {
                // Project titles serve only the local alias editor; a failure here never hides activity.
                try { projects = await state.Client.ListProjectsAsync(mcpUri, token.AccessToken, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (T3AuthenticationException) { throw; }
                catch { projects = null; }
            }

            if (!Current()) return;
            var drivers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in capabilities.Providers)
                if (!string.IsNullOrWhiteSpace(p.ProviderInstanceId)) drivers[p.ProviderInstanceId] = p.DriverKind ?? string.Empty;

            // Titles stay in the in-memory picker only; the published snapshot carries none.
            var pickerTasks = threads.Where(t => !string.IsNullOrWhiteSpace(t.ThreadId))
                .GroupBy(t => t.ThreadId, StringComparer.Ordinal).Select(g => g.First())
                .Select(t => new T3PickerTask(state.Id, t.ThreadId, t.Title, t.ProjectId, t.CreatedAtValue, t.UpdatedAtValue,
                    string.Equals(t.RelationshipToParent, "subagent", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(t => t.UpdatedAt ?? t.CreatedAt ?? DateTimeOffset.MinValue).ToList();
            var stripped = threads.Select(t => new T3ThreadListItem
            {
                ThreadId = t.ThreadId, ProviderInstanceId = t.ProviderInstanceId, Status = t.Status,
                RelationshipToParent = t.RelationshipToParent, ParentThreadId = t.ParentThreadId, ProjectId = t.ProjectId,
                Model = t.Model, CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt
            }).ToList();
            var now = DateTimeOffset.UtcNow;
            var catalog = projects != null
                ? projects.Select(p => new T3ProjectListItem { ProjectId = p.ProjectId }).ToList()
                : state.ProjectCatalog;
            var envSnapshot = new T3EnvironmentSnapshot { EnvironmentId = state.Id, Timestamp = now, Drivers = drivers, Threads = stripped, Projects = catalog };
            bool hasWork = T3WorkloadView.Entries(new[] { envSnapshot }, null).Any(e => e.Phase != T3Phase.None);

            lock (_stateLock)
            {
                if (!Current()) return;
                state.Snapshot = envSnapshot;
                state.Tasks = pickerTasks;
                if (projects != null)
                {
                    state.ProjectsFetchedAt = now;
                    state.Projects = projects.Select(p => new T3PickerProject(state.Id, p.ProjectId, p.Title)).ToList();
                    state.ProjectCatalog = catalog;
                }
                state.HasPolled = true;
                if (hasWork) state.HasReturnedWork = true;
                SetEnvStatus(state, T3ConnectionStatus.Connected, state.HasReturnedWork ? MsgConnected : MsgNoWorkHere);
            }
            RebuildSnapshot();
            PublishStatus();
            EnvironmentsChanged?.Invoke();
        }
        catch (T3AuthenticationException)
        {
            if (!Current()) return;
            lock (_stateLock) { state.Snapshot = null; SetEnvStatus(state, T3ConnectionStatus.SignInAgain, MsgExpired); }
            RebuildSnapshot();
            PublishStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (pollGen != Interlocked.Read(ref _pollGeneration) || envGen != Interlocked.Read(ref state.Generation)) return;
            // A failed environment is dropped from the combined snapshot, never zeroed.
            lock (_stateLock)
            {
                state.Snapshot = null;
                SetEnvStatus(state, T3ConnectionStatus.Connected, MsgUnavailable);
                state.Unreachable = ex is HttpRequestException or TimeoutException;
            }
            RebuildSnapshot();
            PublishStatus();
        }
        finally
        {
            state.PollLock.Release();
        }
    }

    /// <summary>Combines every fresh environment snapshot. Stale environments are dropped, not zeroed.</summary>
    private void RebuildSnapshot()
    {
        T3Snapshot? snapshot = null;
        bool changed;
        lock (_stateLock)
        {
            var now = DateTimeOffset.UtcNow;
            var fresh = OrderedStates()
                .Where(e => e.Snapshot != null && e.Status == T3ConnectionStatus.Connected && now - e.Snapshot.Timestamp <= SnapshotTtl)
                .Select(e => e.Snapshot!).ToList();
            if (fresh.Count > 0)
            {
                var settings = _settingsService.Settings;
                var entries = T3WorkloadView.Entries(fresh, settings.ExcludedProjects);
                var workloads = T3WorkloadView.Aggregate(entries);
                var winner = _leaderTracker.SelectLeader(workloads, now);
                snapshot = new T3Snapshot
                {
                    Timestamp = fresh.Max(e => e.Timestamp),
                    IsValid = true,
                    Environments = fresh,
                    Workloads = workloads,
                    WinningHarness = winner,
                    WinningState = winner != null ? T3WorkloadAggregator.FormatState(winner) : null,
                    Details = winner != null ? $"T3 Code · {winner.HarnessName}" : ActivityFormatter.HeadlineT3Code,
                    TotalThreadsObserved = entries.Count
                };
            }
            changed = !T3Snapshot.AreEquivalent(_currentSnapshot, snapshot);
            _currentSnapshot = snapshot;
        }
        if (changed) SnapshotUpdated?.Invoke(snapshot);
    }

    public void ClearSnapshot()
    {
        bool had;
        lock (_stateLock)
        {
            foreach (var env in _environments.Values) env.Snapshot = null;
            had = _currentSnapshot != null;
            _currentSnapshot = null;
        }
        if (had) SnapshotUpdated?.Invoke(null);
    }

    /// <summary>
    /// Invalidates active polling and clears snapshots on focus, pause, or eligibility changes.
    /// Does NOT kill or invalidate an in-flight sign-in.
    /// </summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _pollGeneration);
        ClearSnapshot();
        _leaderTracker.Reset();
    }

    private static void SetEnvStatus(EnvState state, T3ConnectionStatus status, string detail)
    {
        state.Status = status;
        state.Detail = detail;
        state.Unreachable = false;
    }

    /// <summary>Recomputes the combined status line, e.g. “Connected to 2 of 3 environments”.</summary>
    private void PublishStatus(string? overrideDetail = null)
    {
        T3ConnectionStatus status;
        string detail;
        lock (_stateLock)
        {
            var states = OrderedStates().ToList();
            if (states.Count == 0)
            {
                status = T3ConnectionStatus.NotConnected;
                detail = _statusDetail is var d && d.StartsWith("To revoke", StringComparison.Ordinal) ? d : MsgNotConnected;
            }
            else
            {
                var opening = states.FirstOrDefault(s => s.Status == T3ConnectionStatus.OpeningSignIn);
                int connected = states.Count(s => s.Status == T3ConnectionStatus.Connected);
                if (opening != null) { status = T3ConnectionStatus.OpeningSignIn; detail = opening.Detail; }
                else if (connected > 0)
                {
                    status = T3ConnectionStatus.Connected;
                    var connectedStates = states.Where(s => s.Status == T3ConnectionStatus.Connected).ToList();
                    if (states.Count == 1) detail = connectedStates[0].Detail;
                    else
                    {
                        detail = $"Connected to {connected} of {states.Count} environments";
                        if (connectedStates.All(s => s.HasPolled && !s.HasReturnedWork)) detail += ". No tasks are running right now.";
                    }
                }
                else
                {
                    var first = states.FirstOrDefault(s => s.Status == T3ConnectionStatus.SignInAgain)
                                ?? states.FirstOrDefault(s => s.Status == T3ConnectionStatus.CouldNotReach)
                                ?? states[0];
                    status = first.Status;
                    detail = first.Detail;
                }
            }
        }
        SetStatus(status, overrideDetail ?? detail);
        EnvironmentsChanged?.Invoke();
    }

    private void SetStatus(T3ConnectionStatus status, string detail)
    {
        if (_connectionStatus != status || !string.Equals(_statusDetail, detail, StringComparison.Ordinal))
        {
            _connectionStatus = status;
            _statusDetail = detail;
            StatusChanged?.Invoke(status, detail);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        List<EnvState> all;
        lock (_stateLock) { all = _environments.Values.ToList(); }
        foreach (var state in all)
        {
            Interlocked.Increment(ref state.AuthGeneration);
            state.SignInCts?.Cancel();
            state.SignInCts?.Dispose();
        }
        Invalidate();
        if (_sharedMcpClient != null) await _sharedMcpClient.DisposeAsync().ConfigureAwait(false);
        else foreach (var state in all) await state.Client.DisposeAsync().ConfigureAwait(false);
        _authLock.Dispose();
    }
}
