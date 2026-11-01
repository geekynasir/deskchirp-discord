using System.Text.Json;
using DeveloperPresence.Core.Credentials;
using DeveloperPresence.Core.Foreground;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;
using DeveloperPresence.Core.Services;
using DeveloperPresence.Core.T3;
using DeveloperPresence.Core.T3.Usage;
using Xunit;

namespace DeveloperPresence.Tests;

public class PresenceV3SettingsTests
{
    [Fact]
    public void DefaultsPreservePrivacy()
    {
        var s = new PresenceSettings();
        Assert.Empty(s.T3Environments);
        Assert.Equal(CardStyle.Workload, s.CardStyle);
        Assert.Equal(UsageMetric.Tokens, s.UsageMetric);
        Assert.Equal(UsageScope.Today, s.UsageScope);
        Assert.Equal(TokenCategory.Total, s.TokenCategory);
        Assert.Equal(TokenRounding.Compact, s.TokenRounding);
        Assert.Equal(SmallBadge.Status, s.SmallBadge);
        Assert.False(s.ShareProjectAlias);
        Assert.Empty(s.ProjectAliases);
        Assert.False(s.ShareModel);
        Assert.False(s.ShareEstimatedCost);
        Assert.True(s.ShowElapsedTimer);
        Assert.False(s.PlayfulWording);
        Assert.Equal(PresenceButtons.None, s.Buttons);
        Assert.Equal(string.Empty, s.PublicRepoUrl);
        Assert.Empty(s.ExcludedProjects);
        Assert.Equal(string.Empty, s.PinnedTask);
        Assert.Equal(string.Empty, s.PinnedProject);
        Assert.Equal(SelectionMode.KeepBackgroundVisible, s.SelectionMode);
    }

    [Fact]
    public async Task Preview3FileLoadsAndLegacyAddressBecomesFirstEnvironment()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path,
                "{\"EnableSharing\":true,\"ShowT3Activity\":true,\"T3Address\":\"https://remote.example:8443\",\"ArtworkDefaultsVersion\":1," +
                "\"ProcessMappings\":{\"t3code.exe\":1}}");
            var service = new SettingsService(path);
            await service.LoadSettingsAsync();
            var s = service.Settings;
            Assert.True(s.EnableSharing);
            var env = Assert.Single(s.T3Environments);
            Assert.Equal("https://remote.example:8443/mcp", env.Address);
            Assert.Equal("remote.example", env.Label);
            Assert.True(Guid.TryParse(env.Id, out _));
            Assert.Equal(string.Empty, s.T3Address);
            Assert.Equal(CardStyle.Workload, s.CardStyle);

            // Saving and reloading keeps the environment and does not duplicate it.
            await service.SaveSettingsAsync();
            var again = new SettingsService(path);
            await again.LoadSettingsAsync();
            Assert.Equal(env.Id, Assert.Single(again.Settings.T3Environments).Id);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task NewKeysRoundTripWithSpecValuesAndUnknownValuesFallBack()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path,
                "{\"cardStyle\":\"usage\",\"usageMetric\":\"context\",\"usageScope\":\"pinnedTask\",\"tokenCategory\":\"output\"," +
                "\"tokenRounding\":\"exact\",\"smallBadge\":\"surprise\",\"buttons\":\"companionAndRepo\",\"selectionMode\":\"followActiveApp\"," +
                "\"projectAliases\":{\"e/p\":\"Orbit\"},\"excludedProjects\":[\"e/x\"],\"pinnedTask\":\"e/t\",\"playfulWording\":true}");
            var service = new SettingsService(path);
            await service.LoadSettingsAsync();
            var s = service.Settings;
            Assert.Equal(CardStyle.Usage, s.CardStyle);
            Assert.Equal(UsageMetric.Context, s.UsageMetric);
            Assert.Equal(UsageScope.PinnedTask, s.UsageScope);
            Assert.Equal(TokenCategory.Output, s.TokenCategory);
            Assert.Equal(TokenRounding.Exact, s.TokenRounding);
            Assert.Equal(SmallBadge.Status, s.SmallBadge); // unknown value → default
            Assert.Equal(PresenceButtons.CompanionAndRepo, s.Buttons);
            Assert.Equal(SelectionMode.FollowActiveApp, s.SelectionMode);
            Assert.Equal("Orbit", s.ProjectAliases["e/p"]);
            Assert.Contains("e/x", s.ExcludedProjects);
            Assert.True(s.PlayfulWording);

            await service.SaveSettingsAsync();
            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"CardStyle\": \"usage\"", json);
            Assert.Contains("\"Buttons\": \"companionAndRepo\"", json);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("https://github.com/octo-org/orbit-notes", true)]
    [InlineData("https://github.com/a/b.c_d-e", true)]
    [InlineData("http://github.com/octo/orbit", false)]
    [InlineData("https://github.com/octo/orbit/", false)]
    [InlineData("https://github.com/octo/orbit?x=1", false)]
    [InlineData("https://github.com/octo/orbit#readme", false)]
    [InlineData("https://github.com/octo/orbit/tree/main", false)]
    [InlineData("https://gitlab.com/octo/orbit", false)]
    [InlineData("https://github.com/octo_org/orbit", false)]
    [InlineData("https://github.com/octo/..", false)]
    public void PublicRepoLinkValidation(string url, bool valid) => Assert.Equal(valid, PresenceText.IsValidPublicRepoUrl(url));

    [Theory]
    [InlineData("claude-opus-5-5", "claude-opus-5-5")]
    [InlineData("gpt-5.5 codex:high/xl", "gpt-5.5 codex:high/xl")]
    [InlineData("model<script>", null)]
    [InlineData("a-model-name-that-is-definitely-longer-than-forty", null)]
    [InlineData("   ", null)]
    public void ModelLabels(string model, string? expected) => Assert.Equal(expected, PresenceText.SafeModelLabel(model));

    [Fact]
    public void HardCapEndsInEllipsis()
    {
        string capped = PresenceText.Cap(new string('a', 200))!;
        Assert.Equal(128, capped.Length);
        Assert.EndsWith("…", capped);
        Assert.Equal("short", PresenceText.Cap("short"));
    }
}

