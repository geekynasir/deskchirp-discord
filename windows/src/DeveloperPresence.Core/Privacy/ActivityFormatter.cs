using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.T3;

namespace DeveloperPresence.Core.Privacy;

/// <summary>
/// Strict privacy-enforcing presence formatter.
/// Guaranteed behavior:
/// - Only formats whitelisted static strings: "Using T3 Code", "Using Claude", "Using Codex", or safe harness labels.
/// - Window titles, file names, URLs, Git repositories, model names, and screens are NEVER read or formatted.
/// - Never fabricates fake task counts or agent activity.
/// - Returns null activity (clearing Discord presence) when disabled, paused, or when focus is unsupported.
/// </summary>
public static class ActivityFormatter
{
    public const string HeadlineT3Code = "Using T3 Code";
    public const string HeadlineClaude = "Using Claude";
    public const string HeadlineCodex = "Using Codex";
    public const string HeadlineNone = "None / Cleared";

    /// <summary>
    /// Gets the human-readable headline for the given app.
    /// </summary>
    public static string GetHeadline(SupportedApp app) => app switch
    {
        SupportedApp.T3Code => HeadlineT3Code,
        SupportedApp.Claude => HeadlineClaude,
        SupportedApp.Codex => HeadlineCodex,
        _ => HeadlineNone
    };

    /// <summary>
    /// Formats a safe DiscordActivity object for the specified application and privacy settings.
    /// Returns null if sharing is disabled, paused, or if the focused application is unsupported.
    /// Strictly details-only: no timestamps or assets are included.
    /// </summary>
    public static DiscordActivity? FormatActivity(
        SupportedApp app,
        bool enableSharing,
        bool isPaused)
    {
        // Enforce privacy: sharing must be explicitly enabled and not paused
        if (!enableSharing || isPaused || app == SupportedApp.None)
        {
            return null;
        }

        string headline = GetHeadline(app);
        if (headline == HeadlineNone)
        {
            return null;
        }

        return new DiscordActivity
        {
            Details = headline
        };
    }

    /// <summary>
    /// Formats safe Discord activity for T3 Code with optional rich agent workload and app icons.
    /// Returns null if sharing is disabled or paused.
    /// </summary>
    public static DiscordActivity? FormatT3Activity(
        T3Snapshot? snapshot,
        bool enableSharing,
        bool isPaused,
        bool showT3Activity,
        bool showAppIcons)
    {
        if (!enableSharing || isPaused)
        {
            return null;
        }

        if (!showT3Activity || snapshot == null || !snapshot.IsValid || snapshot.WinningHarness == null)
        {
            // App-only presence for T3 Code
            var activity = new DiscordActivity
            {
                Details = HeadlineT3Code
            };

            if (showAppIcons)
            {
                activity.Assets = new DiscordActivityAssets
                {
                    LargeImage = "t3_code",
                    LargeText = "T3 Code"
                };
            }

            return activity;
        }

        // Rich T3 activity with most active agent tool and worker counts
        var richActivity = new DiscordActivity
        {
            Details = $"T3 Code · {snapshot.WinningHarness.HarnessName}",
            State = snapshot.WinningState
        };

        if (showAppIcons)
        {
            richActivity.Assets = new DiscordActivityAssets
            {
                LargeImage = "t3_code",
                LargeText = "T3 Code",
                SmallImage = snapshot.WinningHarness.SmallIcon,
                SmallText = snapshot.WinningHarness.SmallIcon != null ? snapshot.WinningHarness.HarnessName : null
            };
        }

        return richActivity;
    }

    /// <summary>
    /// Legacy overload provided for compatibility. Timestamps and assets are not supported.
    /// </summary>
    [Obsolete("Timestamps and assets are excluded by policy. Use FormatActivity(app, enableSharing, isPaused).")]
    public static DiscordActivity? FormatActivity(
        SupportedApp app,
        bool enableSharing,
        bool isPaused,
        long? sessionStartTime)
    {
        return FormatActivity(app, enableSharing, isPaused);
    }
}
