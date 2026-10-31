using DeveloperPresence.App.ViewModels;
using DeveloperPresence.Core.Discord;
using DeveloperPresence.Core.Foreground;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Services;
using DeveloperPresence.Core.T3;
using Xunit;

public class AutomaticSetupTests
{
    [Fact]
    public async Task ApprovalFindsLocalAppAndEnablesCountsWithoutEnablingSharing()
    {
        var settings = new SettingsStub();
        var t3 = new ActivityStub();
        var discovery = new DiscoveryStub();
        await using var discord = new DiscordIpcClient();
        await using var coordinator = new PresenceCoordinator(settings, discord, new ForegroundDetector(() => null));
        using var model = new MainViewModel(settings, coordinator, t3ActivityService: t3, t3Discovery: discovery);
        model.ConnectT3Command.Execute(null);
        Assert.True(model.IsT3Connecting);
        model.ConnectT3Command.Execute(null); // Cancel before discovery completes.
        discovery.Completion.TrySetResult("http://127.0.0.1:4123/mcp");
        await Task.Delay(30);
        Assert.Null(t3.ConnectedAddress);
        Assert.False(model.ShowT3Activity);
        Assert.False(model.EnableSharing);

        model.ConnectT3Command.Execute(null);
        await t3.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (int i = 0; i < 20 && !model.ShowT3Activity; i++) await Task.Delay(10);
        Assert.Equal("http://127.0.0.1:4123/mcp", t3.ConnectedAddress);
        Assert.True(model.ShowT3Activity);
        Assert.False(model.EnableSharing);
    }

    [Fact]
    public async Task ExplicitAddressBypassesDiscovery()
    {
        var settings = new SettingsStub();
        settings.Settings.T3Address = "https://t3.example/mcp";
        var t3 = new ActivityStub();
        var discovery = new DiscoveryStub();
        await using var discord = new DiscordIpcClient();
        await using var coordinator = new PresenceCoordinator(settings, discord, new ForegroundDetector(() => null));
        using var model = new MainViewModel(settings, coordinator, t3ActivityService: t3, t3Discovery: discovery);
        model.ConnectT3Command.Execute(null);
        await t3.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("https://t3.example/mcp", t3.ConnectedAddress);
        Assert.Equal(0, discovery.Calls);
    }

    private sealed class DiscoveryStub : IT3LocalDiscovery
    {
        public int Calls;
        public TaskCompletionSource<string> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> FindAsync(CancellationToken ct = default) { Calls++; return Completion.Task.WaitAsync(ct); }
    }
    private sealed class SettingsStub : ISettingsService
    {
        public PresenceSettings Settings { get; } = new();
        public event Action<PresenceSettings>? SettingsChanged;
        public Task LoadSettingsAsync() => Task.CompletedTask;
        public Task SaveSettingsAsync() => Task.CompletedTask;
        public Task UpdateSettingsAsync(Action<PresenceSettings> update) { update(Settings); SettingsChanged?.Invoke(Settings); return Task.CompletedTask; }
    }
    private sealed class ActivityStub : IT3ActivityService
    {
        public string? ConnectedAddress;
        public TaskCompletionSource Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public T3ConnectionStatus ConnectionStatus => T3ConnectionStatus.NotConnected;
        public string StatusDetail => "Ready";
        public T3Snapshot? CurrentSnapshot => null;
        public bool IsSnapshotValid => false;
        public event Action<T3ConnectionStatus, string>? StatusChanged { add { } remove { } }
        public event Action<T3Snapshot?>? SnapshotUpdated { add { } remove { } }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ConnectAsync(string rawAddress, CancellationToken ct = default) { ConnectedAddress = rawAddress; Called.TrySetResult(); return Task.FromResult(true); }
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CancelSignInAsync() => Task.CompletedTask;
        public Task<T3Snapshot?> PollOnceAsync(CancellationToken ct = default) => Task.FromResult<T3Snapshot?>(null);
        public void ClearSnapshot() { }
        public void Invalidate() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
