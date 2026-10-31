using DeveloperPresence.Core.Discord;
using DeveloperPresence.Core.Foreground;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;
using DeveloperPresence.Core.T3;
using DeveloperPresence.Core.Computers;

namespace DeveloperPresence.Core.Services;

/// <summary>
/// Central coordinator orchestrating foreground app detection, privacy filtering,
/// rate-limited Discord updates, session locks, power suspensions, settings persistence,
/// and T3 agent workload polling.
/// </summary>
public sealed class PresenceCoordinator : IAsyncDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IDiscordIpcClient _discordClient;
    private readonly IForegroundDetector _foregroundDetector;
    private readonly IT3ActivityService? _t3ActivityService;
    private readonly IComputerLinkService? _computers;

    private readonly SemaphoreSlim _evalLock = new(1, 1);
    private readonly SemaphoreSlim _triggerSignal = new(0, 1);
    private readonly object _stateLock = new();
    private readonly object _operationLock = new();
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _t3PollCts;

    private readonly PresenceState _currentState = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    private DateTimeOffset _lastSentTimestamp = DateTimeOffset.MinValue;
    private SupportedApp _lastSentApp = SupportedApp.None;
    private DiscordActivity? _lastSentActivity;
    private DateTimeOffset _lastT3PollTimestamp = DateTimeOffset.MinValue;

    private string? _connectedClientId;
    private bool _connectionNeedsPublish;
    private long _currentGeneration;

    private bool _isStarted;
    private bool _isSessionLocked;
    private bool _isSystemSuspended;
    private bool _isDisposed;
    private bool _wasT3Eligible;

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
        : this(settingsService, discordClient, foregroundDetector, null)
    {
    }

    public PresenceCoordinator(
        ISettingsService settingsService,
        IDiscordIpcClient discordClient,
        IForegroundDetector foregroundDetector,
        IT3ActivityService? t3ActivityService,
        IComputerLinkService? computers = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        _foregroundDetector = foregroundDetector ?? throw new ArgumentNullException(nameof(foregroundDetector));
        _t3ActivityService = t3ActivityService;
        _computers = computers;
        _discordClient.PublishAllowed = () => !_settingsService.Settings.FollowComputers || _computers?.CanPublish == true;
        if (_computers != null) _computers.Changed += OnComputerChanged;

        _discordClient.StatusChanged += OnDiscordStatusChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;

        if (_t3ActivityService != null)
        {
            _t3ActivityService.StatusChanged += OnT3StatusChanged;
            _t3ActivityService.SnapshotUpdated += OnT3SnapshotUpdated;
        }
    }

    /// <summary>
    /// Starts the coordinator. Loads privacy preferences before any presence actions.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_isStarted) return;
        // Strict requirement: load saved privacy settings BEFORE any sends or connections
        await _settingsService.LoadSettingsAsync().ConfigureAwait(false);

        if (_computers != null) await _computers.InitializeAsync(ct).ConfigureAwait(false);

        if (_t3ActivityService != null)
        {
            await _t3ActivityService.InitializeAsync(ct).ConfigureAwait(false);
        }

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
        _computers?.UpdateContext(false, null);
        InvalidateOperation();
        if (_wasT3Eligible)
        {
            _wasT3Eligible = false;
            CancelT3Poll();
                    _t3ActivityService?.Invalidate();
        }
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
        if (_computers != null) await _computers.StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Invoked when Windows reports WTS_SESSION_LOCK.
    /// Immediately cancels pending sends and clears Discord presence, closing socket.
    /// </summary>
    public void OnSessionLock()
    {
        _isSessionLocked = true;
        _computers?.UpdateContext(false, null);
        InvalidateOperation();
        if (_wasT3Eligible)
        {
            _wasT3Eligible = false;
            CancelT3Poll();
                    _t3ActivityService?.Invalidate();
        }

        _ = ClearAndDropConnectionAsync();

        UpdateState(state =>
        {
            state.OutgoingHeadline = ActivityFormatter.HeadlineNone;
            state.OutgoingState = null;
            state.ActivityNote = null;
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
        _computers?.UpdateContext(false, null);
        InvalidateOperation();
        if (_wasT3Eligible)
        {
            _wasT3Eligible = false;
            CancelT3Poll();
                    _t3ActivityService?.Invalidate();
        }

        _ = ClearAndDropConnectionAsync();

        UpdateState(state =>
        {
            state.OutgoingHeadline = ActivityFormatter.HeadlineNone;
            state.OutgoingState = null;
            state.ActivityNote = null;
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
                _computers?.UpdateContext(false, null);
                Interlocked.Increment(ref _currentGeneration);
                if (_wasT3Eligible)
                {
                    _wasT3Eligible = false;
                    CancelT3Poll();
                    _t3ActivityService?.Invalidate();
                }

                if (_lastSentApp != SupportedApp.None || _lastSentActivity != null || _discordClient.IsConnected)
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
                    _lastSentActivity = null;
                    _connectedClientId = null;
                }

                UpdateState(s =>
                {
                    s.ForegroundApp = SupportedApp.None;
                    s.OutgoingHeadline = ActivityFormatter.HeadlineNone;
                    s.OutgoingState = null;
                    s.ActivityNote = null;
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

            if (settings.FollowComputers)
            {
                // Even with Discord closed, recent use here must suppress an unattended peer.
                _computers?.UpdateContext(true, _discordClient.CurrentUserId);
                // A standby connection reads READY account identity but sends no activity.
                if (!_discordClient.IsConnected)
                {
                    if (!await _discordClient.ConnectAsync(settings.DiscordClientId, token).ConfigureAwait(false))
                    { _computers?.UpdateContext(true, null); return; }
                    _connectedClientId = settings.DiscordClientId;
                    _connectionNeedsPublish = true;
                }
                _computers?.UpdateContext(true, _discordClient.CurrentUserId);
                if (_computers?.CanPublish != true)
                {
                    if (_wasT3Eligible) { _wasT3Eligible = false; CancelT3Poll(); _t3ActivityService?.Invalidate(); }
                    if (_lastSentActivity != null || _lastSentApp != SupportedApp.None)
                        await ClearAndDropConnectionAsync().ConfigureAwait(false);
                    _lastSentApp = SupportedApp.None; _lastSentActivity = null;
                    UpdateState(s => {
                        s.ForegroundApp = focusedApp; s.OutgoingHeadline = ActivityFormatter.HeadlineNone;
                        s.OutgoingState = null; s.ActivityNote = null;
                        s.ComputerStatus = _computers?.Status ?? "Waiting for a connection";
                    });
                    return;
                }
            }

            bool t3Running = focusedApp == SupportedApp.T3Code || _foregroundDetector.IsAppRunning(SupportedApp.T3Code, settings.ProcessMappings);
            bool isT3Eligible = isSharingAllowed && t3Running && settings.ShowT3Activity;
            if (_wasT3Eligible && !isT3Eligible)
            {
                _wasT3Eligible = false;
                CancelT3Poll();
                    _t3ActivityService?.Invalidate();
            }
            else if (isT3Eligible)
            {
                _wasT3Eligible = true;
            }

            if (isT3Eligible && _t3ActivityService?.ConnectionStatus == T3ConnectionStatus.Connected &&
                DateTimeOffset.UtcNow - _lastT3PollTimestamp >= TimeSpan.FromSeconds(15))
            {
                _lastT3PollTimestamp = DateTimeOffset.UtcNow;
                BeginT3Poll();
            }
            SupportedApp displayedApp = ChooseDisplayedApp(focusedApp, t3Running, settings);
            if (displayedApp == SupportedApp.None)
            {
                // Keep a linked standby connection for account identity. Unsupported apps
                // still select this computer, but never publish developer activity.
                if (!settings.FollowComputers || _lastSentActivity != null || _lastSentApp != SupportedApp.None)
                    await ClearAndDropConnectionAsync().ConfigureAwait(false);
                _lastSentApp = SupportedApp.None;
                _lastSentActivity = null;
                _connectedClientId = null;
                UpdateState(s =>
                {
                    s.ForegroundApp = SupportedApp.None;
                    s.OutgoingHeadline = ActivityFormatter.HeadlineNone;
                    s.OutgoingState = null;
                    s.ActivityNote = null;
                    s.StatusMessage = "No supported developer app in foreground";
                });
                return;
            }

            // Handle T3 Code polling and rich workload formatting
            DiscordActivity? activity = null;
            string headline;
            string? outgoingState = null;
            string? activityNote = null;

            if (displayedApp == SupportedApp.T3Code)
            {
                if (settings.ShowT3Activity)
                {
                    if (_t3ActivityService != null && _t3ActivityService.ConnectionStatus == T3ConnectionStatus.Connected)
                    {
                        // Poll no more often than every 15 seconds while awake and T3 is foreground, including on failures
                        var now = DateTimeOffset.UtcNow;
                        if ((now - _lastT3PollTimestamp) >= TimeSpan.FromSeconds(15))
                        {
                            _lastT3PollTimestamp = now;
                            BeginT3Poll();
                        }

                        var snapshot = _t3ActivityService.CurrentSnapshot;
                        bool isValid = snapshot != null && snapshot.IsValid && (DateTimeOffset.UtcNow - snapshot.Timestamp) <= T3ActivityService.SnapshotTtl;

                        if (isValid && snapshot!.WinningHarness != null)
                        {
                            activity = ActivityFormatter.FormatT3Activity(snapshot, true, false, true, settings.ShowAppIcons);
                            headline = activity?.Details ?? ActivityFormatter.HeadlineT3Code;
                            outgoingState = activity?.State;
                            activityNote = null;
                        }
                        else if (isValid && snapshot!.WinningHarness == null)
                        {
                            activity = ActivityFormatter.FormatT3Activity(null, true, false, false, settings.ShowAppIcons);
                            headline = ActivityFormatter.HeadlineT3Code;
                            outgoingState = null;
                            activityNote = "No tasks right now, so only the app name is shown.";
                        }
                        else
                        {
                            activity = ActivityFormatter.FormatT3Activity(null, true, false, false, settings.ShowAppIcons);
                            headline = ActivityFormatter.HeadlineT3Code;
                            outgoingState = null;
                            activityNote = "T3 activity isn’t available right now, so only the app name is shown.";
                        }
                    }
                    else
                    {
                        activity = ActivityFormatter.FormatT3Activity(null, true, false, false, settings.ShowAppIcons);
                        headline = ActivityFormatter.HeadlineT3Code;
                        outgoingState = null;
                        activityNote = "Connect T3 Code to show agent activity.";
                    }
                }
                else
                {
                    activity = ActivityFormatter.FormatT3Activity(null, true, false, false, settings.ShowAppIcons);
                    headline = ActivityFormatter.HeadlineT3Code;
                    outgoingState = null;
                    activityNote = null;
                }
            }
            else
            {
                // Claude / Codex desktop: T3 activity is never published to other apps
                activity = ActivityFormatter.FormatActivity(displayedApp, true, false);
                headline = ActivityFormatter.GetHeadline(displayedApp);
                outgoingState = null;
                activityNote = null;
            }

            activity = DiscordArtwork.Apply(activity, displayedApp, settings.ShowAppIcons);
            UpdateState(s =>
            {
                s.ForegroundApp = focusedApp;
                s.OutgoingHeadline = headline;
                s.OutgoingState = outgoingState;
                s.ActivityNote = activityNote;
            });

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
                                    genBeforeConnect == Interlocked.Read(ref _currentGeneration) &&
                                    (!freshSettings.FollowComputers || _computers?.CanPublish == true);

                if (!stillAllowed)
                {
                    // Privacy invalidated during connect: drop socket immediately
                    await _discordClient.CloseAsync().ConfigureAwait(false);
                    _connectedClientId = null;
                    return;
                }

                SupportedApp freshFocus = _foregroundDetector.DetectForegroundApp(freshSettings.ProcessMappings);
                bool freshT3Running = freshFocus == SupportedApp.T3Code || _foregroundDetector.IsAppRunning(SupportedApp.T3Code, freshSettings.ProcessMappings);
                SupportedApp freshApp = ChooseDisplayedApp(freshFocus, freshT3Running, freshSettings);
                if (freshApp == SupportedApp.None)
                {
                    await ClearAndDropConnectionAsync().ConfigureAwait(false);
                    _lastSentApp = SupportedApp.None;
                    _lastSentActivity = null;
                    return;
                }

                if (freshApp != displayedApp)
                {
                    // Focus changed during connect; retry evaluation immediately
                    TriggerImmediateEvaluation();
                    return;
                }
            }

            // Respect minimum throttle interval between Discord updates
            var elapsed = DateTimeOffset.UtcNow - _lastSentTimestamp;
            double throttleLimitSeconds = Math.Max(5.0, settings.UpdateIntervalSeconds);

            // Same-app count republish: detect when activity details, state, or icons change
            bool activityChanged = !DiscordActivity.Equals(activity, _lastSentActivity);
            bool needsPublish = _connectionNeedsPublish || activityChanged;

            if (!needsPublish)
            {
                return;
            }

            if (_connectionNeedsPublish || elapsed.TotalSeconds >= throttleLimitSeconds)
            {
                // Rate limit satisfied or fresh connection: send immediately
                token.ThrowIfCancellationRequested();
                if (settings.FollowComputers && _computers?.CanPublish != true) return;

                if (activity != null)
                {
                    await _discordClient.SendActivityAsync(activity, token).ConfigureAwait(false);
                }
                else
                {
                    await _discordClient.SendActivityAsync(displayedApp, token).ConfigureAwait(false);
                }

                _lastSentTimestamp = DateTimeOffset.UtcNow;
                _lastSentApp = displayedApp;
                _lastSentActivity = activity;
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

    private SupportedApp ChooseDisplayedApp(SupportedApp foreground, bool t3Running, PresenceSettings settings)
    {
        var snapshot = _t3ActivityService?.CurrentSnapshot;
        var age = snapshot == null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - snapshot.Timestamp;
        bool hasWork = settings.ShowT3Activity && snapshot?.IsValid == true &&
            snapshot.WinningHarness?.TotalActive > 0 &&
            age >= TimeSpan.Zero && age <= T3ActivityService.SnapshotTtl;
        return t3Running && (hasWork || foreground == SupportedApp.None) ? SupportedApp.T3Code : foreground;
    }

    private void OnDiscordStatusChanged(PresenceConnectionStatus status, string? message)
    {
        if (status != PresenceConnectionStatus.Connected)
        {
            var settings = _settingsService.Settings;
            _computers?.UpdateContext(settings.EnableSharing && !settings.IsPaused &&
                !_isSessionLocked && !_isSystemSuspended && settings.IsValidDiscordClientId(), null);
        }
        UpdateState(s =>
        {
            s.ConnectionStatus = status;
            if (!string.IsNullOrEmpty(message))
            {
                s.StatusMessage = message;
            }
        });
    }

    private void BeginT3Poll()
    {
        CancellationTokenSource source;
        lock (_operationLock)
        {
            if (_t3PollCts != null || _t3ActivityService == null) return;
            source = CancellationTokenSource.CreateLinkedTokenSource(_loopCts?.Token ?? CancellationToken.None);
            _t3PollCts = source;
        }
        _ = PollInBackgroundAsync(source);
    }

    private async Task PollInBackgroundAsync(CancellationTokenSource source)
    {
        try { await _t3ActivityService!.PollOnceAsync(source.Token).ConfigureAwait(false); }
        catch { /* The service clears failed snapshots; the next attempt waits 15 seconds. */ }
        finally
        {
            lock (_operationLock)
            {
                if (ReferenceEquals(_t3PollCts, source)) _t3PollCts = null;
            }
            source.Dispose();
        }
    }

    private void CancelT3Poll()
    {
        lock (_operationLock) { _t3PollCts?.Cancel(); }
    }

    private void OnT3StatusChanged(T3ConnectionStatus status, string detail)
    {
        UpdateState(s =>
        {
            s.T3ConnectionStatus = status;
            s.T3StatusDetail = detail;
        });
    }

    private void OnT3SnapshotUpdated(T3Snapshot? snapshot)
    {
        if (_wasT3Eligible || snapshot != null || _lastSentApp == SupportedApp.T3Code)
        {
            TriggerImmediateEvaluation();
        }
    }

    private void OnSettingsChanged(PresenceSettings settings)
    {
        if (!_isStarted) return;
        _computers?.UpdateContext(false, null);
        InvalidateOperation();
        CancelT3Poll();
        _t3ActivityService?.Invalidate();
        // Capture and close the old socket now, before a worker can create its replacement.
        _ = ClearAndDropConnectionAsync();
        TriggerImmediateEvaluation();
    }

    private void OnComputerChanged()
    {
        if (!_isStarted) return;
        UpdateState(s => s.ComputerStatus = _computers?.Status ?? "Waiting for a connection");
        if (_settingsService.Settings.FollowComputers && _computers?.CanPublish != true)
        {
            if (_lastSentActivity != null || _lastSentApp != SupportedApp.None)
            {
                InvalidateOperation();
                _ = ClearAndDropConnectionAsync();
            }
        }
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
        if (_computers != null) _computers.Changed -= OnComputerChanged;

        if (_t3ActivityService != null)
        {
            _t3ActivityService.StatusChanged -= OnT3StatusChanged;
            _t3ActivityService.SnapshotUpdated -= OnT3SnapshotUpdated;
            await _t3ActivityService.DisposeAsync().ConfigureAwait(false);
        }

        await StopAsync().ConfigureAwait(false);
        _evalLock.Dispose();
        _triggerSignal.Dispose();
    }
}
