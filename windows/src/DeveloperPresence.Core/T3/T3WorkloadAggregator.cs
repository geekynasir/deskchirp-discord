namespace DeveloperPresence.Core.T3;

public static class T3WorkloadAggregator
{
    private static readonly Dictionary<string, (string Name, string? SmallIcon)> KnownHarnesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claudeAgent"] = ("Claude Code", "harness_claude"),
            ["codex"] = ("Codex", "harness_codex"),
            ["antigravity"] = ("Antigravity", "harness_antigravity"),
            ["cursor"] = ("Cursor", null),
            ["opencode"] = ("OpenCode", null),
            ["grok"] = ("Grok", null),
            ["muse"] = ("Muse", null),
            ["pi"] = ("Pi", null)
        };

    public const string OtherToolsName = "Other tools";

    public static (string Name, string? SmallIcon) ResolveHarness(string? driverKind)
    {
        if (!string.IsNullOrWhiteSpace(driverKind) && KnownHarnesses.TryGetValue(driverKind, out var info))
        {
            return info;
        }
        return (OtherToolsName, null);
    }

    public static List<T3HarnessWorkload> AggregateThreads(
        IEnumerable<T3ProviderCapability> providers,
        IEnumerable<T3ThreadListItem> threads)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(threads);

        var providerToDriver = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in providers)
        {
            if (!string.IsNullOrWhiteSpace(p.ProviderInstanceId))
            {
                providerToDriver[p.ProviderInstanceId] = p.DriverKind ?? string.Empty;
            }
        }

        var workloads = new Dictionary<string, T3HarnessWorkload>(StringComparer.OrdinalIgnoreCase);
        var seenThreadIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var thread in threads)
        {
            if (string.IsNullOrWhiteSpace(thread.ThreadId)) continue;
            if (!seenThreadIds.Add(thread.ThreadId)) continue; // Deduplicate threads

            providerToDriver.TryGetValue(thread.ProviderInstanceId ?? "", out string? driverKind);
            var (harnessName, smallIcon) = ResolveHarness(driverKind);

            if (!workloads.TryGetValue(harnessName, out var workload))
            {
                workload = new T3HarnessWorkload
                {
                    HarnessName = harnessName,
                    SmallIcon = smallIcon
                };
                workloads[harnessName] = workload;
            }

            bool isHelper = string.Equals(thread.RelationshipToParent, "subagent", StringComparison.OrdinalIgnoreCase);
            string status = thread.Status?.Trim().ToLowerInvariant() ?? "";

            switch (status)
            {
                case "running":
                    if (isHelper) workload.RunningHelpers++;
                    else workload.RunningTasks++;
                    break;

                case "waiting":
                    if (isHelper) workload.WaitingHelpers++;
                    else workload.WaitingTasks++;
                    break;

                case "starting":
                case "queued":
                case "preparing":
                    if (isHelper) workload.StartingHelpers++;
                    else workload.StartingTasks++;
                    break;

                // Finished/cancelled/failed/idle rows do not contribute to live count
                default:
                    break;
            }
        }

        return workloads.Values.ToList();
    }

    public static string? FormatState(T3HarnessWorkload workload)
    {
        ArgumentNullException.ThrowIfNull(workload);

        if (workload.TotalRunning > 0)
        {
            var parts = new List<string>(2);
            if (workload.RunningTasks > 0)
            {
                parts.Add(workload.RunningTasks == 1 ? "1 task" : $"{workload.RunningTasks} tasks");
            }
            if (workload.RunningHelpers > 0)
            {
                parts.Add(workload.RunningHelpers == 1 ? "1 helper" : $"{workload.RunningHelpers} helpers");
            }
            return string.Join(" · ", parts);
        }

        if (workload.TotalWaiting > 0)
        {
            var parts = new List<string>(2);
            if (workload.WaitingTasks > 0)
            {
                parts.Add(workload.WaitingTasks == 1 ? "1 task waiting" : $"{workload.WaitingTasks} tasks waiting");
            }
            if (workload.WaitingHelpers > 0)
            {
                parts.Add(workload.WaitingHelpers == 1 ? "1 helper waiting" : $"{workload.WaitingHelpers} helpers waiting");
            }
            return string.Join(" · ", parts);
        }

        if (workload.TotalStarting > 0)
        {
            var parts = new List<string>(2);
            if (workload.StartingTasks > 0)
            {
                parts.Add(workload.StartingTasks == 1 ? "1 task starting" : $"{workload.StartingTasks} tasks starting");
            }
            if (workload.StartingHelpers > 0)
            {
                parts.Add(workload.StartingHelpers == 1 ? "1 helper starting" : $"{workload.StartingHelpers} helpers starting");
            }
            return string.Join(" · ", parts);
        }

        return null;
    }
}