public class WorkSessionTimerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1793500000);

    [Fact]
    public void SessionStartsOnFirstWorkSurvivesPhaseChangesAndEndsAfter120Seconds()
    {
        var timer = new WorkSessionTimer();
        Assert.Null(timer.Observe(false, T0).WorkSessionStart);
        Assert.Equal(T0, timer.Observe(true, T0.AddSeconds(1)).WorkSessionStart?.AddSeconds(-1));
        // running → waiting → starting all count as unfinished work.
        Assert.Equal(T0.AddSeconds(1), timer.Observe(true, T0.AddSeconds(30)).WorkSessionStart);
        Assert.Equal(T0.AddSeconds(1), timer.Observe(true, T0.AddSeconds(60)).WorkSessionStart);
        // A short gap keeps the session.
        Assert.Equal(T0.AddSeconds(1), timer.Observe(false, T0.AddSeconds(100)).WorkSessionStart);
        Assert.Equal(T0.AddSeconds(1), timer.Observe(true, T0.AddSeconds(170)).WorkSessionStart);
        Assert.Equal(T0.AddSeconds(1), timer.Observe(false, T0.AddSeconds(289)).WorkSessionStart);
        // 120 seconds without unfinished work ends it; new work starts a new session.
        Assert.Null(timer.Observe(false, T0.AddSeconds(290)).WorkSessionStart);
        Assert.Equal(T0.AddSeconds(400), timer.Observe(true, T0.AddSeconds(400)).WorkSessionStart);
    }

    [Fact]
    public void StoppingSharingEndsSessionAndAppClockFollowsShownApp()
    {
        var timer = new WorkSessionTimer();
        timer.Observe(true, T0);
        timer.EndSession();
        Assert.Null(timer.WorkSessionStart);

        Assert.Equal(T0, timer.ObserveShownApp(SupportedApp.T3Code, T0).AppShownStart);
        Assert.Equal(T0, timer.ObserveShownApp(SupportedApp.T3Code, T0.AddSeconds(50)).AppShownStart);
        Assert.Equal(T0.AddSeconds(60), timer.ObserveShownApp(SupportedApp.Claude, T0.AddSeconds(60)).AppShownStart);
        Assert.Null(timer.ObserveShownApp(SupportedApp.None, T0.AddSeconds(70)).AppShownStart);
    }

    [Fact]
    public void RichCardWithoutFreshWorkFallsBackToAppClock()
    {
        var settings = new PresenceSettings { EnableSharing = true, ShowT3Activity = true };
        var result = PresenceCardFormatter.Format(new PresenceCardInput
        {
            Settings = settings, App = SupportedApp.T3Code, Now = T0.AddSeconds(600),
            Timer = new PresenceTimerState(T0, T0.AddSeconds(500)), Snapshot = null
        });
        Assert.Equal(T0.AddSeconds(500).ToUnixTimeSeconds(), result.Activity!.Timestamps!.Start);
        Assert.Equal("Using T3 Code", result.Activity.Details);
    }
}

