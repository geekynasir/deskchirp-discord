using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Services;
using DeveloperPresence.Core.T3;
using DeveloperPresence.Core.T3.Usage;

namespace DeveloperPresence.Core.Privacy;

public static class PreviewSource
{
    public const string Deskchirp = "Deskchirp";
    public const string T3Code = "T3 Code";
    public const string T3Usage = "T3 usage";
}

/// <summary>One outgoing field shown in the settings preview, with where its value came from.</summary>
public sealed record PresencePreviewRow(string Field, string Value, string Source, bool FitWarning = false);

public sealed class PresenceCardInput
{
    public required PresenceSettings Settings { get; init; }
    public T3Snapshot? Snapshot { get; init; }
    public T3UsageSnapshot? Usage { get; init; }
    public PresenceTimerState Timer { get; init; }
    /// <summary>The app the card represents (after selection).</summary>
    public SupportedApp App { get; init; }
    public DateTimeOffset Now { get; init; }
    /// <summary>Selection may forbid rich T3 data (followActiveApp while another app is active).</summary>
    public bool RichAllowed { get; init; } = true;
    /// <summary>At least one environment has the token usage permission.</summary>
    public bool UsagePermitted { get; init; } = true;
    /// <summary>Optional caller-owned winner state for the sustained-lead rule; a fresh one is used otherwise.</summary>
    public T3LeaderTracker? Leader { get; init; }
    /// <summary>The project whose alias was shown last time; keeps the alias stable on ties.</summary>
    public string? PreviousAliasProject { get; init; }
}

public sealed class PresenceCardResult
{
    public DiscordActivity? Activity { get; init; }
    public IReadOnlyList<PresencePreviewRow> Rows { get; init; } = Array.Empty<PresencePreviewRow>();
    /// <summary>Fields ("details", "state") longer than the soft fit.</summary>
    public IReadOnlyList<string> FitWarnings { get; init; } = Array.Empty<string>();
    public bool IsRich { get; init; }
    public bool UsageShown { get; init; }
    public T3Phase Phase { get; init; }
    public string? AliasProjectKey { get; init; }
}

/// <summary>
/// Pure presence-v3 card formatter (spec/presence-v3.md §5). Only whitelisted text, validated
/// user aliases, validated model labels, fresh complete numbers, and fixed asset keys leave here.
/// Thread titles, project titles, environment labels/addresses and provider display names are
/// never read.
/// </summary>
public static class PresenceCardFormatter
{
    public const string CompanionLabel = "Get Deskchirp";
    public const string CompanionUrl = "https://github.com/geekynasir/deskchirp-discord";
    public const string RepoLabel = "View project";

    public const string HoverToday = "Tokens counted today across providers, including use outside T3";
    public const string HoverSession = "Tokens counted since this work session started";
    public const string HoverPinned = "Tokens counted for the pinned task";
    public const string HoverContext = "How full the pinned task's context window is";

    private static readonly HashSet<string> PublicToolNames = new(StringComparer.Ordinal)
    { "Claude Code", "Codex", "Antigravity", "Cursor", "OpenCode", "Grok", "Muse", "Pi", T3WorkloadAggregator.OtherToolsName };

    public static string? ProviderBadgeKey(string tool) => tool switch
    {
        "Claude Code" => "harness_claude", "Codex" => "harness_codex", "Antigravity" => "harness_antigravity",
        "Cursor" => "harness_cursor", "OpenCode" => "harness_opencode", "Grok" => "harness_grok", _ => null
    };

