using System.Text.Json;
using DeveloperPresence.Core.Credentials;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Services;

namespace DeveloperPresence.Core.T3;

public sealed class T3ActivityService : IT3ActivityService
{
    private readonly ISettingsService _settingsService;
    private readonly ICredentialStore _credentialStore;
    private readonly IT3OAuthClient _oauthClient;
    private readonly IT3McpClient _mcpClient;
    private readonly T3LeaderTracker _leaderTracker = new();
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly object _stateLock = new();

    private T3ConnectionStatus _connectionStatus = T3ConnectionStatus.NotConnected;
    private string _statusDetail = "Deskchirp finds T3 Code on this computer. Allow access once to share task and helper counts.";
    private T3Snapshot? _currentSnapshot;
    private T3StoredToken? _storedToken;
    private CancellationTokenSource? _signInCts;
    private long _pollGeneration;
    private long _authGeneration;
    private bool _isDisposed;
    private string _observedAddress;

    public const string CredentialTarget = "DeveloperPresence:T3Code";
    public const string CredentialUsername = "OAuthBearerToken";
    public const int MaxPollPages = 10;
    public const int MaxPollThreads = 1000;
    public static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(45);

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

    public T3ActivityService(
        ISettingsService settingsService,
        ICredentialStore credentialStore,
        IT3OAuthClient? oauthClient = null,
        IT3McpClient? mcpClient = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _oauthClient = oauthClient ?? new T3OAuthClient();
        _mcpClient = mcpClient ?? new T3McpClient();
        _observedAddress = _settingsService.Settings.T3Address;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(PresenceSettings settings)
    {
        if (string.Equals(_observedAddress, settings.T3Address, StringComparison.Ordinal)) return;
        _observedAddress = settings.T3Address;
        Invalidate();
        var saved = _storedToken;
        if (saved == null) return;
        bool matches = T3UrlValidator.TryNormalizeT3Address(settings.T3Address, out var selected, out _) &&
                       string.Equals(selected!.AbsoluteUri, saved.McpUri, StringComparison.Ordinal);
        SetStatus(matches && !saved.IsExpired ? T3ConnectionStatus.Connected : T3ConnectionStatus.NotConnected,
            matches ? "T3 work stays visible while the app is minimized." : "Deskchirp finds T3 Code on this computer. Allow access once to share task and helper counts.");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _authLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? secret = await _credentialStore.ReadCredentialAsync(CredentialTarget, CredentialUsername, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(secret))
            {
                var token = JsonSerializer.Deserialize<T3StoredToken>(secret);
                if (token != null && !string.IsNullOrWhiteSpace(token.AccessToken))
                {
                    _storedToken = token;
                    if (token.IsExpired)
                    {
                        SetStatus(T3ConnectionStatus.SignInAgain, "Your T3 Code sign-in expired. Connect again to keep showing activity.");
                    }
                    else
                    {
                        SetStatus(T3ConnectionStatus.Connected, "T3 work stays visible while the app is minimized.");
                    }
                    return;
                }
            }
        }
        catch { }
        finally
        {
            _authLock.Release();
        }

        SetStatus(T3ConnectionStatus.NotConnected, "Deskchirp finds T3 Code on this computer. Allow access once to share task and helper counts.");
    }

