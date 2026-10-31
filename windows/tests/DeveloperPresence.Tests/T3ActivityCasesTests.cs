using System.Text.Json;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;
using DeveloperPresence.Core.T3;
using Xunit;

namespace DeveloperPresence.Tests;

public class T3ActivityCasesTests
{
    private static string FindSpecPath(string filename)
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "spec", filename);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir)
            {
                break;
            }
            dir = parent;
        }
        throw new FileNotFoundException($"Spec file '{filename}' could not be located.");
    }

    private sealed class T3CasesFile
    {
        public string? Description { get; set; }
        public List<T3ProviderCapability> Providers { get; set; } = new();
        public List<T3TestCase> Cases { get; set; } = new();
    }

    private sealed class T3TestCase
    {
        public string Name { get; set; } = string.Empty;
        public List<T3ThreadListItem> Threads { get; set; } = new();
        public T3Expected Expected { get; set; } = new();
    }

    private sealed class T3Expected
    {
        public string Details { get; set; } = string.Empty;
        public string? State { get; set; }
    }

    [Fact]
    public async Task SpecCasesParity_MatchesJsonSpecification()
    {
        string path = FindSpecPath("t3-activity-cases.json");
        string json = await File.ReadAllTextAsync(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        var fixture = JsonSerializer.Deserialize<T3CasesFile>(json, options);
        Assert.NotNull(fixture);
        Assert.NotEmpty(fixture.Cases);

        foreach (var tc in fixture.Cases)
        {
            var workloads = T3WorkloadAggregator.AggregateThreads(fixture.Providers, tc.Threads);
            var tracker = new T3LeaderTracker();
            var winner = tracker.SelectLeader(workloads, DateTimeOffset.UtcNow);
            string? state = winner != null ? T3WorkloadAggregator.FormatState(winner) : null;
            string details = winner != null ? $"T3 Code · {winner.HarnessName}" : "Using T3 Code";

            var snapshot = new T3Snapshot
            {
                Timestamp = DateTimeOffset.UtcNow,
                IsValid = true,
                Workloads = workloads,
                WinningHarness = winner,
                WinningState = state,
                Details = details,
                TotalThreadsObserved = tc.Threads.Count
            };

            var activity = ActivityFormatter.FormatT3Activity(snapshot, true, false, true, false);
            Assert.NotNull(activity);

            Assert.True(
                string.Equals(activity.Details, tc.Expected.Details, StringComparison.Ordinal),
                $"Case '{tc.Name}' Details mismatch: expected '{tc.Expected.Details}', got '{activity.Details}'");

            Assert.True(
                string.Equals(activity.State, tc.Expected.State, StringComparison.Ordinal),
                $"Case '{tc.Name}' State mismatch: expected '{tc.Expected.State}', got '{activity.State}'");
        }
    }

    [Fact]
    public void PluralizationRules_SingularAndPluralTasksAndHelpers()
    {
        // 1 running task
        var w1 = new T3HarnessWorkload { HarnessName = "Antigravity", RunningTasks = 1 };
        Assert.Equal("1 task", T3WorkloadAggregator.FormatState(w1));

        // 2 running tasks
        var w2 = new T3HarnessWorkload { HarnessName = "Antigravity", RunningTasks = 2 };
        Assert.Equal("2 tasks", T3WorkloadAggregator.FormatState(w2));

        // 1 running helper
        var w3 = new T3HarnessWorkload { HarnessName = "Antigravity", RunningHelpers = 1 };
        Assert.Equal("1 helper", T3WorkloadAggregator.FormatState(w3));

        // 3 running helpers
        var w4 = new T3HarnessWorkload { HarnessName = "Antigravity", RunningHelpers = 3 };
        Assert.Equal("3 helpers", T3WorkloadAggregator.FormatState(w4));

        // 1 task and 2 helpers
        var w5 = new T3HarnessWorkload { HarnessName = "Antigravity", RunningTasks = 1, RunningHelpers = 2 };
        Assert.Equal("1 task · 2 helpers", T3WorkloadAggregator.FormatState(w5));

        // 2 tasks and 1 helper
        var w6 = new T3HarnessWorkload { HarnessName = "Antigravity", RunningTasks = 2, RunningHelpers = 1 };
        Assert.Equal("2 tasks · 1 helper", T3WorkloadAggregator.FormatState(w6));

        // Waiting tasks and helpers
        var wWait = new T3HarnessWorkload { HarnessName = "Antigravity", WaitingTasks = 1, WaitingHelpers = 1 };
        Assert.Equal("1 task waiting · 1 helper waiting", T3WorkloadAggregator.FormatState(wWait));

        var wWait2 = new T3HarnessWorkload { HarnessName = "Antigravity", WaitingTasks = 3 };
        Assert.Equal("3 tasks waiting", T3WorkloadAggregator.FormatState(wWait2));

        // Starting tasks and helpers
        var wStart = new T3HarnessWorkload { HarnessName = "Antigravity", StartingTasks = 1, StartingHelpers = 2 };
        Assert.Equal("1 task starting · 2 helpers starting", T3WorkloadAggregator.FormatState(wStart));
    }

    [Fact]
    public void AppIconsAssets_IncludedOnlyWhenEnabled()
    {
        var workload = new T3HarnessWorkload
        {
            HarnessName = "Claude Code",
            SmallIcon = "harness_claude",
            RunningTasks = 1
        };
        var snapshot = new T3Snapshot
        {
            IsValid = true,
            WinningHarness = workload,
            WinningState = "1 task",
            Details = "T3 Code · Claude Code"
        };

        var withIcons = ActivityFormatter.FormatT3Activity(snapshot, true, false, true, showAppIcons: true);
        Assert.NotNull(withIcons?.Assets);
        Assert.Equal("t3_code", withIcons.Assets.LargeImage);
        Assert.Equal("harness_claude", withIcons.Assets.SmallImage);
        Assert.Equal("Claude Code", withIcons.Assets.SmallText);

        var withoutIcons = ActivityFormatter.FormatT3Activity(snapshot, true, false, true, showAppIcons: false);
        Assert.Null(withoutIcons?.Assets);
    }
}