    public static bool IsSnapshotFresh(T3Snapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot == null || !snapshot.IsValid) return false;
        var age = now - snapshot.Timestamp;
        return age >= TimeSpan.FromSeconds(-5) && age <= T3ActivityService.SnapshotTtl;
    }

    /// <summary>Distinct, non-excluded threads, restricted to the pinned project when it is present.</summary>
    public static List<T3ThreadEntry> VisibleEntries(PresenceSettings settings, T3Snapshot snapshot, DateTimeOffset now)
    {
        // Stale environments are dropped, never zeroed.
        var fresh = snapshot.Environments.Where(e => now - e.Timestamp >= TimeSpan.FromSeconds(-5) && now - e.Timestamp <= T3ActivityService.SnapshotTtl);
        var entries = T3WorkloadView.Entries(fresh, settings.ExcludedProjects);
        // A pinned project always restricts counts, even when it has no work right now.
        if (!string.IsNullOrEmpty(settings.PinnedProject))
            entries = entries.Where(e => e.ProjectKey == settings.PinnedProject).ToList();
        return entries;
    }

    /// <summary>Workloads after exclusions and the pinned project. Older snapshots use their own workloads.</summary>
    public static IReadOnlyList<T3HarnessWorkload> VisibleWorkloads(PresenceSettings settings, T3Snapshot snapshot, DateTimeOffset now) =>
        snapshot.Environments.Count > 0
            ? T3WorkloadView.Aggregate(VisibleEntries(settings, snapshot, now))
            // Workload-only snapshots carry no projects, so a pinned project can't be confirmed.
            : !string.IsNullOrEmpty(settings.PinnedProject) ? Array.Empty<T3HarnessWorkload>()
            : snapshot.Workloads.Count > 0 ? snapshot.Workloads
            : snapshot.WinningHarness != null ? new[] { snapshot.WinningHarness } : Array.Empty<T3HarnessWorkload>();

    /// <summary>True when a fresh snapshot has running, waiting, or starting work that may be shown.</summary>
    public static bool HasFreshUnfinishedWork(PresenceSettings settings, T3Snapshot? snapshot, DateTimeOffset now) =>
        settings.ShowT3Activity && IsSnapshotFresh(snapshot, now) &&
        VisibleWorkloads(settings, snapshot!, now).Any(w => w.TotalActive > 0 && PublicToolNames.Contains(w.HarnessName));

    public static PresenceCardResult Format(PresenceCardInput input)
    {
        var settings = input.Settings;
        if (!settings.EnableSharing || settings.IsPaused || input.App == SupportedApp.None)
            return new PresenceCardResult();

        if (input.App != SupportedApp.T3Code)
        {
            string headline = ActivityFormatter.GetHeadline(input.App);
            if (headline == ActivityFormatter.HeadlineNone) return new PresenceCardResult();
            var (key, label) = input.App == SupportedApp.Claude ? ("harness_claude", "Claude") : ("harness_codex", "Codex");
            return Build(settings, headline, null, settings.ShowAppIcons ? key : null, label, null, null,
                input.Timer.AppShownStart, alias: null, rich: false, usageShown: false, T3Phase.None, null, usageSource: false);
        }

        // T3 Code: decide between the app-only and the rich card.
        T3HarnessWorkload? winner = null;
        List<T3ThreadEntry> entries = new();
        bool fresh = IsSnapshotFresh(input.Snapshot, input.Now);
        if (settings.CardStyle != CardStyle.AppOnly && settings.ShowT3Activity && input.RichAllowed && fresh)
        {
            var snapshot = input.Snapshot!;
            if (snapshot.Environments.Count > 0)
            {
                entries = VisibleEntries(settings, snapshot, input.Now);
                var workloads = T3WorkloadView.Aggregate(entries);
                winner = (input.Leader ?? new T3LeaderTracker()).SelectLeader(workloads, input.Now);
            }
            else if (string.IsNullOrEmpty(settings.PinnedProject))
            {
                winner = snapshot.WinningHarness;
            }
            if (winner != null && (winner.TotalActive <= 0 || !PublicToolNames.Contains(winner.HarnessName))) winner = null;
        }

        if (winner == null)
        {
            return Build(settings, ActivityFormatter.HeadlineT3Code, null, settings.ShowAppIcons ? "t3_code" : null, "T3 Code",
                null, null, input.Timer.AppShownStart, alias: null, rich: false, usageShown: false, T3Phase.None, null, usageSource: false);
        }

        string tool = winner.HarnessName;
        var phase = T3WorkloadView.LeadingPhase(winner);

        // Alias: pinned project if present, otherwise the featured tool's busiest project.
        string? aliasProject = null;
        string? alias = null;
        if (settings.ShareProjectAlias && entries.Count > 0)
        {
            aliasProject = !string.IsNullOrEmpty(settings.PinnedProject)
                ? settings.PinnedProject
                : BusiestProject(entries, tool, input.PreviousAliasProject);
            if (aliasProject != null && settings.ProjectAliases.TryGetValue(aliasProject, out var raw))
                alias = PresenceText.SanitizeAlias(raw);
        }
        string details = alias != null ? $"T3 Code · {alias}" : $"T3 Code · {tool}";

        // Workload state, optionally playful, optionally with the model.
        string? workloadState = settings.PlayfulWording ? PlayfulState(winner, phase) : T3WorkloadAggregator.FormatState(winner);
        if (workloadState != null && settings.ShareModel)
        {
            string? model = PresenceText.SafeModelLabel(ModelFor(entries, tool, settings.PinnedTask));
            if (model != null) workloadState += $" · {model}";
        }

        string? state = workloadState;
        string largeText = "T3 Code";
        bool usageShown = false;
        if (settings.CardStyle == CardStyle.Usage && input.UsagePermitted)
        {
            bool pinnedVisible = IsPinnedTaskVisible(settings, input.Snapshot, input.Now);
            var (usageText, definition) = UsageLine(settings, input.Usage, input.Timer, input.Now, pinnedVisible);
            if (usageText != null)
            {
                state = usageText;
                largeText = $"T3 Code · {definition}";
                usageShown = true;
            }
        }

        string? smallKey = null, smallText = null;
        if (settings.SmallBadge == SmallBadge.Status)
        {
            (smallKey, smallText) = phase switch
            {
                T3Phase.Running => ("status_working", "Working"),
                T3Phase.Waiting => ("status_attention", "Waiting"),
                T3Phase.Starting => ("status_working", "Starting"),
                _ => ((string?)null, (string?)null)
            };
        }
        else if (settings.SmallBadge == SmallBadge.Provider)
        {
            smallKey = ProviderBadgeKey(tool);
            smallText = smallKey == null ? null : tool;
        }

        return Build(settings, details, state, settings.ShowAppIcons ? "t3_code" : null, largeText,
            settings.ShowAppIcons ? smallKey : null, smallText, input.Timer.WorkSessionStart ?? input.Timer.AppShownStart,
            alias, rich: true, usageShown, phase, aliasProject, usageSource: usageShown);
    }

    private static string? BusiestProject(List<T3ThreadEntry> entries, string tool, string? previous)
    {
        var counts = entries
            .Where(e => e.ProjectKey != null && e.HarnessName == tool && e.Phase != T3Phase.None)
            .GroupBy(e => e.ProjectKey!, StringComparer.Ordinal)
            .Select(g => (Key: g.Key, Running: g.Count(e => e.Phase == T3Phase.Running), Active: g.Count()))
            .ToList();
        if (counts.Count == 0) return null;
        int bestRunning = counts.Max(c => c.Running);
        var top = counts.Where(c => c.Running == bestRunning).ToList();
        int bestActive = top.Max(c => c.Active);
        top = top.Where(c => c.Active == bestActive).ToList();
        if (previous != null && top.Any(c => c.Key == previous)) return previous;
        return top.OrderBy(c => c.Key, StringComparer.Ordinal).First().Key;
    }

    private static string? ModelFor(List<T3ThreadEntry> entries, string tool, string pinnedTask)
    {
        if (!string.IsNullOrEmpty(pinnedTask))
        {
            var pinned = entries.FirstOrDefault(e => e.ThreadKey == pinnedTask);
            if (pinned != null) return pinned.Thread.Model;
        }
        T3ThreadEntry? best = null;
        foreach (var entry in entries)
        {
            if (entry.HarnessName != tool || entry.IsHelper || entry.Phase != T3Phase.Running) continue;
            if (best == null || (entry.Thread.UpdatedAtValue ?? DateTimeOffset.MinValue) > (best.Thread.UpdatedAtValue ?? DateTimeOffset.MinValue))
                best = entry;
        }
        return best?.Thread.Model;
    }

    private static string? PlayfulState(T3HarnessWorkload w, T3Phase phase) => phase switch
    {
        T3Phase.Running => w.TotalRunning == 1 ? "Herding 1 agent" : $"Herding {w.TotalRunning} agents",
        T3Phase.Waiting => w.TotalWaiting == 1 ? "1 agent on standby" : $"{w.TotalWaiting} agents on standby",
        T3Phase.Starting => w.TotalStarting == 1 ? "Warming up 1 agent" : $"Warming up {w.TotalStarting} agents",
        _ => null
    };

    /// <summary>The effective scope: context is measured per task.</summary>
    public static UsageScope EffectiveScope(PresenceSettings settings) =>
        settings.UsageMetric == UsageMetric.Context ? UsageScope.PinnedTask : settings.UsageScope;

    /// <summary>
    /// True only when the pinned task is present in a fresh snapshot after exclusions, so neither its
    /// project nor any parent's project is excluded. Older workload-only snapshots can't prove that.
    /// </summary>
    public static bool IsPinnedTaskVisible(PresenceSettings settings, T3Snapshot? snapshot, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(settings.PinnedTask) || snapshot == null || !IsSnapshotFresh(snapshot, now)) return false;
        var fresh = snapshot.Environments.Where(e => now - e.Timestamp >= TimeSpan.FromSeconds(-5) && now - e.Timestamp <= T3ActivityService.SnapshotTtl);
        return T3WorkloadView.Entries(fresh, settings.ExcludedProjects).Any(e => e.ThreadKey == settings.PinnedTask);
    }

    /// <summary>
    /// Returns the usage state line, or null when the value is unavailable, stale, or partial.
    /// <paramref name="pinnedTaskVisible"/> must come from <see cref="IsPinnedTaskVisible"/>; pinned-task
    /// usage is never shown for a task that is excluded or missing from the filtered snapshot.
    /// </summary>
    public static (string? Text, string? Definition) UsageLine(PresenceSettings settings, T3UsageSnapshot? usage, PresenceTimerState timer,
        DateTimeOffset now, bool pinnedTaskVisible = false)
    {
        if (usage == null || !usage.IsFresh(now)) return (null, null);
        var scope = EffectiveScope(settings);
        T3UsageValue? value;
        switch (scope)
        {
            case UsageScope.Today:
                // The provider aggregate cannot skip excluded projects.
                if (settings.ExcludedProjects.Count > 0) return (null, null);
                value = usage.Today;
                break;
            case UsageScope.Session:
                // Usage computed for an earlier work session (or with no session now) is never shown.
                if (timer.WorkSessionStart == null) return (null, null);
                if (usage.SessionStart != null && usage.SessionStart != timer.WorkSessionStart) return (null, null);
                value = usage.Session;
                break;
            default:
                if (string.IsNullOrEmpty(settings.PinnedTask) || !pinnedTaskVisible) return (null, null);
                if (usage.PinnedTaskKey != null && usage.PinnedTaskKey != settings.PinnedTask) return (null, null);
                value = usage.PinnedTask;
                break;
        }
        if (value == null) return (null, null);

        bool playful = settings.PlayfulWording;
        if (settings.UsageMetric == UsageMetric.Context)
        {
            // Context stands on its own used/max; turn-token completeness applies only to tokens.
            if (value.ContextUsedTokens is not { } used || value.ContextMaxTokens is not { } max || max <= 0 || used < 0) return (null, null);
            long percent = (long)((Int128)used * 100 / max);
            return (playful ? $"Context tank {percent}% full" : $"{percent}% of context used", HoverContext);
        }

        if (value.Partial || value.Tokens is not { IsValid: true } tokens) return (null, null);
        long amount;
        try
        {
            amount = settings.TokenCategory switch
            {
                TokenCategory.Input => tokens.Input,
                TokenCategory.Output => tokens.Output,
                _ => tokens.Total
            };
        }
        catch (OverflowException) { return (null, null); }
        string number = settings.TokenRounding == TokenRounding.Exact ? PresenceText.Exact(amount) : PresenceText.Compact(amount);
        string scopeText = scope switch { UsageScope.Today => "today", UsageScope.Session => "this session", _ => "on this task" };
        string unit = amount == 1 ? "token" : "tokens";
        string text = playful ? $"Crunched {number} {unit} {scopeText}" : $"{number} {unit} {scopeText}";
        if (settings.ShareEstimatedCost && scope == UsageScope.Today && value.CostUsd is { } cost && !value.CostPartial &&
            PresenceText.Cost(cost) is { } costText)
            text += $" · {costText} API est.";
        string definition = scope switch { UsageScope.Today => HoverToday, UsageScope.Session => HoverSession, _ => HoverPinned };
        return (text, definition);
    }

    public static List<DiscordActivityButton> ButtonsFor(PresenceSettings settings, string? shownAlias)
    {
        var buttons = new List<DiscordActivityButton>(2);
        if (settings.Buttons == PresenceButtons.None) return buttons;
        buttons.Add(new DiscordActivityButton { Label = CompanionLabel, Url = CompanionUrl });
        if (settings.Buttons == PresenceButtons.CompanionAndRepo && settings.ShareProjectAlias && shownAlias != null &&
            PresenceText.IsValidPublicRepoUrl(settings.PublicRepoUrl))
            buttons.Add(new DiscordActivityButton { Label = RepoLabel, Url = settings.PublicRepoUrl });
        return buttons.Where(b => b.Label.Length <= PresenceText.ButtonLabelMaxLength).Take(2).ToList();
    }

    private static PresenceCardResult Build(PresenceSettings settings, string details, string? state, string? largeKey, string largeText,
        string? smallKey, string? smallText, DateTimeOffset? timerStart, string? alias, bool rich, bool usageShown, T3Phase phase,
        string? aliasProject, bool usageSource)
    {
        details = PresenceText.Cap(details)!;
        state = PresenceText.Cap(state);
        var activity = new DiscordActivity { Details = details, State = state };
        var rows = new List<PresencePreviewRow>();
        var warnings = new List<string>();

        bool detailsLong = details.Length > PresenceText.SoftFit;
        if (detailsLong) warnings.Add("details");
        rows.Add(new PresencePreviewRow("details", details, rich ? PreviewSource.T3Code : PreviewSource.Deskchirp, detailsLong));
        if (state != null)
        {
            bool stateLong = state.Length > PresenceText.SoftFit;
            if (stateLong) warnings.Add("state");
            rows.Add(new PresencePreviewRow("state", state, usageSource ? PreviewSource.T3Usage : PreviewSource.T3Code, stateLong));
        }

        if (settings.ShowAppIcons && largeKey != null)
        {
            var assets = new DiscordActivityAssets { LargeImage = largeKey, LargeText = PresenceText.Cap(largeText) };
            rows.Add(new PresencePreviewRow("largeImage", largeKey, PreviewSource.Deskchirp));
            rows.Add(new PresencePreviewRow("largeText", assets.LargeText!, usageShown ? PreviewSource.T3Usage : PreviewSource.Deskchirp));
            if (smallKey != null && smallText != null)
            {
                assets.SmallImage = smallKey;
                assets.SmallText = PresenceText.Cap(smallText);
                rows.Add(new PresencePreviewRow("smallImage", smallKey, PreviewSource.T3Code));
                rows.Add(new PresencePreviewRow("smallText", assets.SmallText!, PreviewSource.T3Code));
            }
            activity.Assets = assets;
        }

        if (settings.ShowElapsedTimer && timerStart is { } start)
        {
            activity.Timestamps = new DiscordActivityTimestamps { Start = start.ToUnixTimeSeconds() };
            rows.Add(new PresencePreviewRow("timestamps.start", activity.Timestamps.Start.ToString(System.Globalization.CultureInfo.InvariantCulture), PreviewSource.Deskchirp));
        }

        var buttons = ButtonsFor(settings, rich ? alias : null);
        if (buttons.Count > 0)
        {
            activity.Buttons = buttons;
            for (int i = 0; i < buttons.Count; i++)
                rows.Add(new PresencePreviewRow($"buttons[{i}]", $"{buttons[i].Label} → {buttons[i].Url}", PreviewSource.Deskchirp));
        }

        return new PresenceCardResult
        {
            Activity = activity, Rows = rows, FitWarnings = warnings, IsRich = rich, UsageShown = usageShown,
            Phase = phase, AliasProjectKey = alias != null ? aliasProject : null
        };
    }
}
