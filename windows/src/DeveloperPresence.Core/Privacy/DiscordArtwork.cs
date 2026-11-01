using DeveloperPresence.Core.Models;

namespace DeveloperPresence.Core.Privacy;

/// <summary>Fixed public artwork fetched by Discord. Never accepts a local path or user-controlled URL.</summary>
public static class DiscordArtwork
{
    /// <summary>Release tag that pins the published artwork. Change only this to move to a new pack.</summary>
    public const string ArtworkTag = "artwork-v2";
    public const string BaseUrl = "https://raw.githubusercontent.com/geekynasir/deskchirp-discord/" + ArtworkTag + "/assets/discord/";
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "deskchirp", "t3_code", "harness_claude", "harness_codex", "harness_antigravity", "harness_cursor", "harness_opencode", "harness_grok",
        "status_working", "status_attention", "status_idle"
    };

    public static bool IsKnownKey(string? key) => key != null && Keys.Contains(key);
    public static string? UrlFor(string? key) => IsKnownKey(key) ? BaseUrl + key + ".png" : null;

    /// <summary>
    /// Resolves whitelisted asset keys to their public URLs. Hover texts already chosen by the
    /// formatter are kept; otherwise the app's public name is used. Timer and buttons pass through.
    /// </summary>
    public static DiscordActivity? Apply(DiscordActivity? activity, SupportedApp app, bool enabled)
    {
        if (activity == null || !enabled) return activity;
        var (key, label) = app switch {
            SupportedApp.T3Code => ("t3_code", "T3 Code"),
            SupportedApp.Claude => ("harness_claude", "Claude"),
            SupportedApp.Codex => ("harness_codex", "Codex"),
            _ => ("deskchirp", "Deskchirp")
        };
        var source = activity.Assets;
        string? small = UrlFor(source?.SmallImage);
        return new DiscordActivity
        {
            Details = activity.Details,
            State = activity.State,
            Timestamps = activity.Timestamps,
            Buttons = activity.Buttons,
            Assets = new DiscordActivityAssets {
                LargeImage = UrlFor(source?.LargeImage ?? key), LargeText = source?.LargeText ?? label,
                SmallImage = small, SmallText = small == null ? null : source?.SmallText
            }
        };
    }
}
