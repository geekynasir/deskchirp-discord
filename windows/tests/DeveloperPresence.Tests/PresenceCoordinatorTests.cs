using DeveloperPresence.Core.Discord;
using DeveloperPresence.Core.Foreground;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Services;
using Xunit;

namespace DeveloperPresence.Tests;

public class MockDiscordClient : IDiscordIpcClient
{
    public event Action<PresenceConnectionStatus, string?>? StatusChanged;
    public event Action<DiscordIpcFrame>? FrameReceived;

    public bool IsConnected { get; set; } = false;
    public int SendActivityCallCount { get; set; }
    public int ClearActivityCallCount { get; set; }
    public int ConnectCallCount { get; set; }
    public int CloseCallCount { get; set; }
    public SupportedApp? LastSentApp { get; set; }

    public Func<string, CancellationToken, Task<bool>>? ConnectBehavior { get; set; }
    public Func<SupportedApp, CancellationToken, Task>? SendBehavior { get; set; }

    public Task<bool> ConnectAsync(string clientId, CancellationToken ct = default)
    {
        ConnectCallCount++;
        if (ConnectBehavior != null)
        {
            return ConnectBehavior(clientId, ct);
        }
        IsConnected = true;
        StatusChanged?.Invoke(PresenceConnectionStatus.Connected, "Connected");
        return Task.FromResult(true);
    }

    public DiscordActivity? LastSentActivity { get; set; }
    public Func<DiscordActivity, CancellationToken, Task>? SendActivityCustomBehavior { get; set; }

    public Task SendActivityAsync(SupportedApp app, CancellationToken ct = default)
    {
        SendActivityCallCount++;
        LastSentApp = app;
        if (SendBehavior != null) return SendBehavior(app, ct);
        return Task.CompletedTask;
    }

    public Task SendActivityAsync(DiscordActivity activity, CancellationToken ct = default)
    {
        SendActivityCallCount++;
        LastSentActivity = activity;
        SupportedApp app = SupportedApp.None;
        if (activity.Details != null)
        {
            if (activity.Details.Contains("T3 Code", StringComparison.OrdinalIgnoreCase))
                app = SupportedApp.T3Code;
            else if (activity.Details.Contains("Claude", StringComparison.OrdinalIgnoreCase))
                app = SupportedApp.Claude;
            else if (activity.Details.Contains("Codex", StringComparison.OrdinalIgnoreCase))
                app = SupportedApp.Codex;
        }
        if (app != SupportedApp.None)
        {
            LastSentApp = app;
        }

        if (SendActivityCustomBehavior != null)
        {
            return SendActivityCustomBehavior(activity, ct);
        }
        if (SendBehavior != null)
        {
            return SendBehavior(app != SupportedApp.None ? app : SupportedApp.T3Code, ct);
        }

        return Task.CompletedTask;
    }

    public Task ClearActivityAsync(CancellationToken ct = default)
    {
        ClearActivityCallCount++;
        LastSentApp = null;
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        CloseCallCount++;
        IsConnected = false;
        StatusChanged?.Invoke(PresenceConnectionStatus.Disconnected, "Closed");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    protected virtual void OnFrameReceived(DiscordIpcFrame frame) => FrameReceived?.Invoke(frame);
}

public class MockSettingsService : ISettingsService
{
    public PresenceSettings Settings { get; set; } = new();
    public event Action<PresenceSettings>? SettingsChanged;
    public bool WasLoaded { get; private set; }

    public Task LoadSettingsAsync()
    {
        WasLoaded = true;
        return Task.CompletedTask;
    }

    public Task SaveSettingsAsync()
    {
        SettingsChanged?.Invoke(Settings);
        return Task.CompletedTask;
    }

    public Task UpdateSettingsAsync(Action<PresenceSettings> updateAction)
    {
        updateAction(Settings);
        SettingsChanged?.Invoke(Settings);
        return Task.CompletedTask;
    }
}

public class PresenceCoordinatorTests
{
    private const string ValidClientId = "123456789012345678";

    [Fact]
    public async Task StartAsync_LoadsSettingsBeforeAnyDispatch()
    {
        var settingsService = new MockSettingsService();
        var discord = new MockDiscordClient();
        var detector = new ForegroundDetector(() => "t3code.exe");

        var coordinator = new PresenceCoordinator(settingsService, discord, detector);
        await coordinator.StartAsync();

        Assert.True(settingsService.WasLoaded);

        // Master sharing is OFF by default, so no activity should have been sent
        Assert.Equal(0, discord.SendActivityCallCount);

        await coordinator.StopAsync();
    }

