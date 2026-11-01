using DeveloperPresence.Core.Discord;
using DeveloperPresence.Core.Foreground;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;
using DeveloperPresence.Core.T3;
using DeveloperPresence.Core.Computers;
using DeveloperPresence.Core.T3.Usage;

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
    private readonly IT3UsageService? _usageService;
    private readonly WorkSessionTimer _timer = new();
    private readonly T3LeaderTracker _cardLeader = new();
    private string? _aliasProject;
    private CancellationTokenSource? _usagePollCts;
    private DateTimeOffset _lastUsagePollTimestamp = DateTimeOffset.MinValue;
    private bool _wasUsageEligible;

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
        IComputerLinkService? computers = null,
        IT3UsageService? usageService = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _discordClient = discordClient ?? throw new ArgumentNullException(nameof(discordClient));
        _foregroundDetector = foregroundDetector ?? throw new ArgumentNullException(nameof(foregroundDetector));
        _t3ActivityService = t3ActivityService;
        _computers = computers;
        _usageService = usageService;
        if (_usageService != null) _usageService.Changed += OnUsageChanged;
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
        if (_usageService != null)
        {
            await _usageService.InitializeAsync(ct).ConfigureAwait(false);
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
        StopActivityClocks();
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
        StopActivityClocks();
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
        StopActivityClocks();
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
                StopActivityClocks();
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
                    s.PreviewRows = Array.Empty<PresencePreviewRow>();
                    s.FitWarnings = Array.Empty<string>();
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
                _timer.EndSession();
            }
            else if (isT3Eligible)
            {
                _wasT3Eligible = true;
            }

            if (isT3Eligible && _t3ActivityService?.AnyEnvironmentConnected == true &&
                DateTimeOffset.UtcNow - _lastT3PollTimestamp >= TimeSpan.FromSeconds(15))
            {
                _lastT3PollTimestamp = DateTimeOffset.UtcNow;
                BeginT3Poll();
            }
            SupportedApp displayedApp = ChooseDisplayedApp(focusedApp, t3Running, settings);
            var now = DateTimeOffset.UtcNow;
            _timer.ObserveShownApp(displayedApp, now);
            var t3Snapshot = _t3ActivityService?.AnyEnvironmentConnected == true ? _t3ActivityService.CurrentSnapshot : null;
            bool freshWork = isT3Eligible && PresenceCardFormatter.HasFreshUnfinishedWork(settings, t3Snapshot, now);
            _timer.Observe(freshWork, now);
            UpdateUsagePolling(isT3Eligible, settings, t3Snapshot);
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
                    s.PreviewRows = Array.Empty<PresencePreviewRow>();
                    s.FitWarnings = Array.Empty<string>();
                    s.StatusMessage = "No supported developer app in foreground";
                });
                return;
            }

            // One allowlisted formatter builds every card (spec/presence-v3.md §5).
            bool richAllowed = settings.SelectionMode == SelectionMode.KeepBackgroundVisible ||
                focusedApp == SupportedApp.T3Code ||
                (focusedApp == SupportedApp.None &&
                 !_foregroundDetector.IsAppRunning(SupportedApp.Claude, settings.ProcessMappings) &&
                 !_foregroundDetector.IsAppRunning(SupportedApp.Codex, settings.ProcessMappings));
            bool t3Connected = _t3ActivityService?.AnyEnvironmentConnected == true;
            if (displayedApp == SupportedApp.T3Code && settings.ShowT3Activity && t3Connected &&
                now - _lastT3PollTimestamp >= TimeSpan.FromSeconds(15))
            {
                // Poll no more often than every 15 seconds, including on failures
                _lastT3PollTimestamp = now;
                BeginT3Poll();
            }
            var card = PresenceCardFormatter.Format(new PresenceCardInput
            {
                Settings = settings,
                Snapshot = isT3Eligible && t3Connected ? t3Snapshot : null,
                Usage = _usageService?.CurrentSnapshot,
                UsagePermitted = _usageService?.HasAnyPermission == true,
                Timer = _timer.State,
                App = displayedApp,
                Now = now,
                RichAllowed = richAllowed,
                Leader = _cardLeader,
                PreviousAliasProject = _aliasProject
            });
            _aliasProject = card.AliasProjectKey ?? _aliasProject;
            DiscordActivity? activity = card.Activity;
            string headline = activity?.Details ?? ActivityFormatter.GetHeadline(displayedApp);
            string? outgoingState = activity?.State;
            string? activityNote = null;
            if (displayedApp == SupportedApp.T3Code && settings.ShowT3Activity && settings.CardStyle != CardStyle.AppOnly)
            {
                bool fresh = PresenceCardFormatter.IsSnapshotFresh(t3Snapshot, now);
                activityNote = !t3Connected ? "Connect T3 Code to show agent activity."
                    : !richAllowed ? "T3 work shows while T3 Code is the active app."
                    : card.IsRich && settings.CardStyle == CardStyle.Usage && !card.UsageShown ? T3UsageText.Unavailable
                    : card.IsRich ? null
                    : fresh ? "No tasks right now, so only the app name is shown."
                    : "T3 activity isn’t available right now, so only the app name is shown.";
            }

            activity = DiscordArtwork.Apply(activity, displayedApp, settings.ShowAppIcons);
            UpdateState(s =>
            {
                s.ForegroundApp = focusedApp;
                s.OutgoingHeadline = headline;
                s.OutgoingState = outgoingState;
                s.ActivityNote = activityNote;
                s.PreviewRows = card.Rows;
                s.FitWarnings = card.FitWarnings;
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
        if (settings.SelectionMode == SelectionMode.FollowActiveApp)
        {
            // The active supported app always wins; T3 shows when nothing else is active.
            return foreground != SupportedApp.None ? foreground : t3Running ? SupportedApp.T3Code : SupportedApp.None;
        }
        // keepBackgroundVisible: fresh unfinished T3 work wins over another foreground app.
        bool hasWork = settings.CardStyle != CardStyle.AppOnly &&
            PresenceCardFormatter.HasFreshUnfinishedWork(settings, _t3ActivityService?.CurrentSnapshot, DateTimeOffset.UtcNow);
        return t3Running && (hasWork || foreground == SupportedApp.None) ? SupportedApp.T3Code : foreground;
    }

    /// <summary>Usage polls run every 60 seconds only while the usage card is chosen and eligible.</summary>
    private void UpdateUsagePolling(bool t3Eligible, PresenceSettings settings, T3Snapshot? snapshot)
    {
        if (_usageService == null) return;
        bool eligible = t3Eligible && settings.CardStyle == CardStyle.Usage &&
            _t3ActivityService?.AnyEnvironmentConnected == true && _usageService.HasAnyPermission;
        if (!eligible)
        {
            if (_wasUsageEligible)
            {
                _wasUsageEligible = false;
                CancelUsagePoll();
                _usageService.Invalidate();
                _lastUsagePollTimestamp = DateTimeOffset.MinValue;
            }
            return;
        }
        _wasUsageEligible = true;
        var now = DateTimeOffset.UtcNow;
        if (PresenceCardFormatter.EffectiveScope(settings) == UsageScope.Session)
        {
            // Session usage belongs to one work session. Drop numbers from an earlier (or ended) session
            // and ask again right away for the new one.
            var current = _usageService.CurrentSnapshot;
            if (current != null && (current.Session != null || current.SessionStart != null) && current.SessionStart != _timer.WorkSessionStart)
            {
                CancelUsagePoll();
                _usageService.Invalidate();
                _lastUsagePollTimestamp = DateTimeOffset.MinValue;
            }
            // Session usage needs both a session and fresh work; wait for them rather than spending the interval.
            if (_timer.WorkSessionStart == null || snapshot == null) return;
        }
        if (now - _lastUsagePollTimestamp < T3UsageService.PollInterval) return;
        _lastUsagePollTimestamp = now;
        var request = new T3UsagePollRequest { Settings = settings, Snapshot = snapshot, WorkSessionStart = _timer.WorkSessionStart };
        CancellationTokenSource source;
        lock (_operationLock)
        {
            if (_usagePollCts != null) return;
            source = CancellationTokenSource.CreateLinkedTokenSource(_loopCts?.Token ?? CancellationToken.None);
            _usagePollCts = source;
        }
        _ = UsagePollInBackgroundAsync(source, request);
    }

    private async Task UsagePollInBackgroundAsync(CancellationTokenSource source, T3UsagePollRequest request)
    {
        try { await _usageService!.PollOnceAsync(request, source.Token).ConfigureAwait(false); }
        catch { /* Usage stays unavailable; the card falls back to the workload line. */ }
        finally
        {
            lock (_operationLock) { if (ReferenceEquals(_usagePollCts, source)) _usagePollCts = null; }
            source.Dispose();
        }
    }

    private void CancelUsagePoll()
    {
        lock (_operationLock) { _usagePollCts?.Cancel(); }
    }

    /// <summary>Activity sharing stopped: end the work session and stop usage.</summary>
    private void StopActivityClocks()
    {
        _timer.Reset();
        _cardLeader.Reset();
        if (_wasUsageEligible)
        {
            _wasUsageEligible = false;
            CancelUsagePoll();
            _usageService?.Invalidate();
            _lastUsagePollTimestamp = DateTimeOffset.MinValue;
        }
    }

    private void OnUsageChanged() => TriggerImmediateEvaluation();

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

    /// <summary>
    /// Starts one polling round. Rounds share a cancellation source but never wait for each other:
    /// the service polls each environment under its own lock, so a slow environment skips only its
    /// own next turn while healthy environments keep fresh snapshots.
    /// </summary>
    private void BeginT3Poll()
    {
        CancellationToken token;
        lock (_operationLock)
        {
            if (_t3ActivityService == null) return;
            if (_t3PollCts == null || _t3PollCts.IsCancellationRequested)
                _t3PollCts = CancellationTokenSource.CreateLinkedTokenSource(_loopCts?.Token ?? CancellationToken.None);
            token = _t3PollCts.Token;
        }
        _ = PollInBackgroundAsync(token);
    }

    private async Task PollInBackgroundAsync(CancellationToken token)
    {
        try { await _t3ActivityService!.PollOnceAsync(token).ConfigureAwait(false); }
        catch { /* The service clears failed snapshots; the next attempt waits 15 seconds. */ }
    }

    private void CancelT3Poll()
    {
        lock (_operationLock)
        {
            // Cancel in-flight rounds; the next round gets a fresh source. Not disposed here
            // because background rounds may still observe the token.
            _t3PollCts?.Cancel();
            _t3PollCts = null;
        }
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
        _cardLeader.Reset();
        // Changed exclusions, pins or scope must never reuse numbers computed under the old settings.
        CancelUsagePoll();
        _usageService?.Invalidate();
        _lastUsagePollTimestamp = DateTimeOffset.MinValue;
        _lastT3PollTimestamp = DateTimeOffset.MinValue;
        if (!settings.ShowT3Activity) _timer.EndSession();
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
        if (_usageService != null)
        {
            _usageService.Changed -= OnUsageChanged;
            await _usageService.DisposeAsync().ConfigureAwait(false);
        }

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
