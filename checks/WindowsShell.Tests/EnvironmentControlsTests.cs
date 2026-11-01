using DeveloperPresence.App.ViewModels;
using DeveloperPresence.Core.Discord;
using DeveloperPresence.Core.Foreground;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Services;
using DeveloperPresence.Core.T3;
using Xunit;

public class EnvironmentControlsTests
{
    [Fact]
    public async Task CancellingOneEnvironmentSignInLeavesOthersAlone()
    {
        var settings = new SettingsStub();
        settings.Settings.T3Environments.Add(new T3Environment { Id = "env-a", Address = "https://a.example/mcp" });
        settings.Settings.T3Environments.Add(new T3Environment { Id = "env-b", Address = "https://b.example/mcp" });
        var t3 = new ActivityStub
        {
            Statuses = new()
            {
                new T3EnvironmentStatus { EnvironmentId = "env-a", Status = T3ConnectionStatus.OpeningSignIn },
                new T3EnvironmentStatus { EnvironmentId = "env-b", Status = T3ConnectionStatus.OpeningSignIn }
            }
        };
        await using var discord = new DiscordIpcClient();
        await using var coordinator = new PresenceCoordinator(settings, discord, new ForegroundDetector(() => null));
        using var model = new MainViewModel(settings, coordinator, t3ActivityService: t3);

        model.AllowEnvironment(model.Environments.Single(e => e.Id == "env-b"));
        Assert.Equal(new[] { "env-b" }, t3.CancelledIds);
        Assert.Equal(0, t3.GlobalCancels);
    }

    [Fact]
    public async Task PinnedProjectNotFoundAppearsOnceCatalogsLoad()
    {
        var settings = new SettingsStub();
        settings.Settings.T3Environments.Add(new T3Environment { Id = "env-a", Address = "https://a.example/mcp" });
        settings.Settings.PinnedProject = "env-a/gone";
        var t3 = new ActivityStub
        {
            Statuses = new() { new T3EnvironmentStatus { EnvironmentId = "env-a", Status = T3ConnectionStatus.Connected, ProjectCatalogLoaded = true } },
            Projects = new() { new T3PickerProject("env-a", "p1", "Local title") }
        };
        await using var discord = new DiscordIpcClient();
        await using var coordinator = new PresenceCoordinator(settings, discord, new ForegroundDetector(() => null));
        using var model = new MainViewModel(settings, coordinator, t3ActivityService: t3);
        Assert.True(model.IsPinnedProjectMissing);
        Assert.Equal("Pinned project not found", model.PinnedProjectNotFoundText);

        await settings.UpdateSettingsAsync(s => s.PinnedProject = "env-a/p1");
        Assert.False(model.IsPinnedProjectMissing);
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
        public List<T3EnvironmentStatus> Statuses { get; init; } = new();
        public List<T3PickerProject> Projects { get; init; } = new();
        public List<string> CancelledIds { get; } = new();
        public int GlobalCancels;
        public T3ConnectionStatus ConnectionStatus => T3ConnectionStatus.OpeningSignIn;
        public string StatusDetail => "Opening";
        public T3Snapshot? CurrentSnapshot => null;
        public bool IsSnapshotValid => false;
        public event Action<T3ConnectionStatus, string>? StatusChanged { add { } remove { } }
        public event Action<T3Snapshot?>? SnapshotUpdated { add { } remove { } }
        public IReadOnlyList<T3EnvironmentStatus> Environments => Statuses;
        public IReadOnlyList<T3PickerProject> PickerProjects => Projects;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ConnectAsync(string rawAddress, CancellationToken ct = default) => Task.FromResult(false);
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CancelSignInAsync() { GlobalCancels++; return Task.CompletedTask; }
        public Task CancelSignInAsync(string environmentId) { CancelledIds.Add(environmentId); return Task.CompletedTask; }
        public Task<T3Snapshot?> PollOnceAsync(CancellationToken ct = default) => Task.FromResult<T3Snapshot?>(null);
        public void ClearSnapshot() { }
        public void Invalidate() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
