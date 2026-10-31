using DeveloperPresence.Core.Computers;
using DeveloperPresence.Core.Foreground;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Services;
using Xunit;

namespace DeveloperPresence.Tests;

public sealed class ComputerCoordinatorTests
{
    private sealed class Computers : IComputerLinkService
    {
        public event Action? Changed { add { } remove { } }
        public bool CanPublish { get; set; }
        public string Status => CanPublish ? "Selected computer: this computer" : "Selected computer: My Mac";
        public string PairingCode => "";
        public bool HasSavedLink => true;
        public bool Available;
        public string? Account;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void UpdateContext(bool available, string? user) { Available = available; Account = user; }
        public Task LinkAsync(string address, string code, string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateCodeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task WaitingComputerOnlyHandshakesAndLosingPermissionClearsOldActivity()
    {
        var settings = new MockSettingsService();
        settings.Settings.EnableSharing = true; settings.Settings.FollowComputers = true;
        settings.Settings.DiscordClientId = "123456789012345678";
        var discord = new MockDiscordClient(); var computers = new Computers();
        var coordinator = new PresenceCoordinator(settings, discord, new ForegroundDetector(() => "t3code.exe"), null, computers);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.True(discord.IsConnected); Assert.Equal(0, discord.SendActivityCallCount);
        Assert.Equal("None / Cleared", coordinator.CurrentState.OutgoingHeadline);
        computers.CanPublish = true;
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal(1, discord.SendActivityCallCount);
        computers.CanPublish = false;
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal(1, discord.SendActivityCallCount);
        Assert.True(discord.ClearActivityCallCount > 0);
        Assert.Equal("None / Cleared", coordinator.CurrentState.OutgoingHeadline);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task BrowserRemainsCandidateWithoutPublishingAnyActivity()
    {
        var settings = new MockSettingsService();
        settings.Settings.EnableSharing = true; settings.Settings.FollowComputers = true;
        settings.Settings.DiscordClientId = "123456789012345678";
        var discord = new MockDiscordClient(); var computers = new Computers { CanPublish = true };
        var coordinator = new PresenceCoordinator(settings, discord, new ForegroundDetector(() => "browser.exe"), null, computers);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.True(computers.Available); Assert.True(discord.IsConnected);
        Assert.Equal(0, discord.SendActivityCallCount);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task DiscordClosedStillReportsRecentUseWithoutPublishing()
    {
        var settings = new MockSettingsService();
        settings.Settings.EnableSharing = true; settings.Settings.FollowComputers = true;
        settings.Settings.DiscordClientId = "123456789012345678";
        var discord = new MockDiscordClient {
            CurrentUserId = null, ConnectBehavior = (_, _) => Task.FromResult(false)
        };
        var computers = new Computers();
        var coordinator = new PresenceCoordinator(settings, discord, new ForegroundDetector(() => "t3code.exe"), null, computers);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.True(computers.Available);
        Assert.Null(computers.Account);
        Assert.Equal(0, discord.SendActivityCallCount);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task MissingLinkServiceFailsClosedEvenWithSavedModeEnabled()
    {
        var settings = new MockSettingsService();
        settings.Settings.EnableSharing = true; settings.Settings.FollowComputers = true;
        settings.Settings.DiscordClientId = "123456789012345678";
        var discord = new MockDiscordClient();
        var coordinator = new PresenceCoordinator(settings, discord, new ForegroundDetector(() => "t3code.exe"));
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal(0, discord.SendActivityCallCount);
        await coordinator.DisposeAsync();
    }
}
