using DeveloperPresence.Core.Foreground;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;
using DeveloperPresence.Core.Services;
using DeveloperPresence.Core.T3;
using Xunit;

namespace DeveloperPresence.Tests;

public class BackgroundPresenceTests
{
    [Fact]
    public async Task MinimizedT3KeepsWorkVisibleAndQuitClearsIt()
    {
        var settings = new MockSettingsService();
        settings.Settings.EnableSharing = true;
        settings.Settings.ShowT3Activity = true;
        var discord = new MockDiscordClient();
        bool t3Running = true;
        string active = "browser.exe";
        var detector = new ForegroundDetector(() => active, app => app == SupportedApp.T3Code && t3Running);
        var workload = new T3HarnessWorkload { HarnessName = "Claude Code", RunningTasks = 2, RunningHelpers = 4 };
        var t3 = new MockT3ActivityService { CurrentSnapshot = new T3Snapshot { WinningHarness = workload, Workloads = new[] { workload } } };
        await using var coordinator = new PresenceCoordinator(settings, discord, detector, t3);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal("T3 Code · Claude Code", coordinator.CurrentState.OutgoingHeadline);
        Assert.Equal("2 tasks · 4 helpers", coordinator.CurrentState.OutgoingState);
        Assert.True(t3.PollOnceCallCount > 0);
        Assert.Equal(DiscordArtwork.BaseUrl + "t3_code.png", discord.LastSentActivity?.Assets?.LargeImage);
        Assert.Equal(DiscordArtwork.BaseUrl + "harness_claude.png", discord.LastSentActivity?.Assets?.SmallImage);

        active = "claude.exe";
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal("T3 Code · Claude Code", coordinator.CurrentState.OutgoingHeadline);
        t3Running = false;
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal("Using Claude", coordinator.CurrentState.OutgoingHeadline);
        Assert.Null(coordinator.CurrentState.OutgoingState);
        settings.Settings.IsPaused = true;
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal(ActivityFormatter.HeadlineNone, coordinator.CurrentState.OutgoingHeadline);
    }

    [Fact]
    public async Task IdleMinimizedT3ShowsAppButActiveDeveloperAppTakesPrecedence()
    {
        var settings = new MockSettingsService();
        settings.Settings.EnableSharing = true;
        string active = "browser.exe";
        var detector = new ForegroundDetector(() => active, app => app == SupportedApp.T3Code);
        await using var coordinator = new PresenceCoordinator(settings, new MockDiscordClient(), detector);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal("Using T3 Code", coordinator.CurrentState.OutgoingHeadline);
        active = "codex.exe";
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal("Using Codex", coordinator.CurrentState.OutgoingHeadline);
        settings.Settings.EnableSharing = false;
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal(ActivityFormatter.HeadlineNone, coordinator.CurrentState.OutgoingHeadline);
    }

    [Fact]
    public void ArtworkUsesOnlyFixedPublicImagesAndSupportsOptOut()
    {
        Assert.Null(DiscordArtwork.UrlFor("https://private.example/secret"));
        var activity = new DiscordActivity { Details = "Using Codex" };
        Assert.Null(DiscordArtwork.Apply(activity, SupportedApp.Codex, false)!.Assets);
        Assert.Equal(DiscordArtwork.BaseUrl + "harness_codex.png", DiscordArtwork.Apply(activity, SupportedApp.Codex, true)!.Assets!.LargeImage);
    }
}
