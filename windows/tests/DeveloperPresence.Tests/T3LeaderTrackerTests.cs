using DeveloperPresence.Core.T3;
using Xunit;

namespace DeveloperPresence.Tests;

public class T3LeaderTrackerTests
{
    [Fact]
    public void ChallengerLead_Requires10SecondsSustainedLead()
    {
        var tracker = new T3LeaderTracker();
        var t0 = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        // Initial: Antigravity has 2 running, Claude has 1 running
        var ag = new T3HarnessWorkload { HarnessName = "Antigravity", RunningTasks = 2 };
        var claude = new T3HarnessWorkload { HarnessName = "Claude Code", RunningTasks = 1 };

        var winner0 = tracker.SelectLeader(new[] { ag, claude }, t0);
        Assert.NotNull(winner0);
        Assert.Equal("Antigravity", winner0.HarnessName);

        // At t = 5s: Claude surges to 3 running tasks
        var t5 = t0.AddSeconds(5);
        claude.RunningTasks = 3;

        var winner5 = tracker.SelectLeader(new[] { ag, claude }, t5);
        // Less than 10 seconds has passed: Antigravity is retained
        Assert.NotNull(winner5);
        Assert.Equal("Antigravity", winner5.HarnessName);
        Assert.Equal("Claude Code", tracker.ChallengerName);

        // At t = 9.9s: Still under 10 seconds
        var t9 = t0.AddSeconds(9.9);
        var winner9 = tracker.SelectLeader(new[] { ag, claude }, t9);
        Assert.NotNull(winner9);
        Assert.Equal("Antigravity", winner9.HarnessName);

        // At t = 15.1s (10.1s after challenge began at t5): Dethrone!
        var t15 = t0.AddSeconds(15.1);
        var winner15 = tracker.SelectLeader(new[] { ag, claude }, t15);
        Assert.NotNull(winner15);
        Assert.Equal("Claude Code", winner15.HarnessName);
        Assert.Null(tracker.ChallengerName);
    }

    [Fact]
    public void ImmediateLead_WhenCurrentWinnerHasNoRunningWork()
    {
        var tracker = new T3LeaderTracker();
        var t0 = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        var ag = new T3HarnessWorkload { HarnessName = "Antigravity", RunningTasks = 1 };
        var claude = new T3HarnessWorkload { HarnessName = "Claude Code", RunningTasks = 0 };

        var winner0 = tracker.SelectLeader(new[] { ag, claude }, t0);
        Assert.Equal("Antigravity", winner0?.HarnessName);

        // At t = 1s: Antigravity has finished (0 running), Claude has 1 running task
        var t1 = t0.AddSeconds(1);
        ag.RunningTasks = 0;
        claude.RunningTasks = 1;

        // Claude wins immediately without 10-second delay
        var winner1 = tracker.SelectLeader(new[] { ag, claude }, t1);
        Assert.Equal("Claude Code", winner1?.HarnessName);
    }

    [Fact]
    public void Ties_RetainCurrentWinner()
    {
        var tracker = new T3LeaderTracker();
        var t0 = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        var ag = new T3HarnessWorkload { HarnessName = "Antigravity", RunningTasks = 2 };
        var claude = new T3HarnessWorkload { HarnessName = "Claude Code", RunningTasks = 1 };

        var winner0 = tracker.SelectLeader(new[] { ag, claude }, t0);
        Assert.Equal("Antigravity", winner0?.HarnessName);

        // At t = 15s: Claude ties Antigravity with 2 running tasks
        var t15 = t0.AddSeconds(15);
        claude.RunningTasks = 2;

        var winnerTied = tracker.SelectLeader(new[] { ag, claude }, t15);
        Assert.Equal("Antigravity", winnerTied?.HarnessName);
    }

    [Fact]
    public void Fallbacks_WaitingThenStartingThenNull()
    {
        var tracker = new T3LeaderTracker();
        var now = DateTimeOffset.UtcNow;

        // Case 1: No running work; Claude has 1 waiting task
        var cWait = new T3HarnessWorkload { HarnessName = "Claude Code", WaitingTasks = 1 };
        var agIdle = new T3HarnessWorkload { HarnessName = "Antigravity" };
        var winnerWait = tracker.SelectLeader(new[] { cWait, agIdle }, now);
        Assert.Equal("Claude Code", winnerWait?.HarnessName);

        // Case 2: No running and no waiting; Antigravity has 1 starting task
        tracker.Reset();
        var agStart = new T3HarnessWorkload { HarnessName = "Antigravity", StartingTasks = 1 };
        var winnerStart = tracker.SelectLeader(new[] { agStart }, now);
        Assert.Equal("Antigravity", winnerStart?.HarnessName);

        // Case 3: All zero -> winner is null
        tracker.Reset();
        var allZero = new T3HarnessWorkload { HarnessName = "Antigravity" };
        var winnerNone = tracker.SelectLeader(new[] { allZero }, now);
        Assert.Null(winnerNone);
    }
}