public class MultiEnvironmentTests
{
    private const string EnvA = "https://a.example/mcp";
    private const string EnvB = "https://b.example/mcp";

    private static T3StoredToken Token(string uri) => new() { AccessToken = "synthetic-" + uri, McpUri = uri, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };

    /// <summary>Answers per endpoint: threads to return, or an exception.</summary>
    private sealed class ScriptedMcp : IT3McpClient
    {
        public Dictionary<string, Func<T3ThreadListResult>> Threads { get; } = new();
        public List<string> ProjectCalls { get; } = new();
        public Task<T3CapabilitiesResult> GetCapabilitiesAsync(Uri uri, string token, CancellationToken ct = default) =>
            Task.FromResult(new T3CapabilitiesResult { Providers = new() {
                new() { ProviderInstanceId = "claude", DriverKind = "claudeAgent" },
                new() { ProviderInstanceId = "codex", DriverKind = "codex" } } });
        public Task<T3ThreadListResult> ListThreadsAsync(Uri uri, string token, int? cursor = null, CancellationToken ct = default) =>
            Task.FromResult(Threads[uri.AbsoluteUri]());
        public Task<IReadOnlyList<T3ProjectListItem>> ListProjectsAsync(Uri uri, string token, CancellationToken ct = default)
        {
            ProjectCalls.Add(uri.AbsoluteUri);
            return Task.FromResult<IReadOnlyList<T3ProjectListItem>>(new[] { new T3ProjectListItem { ProjectId = "p1", Title = "PRIVATE project" } });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static T3ThreadListItem Thread(string id, string status, string provider = "claude", string? project = "p1", string? parent = null, bool helper = false) =>
        new() { ThreadId = id, ProviderInstanceId = provider, Status = status, ProjectId = project, ParentThreadId = parent,
                RelationshipToParent = helper ? "subagent" : null, Title = "PRIVATE title " + id };

    private static async Task<(MockSettingsService, InMemoryCredentialStore, ScriptedMcp, T3ActivityService)> Setup()
    {
        var settings = new MockSettingsService();
        settings.Settings.T3Environments.Add(new T3Environment { Id = "env-a", Address = EnvA, Label = "PRIVATE laptop" });
        settings.Settings.T3Environments.Add(new T3Environment { Id = "env-b", Address = EnvB, Label = "PRIVATE server" });
        var store = new InMemoryCredentialStore();
        foreach (var uri in new[] { EnvA, EnvB })
            await store.SaveCredentialAsync(T3ActivityService.CredentialTargetFor(uri), T3ActivityService.CredentialUsername, JsonSerializer.Serialize(Token(uri)));
        var mcp = new ScriptedMcp();
        var service = new T3ActivityService(settings, store, null, mcp);
        await service.InitializeAsync();
        return (settings, store, mcp, service);
    }

    [Fact]
    public async Task CombinesEnvironmentsAndCountsSameThreadIdTwice()
    {
        var (_, _, mcp, service) = await Setup();
        await using var _s = service;
        mcp.Threads[EnvA] = () => new T3ThreadListResult { Total = 1, Threads = new() { Thread("same", "running") } };
        mcp.Threads[EnvB] = () => new T3ThreadListResult { Total = 3, Threads = new() { Thread("same", "running"), Thread("same", "running"), Thread("h", "running", parent: "same", helper: true) } };
        var snapshot = await service.PollOnceAsync();
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Environments.Count);
        Assert.Equal(2, snapshot.WinningHarness!.RunningTasks);
        Assert.Equal(1, snapshot.WinningHarness.RunningHelpers);
        Assert.Equal("Connected to 2 of 2 environments", service.StatusDetail);
        // Titles never enter the published snapshot; they stay in the local picker.
        Assert.All(snapshot.Environments.SelectMany(e => e.Threads), t => Assert.Null(t.Title));
        Assert.Contains(service.PickerTasks, t => t.Title == "PRIVATE title same");
        Assert.Contains(service.PickerProjects, p => p.Title == "PRIVATE project");
    }