    [Fact]
    public async Task Evaluate_DoesNotSendWhenSharingIsDisabled()
    {
        var settingsService = new MockSettingsService();
        settingsService.Settings.EnableSharing = false; // Disabled by default
        settingsService.Settings.DiscordClientId = ValidClientId;
        var discord = new MockDiscordClient();
        var detector = new ForegroundDetector(() => "t3code.exe");

        var coordinator = new PresenceCoordinator(settingsService, discord, detector);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);

        Assert.Equal(0, discord.SendActivityCallCount);
        Assert.Equal(SupportedApp.None, coordinator.CurrentState.ForegroundApp);
        Assert.Equal("None / Cleared", coordinator.CurrentState.OutgoingHeadline);
    }

    [Fact]
    public async Task Evaluate_SendsActivityWhenSharingIsEnabled()
    {
        var settingsService = new MockSettingsService();
        settingsService.Settings.EnableSharing = true;
        settingsService.Settings.DiscordClientId = ValidClientId;
        var discord = new MockDiscordClient();
        var detector = new ForegroundDetector(() => "t3code.exe");

        var coordinator = new PresenceCoordinator(settingsService, discord, detector);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);

        Assert.Equal(1, discord.SendActivityCallCount);
        Assert.Equal(SupportedApp.T3Code, discord.LastSentApp);
        Assert.Equal("Using T3 Code", coordinator.CurrentState.OutgoingHeadline);
    }

    [Fact]
    public async Task Reconnect_UnchangedFocus_PublishesImmediatelyWithoutWaiting60Seconds()
    {
        var settingsService = new MockSettingsService();
        settingsService.Settings.EnableSharing = true;
        settingsService.Settings.DiscordClientId = ValidClientId;
        var discord = new MockDiscordClient();
        var detector = new ForegroundDetector(() => "t3code.exe");

        var coordinator = new PresenceCoordinator(settingsService, discord, detector);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal(1, discord.SendActivityCallCount);
        Assert.Equal(SupportedApp.T3Code, discord.LastSentApp);

        // Discord client disconnects (simulating Discord restart)
        discord.IsConnected = false;

        // When coordinator evaluates again with unchanged focus, it connects and MUST publish immediately
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);

        Assert.Equal(2, discord.SendActivityCallCount);
        Assert.Equal(SupportedApp.T3Code, discord.LastSentApp);
    }

    [Fact]
    public async Task Disable_DuringConnect_CancelsConnectAndClosesClient()
    {
        var settingsService = new MockSettingsService();
        settingsService.Settings.EnableSharing = true;
        settingsService.Settings.DiscordClientId = ValidClientId;
        var discord = new MockDiscordClient();
        var detector = new ForegroundDetector(() => "t3code.exe");

        var connectStartedTcs = new TaskCompletionSource<bool>();
        var allowConnectTcs = new TaskCompletionSource<bool>();

        discord.ConnectBehavior = async (id, ct) =>
        {
            connectStartedTcs.TrySetResult(true);
            await allowConnectTcs.Task;
            ct.ThrowIfCancellationRequested();
            discord.IsConnected = true;
            return true;
        };

        var coordinator = new PresenceCoordinator(settingsService, discord, detector);

        // Start evaluation which begins connecting
        var evalTask = coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        await connectStartedTcs.Task;

        // User turns sharing OFF while connect is in flight
        settingsService.Settings.EnableSharing = false;
        allowConnectTcs.TrySetResult(true);

        await evalTask;

        // Connection should be closed/dropped and no activity sent
        Assert.Equal(0, discord.SendActivityCallCount);
        Assert.True(discord.CloseCallCount >= 1);
    }

    [Fact]
    public async Task IdChange_ClosesOldConnectionBeforeUsingNewIdentity()
    {
        var settingsService = new MockSettingsService();
        settingsService.Settings.EnableSharing = true;
        settingsService.Settings.DiscordClientId = "111111111111111111";
        var discord = new MockDiscordClient();
        var detector = new ForegroundDetector(() => "t3code.exe");

        var coordinator = new PresenceCoordinator(settingsService, discord, detector);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal(1, discord.SendActivityCallCount);

        int initialCloseCount = discord.CloseCallCount;

        // Change application ID
        settingsService.Settings.DiscordClientId = "222222222222222222";
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);

        // Old connection must have been closed
        Assert.True(discord.CloseCallCount > initialCloseCount);
    }

    [Fact]
    public async Task QueuedSend_IsCancelledImmediately_WhenDisabled()
    {
        var settingsService = new MockSettingsService();
        settingsService.Settings.EnableSharing = true;
        settingsService.Settings.DiscordClientId = ValidClientId;
        settingsService.Settings.UpdateIntervalSeconds = 5;

        var discord = new MockDiscordClient();
        string activeApp = "t3code.exe";
        var detector = new ForegroundDetector(() => activeApp);

        var coordinator = new PresenceCoordinator(settingsService, discord, detector);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal(1, discord.SendActivityCallCount);
        Assert.Equal(SupportedApp.T3Code, discord.LastSentApp);

        // Rapidly switch to Claude within 5-second coalescing interval
        activeApp = "claude.exe";
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);

        // Update is queued / throttled; SendActivity has not been called for Claude yet
        Assert.Equal(1, discord.SendActivityCallCount);

        // User turns sharing OFF before coalescing delay fires
        settingsService.Settings.EnableSharing = false;
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);

        // Wait to ensure delayed task does not execute stale send
        await Task.Delay(200);

        Assert.Equal(1, discord.SendActivityCallCount); // Still only 1 send, Claude was cancelled
    }

    [Fact]
    public async Task OnSessionLock_ImmediatelyClearsPresenceAndClosesSocket()
    {
        var settingsService = new MockSettingsService();
        settingsService.Settings.EnableSharing = true;
        settingsService.Settings.DiscordClientId = ValidClientId;
        var discord = new MockDiscordClient();
        var detector = new ForegroundDetector(() => "t3code.exe");

        var coordinator = new PresenceCoordinator(settingsService, discord, detector);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal(1, discord.SendActivityCallCount);

        // User locks workstation (Win+L)
        coordinator.OnSessionLock();
        Assert.True(coordinator.IsSessionLocked);

        await Task.Delay(100);

        Assert.True(discord.ClearActivityCallCount >= 1);
        Assert.True(discord.CloseCallCount >= 1);
        Assert.Equal("None / Cleared", coordinator.CurrentState.OutgoingHeadline);
    }

    [Fact]
    public async Task OnSystemSuspend_ImmediatelyClearsPresenceAndClosesClient()
    {
        var settingsService = new MockSettingsService();
        settingsService.Settings.EnableSharing = true;
        settingsService.Settings.DiscordClientId = ValidClientId;
        var discord = new MockDiscordClient();
        var detector = new ForegroundDetector(() => "t3code.exe");

        var coordinator = new PresenceCoordinator(settingsService, discord, detector);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);

        coordinator.OnSystemSuspend();
        Assert.True(coordinator.IsSystemSuspended);

        await Task.Delay(100);

        Assert.True(discord.ClearActivityCallCount >= 1);
        Assert.True(discord.CloseCallCount >= 1);
    }

    [Fact]
    public async Task SettingsChange_CancelsAnAlreadyWaitingWriteBeforeItCommits()
    {
        var settings = new MockSettingsService();
        settings.Settings.EnableSharing = true;
        settings.Settings.DiscordClientId = ValidClientId;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int commits = 0;
        var discord = new MockDiscordClient
        {
            SendBehavior = async (app, ct) =>
            {
                entered.TrySetResult();
                using var registration = ct.Register(() => cancelled.TrySetResult());
                await Task.Delay(Timeout.Infinite, ct);
                Interlocked.Increment(ref commits);
            }
        };
        await using var coordinator = new PresenceCoordinator(settings, discord, new ForegroundDetector(() => "t3code.exe"));
        await coordinator.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await settings.UpdateSettingsAsync(s => s.EnableSharing = false);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, commits);
        Assert.True(discord.CloseCallCount > 0);
    }

    [Fact]
    public void CurrentState_ReturnsSnapshotClone()
    {
        var settingsService = new MockSettingsService();
        var discord = new MockDiscordClient();
        var detector = new ForegroundDetector(() => "t3code.exe");

        var coordinator = new PresenceCoordinator(settingsService, discord, detector);
        var snapshot1 = coordinator.CurrentState;
        var snapshot2 = coordinator.CurrentState;

        Assert.NotSame(snapshot1, snapshot2);
        Assert.Equal(snapshot1.OutgoingHeadline, snapshot2.OutgoingHeadline);
    }
}
