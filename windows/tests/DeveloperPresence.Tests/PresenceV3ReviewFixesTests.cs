using System.Net;
using System.Reflection;
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

// Regression tests for the PR #4 review findings (one or more per finding).

public class ReviewFormatterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1793500600);
    private static readonly Dictionary<string, string> Drivers = new() { ["claude"] = "claudeAgent" };

    private static T3Snapshot Snapshot(params T3ThreadListItem[] threads) => new()
    {
        Timestamp = Now,
        Environments = new[] { new T3EnvironmentSnapshot { EnvironmentId = "env-a", Timestamp = Now, Drivers = Drivers, Threads = threads } }
    };

    private static T3ThreadListItem Thread(string id, string project, string? parent = null) => new()
    {
        ThreadId = id, ProviderInstanceId = "claude", Status = "running", ProjectId = project, ParentThreadId = parent,
        RelationshipToParent = parent == null ? null : "subagent"
    };

    private static PresenceCardResult Context(PresenceSettings settings, T3Snapshot snapshot, T3UsageValue value) =>
        PresenceCardFormatter.Format(new PresenceCardInput
        {
            Settings = settings, Snapshot = snapshot, App = SupportedApp.T3Code, Now = Now,
            Timer = new PresenceTimerState(Now.AddSeconds(-600), Now),
            Usage = new T3UsageSnapshot { Timestamp = Now, PinnedTask = value }
        });

    private static PresenceSettings ContextSettings(string pinned) => new()
    {
        EnableSharing = true, ShowT3Activity = true, CardStyle = CardStyle.Usage, UsageMetric = UsageMetric.Context, PinnedTask = pinned
    };

    // Finding 1: pinned-task usage respects exclusions (own project or parent's project) and presence in the snapshot.
    [Fact]
    public void PinnedTaskUsageIsHiddenWhenExcludedOrMissing()
    {
        var value = new T3UsageValue { ContextUsedTokens = 124999, ContextMaxTokens = 200000 };
        var snapshot = Snapshot(Thread("a1", "p-orbit"), Thread("parent", "p-secret"), Thread("child", "p-orbit", parent: "parent"));

        Assert.Equal("62% of context used", Context(ContextSettings("env-a/a1"), snapshot, value).Activity!.State);

        var excluded = ContextSettings("env-a/a1");
        excluded.ExcludedProjects.Add("env-a/p-orbit");
        Assert.Equal("1 task", Context(excluded, Snapshot(Thread("a1", "p-orbit"), Thread("b", "p-other")), value).Activity!.State);

        var parentExcluded = ContextSettings("env-a/child");
        parentExcluded.ExcludedProjects.Add("env-a/p-secret");
        Assert.Equal("1 task", Context(parentExcluded, snapshot, value).Activity!.State);

        Assert.Equal("2 tasks · 1 helper", Context(ContextSettings("env-a/gone"), snapshot, value).Activity!.State);
    }

    // Finding 2: context is validated only on its own used/max, not on turn-token completeness.
    [Fact]
    public void ContextShowsEvenWhenTurnTokensArePartial()
    {
        var value = new T3UsageValue { ContextUsedTokens = 124999, ContextMaxTokens = 200000, Partial = true };
        Assert.Equal("62% of context used", Context(ContextSettings("env-a/a1"), Snapshot(Thread("a1", "p")), value).Activity!.State);

        // Tokens on the same partial value are still never shown.
        var tokens = ContextSettings("env-a/a1");
        tokens.UsageMetric = UsageMetric.Tokens;
        tokens.UsageScope = UsageScope.PinnedTask;
        value.Tokens = new T3TokenCounts { OutputTokens = 5 };
        Assert.Equal("1 task", Context(tokens, Snapshot(Thread("a1", "p")), value).Activity!.State);
    }

    // Finding 5: usage computed for a previous work session (or with no session now) falls back to workload.
    [Fact]
    public void SessionUsageFromAnotherWorkSessionIsRejected()
    {
        var settings = new PresenceSettings { EnableSharing = true, ShowT3Activity = true, CardStyle = CardStyle.Usage, UsageScope = UsageScope.Session };
        var start = Now.AddSeconds(-600);
        PresenceCardResult Run(DateTimeOffset? usageStart, DateTimeOffset? timerStart) => PresenceCardFormatter.Format(new PresenceCardInput
        {
            Settings = settings, Snapshot = Snapshot(Thread("a1", "p")), App = SupportedApp.T3Code, Now = Now,
            Timer = new PresenceTimerState(timerStart, Now),
            Usage = new T3UsageSnapshot { Timestamp = Now, SessionStart = usageStart,
                Session = new T3UsageValue { Tokens = new T3TokenCounts { OutputTokens = 1500 } } }
        });
        Assert.Equal("1.5K tokens this session", Run(start, start).Activity!.State);
        Assert.Equal("1 task", Run(start.AddSeconds(-1000), start).Activity!.State);
        Assert.Equal("1 task", Run(start, null).Activity!.State);
    }
}

