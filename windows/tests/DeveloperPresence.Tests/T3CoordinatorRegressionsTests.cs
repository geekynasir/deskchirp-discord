using DeveloperPresence.Core.Discord;
using DeveloperPresence.Core.Foreground;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;
using DeveloperPresence.Core.Services;
using DeveloperPresence.Core.T3;
using DeveloperPresence.Core.Credentials;
using Xunit;

namespace DeveloperPresence.Tests;

public class MockT3ActivityService : IT3ActivityService
{
    public T3ConnectionStatus ConnectionStatus { get; set; } = T3ConnectionStatus.Connected;
    /// <summary>When set, overrides "any environment connected" independently of the combined status.</summary>
    public bool? AnyConnectedOverride { get; set; }
    public bool AnyEnvironmentConnected => AnyConnectedOverride ?? ConnectionStatus == T3ConnectionStatus.Connected;
    public string StatusDetail { get; set; } = "Connected";
    public T3Snapshot? CurrentSnapshot { get; set; }
    public bool IsSnapshotValid => CurrentSnapshot != null && CurrentSnapshot.IsValid;

    public int InvalidateCallCount { get; set; }
    public int PollOnceCallCount { get; set; }
    public int ClearSnapshotCallCount { get; set; }

    public event Action<T3ConnectionStatus, string>? StatusChanged;
    public event Action<T3Snapshot?>? SnapshotUpdated;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> ConnectAsync(string rawAddress, CancellationToken ct = default) => Task.FromResult(true);
    public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task CancelSignInAsync() => Task.CompletedTask;

    public Task<T3Snapshot?> PollOnceAsync(CancellationToken ct = default)
    {
        PollOnceCallCount++;
        return Task.FromResult(CurrentSnapshot);
    }

    public void ClearSnapshot()
    {
        ClearSnapshotCallCount++;
        if (CurrentSnapshot != null)
        {
            CurrentSnapshot = null;
            SnapshotUpdated?.Invoke(null);
        }
    }

    public void Invalidate()
    {
        InvalidateCallCount++;
        ClearSnapshot();
    }

    public void TriggerSnapshotUpdate(T3Snapshot? snapshot)
    {
        CurrentSnapshot = snapshot;
        SnapshotUpdated?.Invoke(snapshot);
    }

