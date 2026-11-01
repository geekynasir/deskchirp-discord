namespace DeveloperPresence.Core.T3;

public enum T3Phase { None = 0, Running = 1, Waiting = 2, Starting = 3 }

/// <summary>One deduplicated, non-excluded thread with its public agent tool name.</summary>
public sealed record T3ThreadEntry(
    string EnvironmentId,
    T3ThreadListItem Thread,
    string HarnessName,
    string? HarnessIcon,
    bool IsHelper,
    T3Phase Phase)
{
    public string ThreadKey => T3WorkloadView.Key(EnvironmentId, Thread.ThreadId);
    public string? ProjectKey => string.IsNullOrEmpty(Thread.ProjectId) ? null : T3WorkloadView.Key(EnvironmentId, Thread.ProjectId);
}

/// <summary>
/// Pure presence-v3 aggregation over a combined multi-environment snapshot.
/// Thread identity is (environmentId, threadId). Exclusions apply before counting.
/// </summary>
public static class T3WorkloadView
{
    public static string Key(string environmentId, string id) => environmentId + "/" + id;

    public static T3Phase PhaseOf(string? status) => (status?.Trim().ToLowerInvariant() ?? "") switch
    {
        "running" => T3Phase.Running,
        "waiting" => T3Phase.Waiting,
        "starting" or "queued" or "preparing" => T3Phase.Starting,
        _ => T3Phase.None
    };

    /// <summary>
    /// Returns every distinct thread across environments, dropping threads in excluded projects and
    /// helpers (at any depth) whose parent resolves to an excluded thread.
    /// </summary>
    public static List<T3ThreadEntry> Entries(IEnumerable<T3EnvironmentSnapshot> environments, IReadOnlySet<string>? excludedProjects)
    {
        var result = new List<T3ThreadEntry>();
        foreach (var env in environments)
        {
            var byId = new Dictionary<string, T3ThreadListItem>(StringComparer.Ordinal);
            var order = new List<T3ThreadListItem>();
            foreach (var thread in env.Threads)
            {
                if (thread == null || string.IsNullOrWhiteSpace(thread.ThreadId)) continue;
                if (byId.TryAdd(thread.ThreadId, thread)) order.Add(thread); // duplicates inside one environment collapse
            }

            var excluded = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool IsExcluded(T3ThreadListItem thread, int depth)
            {
                if (excluded.TryGetValue(thread.ThreadId, out bool known)) return known;
                bool value = !string.IsNullOrEmpty(thread.ProjectId) && excludedProjects?.Contains(Key(env.EnvironmentId, thread.ProjectId)) == true;
                if (!value && depth < 32 && !string.IsNullOrEmpty(thread.ParentThreadId) &&
                    !string.Equals(thread.ParentThreadId, thread.ThreadId, StringComparison.Ordinal) &&
                    byId.TryGetValue(thread.ParentThreadId, out var parent))
                    value = IsExcluded(parent, depth + 1);
                excluded[thread.ThreadId] = value;
                return value;
            }

            foreach (var thread in order)
            {
                if (excludedProjects is { Count: > 0 } && IsExcluded(thread, 0)) continue;
                env.Drivers.TryGetValue(thread.ProviderInstanceId ?? string.Empty, out string? driver);
                var (name, icon) = T3WorkloadAggregator.ResolveHarness(driver);
                bool helper = string.Equals(thread.RelationshipToParent, "subagent", StringComparison.OrdinalIgnoreCase);
                result.Add(new T3ThreadEntry(env.EnvironmentId, thread, name, icon, helper, PhaseOf(thread.Status)));
            }
        }
        return result;
    }

    public static List<T3HarnessWorkload> Aggregate(IEnumerable<T3ThreadEntry> entries)
    {
        var workloads = new Dictionary<string, T3HarnessWorkload>(StringComparer.OrdinalIgnoreCase);
        var order = new List<T3HarnessWorkload>();
        foreach (var entry in entries)
        {
            if (!workloads.TryGetValue(entry.HarnessName, out var workload))
            {
                workload = new T3HarnessWorkload { HarnessName = entry.HarnessName, SmallIcon = entry.HarnessIcon };
                workloads[entry.HarnessName] = workload;
                order.Add(workload);
            }
            switch (entry.Phase)
            {
                case T3Phase.Running: if (entry.IsHelper) workload.RunningHelpers++; else workload.RunningTasks++; break;
                case T3Phase.Waiting: if (entry.IsHelper) workload.WaitingHelpers++; else workload.WaitingTasks++; break;
                case T3Phase.Starting: if (entry.IsHelper) workload.StartingHelpers++; else workload.StartingTasks++; break;
            }
        }
        return order;
    }

    /// <summary>The phase the featured tool's state line shows: running, then waiting, then starting.</summary>
    public static T3Phase LeadingPhase(T3HarnessWorkload workload) =>
        workload.TotalRunning > 0 ? T3Phase.Running :
        workload.TotalWaiting > 0 ? T3Phase.Waiting :
        workload.TotalStarting > 0 ? T3Phase.Starting : T3Phase.None;
}