public partial class MultiEnvironmentTests
{
    private sealed class PendingOAuth : IT3OAuthClient
    {
        public TaskCompletionSource<T3StoredToken> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T3StoredToken> SignInAsync(Uri normalizedMcpUri, CancellationToken ct = default) => Completion.Task;
    }

    // Finding 3: a second environment signing in must not stop the connected one from contributing.
    [Fact]
    public async Task SigningInToAnotherEnvironmentKeepsConnectedOnePolling()
    {
        var settings = new MockSettingsService();
        settings.Settings.T3Environments.Add(new T3Environment { Id = "env-a", Address = EnvA });
        settings.Settings.T3Environments.Add(new T3Environment { Id = "env-b", Address = EnvB });
        var store = new InMemoryCredentialStore();
        await store.SaveCredentialAsync(T3ActivityService.CredentialTargetFor(EnvA), T3ActivityService.CredentialUsername, JsonSerializer.Serialize(Token(EnvA)));
        var mcp = new ScriptedMcp();
        mcp.Threads[EnvA] = () => new T3ThreadListResult { Total = 1, Threads = new() { Thread("a1", "running") } };
        var oauth = new PendingOAuth();
        await using var service = new T3ActivityService(settings, store, oauth, mcp);
        await service.InitializeAsync();

        var signIn = service.ConnectEnvironmentAsync("env-b");
        Assert.Equal(T3ConnectionStatus.OpeningSignIn, service.ConnectionStatus);
        Assert.True(service.AnyEnvironmentConnected);
        var snapshot = await service.PollOnceAsync();
        Assert.Equal("env-a", Assert.Single(snapshot!.Environments).EnvironmentId);
        oauth.Completion.SetException(new OperationCanceledException());
        await signIn;
    }

    // Finding 6: a slow environment doesn't block polls of a healthy one.
    [Fact]
    public async Task SlowEnvironmentDoesNotBlockHealthyEnvironment()
    {
        var (_, _, mcp, service) = await Setup();
        await using var _s = service;
        var slow = new TaskCompletionSource<T3ThreadListResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int healthyPolls = 0;
        mcp.AsyncThreads[EnvA] = () => slow.Task;
        mcp.Threads[EnvB] = () => { healthyPolls++; return new T3ThreadListResult { Total = 1, Threads = new() { Thread("b1", "running") } }; };

        var first = service.PollOnceAsync();
        await TestWait.Until(() => healthyPolls == 1);
        var second = await service.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, healthyPolls);
        Assert.Equal("env-b", Assert.Single(second!.Environments).EnvironmentId);
        slow.SetResult(new T3ThreadListResult { Total = 0, Threads = new() });
        await first;
    }
}