    [Fact]
    public async Task FailingEnvironmentContributesNothingAndDoesNotInvalidateOthers()
    {
        var (_, _, mcp, service) = await Setup();
        await using var _s = service;
        mcp.Threads[EnvA] = () => new T3ThreadListResult { Total = 1, Threads = new() { Thread("a1", "running") } };
        mcp.Threads[EnvB] = () => throw new HttpRequestException("synthetic outage");
        var snapshot = await service.PollOnceAsync();
        Assert.NotNull(snapshot);
        Assert.Equal("env-a", Assert.Single(snapshot!.Environments).EnvironmentId);
        Assert.Equal(1, snapshot.WinningHarness!.RunningTasks);
        Assert.Equal(T3ConnectionStatus.Connected, service.ConnectionStatus);
        var b = service.Environments.Single(e => e.EnvironmentId == "env-b");
        Assert.Equal(T3ConnectionStatus.Connected, b.Status);
        Assert.Contains("isn’t available", b.Detail);

        // Authentication failure on one environment asks only that one to sign in again.
        mcp.Threads[EnvB] = () => throw new T3AuthenticationException("expired");
        snapshot = await service.PollOnceAsync();
        Assert.Equal("env-a", Assert.Single(snapshot!.Environments).EnvironmentId);
        Assert.Equal(T3ConnectionStatus.SignInAgain, service.Environments.Single(e => e.EnvironmentId == "env-b").Status);
        Assert.Equal("Connected to 1 of 2 environments", service.StatusDetail);

        // Every environment failing → no combined snapshot (never zeroed counts).
        mcp.Threads[EnvA] = () => throw new HttpRequestException("synthetic outage");
        Assert.Null(await service.PollOnceAsync());
        Assert.Null(service.CurrentSnapshot);
    }

    [Fact]
    public async Task ConnectedEnvironmentWithoutWorkSuggestsAddingTheRightOne()
    {
        var (settings, _, mcp, service) = await Setup();
        await using var _s = service;
        settings.Settings.T3Environments.RemoveAt(1);
        await settings.UpdateSettingsAsync(_ => { });
        mcp.Threads[EnvA] = () => new T3ThreadListResult { Total = 1, Threads = new() { Thread("done", "completed") } };
        await service.PollOnceAsync();
        Assert.Equal(T3ActivityService.MsgNoWorkHere, service.StatusDetail);
        Assert.False(service.Environments.Single().HasReturnedWork);
    }

    [Fact]
    public async Task RemovingEnvironmentDeletesItsCredentialAndReferences()
    {
        var (settings, store, mcp, service) = await Setup();
        await using var _s = service;
        settings.Settings.ProjectAliases["env-b/p1"] = "Alias";
        settings.Settings.ExcludedProjects.Add("env-b/p2");
        settings.Settings.PinnedTask = "env-b/t1";
        await service.RemoveEnvironmentAsync("env-b");
        Assert.Null(await store.ReadCredentialAsync(T3ActivityService.CredentialTargetFor(EnvB), T3ActivityService.CredentialUsername));
        Assert.NotNull(await store.ReadCredentialAsync(T3ActivityService.CredentialTargetFor(EnvA), T3ActivityService.CredentialUsername));
        Assert.Single(settings.Settings.T3Environments);
        Assert.Empty(settings.Settings.ProjectAliases);
        Assert.Empty(settings.Settings.ExcludedProjects);
        Assert.Equal(string.Empty, settings.Settings.PinnedTask);
        mcp.Threads[EnvA] = () => new T3ThreadListResult { Total = 0, Threads = new() };
        Assert.NotNull(await service.PollOnceAsync());
    }

    [Fact]
    public async Task AddEnvironmentUsesUrlValidator()
    {
        var (settings, _, _, service) = await Setup();
        await using var _s = service;
        Assert.Equal(T3UrlValidator.MsgNotHttpsOrLocalhost, await service.AddEnvironmentAsync("http://remote.example"));
        Assert.Equal(T3ActivityService.MsgAlreadyAdded, await service.AddEnvironmentAsync("https://a.example"));
        Assert.Null(await service.AddEnvironmentAsync("https://c.example"));
        Assert.Equal("https://c.example/mcp", settings.Settings.T3Environments.Last().Address);
        Assert.Equal("c.example", settings.Settings.T3Environments.Last().Label);
    }

