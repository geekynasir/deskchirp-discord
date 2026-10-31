using System.Text.Json;
using DeveloperPresence.Core.Credentials;
using DeveloperPresence.Core.Foreground;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Services;
using DeveloperPresence.Core.T3;
using Xunit;

namespace DeveloperPresence.Tests;

public class T3ServiceLifecycleTests
{
    private static T3StoredToken Token(string value = "synthetic-token") => new()
    {
        AccessToken = value, McpUri = "http://localhost:3773/mcp", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectWinsOverLateSignInFailure(bool cancelled)
    {
        var settings = new MockSettingsService();
        settings.Settings.T3Address = Token().McpUri;
        var store = new InMemoryCredentialStore();
        await store.SaveCredentialAsync(T3ActivityService.CredentialTarget, T3ActivityService.CredentialUsername, JsonSerializer.Serialize(Token()));
        var oauth = new PendingOAuth();
        await using var service = new T3ActivityService(settings, store, oauth, new PendingMcp());
        await service.InitializeAsync();
        var signIn = service.ConnectAsync(Token().McpUri);
        Assert.Equal(T3ConnectionStatus.OpeningSignIn, service.ConnectionStatus);
        await service.DisconnectAsync();
        oauth.Completion.SetException(cancelled ? new OperationCanceledException() : new IOException("synthetic failure"));
        Assert.False(await signIn);
        Assert.Equal(T3ConnectionStatus.NotConnected, service.ConnectionStatus);
        Assert.Null(await store.ReadCredentialAsync(T3ActivityService.CredentialTarget, T3ActivityService.CredentialUsername));
        Assert.Null(service.CurrentSnapshot);
    }

    [Fact]
    public async Task LatePollCannotRestoreCountsAfterInvalidation()
    {
        var settings = new MockSettingsService();
        settings.Settings.T3Address = Token().McpUri;
        var store = new InMemoryCredentialStore();
        await store.SaveCredentialAsync(T3ActivityService.CredentialTarget, T3ActivityService.CredentialUsername, JsonSerializer.Serialize(Token()));
        var mcp = new PendingMcp();
        await using var service = new T3ActivityService(settings, store, new PendingOAuth(), mcp);
        await service.InitializeAsync();
        var poll = service.PollOnceAsync();
        await mcp.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.Invalidate();
        mcp.Completion.SetResult(new T3ThreadListResult { Total = 1, Threads = new() { new() { ThreadId = "task", ProviderInstanceId = "provider", Status = "running" } } });
        Assert.Null(await poll);
        Assert.Null(service.CurrentSnapshot);
    }

    [Fact]
    public async Task EditingAddressStopsUsingPreviousEnvironment()
    {
        var settings = new MockSettingsService();
        settings.Settings.T3Address = Token().McpUri;
        var store = new InMemoryCredentialStore();
        await store.SaveCredentialAsync(T3ActivityService.CredentialTarget, T3ActivityService.CredentialUsername, JsonSerializer.Serialize(Token()));
        var mcp = new PendingMcp();
        await using var service = new T3ActivityService(settings, store, new PendingOAuth(), mcp);
        await service.InitializeAsync();
        await settings.UpdateSettingsAsync(s => s.T3Address = "https://different.example/mcp");
        Assert.Equal(T3ConnectionStatus.NotConnected, service.ConnectionStatus);
        Assert.Null(await service.PollOnceAsync());
        Assert.False(mcp.Started.Task.IsCompleted);
        await settings.UpdateSettingsAsync(s => s.T3Address = Token().McpUri);
        Assert.Equal(T3ConnectionStatus.Connected, service.ConnectionStatus);
    }

    [Fact]
    public async Task SlowPollDoesNotBlockFocusChange()
    {
        var settings = new MockSettingsService();
        settings.Settings.EnableSharing = true;
        settings.Settings.ShowT3Activity = true;
        settings.Settings.DiscordClientId = "123456789012345678";
        var pending = new TaskCompletionSource<T3Snapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SlowActivity(pending.Task);
        string focused = "t3code.exe";
        await using var coordinator = new PresenceCoordinator(settings, new MockDiscordClient(), new ForegroundDetector(() => focused), service);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        focused = "notepad.exe";
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SupportedApp.None, coordinator.CurrentState.ForegroundApp);
        Assert.True(service.Cancelled);
        pending.SetResult(null);
    }

    private sealed class PendingOAuth : IT3OAuthClient
    {
        public TaskCompletionSource<T3StoredToken> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T3StoredToken> SignInAsync(Uri normalizedMcpUri, CancellationToken ct = default) => Completion.Task;
    }

    private sealed class PendingMcp : IT3McpClient
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<T3ThreadListResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T3CapabilitiesResult> GetCapabilitiesAsync(Uri uri, string token, CancellationToken ct = default) => Task.FromResult(new T3CapabilitiesResult
        { Providers = new() { new() { ProviderInstanceId = "provider", DriverKind = "claudeAgent" } } });
        public Task<T3ThreadListResult> ListThreadsAsync(Uri uri, string token, int? cursor = null, CancellationToken ct = default)
        { Started.TrySetResult(true); return Completion.Task; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SlowActivity : IT3ActivityService
    {
        private readonly Task<T3Snapshot?> _pending;
        public SlowActivity(Task<T3Snapshot?> pending) { _pending = pending; }
        public bool Cancelled { get; private set; }
        public T3ConnectionStatus ConnectionStatus => T3ConnectionStatus.Connected;
        public string StatusDetail => "Connected";
        public T3Snapshot? CurrentSnapshot => null;
        public bool IsSnapshotValid => false;
        public event Action<T3ConnectionStatus, string>? StatusChanged { add { } remove { } }
        public event Action<T3Snapshot?>? SnapshotUpdated { add { } remove { } }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ConnectAsync(string address, CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CancelSignInAsync() => Task.CompletedTask;
        public async Task<T3Snapshot?> PollOnceAsync(CancellationToken ct = default)
        { using var registration = ct.Register(() => Cancelled = true); return await _pending; }
        public void ClearSnapshot() { }
        public void Invalidate() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
