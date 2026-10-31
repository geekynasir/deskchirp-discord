namespace DeveloperPresence.Core.T3;

public sealed class T3HarnessWorkload : IEquatable<T3HarnessWorkload>
{
    public string HarnessName { get; init; } = string.Empty;
    public string? SmallIcon { get; init; }

    public int RunningTasks { get; set; }
    public int RunningHelpers { get; set; }
    public int WaitingTasks { get; set; }
    public int WaitingHelpers { get; set; }
    public int StartingTasks { get; set; }
    public int StartingHelpers { get; set; }

    public int TotalRunning => RunningTasks + RunningHelpers;
    public int TotalWaiting => WaitingTasks + WaitingHelpers;
    public int TotalStarting => StartingTasks + StartingHelpers;
    public int TotalActive => TotalRunning + TotalWaiting + TotalStarting;

    public bool Equals(T3HarnessWorkload? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(HarnessName, other.HarnessName, StringComparison.Ordinal) &&
               string.Equals(SmallIcon, other.SmallIcon, StringComparison.Ordinal) &&
               RunningTasks == other.RunningTasks &&
               RunningHelpers == other.RunningHelpers &&
               WaitingTasks == other.WaitingTasks &&
               WaitingHelpers == other.WaitingHelpers &&
               StartingTasks == other.StartingTasks &&
               StartingHelpers == other.StartingHelpers;
    }

    public override bool Equals(object? obj) => Equals(obj as T3HarnessWorkload);

    public override int GetHashCode() =>
        HashCode.Combine(HarnessName, RunningTasks, RunningHelpers, WaitingTasks, WaitingHelpers);
}

public sealed class T3Snapshot : IEquatable<T3Snapshot>
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public bool IsValid { get; init; } = true;
    public IReadOnlyList<T3HarnessWorkload> Workloads { get; init; } = Array.Empty<T3HarnessWorkload>();
    public T3HarnessWorkload? WinningHarness { get; init; }
    public string? WinningState { get; init; }
    public string Details { get; init; } = "Using T3 Code";
    public int TotalThreadsObserved { get; init; }

    public bool Equals(T3Snapshot? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (IsValid != other.IsValid) return false;
        if (!string.Equals(Details, other.Details, StringComparison.Ordinal)) return false;
        if (!string.Equals(WinningState, other.WinningState, StringComparison.Ordinal)) return false;
        if (TotalThreadsObserved != other.TotalThreadsObserved) return false;

        if (WinningHarness is null != (other.WinningHarness is null)) return false;
        if (WinningHarness != null && !WinningHarness.Equals(other.WinningHarness)) return false;

        if (Workloads.Count != other.Workloads.Count) return false;
        for (int i = 0; i < Workloads.Count; i++)
        {
            if (!Workloads[i].Equals(other.Workloads[i])) return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as T3Snapshot);

    public override int GetHashCode() =>
        HashCode.Combine(IsValid, Details, WinningState, TotalThreadsObserved);

    public static bool AreEquivalent(T3Snapshot? a, T3Snapshot? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.Equals(b);
    }
}
