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

    /// <summary>
    /// Fresh per-environment metadata behind this combined snapshot (presence v3). Empty for
    /// snapshots built only from workloads (older callers and tests); the formatter then uses
    /// <see cref="WinningHarness"/> directly.
    /// </summary>
    public IReadOnlyList<T3EnvironmentSnapshot> Environments { get; init; } = Array.Empty<T3EnvironmentSnapshot>();

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

        if (Environments.Count != other.Environments.Count) return false;
        for (int i = 0; i < Environments.Count; i++)
        {
            if (!Environments[i].ContentEquals(other.Environments[i])) return false;
        }

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

/// <summary>
/// One environment's decoded, privacy-filtered thread-list metadata from a single complete poll.
/// Titles are kept only for the local pinned-task picker.
/// </summary>
public sealed class T3EnvironmentSnapshot
{
    public string EnvironmentId { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>providerInstanceId → driverKind. Display names are never decoded.</summary>
    public IReadOnlyDictionary<string, string> Drivers { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<T3ThreadListItem> Threads { get; init; } = Array.Empty<T3ThreadListItem>();
    /// <summary>Project titles, for the local alias editor and pickers only.</summary>
    public IReadOnlyList<T3ProjectListItem> Projects { get; init; } = Array.Empty<T3ProjectListItem>();

    public bool ContentEquals(T3EnvironmentSnapshot other)
    {
        if (!string.Equals(EnvironmentId, other.EnvironmentId, StringComparison.Ordinal)) return false;
        if (Threads.Count != other.Threads.Count || Drivers.Count != other.Drivers.Count) return false;
        foreach (var (k, v) in Drivers)
            if (!other.Drivers.TryGetValue(k, out var o) || !string.Equals(v, o, StringComparison.Ordinal)) return false;
        for (int i = 0; i < Threads.Count; i++)
        {
            var a = Threads[i]; var b = other.Threads[i];
            if (a.ThreadId != b.ThreadId || a.Status != b.Status || a.ProviderInstanceId != b.ProviderInstanceId ||
                a.RelationshipToParent != b.RelationshipToParent || a.ParentThreadId != b.ParentThreadId ||
                a.ProjectId != b.ProjectId || a.Model != b.Model || a.UpdatedAt != b.UpdatedAt) return false;
        }
        return true;
    }
}