    [Fact]
    public void ExclusionsDropProjectsAndHelpersOfExcludedParentsAtAnyDepth()
    {
        var env = new T3EnvironmentSnapshot
        {
            EnvironmentId = "e",
            Drivers = new Dictionary<string, string> { ["claude"] = "claudeAgent" },
            Threads = new[]
            {
                Thread("keep", "running"),
                Thread("secret", "running", project: "x"),
                Thread("child", "running", project: "p1", parent: "secret", helper: true),
                Thread("grandchild", "running", project: null, parent: "child", helper: true),
                Thread("other-env-parent", "running", project: "p1", parent: "missing", helper: true)
            }
        };
        var entries = T3WorkloadView.Entries(new[] { env }, new HashSet<string> { "e/x" });
        Assert.Equal(new[] { "keep", "other-env-parent" }, entries.Select(e => e.Thread.ThreadId));
        // Exclusions are environment-scoped.
        Assert.Equal(5, T3WorkloadView.Entries(new[] { env }, new HashSet<string> { "other/x" }).Count);
    }

    [Fact]
    public void StaleEnvironmentIsDroppedFromCard()
    {
        var now = DateTimeOffset.UtcNow;
        var drivers = new Dictionary<string, string> { ["claude"] = "claudeAgent" };
        var snapshot = new T3Snapshot
        {
            Timestamp = now,
            Environments = new[]
            {
                new T3EnvironmentSnapshot { EnvironmentId = "fresh", Timestamp = now, Drivers = drivers, Threads = new[] { Thread("f", "running") } },
                new T3EnvironmentSnapshot { EnvironmentId = "stale", Timestamp = now.AddMinutes(-5), Drivers = drivers, Threads = new[] { Thread("s1", "running"), Thread("s2", "running") } }
            }
        };
        var result = PresenceCardFormatter.Format(new PresenceCardInput
        {
            Settings = new PresenceSettings { EnableSharing = true, ShowT3Activity = true }, Snapshot = snapshot, App = SupportedApp.T3Code, Now = now
        });
        Assert.Equal("1 task", result.Activity!.State);
    }
}

public class SelectionAndUsagePollingTests
{
    private const string ValidClientId = "123456789012345678";