    public void TriggerStatusChanged(T3ConnectionStatus status, string detail)
    {
        ConnectionStatus = status;
        StatusDetail = detail;
        StatusChanged?.Invoke(status, detail);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class T3CoordinatorRegressionsTests
{
    private const string ValidClientId = "123456789012345678";

    [Fact]
    public async Task DisabledOrUnsupported_DoesNotCallInvalidateRepeatedly()
    {
        var settingsService = new MockSettingsService();
        settingsService.Settings.EnableSharing = false; // Sharing disabled
        settingsService.Settings.DiscordClientId = ValidClientId;
        settingsService.Settings.ShowT3Activity = true;

        var discord = new MockDiscordClient();
        string focusedApp = "notepad.exe";
        var detector = new ForegroundDetector(() => focusedApp);
        var mockT3 = new MockT3ActivityService();

        var coordinator = new PresenceCoordinator(settingsService, discord, detector, mockT3);

        // Run 5 evaluation cycles while sharing is disabled
        for (int i = 0; i < 5; i++)
        {
            await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        }

        // Invalidate must NOT be called repeatedly on every tick
        Assert.Equal(0, mockT3.InvalidateCallCount);

        // Now enable sharing with an unsupported app (notepad)
        settingsService.Settings.EnableSharing = true;
        for (int i = 0; i < 5; i++)
        {
            await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        }

        // Still not eligible, must NOT call Invalidate on each evaluation
        Assert.Equal(0, mockT3.InvalidateCallCount);

        // Now transition to T3Code (becomes eligible)
        focusedApp = "t3code.exe";
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal(0, mockT3.InvalidateCallCount);

        // Now transition away from T3Code to Claude (transition from eligible to ineligible!)
        focusedApp = "claude.exe";
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal(1, mockT3.InvalidateCallCount); // Exactly 1 invalidation on transition

        // Run more evaluations while remaining on Claude
        for (int i = 0; i < 5; i++)
        {
            await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        }

        // Must still be 1! Idempotent: no repeated invalidations
        Assert.Equal(1, mockT3.InvalidateCallCount);
    }

    [Fact]
    public void ClearSnapshot_EmitsSnapshotUpdatedOnlyWhenSnapshotWasNotNull()
    {
        var store = new InMemoryCredentialStore();
        var settings = new MockSettingsService();
        using var client = new System.Net.Http.HttpClient();
        var mcpClient = new T3McpClient(client);
        var service = new T3ActivityService(settings, store, oauthClient: null, mcpClient: mcpClient);

        int updatesReceived = 0;
        service.SnapshotUpdated += snap => updatesReceived++;

        // Initially CurrentSnapshot is null. ClearSnapshot must be a no-op and NOT emit event
        service.ClearSnapshot();
        Assert.Equal(0, updatesReceived);

        service.Invalidate();
        Assert.Equal(0, updatesReceived);

        // Calling ClearSnapshot repeatedly must never emit or loop
        for (int i = 0; i < 10; i++)
        {
            service.ClearSnapshot();
        }
        Assert.Equal(0, updatesReceived);
    }

    [Fact]
    public void SnapshotEquivalence_DetectsActualStateChanges()
    {
        var t0 = DateTimeOffset.UtcNow;
        var w1 = new T3HarnessWorkload { HarnessName = "Antigravity", RunningTasks = 1 };
        var w2 = new T3HarnessWorkload { HarnessName = "Antigravity", RunningTasks = 1 };

        var snap1 = new T3Snapshot
        {
            Timestamp = t0,
            IsValid = true,
            Workloads = new[] { w1 },
            WinningHarness = w1,
            WinningState = "1 task",
            Details = "T3 Code · Antigravity",
            TotalThreadsObserved = 1
        };

        var snap2 = new T3Snapshot
        {
            Timestamp = t0.AddSeconds(15), // Different timestamp
            IsValid = true,
            Workloads = new[] { w2 },
            WinningHarness = w2,
            WinningState = "1 task",
            Details = "T3 Code · Antigravity",
            TotalThreadsObserved = 1
        };

        // Even with different timestamps, workload content is equivalent
        Assert.True(T3Snapshot.AreEquivalent(snap1, snap2));

        // When count changes, it is NOT equivalent
        var w3 = new T3HarnessWorkload { HarnessName = "Antigravity", RunningTasks = 2 };
        var snap3 = new T3Snapshot
        {
            Timestamp = t0.AddSeconds(15),
            IsValid = true,
            Workloads = new[] { w3 },
            WinningHarness = w3,
            WinningState = "2 tasks",
            Details = "T3 Code · Antigravity",
            TotalThreadsObserved = 2
        };

        Assert.False(T3Snapshot.AreEquivalent(snap1, snap3));
    }

    [Fact]
    public async Task PollFailure_Enforces15SecondBackoff_NoImmediateRetryStorm()
    {
        var settingsService = new MockSettingsService();
        settingsService.Settings.EnableSharing = true;
        settingsService.Settings.DiscordClientId = ValidClientId;
        settingsService.Settings.ShowT3Activity = true;

        var discord = new MockDiscordClient();
        var detector = new ForegroundDetector(() => "t3code.exe");
        var mockT3 = new MockT3ActivityService
        {
            ConnectionStatus = T3ConnectionStatus.Connected,
            CurrentSnapshot = null // Snapshot is invalid / poll fails
        };

        var coordinator = new PresenceCoordinator(settingsService, discord, detector, mockT3);

        // First evaluation: PollOnceAsync is called
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal(1, mockT3.PollOnceCallCount);

        // Immediate subsequent evaluations (simulating 500ms loop or triggered evaluations)
        for (int i = 0; i < 10; i++)
        {
            await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        }

        // Must NOT poll again! PollOnceCallCount remains 1 because 15s have not elapsed
        Assert.Equal(1, mockT3.PollOnceCallCount);
    }

    [Fact]
    public void Settings_T3ActivityDefaults_AreBackwardCompatible()
    {
        var settings = new PresenceSettings();

        // Default ShowT3Activity must be false (OFF by default for privacy)
        Assert.False(settings.ShowT3Activity);
        // Default T3Address must be empty
        Assert.Equal(string.Empty, settings.T3Address);
        // Fixed bundled artwork is available without publisher uploads.
        Assert.True(settings.ShowAppIcons);
    }
}