public partial class SelectionAndUsagePollingTests
{
    private static void ResetPollClock(PresenceCoordinator coordinator) =>
        typeof(PresenceCoordinator).GetField("_lastT3PollTimestamp", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(coordinator, DateTimeOffset.MinValue);

    private static MockSettingsService SharingSettings()
    {
        var settings = new MockSettingsService();
        settings.Settings.EnableSharing = true;
        settings.Settings.ShowT3Activity = true;
        settings.Settings.DiscordClientId = ValidClientId;
        return settings;
    }

    // Finding 3 (coordinator): eligibility is "any environment connected", not the combined status.
    [Fact]
    public async Task CoordinatorKeepsRichCardWhileAnotherEnvironmentSignsIn()
    {
        var t3 = new MockT3ActivityService
        {
            ConnectionStatus = T3ConnectionStatus.OpeningSignIn, AnyConnectedOverride = true, CurrentSnapshot = Working()
        };
        await using var coordinator = new PresenceCoordinator(SharingSettings(), new MockDiscordClient(), new ForegroundDetector(() => "t3code.exe"), t3);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal("T3 Code · Claude Code", coordinator.CurrentState.OutgoingHeadline);
        Assert.Equal(1, t3.PollOnceCallCount);
    }

    private sealed class PendingPolls : IT3ActivityService
    {
        public int Calls;
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
        public Task<T3Snapshot?> PollOnceAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return new TaskCompletionSource<T3Snapshot?>().Task; // never finishes
        }
        public void ClearSnapshot() { }
        public void Invalidate() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Finding 6 (coordinator): an unfinished round doesn't block the next 15-second round.
    [Fact]
    public async Task UnfinishedPollRoundDoesNotBlockNextRound()
    {
        var t3 = new PendingPolls();
        await using var coordinator = new PresenceCoordinator(SharingSettings(), new MockDiscordClient(), new ForegroundDetector(() => "t3code.exe"), t3);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal(1, t3.Calls);
        ResetPollClock(coordinator);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.Equal(2, t3.Calls);
    }

    // Finding 5 (coordinator): usage from an earlier work session is cleared and requested again.
    [Fact]
    public async Task UsageFromPreviousSessionIsClearedAndRepolled()
    {
        var settings = SharingSettings();
        settings.Settings.CardStyle = CardStyle.Usage;
        settings.Settings.UsageScope = UsageScope.Session;
        var usage = new CountingUsage
        {
            CurrentSnapshot = new T3UsageSnapshot
            {
                Timestamp = DateTimeOffset.UtcNow, SessionStart = DateTimeOffset.UtcNow.AddHours(-3),
                Session = new T3UsageValue { Tokens = new T3TokenCounts { OutputTokens = 999_000 } }
            }
        };
        var t3 = new MockT3ActivityService { CurrentSnapshot = Working() };
        await using var coordinator = new PresenceCoordinator(settings, new MockDiscordClient(), new ForegroundDetector(() => "t3code.exe"), t3, null, usage);
        await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);
        Assert.True(usage.Invalidations >= 1);
        await TestWait.Until(() => usage.Polls == 1);
        Assert.Equal("2 tasks", coordinator.CurrentState.OutgoingState);
    }
}