    private sealed class CountingUsage : IT3UsageService
    {
        public int Polls;
        public int Invalidations;
        public T3UsageSnapshot? CurrentSnapshot { get; set; }
        public event Action? Changed { add { } remove { } }
        public bool HasPermission(string environmentId) => true;
        public bool HasAnyPermission => true;
        public string? StatusFor(string environmentId) => null;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<T3PairingResult> PairAsync(string environmentId, string rawInput, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveTokenAccessAsync(string environmentId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<T3UsageSnapshot?> PollOnceAsync(T3UsagePollRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Polls);
            return Task.FromResult(CurrentSnapshot);
        }
        public void Invalidate() { Invalidations++; CurrentSnapshot = null; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static T3Snapshot Working() => new()
    {
        WinningHarness = new T3HarnessWorkload { HarnessName = "Claude Code", RunningTasks = 2 },
        Workloads = new[] { new T3HarnessWorkload { HarnessName = "Claude Code", RunningTasks = 2 } }
    };

    [Fact]
    public async Task FollowActiveAppLetsForegroundAppWinOverBackgroundWork()
    {
        var settings = new MockSettingsService();
        settings.Settings.EnableSharing = true;
        settings.Settings.ShowT3Activity = true;
        settings.Settings.DiscordClientId = ValidClientId;
        settings.Settings.SelectionMode = SelectionMode.FollowActiveApp;
        string active = "claude.exe";
        var detector = new ForegroundDetector(() => active, app => app is SupportedApp.T3Code or SupportedApp.Claude);
        var t3 = new MockT3ActivityService { CurrentSnapshot = Working() };
        var discord = new MockDiscordClient();
        await using var coordinator = new PresenceCoordinator(settings, discord, detector, t3);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal("Using Claude", coordinator.CurrentState.OutgoingHeadline);

        // Nothing active but Claude still running: T3 shows app-only.
        active = "browser.exe";
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal("Using T3 Code", coordinator.CurrentState.OutgoingHeadline);

        active = "t3code.exe";
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal("T3 Code · Claude Code", coordinator.CurrentState.OutgoingHeadline);

        // keepBackgroundVisible restores preview 3 behavior.
        settings.Settings.SelectionMode = SelectionMode.KeepBackgroundVisible;
        active = "claude.exe";
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal("T3 Code · Claude Code", coordinator.CurrentState.OutgoingHeadline);
        Assert.NotNull(discord.LastSentActivity?.Timestamps);
    }

    [Fact]
    public async Task UsagePollsOnlyWhileUsageCardIsChosen()
    {
        var settings = new MockSettingsService();
        settings.Settings.EnableSharing = true;
        settings.Settings.ShowT3Activity = true;
        settings.Settings.DiscordClientId = ValidClientId;
        settings.Settings.T3Environments.Add(new T3Environment { Id = "e", Address = "http://localhost:3773/mcp" });
        var detector = new ForegroundDetector(() => "t3code.exe");
        var t3 = new MockT3ActivityService { CurrentSnapshot = Working() };
        var usage = new CountingUsage
        {
            CurrentSnapshot = new T3UsageSnapshot { Timestamp = DateTimeOffset.UtcNow, Today = new T3UsageValue { Tokens = new T3TokenCounts { UncachedInputTokens = 842000 } } }
        };
        var discord = new MockDiscordClient();
        await using var coordinator = new PresenceCoordinator(settings, discord, detector, t3, null, usage);

        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal(0, usage.Polls);
        Assert.Equal("2 tasks", coordinator.CurrentState.OutgoingState);

        settings.Settings.CardStyle = CardStyle.Usage;
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        await TestWait.Until(() => usage.Polls == 1);
        Assert.Equal("842K tokens today", coordinator.CurrentState.OutgoingState);
        for (int i = 0; i < 5; i++) await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal(1, usage.Polls); // 60-second cadence

        settings.Settings.CardStyle = CardStyle.Workload;
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.True(usage.Invalidations >= 1);
        Assert.Null(usage.CurrentSnapshot);
        Assert.Equal("2 tasks", coordinator.CurrentState.OutgoingState);
    }
}

internal static class TestWait
{
    public static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}

public class PresenceAvailabilityTests
{
    [Fact]
    public void CardStylesExplainWhyTheyAreUnavailable()
    {
        var s = new PresenceSettings { ShowT3Activity = true };
        Assert.True(PresenceAvailability.CardStyleOption(CardStyle.AppOnly, s, false, false).Enabled);
        Assert.Equal(PresenceAvailability.ReasonConnect, PresenceAvailability.CardStyleOption(CardStyle.Workload, s, false, false).Reason);
        Assert.True(PresenceAvailability.CardStyleOption(CardStyle.Workload, s, true, false).Enabled);
        Assert.Equal(PresenceAvailability.ReasonUsagePermission, PresenceAvailability.CardStyleOption(CardStyle.Usage, s, true, false).Reason);
        Assert.True(PresenceAvailability.CardStyleOption(CardStyle.Usage, s, true, true).Enabled);
        s.ShowT3Activity = false;
        Assert.Equal(PresenceAvailability.ReasonConnect, PresenceAvailability.CardStyleOption(CardStyle.Usage, s, true, true).Reason);
    }

    [Fact]
    public void ScopeRules()
    {
        var s = new PresenceSettings { CardStyle = CardStyle.Usage };
        Assert.True(PresenceAvailability.ScopeOption(UsageScope.Today, s).Enabled);
        Assert.Equal(PresenceAvailability.ReasonPickTask, PresenceAvailability.ScopeOption(UsageScope.PinnedTask, s).Reason);
        s.ExcludedProjects.Add("e/p");
        Assert.Equal(PresenceAvailability.ReasonTodayExclusions, PresenceAvailability.ScopeOption(UsageScope.Today, s).Reason);
        s.UsageMetric = UsageMetric.Context;
        Assert.Equal(PresenceAvailability.ReasonContextScope, PresenceAvailability.ScopeOption(UsageScope.Session, s).Reason);
        Assert.Equal(UsageScope.PinnedTask, PresenceCardFormatter.EffectiveScope(s));
        Assert.False(PresenceAvailability.ShowEstimatedCost(s));
        s.UsageMetric = UsageMetric.Tokens;
        s.ExcludedProjects.Clear();
        Assert.True(PresenceAvailability.ShowEstimatedCost(s));
    }

    [Fact]
    public void RepoButtonNeedsSharedAliasAndValidLink()
    {
        var s = new PresenceSettings { PublicRepoUrl = "https://github.com/octo/orbit" };
        Assert.Equal(PresenceAvailability.ReasonShareAlias, PresenceAvailability.CompanionAndRepo(s).Reason);
        s.ShareProjectAlias = true;
        Assert.True(PresenceAvailability.CompanionAndRepo(s).Enabled);
        s.PublicRepoUrl = "https://github.com/octo/orbit?private=1";
        Assert.Equal(PresenceText.RepoUrlInvalid, PresenceAvailability.CompanionAndRepo(s).Reason);
    }
}
