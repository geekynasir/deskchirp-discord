using DeveloperPresence.Core.Discord;
using DeveloperPresence.Core.Foreground;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;

namespace DeveloperPresence.Core.Services;

/// <summary>
/// Central coordinator orchestrating foreground app detection, privacy filtering,
/// rate-limited Discord updates, session locks, power suspensions, and settings persistence.
/// </summary>
public sealed class PresenceCoordinator : IAsyncDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IDiscordIpcClient _discordClient;
    private readonly IForegroundDetector _foregroundDetector;
    private readonly SemaphoreSlim _evalLock = new(1, 1);
    private readonly SemaphoreSlim _triggerSignal = new(0, 1);
    private readonly object _stateLock = new();
    private readonly object _operationLock = new();
    private CancellationTokenSource? _operationCts;

    private readonly PresenceState _currentState = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    private DateTimeOffset _lastSentTimestamp = DateTimeOffset.MinValue;
    private SupportedApp _lastSentApp = SupportedApp.None;
    private string? _connectedClientId;
    private bool _connectionNeedsPublish;
    private long _currentGeneration;

    private bool _isStarted;
    private bool _isSessionLocked;
    private bool _isSystemSuspended;
    private bool _isDisposed;

    public event Action<PresenceState>? StateChanged;

    /// <summary>
    /// Thread-safe snapshot clone of the current presence state.
    /// </summary>
    public PresenceState CurrentState
    {
        get
        {
            lock (_stateLock)
            {
                return _currentState.Clone();
            }
        }
    }

    public bool IsSessionLocked => _isSessionLocked;
    public bool IsSystemSuspended => _isSystemSuspended;

    public PresenceCoordinator(
        ISettingsService settingsService,
        IDiscordIpcClient discordClient,
        IForegroundDetector foregroundDetector)
    {
        _settingsService = settingsService;
        _discordClient = discordClient;
        _foregroundDetector = foregroundDetector;

        _discordClient.StatusChanged += OnDiscordStatusChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>
    /// Starts the coordinator. Loads privacy preferences before any presence actions.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_isStarted) return;
        // Strict requirement: load saved privacy settings BEFORE any sends or connections
        await _settingsService.LoadSettingsAsync().ConfigureAwait(false);

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isStarted = true;
        _loopTask = Task.Run(() => CoordinationLoopAsync(_loopCts.Token), _loopCts.Token);

        TriggerImmediateEvaluation();
    }

    /// <summary>
    /// Stops the coordinator cleanly, clears active presence, and closes Discord IPC.
    /// Bounded wait ensures no hung process on exit.
    /// </summary>
    public async Task StopAsync()
    {
        _isStarted = false;
        InvalidateOperation();
        _loopCts?.Cancel();

        try
        {
            if (_discordClient.IsConnected)
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                try
                {
                    await _discordClient.ClearActivityAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch { }
            }
            await _discordClient.CloseAsync().ConfigureAwait(false);
        }
        catch { }

        if (_loopTask != null)
        {
            try
            {
                await _loopTask.WaitAsync(TimeSpan.FromMilliseconds(1000)).ConfigureAwait(false);
            }
            catch { }
        }

        _loopCts?.Dispose();
        _loopCts = null;
    }

    /// <summary>
    /// Invoked when Windows reports WTS_SESSION_LOCK.
    /// Immediately cancels pending sends and clears Discord presence, closing socket.
    /// </summary>
    public void OnSessionLock()
    {
        _isSessionLocked = true;
        InvalidateOperation();

        _ = ClearAndDropConnectionAsync();

        UpdateState(state =>
        {
            state.OutgoingHeadline = ActivityFormatter.HeadlineNone;
            state.StatusMessage = "Session locked (presence cleared)";
        });

        TriggerImmediateEvaluation();
    }

    /// <summary>
    /// Invoked when Windows reports WTS_SESSION_UNLOCK.
    /// </summary>
    public void OnSessionUnlock()
    {
        _isSessionLocked = false;
        TriggerImmediateEvaluation();
    }

    /// <summary>
    /// Invoked when Windows reports PBT_APMSUSPEND.
    /// Immediately clears presence and drops connection.
    /// </summary>
    public void OnSystemSuspend()
    {
        _isSystemSuspended = true;
        InvalidateOperation();

        _ = ClearAndDropConnectionAsync();

        UpdateState(state =>
        {
            state.OutgoingHeadline = ActivityFormatter.HeadlineNone;
            state.StatusMessage = "System suspended (presence cleared)";
        });

        TriggerImmediateEvaluation();
    }

    /// <summary>
    /// Invoked when Windows reports PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND.
    /// </summary>
    public void OnSystemResume()
    {
        _isSystemSuspended = false;
        TriggerImmediateEvaluation();
    }

    public void TriggerImmediateEvaluation()
    {
        if (_triggerSignal.CurrentCount == 0)
        {
            try
            {
                _triggerSignal.Release();
            }
            catch (SemaphoreFullException) { }
        }
    }

    private async Task CoordinationLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await EvaluateAndDispatchAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                UpdateState(s => s.LastError = ex.Message);
            }

            try
            {
                // Check focus every 1.5 seconds, or awake immediately if signaled
                await _triggerSignal.WaitAsync(1500, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task EvaluateAndDispatchAsync(CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        await _evalLock.WaitAsync(token).ConfigureAwait(false);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_operationLock) { _operationCts = operation; }
        token = operation.Token;
        try
        {
            var settings = _settingsService.Settings;

            // Detect if Client ID changed while connected
            if (!string.IsNullOrEmpty(_connectedClientId) &&
                !string.Equals(_connectedClientId, settings.DiscordClientId, StringComparison.Ordinal))
            {
                await _discordClient.CloseAsync().ConfigureAwait(false);
                _connectedClientId = null;
                _connectionNeedsPublish = true;
            }

            // Enforce master disable / pause / session lock / suspend guards
            bool isSharingAllowed = settings.EnableSharing &&
                                   !settings.IsPaused &&
                                   !_isSessionLocked &&
                                   !_isSystemSuspended &&
                                   settings.IsValidDiscordClientId();

            if (!isSharingAllowed)
            {
                Interlocked.Increment(ref _currentGeneration);

                if (_lastSentApp != SupportedApp.None || _discordClient.IsConnected)
                {
                    if (_discordClient.IsConnected)
                    {
                        try
                        {
                            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                            await _discordClient.ClearActivityAsync(timeoutCts.Token).ConfigureAwait(false);
                        }
                        catch { }
                        await _discordClient.CloseAsync().ConfigureAwait(false);
                    }
                    _lastSentApp = SupportedApp.None;
                    _connectedClientId = null;
                }

                UpdateState(s =>
                {
                    s.ForegroundApp = SupportedApp.None;
                    s.OutgoingHeadline = ActivityFormatter.HeadlineNone;
                    s.StatusMessage = !settings.EnableSharing ? "Sharing disabled" :
                                      settings.IsPaused ? "Presence paused" :
                                      _isSessionLocked ? "Session locked" :
                                      _isSystemSuspended ? "System suspended" :
                                      "Invalid Discord Application ID";
                });
                return;
            }

            // Detect current foreground application from exact process identity
            SupportedApp focusedApp = _foregroundDetector.DetectForegroundApp(settings.ProcessMappings);
            string headline = ActivityFormatter.GetHeadline(focusedApp);

            UpdateState(s =>
            {
                s.ForegroundApp = focusedApp;
                s.OutgoingHeadline = headline;
            });

            if (focusedApp == SupportedApp.None)
            {
                await ClearAndDropConnectionAsync().ConfigureAwait(false);
                _lastSentApp = SupportedApp.None;
                _connectedClientId = null;
                UpdateState(s => s.StatusMessage = "No supported developer app in foreground");
                return;
            }

            // Ensure Discord IPC connection is active
            if (!_discordClient.IsConnected)
            {
                long genBeforeConnect = Interlocked.Read(ref _currentGeneration);
                bool connected = await _discordClient.ConnectAsync(settings.DiscordClientId, token).ConfigureAwait(false);
                if (!connected)
                {
                    return;
                }

                _connectedClientId = settings.DiscordClientId;
                // A new connection resets duplicate suppression
                _connectionNeedsPublish = true;

                // Recheck privacy and focus after connect completes!
                var freshSettings = _settingsService.Settings;
                bool stillAllowed = freshSettings.EnableSharing &&
                                    !freshSettings.IsPaused &&
                                    !_isSessionLocked &&
                                    !_isSystemSuspended &&
                                    freshSettings.IsValidDiscordClientId() &&
                                    genBeforeConnect == Interlocked.Read(ref _currentGeneration);

                if (!stillAllowed)
                {
                    // Privacy invalidated during connect: drop socket immediately
                    await _discordClient.CloseAsync().ConfigureAwait(false);
                    _connectedClientId = null;
                    return;
                }

                SupportedApp freshApp = _foregroundDetector.DetectForegroundApp(freshSettings.ProcessMappings);
                if (freshApp == SupportedApp.None)
                {
                    await ClearAndDropConnectionAsync().ConfigureAwait(false);
                    _lastSentApp = SupportedApp.None;
                    return;
                }

                focusedApp = freshApp;
                headline = ActivityFormatter.GetHeadline(focusedApp);
            }

            // Respect 5-second minimum throttle between Discord updates
            var elapsed = DateTimeOffset.UtcNow - _lastSentTimestamp;
            double throttleLimitSeconds = Math.Max(5.0, settings.UpdateIntervalSeconds);

            bool needsPublish = _connectionNeedsPublish ||
                                (focusedApp != _lastSentApp);

            if (!needsPublish)
            {
                return;
            }

            if (_connectionNeedsPublish || elapsed.TotalSeconds >= throttleLimitSeconds)
            {
                // Rate limit satisfied or fresh connection: send immediately
                token.ThrowIfCancellationRequested();
                await _discordClient.SendActivityAsync(focusedApp, token).ConfigureAwait(false);
                _lastSentTimestamp = DateTimeOffset.UtcNow;
                _lastSentApp = focusedApp;
                _connectionNeedsPublish = false;

                UpdateState(s =>
                {
                    s.LastUpdate = _lastSentTimestamp;
                    s.StatusMessage = $"Updated: {headline}";
                });
            }
            else
            {
                // The single worker checks the latest focus again on its next tick.
                // No delayed task retains an old app or can outlive a privacy change.
                UpdateState(s => s.StatusMessage = $"Waiting to update: {headline}");
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        finally
        {
            lock (_operationLock)
            {
                if (ReferenceEquals(_operationCts, operation)) _operationCts = null;
            }
            _evalLock.Release();
        }
    }

    private void OnDiscordStatusChanged(PresenceConnectionStatus status, string? message)
    {
        UpdateState(s =>
        {
            s.ConnectionStatus = status;
            if (!string.IsNullOrEmpty(message))
            {
                s.StatusMessage = message;
            }
        });
    }

    private void OnSettingsChanged(PresenceSettings settings)
    {
        if (!_isStarted) return;
        InvalidateOperation();
        // Capture and close the old socket now, before a worker can create its replacement.
        _ = ClearAndDropConnectionAsync();
        TriggerImmediateEvaluation();
    }

    private void InvalidateOperation()
    {
        Interlocked.Increment(ref _currentGeneration);
        lock (_operationLock) { _operationCts?.Cancel(); }
    }

    private async Task ClearAndDropConnectionAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        Task clear = Task.CompletedTask;
        try
        {
            if (_discordClient.IsConnected) clear = _discordClient.ClearActivityAsync(timeout.Token);
        }
        catch { }
        // CloseAsync snapshots the old connection synchronously; it must not be queued in Task.Run.
        Task close = _discordClient.CloseAsync();
        try { await close.ConfigureAwait(false); } catch { }
        try { await clear.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); } catch { }
    }

    private void UpdateState(Action<PresenceState> updateAction)
    {
        PresenceState snapshot;
        lock (_stateLock)
        {
            updateAction(_currentState);
            snapshot = _currentState.Clone();
        }
        StateChanged?.Invoke(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _discordClient.StatusChanged -= OnDiscordStatusChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;

        await StopAsync().ConfigureAwait(false);
        _evalLock.Dispose();
        _triggerSignal.Dispose();
    }
}