public partial class T3UsageTransportTests
{
    private static T3Snapshot SnapshotWith(string environmentId, params T3ThreadListItem[] threads) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Environments = new[] { new T3EnvironmentSnapshot { EnvironmentId = environmentId, Timestamp = DateTimeOffset.UtcNow,
            Drivers = new Dictionary<string, string> { ["c"] = "claudeAgent" }, Threads = threads } }
    };

    private static T3ThreadListItem Row(string id, string project, string? parent = null) => new()
    {
        ThreadId = id, ProviderInstanceId = "c", Status = "running", ProjectId = project, ParentThreadId = parent,
        RelationshipToParent = parent == null ? null : "subagent"
    };

    // Finding 1 (service): an excluded or missing pinned task is never requested.
    [Fact]
    public async Task PinnedTaskOutsideFilteredSnapshotIsNeverRequested()
    {
        var (settings, _, sockets, _, service) = await UsageSetup();
        await service.PairAsync("env-a", "AbC123xyz");
        settings.Settings.CardStyle = CardStyle.Usage;
        settings.Settings.UsageMetric = UsageMetric.Context;
        settings.Settings.PinnedTask = "env-a/child";
        settings.Settings.ExcludedProjects.Add("env-a/secret");
        var snapshot = SnapshotWith("env-a", Row("parent", "secret"), Row("child", "open", parent: "parent"));

        Assert.Null(await service.PollOnceAsync(new T3UsagePollRequest { Settings = settings.Settings, Snapshot = snapshot }));
        Assert.Null(await service.PollOnceAsync(new T3UsagePollRequest { Settings = settings.Settings, Snapshot = null }));
        settings.Settings.PinnedTask = "env-a/not-listed";
        Assert.Null(await service.PollOnceAsync(new T3UsagePollRequest { Settings = settings.Settings, Snapshot = snapshot }));
        Assert.Empty(sockets.Connections);

        settings.Settings.ExcludedProjects.Clear();
        settings.Settings.PinnedTask = "env-a/child";
        sockets.Reply = _ => new[] { "{\"_tag\":\"Exit\",\"requestId\":\"1\",\"exit\":{\"_tag\":\"Success\",\"value\":" + Projection + "}}" };
        var usage = await service.PollOnceAsync(new T3UsagePollRequest { Settings = settings.Settings, Snapshot = snapshot });
        Assert.Single(sockets.Connections);
        Assert.Equal(124999, usage!.PinnedTask!.ContextUsedTokens);
    }

    // Finding 4: a session thread in an environment without usage permission makes the session partial.
    [Fact]
    public async Task SessionThreadWithoutPermissionMakesSessionPartial()
    {
        var (settings, _, sockets, _, service) = await UsageSetup();
        settings.Settings.T3Environments.Add(new T3Environment { Id = "env-b", Address = "https://other.example/mcp" });
        await service.PairAsync("env-a", "AbC123xyz");
        settings.Settings.UsageScope = UsageScope.Session;
        var now = DateTimeOffset.UtcNow;
        var snapshot = new T3Snapshot
        {
            Timestamp = now,
            Environments = new[]
            {
                SnapshotWith("env-a", Row("a1", "p")).Environments[0],
                SnapshotWith("env-b", Row("b1", "p")).Environments[0]
            }
        };
        sockets.Reply = _ => new[] { "{\"_tag\":\"Exit\",\"requestId\":\"1\",\"exit\":{\"_tag\":\"Success\",\"value\":{\"providerTurns\":[]}}}" };
        var usage = await service.PollOnceAsync(new T3UsagePollRequest { Settings = settings.Settings, Snapshot = snapshot, WorkSessionStart = now.AddMinutes(-5) });
        Assert.True(usage!.Session!.Partial);
        Assert.Single(sockets.Connections);
        Assert.Null(PresenceCardFormatter.UsageLine(settings.Settings, usage, new PresenceTimerState(usage.SessionStart, now), now).Text);
    }

    // Finding 7: removing token access during a pending exchange discards the late credential.
    [Fact]
    public async Task RemoveDuringPairingDoesNotRecreateCredential()
    {
        var settings = new MockSettingsService();
        settings.Settings.T3Environments.Add(new T3Environment { Id = "env-a", Address = "https://remote.example:8443/mcp" });
        var store = new InMemoryCredentialStore();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelayedHandler(started, release);
        var service = new T3UsageService(settings, store, new HttpClient(handler), new FakeSockets());

        var pairing = service.PairAsync("env-a", "AbC123xyz");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.RemoveTokenAccessAsync("env-a");
        release.SetResult();
        var result = await pairing;

        Assert.NotEqual(T3PairingOutcome.Success, result.Outcome);
        Assert.False(service.HasPermission("env-a"));
        Assert.Null(await store.ReadCredentialAsync("deskchirp-usage:https://remote.example:8443", T3UsageService.CredentialUsername));

        // A fresh pairing after removal still works.
        handler.Immediate = true;
        Assert.Equal(T3PairingOutcome.Success, (await service.PairAsync("env-a", "AbC123xyz")).Outcome);
        Assert.True(service.HasPermission("env-a"));
    }

    private sealed class DelayedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started, _release;
        public bool Immediate { get; set; }
        public DelayedHandler(TaskCompletionSource started, TaskCompletionSource release) { _started = started; _release = release; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Immediate)
            {
                _started.TrySetResult();
                // Ignores cancellation on purpose: the server answers after removal anyway.
                await _release.Task;
            }
            return Json(HttpStatusCode.OK,
                "{\"access_token\":\"synthetic-usage\",\"token_type\":\"Bearer\",\"expires_in\":2592000,\"scope\":\"orchestration:read diagnostics:read\"}");
        }
    }
}

