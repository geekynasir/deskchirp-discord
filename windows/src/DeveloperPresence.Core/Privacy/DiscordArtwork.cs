using DeveloperPresence.Core.Models;

namespace DeveloperPresence.Core.Privacy;

/// <summary>Fixed public artwork fetched by Discord. Never accepts a local path or user-controlled URL.</summary>
public static class DiscordArtwork
{
    public const string BaseUrl = "https://raw.githubusercontent.com/geekynasir/deskchirp-discord/artwork-v1/assets/discord/";
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    { "deskchirp", "t3_code", "harness_claude", "harness_codex", "harness_antigravity", "harness_cursor", "harness_opencode", "harness_grok" };
    public static string? UrlFor(string? key) => key != null && Keys.Contains(key) ? BaseUrl + key + ".png" : null;

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
        return new DiscordActivity { Details = activity.Details, State = activity.State, Assets = new DiscordActivityAssets {
            LargeImage = UrlFor(source?.LargeImage ?? key), LargeText = label,
            SmallImage = UrlFor(source?.SmallImage), SmallText = source?.SmallText
        } };
    }
}