    public async Task<bool> ConnectAsync(string rawAddress, CancellationToken ct = default)
    {
        if (!T3UrlValidator.TryNormalizeT3Address(rawAddress, out var normalizedMcpUri, out var urlError))
        {
            SetStatus(_connectionStatus, urlError ?? T3UrlValidator.MsgInvalidUrl);
            return false;
        }

        var priorStatus = _connectionStatus;
        var priorToken = _storedToken;

        // Separate auth generation: invalidates prior in-flight sign-in
        long authGen = Interlocked.Increment(ref _authGeneration);

        _signInCts?.Cancel();
        _signInCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _signInCts.Token;

        SetStatus(T3ConnectionStatus.OpeningSignIn, "Approve Read only access in your browser, then come back here. If T3 asks for a pairing code, create one in T3 Code under Settings → Sharing & access.");

        try
        {
            var storedToken = await _oauthClient.SignInAsync(normalizedMcpUri!, token).ConfigureAwait(false);

            // Serialization guard: check + save serialized with DisconnectAsync
            await _authLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (authGen != Interlocked.Read(ref _authGeneration) || token.IsCancellationRequested)
                {
                    // Disconnected or cancelled while SignInAsync was in flight: do not save!
                    return false;
                }

                // Securely persist token to Credential Manager
                string json = JsonSerializer.Serialize(storedToken);
                await _credentialStore.SaveCredentialAsync(CredentialTarget, CredentialUsername, json, CancellationToken.None).ConfigureAwait(false);

                if (authGen != Interlocked.Read(ref _authGeneration))
                {
                    if (priorToken != null)
                        await _credentialStore.SaveCredentialAsync(CredentialTarget, CredentialUsername, JsonSerializer.Serialize(priorToken), CancellationToken.None).ConfigureAwait(false);
                    else
                        await _credentialStore.DeleteCredentialAsync(CredentialTarget, CredentialUsername, CancellationToken.None).ConfigureAwait(false);
                    return false;
                }

                _storedToken = storedToken;
                Invalidate();

                // Persist normalized endpoint to settings (contains NO secrets)
                await _settingsService.UpdateSettingsAsync(s => s.T3Address = normalizedMcpUri!.AbsoluteUri).ConfigureAwait(false);

                _leaderTracker.Reset();
                SetStatus(T3ConnectionStatus.Connected, "T3 work stays visible while the app is minimized.");
                return true;
            }
            finally
            {
                _authLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            await _authLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
            if (authGen != Interlocked.Read(ref _authGeneration)) return false;
            // Cancelled or denied
            if (priorStatus == T3ConnectionStatus.Connected && priorToken != null && !priorToken.IsExpired)
            {
                SetStatus(T3ConnectionStatus.Connected, "Sign-in was cancelled. Your existing connection wasn’t changed.");
            }
            else
            {
                SetStatus(T3ConnectionStatus.NotConnected, "Sign-in was cancelled.");
            }
            return false;
            }
            finally { _authLock.Release(); }
        }
        catch (Exception ex)
        {
            await _authLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
            if (authGen != Interlocked.Read(ref _authGeneration)) return false;
            string msg = ex.Message.Contains("Check that you copied it with Copy MCP URL", StringComparison.OrdinalIgnoreCase) ||
                         ex.Message.Contains("didn’t respond like T3 Code", StringComparison.OrdinalIgnoreCase) ||
                         ex.Message.Contains("sent Deskchirp to a different address", StringComparison.OrdinalIgnoreCase)
                ? ex.Message
                : "Check that T3 Code is running and the address is right.";

            if (priorStatus == T3ConnectionStatus.Connected && priorToken != null && !priorToken.IsExpired)
            {
                SetStatus(T3ConnectionStatus.Connected, "Sign-in didn’t finish. Your existing connection wasn’t changed.");
            }
            else
            {
                SetStatus(T3ConnectionStatus.CouldNotReach, msg);
            }
            return false;
            }
            finally { _authLock.Release(); }
        }
    }

    public async Task CancelSignInAsync()
    {
        if (_connectionStatus == T3ConnectionStatus.OpeningSignIn)
        {
            Interlocked.Increment(ref _authGeneration);
            _signInCts?.Cancel();
            SetStatus(_storedToken is { IsExpired: false } ? T3ConnectionStatus.Connected : T3ConnectionStatus.NotConnected,
                _storedToken is { IsExpired: false }
                    ? "Sign-in was cancelled. Your existing connection wasn’t changed."
                    : "Sign-in was cancelled.");
            await Task.CompletedTask;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _authGeneration);
        Invalidate();
        _signInCts?.Cancel();

        await _authLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try { await _credentialStore.DeleteCredentialAsync(CredentialTarget, CredentialUsername, ct).ConfigureAwait(false); }
            catch
            {
                SetStatus(_storedToken is { IsExpired: false } ? T3ConnectionStatus.Connected : T3ConnectionStatus.NotConnected,
                    "Couldn’t remove your saved sign-in. Try disconnecting again.");
                return;
            }
            _storedToken = null;
            ClearSnapshot();
            _leaderTracker.Reset();
            SetStatus(T3ConnectionStatus.NotConnected, "To revoke access in T3 too, open T3 Code Settings → Sharing & access.");
        }
        finally
        {
            _authLock.Release();
        }
    }

    public async Task<T3Snapshot?> PollOnceAsync(CancellationToken ct = default)
    {
        var pollToken = _storedToken;
        if (_connectionStatus != T3ConnectionStatus.Connected || pollToken == null ||
            !T3UrlValidator.TryNormalizeT3Address(_settingsService.Settings.T3Address, out var selectedEndpoint, out _) ||
            !string.Equals(selectedEndpoint!.AbsoluteUri, pollToken.McpUri, StringComparison.Ordinal))
        {
            return null;
        }

        if (pollToken.IsExpired)
        {
            ClearSnapshot();
            SetStatus(T3ConnectionStatus.SignInAgain, "Your T3 Code sign-in expired. Connect again to keep showing activity.");
            return null;
        }

        if (!Uri.TryCreate(pollToken.McpUri, UriKind.Absolute, out var mcpUri))
        {
            return null;
        }

        if (!await _pollLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            // Do not overlap polls
            return _currentSnapshot;
        }

        long pollGen = Interlocked.Read(ref _pollGeneration);

        try
        {
            var capabilities = await _mcpClient.GetCapabilitiesAsync(mcpUri, pollToken.AccessToken, ct).ConfigureAwait(false);

            var allThreads = new List<T3ThreadListItem>();
            int? nextCursor = null;
            int pages = 0;

            do
            {
                ct.ThrowIfCancellationRequested();
                pages++;

                if (pages > MaxPollPages)
                {
                    // Capped pages exceeded: reject partial snapshot immediately
                    ClearSnapshot();
                    return null;
                }

                var listResult = await _mcpClient.ListThreadsAsync(mcpUri, pollToken.AccessToken, nextCursor, ct).ConfigureAwait(false);
                if (listResult.Threads != null)
                {
                    allThreads.AddRange(listResult.Threads);
                }

                if (allThreads.Count > MaxPollThreads)
                {
                    // Capped rows exceeded: reject partial snapshot immediately
                    ClearSnapshot();
                    return null;
                }

                nextCursor = listResult.NextCursor;
            } while (nextCursor.HasValue);

            // Generation guard: stop late counts after disconnect, focus change, or pause
            if (pollGen != Interlocked.Read(ref _pollGeneration) || ct.IsCancellationRequested)
            {
                return null;
            }

            lock (_stateLock)
            {
            if (pollGen != Interlocked.Read(ref _pollGeneration) || ct.IsCancellationRequested) return null;
            var now = DateTimeOffset.UtcNow;
            var workloads = T3WorkloadAggregator.AggregateThreads(capabilities.Providers, allThreads);
            var winner = _leaderTracker.SelectLeader(workloads, now);
            string? state = winner != null ? T3WorkloadAggregator.FormatState(winner) : null;
            string details = winner != null ? $"T3 Code · {winner.HarnessName}" : "Using T3 Code";

            var snapshot = new T3Snapshot
            {
                Timestamp = now,
                IsValid = true,
                Workloads = workloads,
                WinningHarness = winner,
                WinningState = state,
                Details = details,
                TotalThreadsObserved = allThreads.Count
            };

            bool changed = !T3Snapshot.AreEquivalent(_currentSnapshot, snapshot);
            _currentSnapshot = snapshot;
            if (changed)
            {
                SnapshotUpdated?.Invoke(snapshot);
            }
            return snapshot;
            }
        }
        catch (T3AuthenticationException)
        {
            if (pollGen != Interlocked.Read(ref _pollGeneration)) return null;
            ClearSnapshot();
            SetStatus(T3ConnectionStatus.SignInAgain, "Your T3 Code sign-in expired. Connect again to keep showing activity.");
            return null;
        }
        catch (OperationCanceledException)
        {
            return _currentSnapshot;
        }
        catch (Exception)
        {
            if (pollGen != Interlocked.Read(ref _pollGeneration)) return null;
            // Poll failure clears snapshot immediately (do not retain until TTL)
            ClearSnapshot();
            SetStatus(T3ConnectionStatus.Connected, "T3 activity isn’t available right now, so only the app name is shown.");
            return null;
        }
        finally
        {
            _pollLock.Release();
        }
    }

    public void ClearSnapshot()
    {
        lock (_stateLock)
        {
            if (_currentSnapshot == null) return;
            _currentSnapshot = null;
            SnapshotUpdated?.Invoke(null);
        }
    }

    /// <summary>
    /// Invalidates active polling and clears snapshots on focus, pause, or eligibility changes.
    /// Does NOT kill or invalidate an in-flight OAuth sign-in.
    /// </summary>
    public void Invalidate()
    {
        lock (_stateLock)
        {
            Interlocked.Increment(ref _pollGeneration);
            ClearSnapshot();
            _leaderTracker.Reset();
        }
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

        Interlocked.Increment(ref _authGeneration);
        Invalidate();
        _signInCts?.Cancel();
        _signInCts?.Dispose();
        await _mcpClient.DisposeAsync().ConfigureAwait(false);
        _pollLock.Dispose();
        _authLock.Dispose();
    }
}
