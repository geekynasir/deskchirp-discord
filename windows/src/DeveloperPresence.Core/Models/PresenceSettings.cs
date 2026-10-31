using System.Text.Json.Serialization;

namespace DeveloperPresence.Core.Models;

/// <summary>
/// User privacy and configuration settings saved in %LocalAppData%\DeveloperPresence\settings.json.
/// Contains NO tokens, secrets, or sensitive credentials.
/// </summary>
public sealed class PresenceSettings
{
    /// <summary>
    /// Master presence sharing toggle. OFF by default for privacy.
    /// </summary>
    public bool EnableSharing { get; set; } = false;

    /// <summary>
    /// Temporary pause toggle for presence broadcast.
    /// </summary>
    public bool IsPaused { get; set; } = false;

    /// <summary>
    /// Public numeric Discord Application ID. EMPTY by default.
    /// </summary>
    public string DiscordClientId { get; set; } = string.Empty;

    /// <summary>
    /// Minimum interval between Discord updates in seconds. Default 5 seconds to respect rate limits.
    /// </summary>
    public int UpdateIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Conservative default executable name mappings. Keys are normalized to lowercase.
    /// Strictly maps process image name to SupportedApp. Does NOT inspect window titles or screens.
    /// </summary>
    public Dictionary<string, SupportedApp> ProcessMappings { get; set; } = CreateDefaultMappings();

    /// <summary>
    /// Separate OFF-by-default switch to show rich T3 agent workload activity on Discord.
    /// </summary>
    public bool ShowT3Activity { get; set; } = false;

    /// <summary>
    /// Normalized MCP endpoint address of the explicitly connected T3 Code environment.
    /// Contains NO tokens, secrets, or credentials.
    /// </summary>
    public string T3Address { get; set; } = string.Empty;

    /// <summary>
    /// Optional OFF-by-default toggle to show Discord rich presence app artwork/icons.
    /// Requires assets configured on user's registered Discord application.
    /// </summary>
    public bool ShowAppIcons { get; set; } = false;

    public bool FollowComputers { get; set; } = false;
    public string ComputerLinkAddress { get; set; } = string.Empty;

    /// <summary>
    /// Validates that Discord Client ID consists purely of ASCII numeric digits (15-20 chars).
    /// Does not permit whitespace or Unicode digits to avoid wire mismatch.
    /// </summary>
    public bool IsValidDiscordClientId() => IsValidClientId(DiscordClientId);

    /// <summary>
    /// Validates whether a candidate Discord Client ID string is valid.
    /// </summary>
    public static bool IsValidClientId(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            return false;
        }

        if (clientId.Length < 15 || clientId.Length > 20)
        {
            return false;
        }

        foreach (char c in clientId)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Creates the standard conservative default process mappings.
    /// Only maps verified desktop applications. CLI executables (e.g. t3.exe) are excluded.
    /// </summary>
    public static Dictionary<string, SupportedApp> CreateDefaultMappings()
    {
        return new Dictionary<string, SupportedApp>(StringComparer.OrdinalIgnoreCase)
        {
            // Upstream resolveDesktopProductName() / appExecutableName in build-desktop-artifact.ts.
            { "t3 code (beta).exe", SupportedApp.T3Code },
            { "t3 code (nightly).exe", SupportedApp.T3Code },
            { "t3 code.exe", SupportedApp.T3Code },
            { "t3code.exe", SupportedApp.T3Code },
            { "claude.exe", SupportedApp.Claude },
            { "codex.exe", SupportedApp.Codex },
            { "codex desktop.exe", SupportedApp.Codex }
        };
    }
}
