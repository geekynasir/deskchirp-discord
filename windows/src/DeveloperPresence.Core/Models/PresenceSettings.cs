using System.Text.Json.Serialization;

namespace DeveloperPresence.Core.Models;

/// <summary>
/// User privacy and configuration settings saved in %LocalAppData%\DeveloperPresence\settings.json.
/// Contains NO tokens, secrets, or sensitive credentials.
/// </summary>
public sealed class PresenceSettings
{
    public const string DefaultDiscordApplicationId = "1566210608569782373";
    /// <summary>
    /// Master presence sharing toggle. OFF by default for privacy.
    /// </summary>
    public bool EnableSharing { get; set; } = false;

    /// <summary>
    /// Temporary pause toggle for presence broadcast.
    /// </summary>
    public bool IsPaused { get; set; } = false;

    /// <summary>
    /// Publisher-owned public Discord application ID.
    /// </summary>
    public string DiscordClientId { get; set; } = DefaultDiscordApplicationId;

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
    /// Artwork is on by default and uses fixed public images fetched by Discord.
    /// No per-user asset upload is required.
    /// </summary>
    public bool ShowAppIcons { get; set; } = true;
    public int ArtworkDefaultsVersion { get; set; } = 0;

    /// <summary>Installed copies check GitHub Releases for new versions and download them in the background.</summary>
    public bool AutomaticUpdates { get; set; } = true;

    public bool FollowComputers { get; set; } = false;
    public string ComputerLinkAddress { get; set; } = string.Empty;

    // Presence controls v3 (spec/presence-v3.md §3). Every field has a default so older
    // settings files still load, and nothing new is shared until the user enables it.

    /// <summary>Ordered T3 environments. A legacy <see cref="T3Address"/> migrates to the first entry.</summary>
    public List<T3Environment> T3Environments { get; set; } = new();
    public CardStyle CardStyle { get; set; } = CardStyle.Workload;
    public UsageMetric UsageMetric { get; set; } = UsageMetric.Tokens;
    public UsageScope UsageScope { get; set; } = UsageScope.Today;
    public TokenCategory TokenCategory { get; set; } = TokenCategory.Total;
    public TokenRounding TokenRounding { get; set; } = TokenRounding.Compact;
    public SmallBadge SmallBadge { get; set; } = SmallBadge.Status;
    public bool ShareProjectAlias { get; set; } = false;
    /// <summary>Keys are "{environmentId}/{projectId}". Values are user-written aliases.</summary>
    public Dictionary<string, string> ProjectAliases { get; set; } = new(StringComparer.Ordinal);
    public bool ShareModel { get; set; } = false;
    public bool ShareEstimatedCost { get; set; } = false;
    public bool ShowElapsedTimer { get; set; } = true;
    public bool PlayfulWording { get; set; } = false;
    public PresenceButtons Buttons { get; set; } = PresenceButtons.None;
    public string PublicRepoUrl { get; set; } = string.Empty;
    /// <summary>Entries are "{environmentId}/{projectId}".</summary>
    public HashSet<string> ExcludedProjects { get; set; } = new(StringComparer.Ordinal);
    /// <summary>"{environmentId}/{threadId}" or empty.</summary>
    public string PinnedTask { get; set; } = string.Empty;
    /// <summary>"{environmentId}/{projectId}" or empty.</summary>
    public string PinnedProject { get; set; } = string.Empty;
    public SelectionMode SelectionMode { get; set; } = SelectionMode.KeepBackgroundVisible;

    /// <summary>
    /// Fills missing collections and moves a non-empty legacy T3 address into the environment list.
    /// The legacy field is cleared afterwards so removing that environment does not resurrect it.
    /// Returns true when anything changed.
    /// </summary>
    public bool NormalizeAndMigrate()
    {
        bool changed = false;
        if (T3Environments == null) { T3Environments = new(); changed = true; }
        T3Environments.RemoveAll(e => e == null || string.IsNullOrWhiteSpace(e.Address));
        foreach (var env in T3Environments)
        {
            if (string.IsNullOrWhiteSpace(env.Id)) { env.Id = Guid.NewGuid().ToString(); changed = true; }
            env.Label ??= string.Empty;
        }
        ProjectAliases = ProjectAliases == null
            ? new(StringComparer.Ordinal)
            : new Dictionary<string, string>(ProjectAliases.Where(p => p.Key != null && p.Value != null), StringComparer.Ordinal);
        ExcludedProjects = ExcludedProjects == null
            ? new(StringComparer.Ordinal)
            : new HashSet<string>(ExcludedProjects.Where(p => !string.IsNullOrEmpty(p)), StringComparer.Ordinal);
        PublicRepoUrl ??= string.Empty;
        PinnedTask ??= string.Empty;
        PinnedProject ??= string.Empty;
        T3Address ??= string.Empty;

        if (!string.IsNullOrWhiteSpace(T3Address))
        {
            string address = T3Address.Trim();
            if (T3.T3UrlValidator.TryNormalizeT3Address(address, out var normalized, out _)) address = normalized!.AbsoluteUri;
            if (!T3Environments.Any(e => string.Equals(e.Address, address, StringComparison.Ordinal)))
            {
                T3Environments.Insert(0, new T3Environment { Address = address, Label = DefaultLabel(address) });
            }
            T3Address = string.Empty;
            changed = true;
        }
        return changed;
    }

    /// <summary>The default local label for an environment: its host name.</summary>
    public static string DefaultLabel(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : "T3 Code";

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