public class ReviewRoundTwoTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly Dictionary<string, string> Drivers = new() { ["claude"] = "claudeAgent" };

    private static T3EnvironmentSnapshot Env(string id, params T3ThreadListItem[] threads) =>
        new() { EnvironmentId = id, Timestamp = Now, Drivers = Drivers, Threads = threads };

    private static T3ThreadListItem Running(string id, string project) =>
        new() { ThreadId = id, ProviderInstanceId = "claude", Status = "running", ProjectId = project };

    // A: pinned-task usage for an environment missing from the snapshot is never shown.
    [Fact]
    public void PinnedTaskInEnvironmentMissingFromSnapshotIsUnavailable()
    {
        var settings = new PresenceSettings { EnableSharing = true, ShowT3Activity = true, CardStyle = CardStyle.Usage,
            UsageMetric = UsageMetric.Context, PinnedTask = "env-a/a1" };
        var snapshot = new T3Snapshot { Timestamp = Now, Environments = new[] { Env("env-b", Running("b1", "p")) } };
        Assert.False(PresenceCardFormatter.IsPinnedTaskVisible(settings, snapshot, Now));
        Assert.False(PresenceCardFormatter.IsPinnedTaskVisible(settings, null, Now));
        var result = PresenceCardFormatter.Format(new PresenceCardInput
        {
            Settings = settings, Snapshot = snapshot, App = SupportedApp.T3Code, Now = Now,
            Usage = new T3UsageSnapshot { Timestamp = Now, PinnedTaskKey = "env-a/a1",
                PinnedTask = new T3UsageValue { ContextUsedTokens = 1, ContextMaxTokens = 2 } }
        });
        Assert.Equal("1 task", result.Activity!.State);
    }

    // B: a pinned project with no unfinished work means no fresh work, never other projects' counts.
    [Fact]
    public void PinnedProjectWithoutWorkShowsAppOnly()
    {
        var settings = new PresenceSettings { EnableSharing = true, ShowT3Activity = true, PinnedProject = "env-a/quiet" };
        var snapshot = new T3Snapshot { Timestamp = Now, Environments = new[] { Env("env-a", Running("a1", "busy")) } };
        var result = PresenceCardFormatter.Format(new PresenceCardInput { Settings = settings, Snapshot = snapshot, App = SupportedApp.T3Code, Now = Now });
        Assert.Equal("Using T3 Code", result.Activity!.Details);
        Assert.Null(result.Activity.State);
        Assert.False(PresenceCardFormatter.HasFreshUnfinishedWork(settings, snapshot, Now));

        // Workload-only snapshots can't confirm the project either.
        var legacy = new T3Snapshot { Timestamp = Now, WinningHarness = new T3HarnessWorkload { HarnessName = "Claude Code", RunningTasks = 1 } };
        Assert.False(PresenceCardFormatter.HasFreshUnfinishedWork(settings, legacy, Now));
    }

    [Fact]
    public void PinnedProjectNotFoundOnlyAfterConnectedCatalogsLoad()
    {
        var settings = new PresenceSettings { PinnedProject = "env-a/gone" };
        var loaded = new[] { new T3EnvironmentStatus { EnvironmentId = "env-a", Status = T3ConnectionStatus.Connected, ProjectCatalogLoaded = true } };
        var notLoaded = new[] { new T3EnvironmentStatus { EnvironmentId = "env-a", Status = T3ConnectionStatus.Connected } };
        var catalog = new[] { new T3PickerProject("env-a", "p1", "Local") };
        Assert.True(PresenceAvailability.IsPinnedProjectMissing(settings, loaded, catalog));
        Assert.False(PresenceAvailability.IsPinnedProjectMissing(settings, notLoaded, catalog));
        settings.PinnedProject = "env-a/p1";
        Assert.False(PresenceAvailability.IsPinnedProjectMissing(settings, loaded, catalog));
    }
}

