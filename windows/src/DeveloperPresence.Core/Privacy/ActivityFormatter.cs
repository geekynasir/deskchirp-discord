using DeveloperPresence.Core.Models;

namespace DeveloperPresence.Core.Privacy;

/// <summary>
/// Strict privacy-enforcing presence formatter.
/// Guaranteed behavior:
/// - Only formats whitelisted static strings: "Using T3 Code", "Using Claude", "Using Codex".
/// - Window titles, file names, URLs, Git repositories, and screens are NEVER read or formatted.
/// - Never fabricates fake task counts or agent activity.
/// - Injects NO assets (icons/images) and NO timestamps/elapsed timers.
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
