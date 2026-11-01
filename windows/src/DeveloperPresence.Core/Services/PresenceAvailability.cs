using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;

namespace DeveloperPresence.Core.Services;

/// <summary>Whether a settings option can be chosen, and the one-line reason when it can't.</summary>
public sealed record OptionAvailability(bool Enabled, string? Reason)
{
    public static readonly OptionAvailability Available = new(true, null);
}

/// <summary>Availability rules for presence v3 settings (spec/presence-v3.md §3). Disabled options stay visible.</summary>
public static class PresenceAvailability
{
    public const string ReasonConnect = "Connect T3 Code to show work.";
    public const string ReasonUsagePermission = "Allow token usage for an environment first.";
    public const string ReasonContextScope = "Context is measured per task.";
    public const string ReasonPickTask = "Pick a task to pin.";
    public const string ReasonTodayExclusions = "Today’s totals can’t skip excluded projects.";
    public const string ReasonShareAlias = "Turn on Share project alias first.";

    public static OptionAvailability CardStyleOption(CardStyle style, PresenceSettings settings, bool anyEnvironmentConnected, bool anyUsagePermission)
    {
        if (style == CardStyle.AppOnly) return OptionAvailability.Available;
        if (!settings.ShowT3Activity || !anyEnvironmentConnected) return new(false, ReasonConnect);
        if (style == CardStyle.Usage && !anyUsagePermission) return new(false, ReasonUsagePermission);
        return OptionAvailability.Available;
    }

    public static OptionAvailability ScopeOption(UsageScope scope, PresenceSettings settings)
    {
        if (settings.UsageMetric == UsageMetric.Context && scope != UsageScope.PinnedTask) return new(false, ReasonContextScope);
        if (scope == UsageScope.Today && settings.ExcludedProjects.Count > 0) return new(false, ReasonTodayExclusions);
        if (scope == UsageScope.PinnedTask && string.IsNullOrEmpty(settings.PinnedTask)) return new(false, ReasonPickTask);
        return OptionAvailability.Available;
    }

    /// <summary>The reason shown under Scope for the current choice, if any.</summary>
    public static string? ScopeReason(PresenceSettings settings)
    {
        if (settings.UsageMetric == UsageMetric.Context)
            return string.IsNullOrEmpty(settings.PinnedTask) ? ReasonContextScope + " " + ReasonPickTask : ReasonContextScope;
        return ScopeOption(settings.UsageScope, settings).Reason;
    }

    /// <summary>Estimated cost applies only to usage + tokens + today; hide the toggle otherwise.</summary>
    public static bool ShowEstimatedCost(PresenceSettings settings) =>
        settings.CardStyle == CardStyle.Usage && settings.UsageMetric == UsageMetric.Tokens &&
        PresenceCardFormatter.EffectiveScope(settings) == UsageScope.Today;

    public static OptionAvailability CompanionAndRepo(PresenceSettings settings)
    {
        if (!settings.ShareProjectAlias) return new(false, ReasonShareAlias);
        if (!PresenceText.IsValidPublicRepoUrl(settings.PublicRepoUrl)) return new(false, PresenceText.RepoUrlInvalid);
        return OptionAvailability.Available;
    }
}