public partial class MultiEnvironmentTests
{
    // B: the t3_project_list catalog travels in the environment snapshot (ids only, no titles).
    [Fact]
    public async Task ProjectCatalogIsCarriedInEnvironmentSnapshot()
    {
        var (_, _, mcp, service) = await Setup();
        await using var _s = service;
        mcp.Threads[EnvA] = () => new T3ThreadListResult { Total = 0, Threads = new() };
        mcp.Threads[EnvB] = () => new T3ThreadListResult { Total = 0, Threads = new() };
        var snapshot = await service.PollOnceAsync();
        Assert.All(snapshot!.Environments, e => Assert.Equal("p1", Assert.Single(e.Projects!).ProjectId));
        Assert.All(snapshot.Environments, e => Assert.Null(e.Projects!.Single().Title));
        Assert.All(service.Environments, e => Assert.True(e.ProjectCatalogLoaded));
    }

    private sealed class PerAddressOAuth : IT3OAuthClient
    {
        public Dictionary<string, TaskCompletionSource<T3StoredToken>> Pending { get; } = new();
        public Task<T3StoredToken> SignInAsync(Uri uri, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<T3StoredToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending[uri.AbsoluteUri] = tcs;
            ct.Register(() => tcs.TrySetCanceled());
            return tcs.Task;
        }
    }

    // C: each environment's sign-in is independent of adding, removing, or cancelling another.
    [Fact]
    public async Task OtherEnvironmentChangesDoNotCancelPendingSignIn()
    {
        var settings = new MockSettingsService();
        settings.Settings.T3Environments.Add(new T3Environment { Id = "env-a", Address = EnvA });
        settings.Settings.T3Environments.Add(new T3Environment { Id = "env-b", Address = EnvB });
        var store = new InMemoryCredentialStore();
        var oauth = new PerAddressOAuth();
        await using var service = new T3ActivityService(settings, store, oauth, new ScriptedMcp());
        await service.InitializeAsync();

        var signInA = service.ConnectEnvironmentAsync("env-a");
        var signInB = service.ConnectEnvironmentAsync("env-b");
        await service.CancelSignInAsync("env-b");
        Assert.False(await signInB);
        Assert.Null(await service.AddEnvironmentAsync("https://c.example"));
        var signInC = service.ConnectEnvironmentAsync(settings.Settings.T3Environments.Last().Id);
        await service.RemoveEnvironmentAsync(settings.Settings.T3Environments.Last().Id);
        Assert.False(await signInC);

        Assert.Equal(T3ConnectionStatus.OpeningSignIn, service.Environments.Single(e => e.EnvironmentId == "env-a").Status);
        oauth.Pending[EnvA].SetResult(Token(EnvA));
        Assert.True(await signInA);
        Assert.Equal(T3ConnectionStatus.Connected, service.Environments.Single(e => e.EnvironmentId == "env-a").Status);
        Assert.NotNull(await store.ReadCredentialAsync(T3ActivityService.CredentialTargetFor(EnvA), T3ActivityService.CredentialUsername));
        Assert.Null(await store.ReadCredentialAsync(T3ActivityService.CredentialTargetFor(EnvB), T3ActivityService.CredentialUsername));
    }
}

public partial class T3UsageTransportTests
{
    // A (service): pinned task whose environment is absent from the snapshot is never requested.
    [Fact]
    public async Task PinnedTaskInAbsentEnvironmentIsNeverRequested()
    {
        var (settings, _, sockets, _, service) = await UsageSetup();
        await service.PairAsync("env-a", "AbC123xyz");
        settings.Settings.CardStyle = CardStyle.Usage;
        settings.Settings.UsageMetric = UsageMetric.Context;
        settings.Settings.PinnedTask = "env-a/a1";
        var otherOnly = SnapshotWith("env-b", Row("b1", "p"));
        Assert.Null(await service.PollOnceAsync(new T3UsagePollRequest { Settings = settings.Settings, Snapshot = otherOnly }));
        Assert.Empty(sockets.Connections);
    }
}
