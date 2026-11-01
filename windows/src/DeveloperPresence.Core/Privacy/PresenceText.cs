using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeveloperPresence.Core.Privacy;

/// <summary>Number, alias, model, link, and length rules from spec/presence-v3.md §5.</summary>
public static partial class PresenceText
{
    public const int HardCap = 128;
    public const int SoftFit = 34;
    public const int AliasMaxLength = 32;
    public const int ModelMaxLength = 40;
    public const int ButtonLabelMaxLength = 32;
    public const string FitWarning = "May be cut off on Discord. Try a shorter alias or turn off a detail.";
    public const string RepoUrlInvalid = "Use a public GitHub repository link.";

    /// <summary>Compact token count: 999, 1K, 1.1K, 100K, 1M, 12.5M, 3.4B. Integer half-up rounding.</summary>
    public static string Compact(long n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        if (n < 1000) return n.ToString(CultureInfo.InvariantCulture);
        long[] units = { 1_000, 1_000_000, 1_000_000_000 };
        string[] suffixes = { "K", "M", "B" };
        int index = n >= 1_000_000_000 ? 2 : n >= 1_000_000 ? 1 : 0;
        while (true)
        {
            long unit = units[index];
            Int128 tenths = ((Int128)n * 10 + unit / 2) / unit;
            if (tenths < 1000)
            {
                long whole = (long)(tenths / 10), fraction = (long)(tenths % 10);
                return fraction == 0
                    ? whole.ToString(CultureInfo.InvariantCulture) + suffixes[index]
                    : whole.ToString(CultureInfo.InvariantCulture) + "." + fraction.ToString(CultureInfo.InvariantCulture) + suffixes[index];
            }
            long units_ = (long)(((Int128)n + unit / 2) / unit);
            if (units_ >= 1000 && index < units.Length - 1) { index++; continue; }
            return units_.ToString(CultureInfo.InvariantCulture) + suffixes[index];
        }
    }

    /// <summary>Exact token count with invariant comma separators: 842,112.</summary>
    public static string Exact(long n) => n.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>Estimated API cost: half-up to cents; below $10 two decimals, otherwise whole dollars.</summary>
    public static string? Cost(decimal costUsd)
    {
        if (costUsd < 0) return null;
        decimal cents = decimal.Floor(costUsd * 100m + 0.5m);
        if (cents < 1000m) return "~$" + (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        decimal dollars = decimal.Floor((cents + 50m) / 100m);
        return "~$" + dollars.ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Removes control and invisible format characters, collapses whitespace, trims, keeps the first
    /// 32 characters and trims again. Returns null for an empty result.
    /// </summary>
    public static string? SanitizeAlias(string? alias)
    {
        if (string.IsNullOrEmpty(alias)) return null;
        var builder = new StringBuilder(alias.Length);
        bool pendingSpace = false;
        foreach (char c in alias)
        {
            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            if (char.IsWhiteSpace(c) || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
            builder.Append(c);
        }
        string text = builder.ToString().Trim();
        if (text.Length > AliasMaxLength)
        {
            int cut = AliasMaxLength;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            text = text[..cut].Trim();
        }
        return text.Length == 0 ? null : text;
    }

    [GeneratedRegex("^[A-Za-z0-9 ._:/-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelPattern();

    /// <summary>Accepts only [A-Za-z0-9 ._:/-], at most 40 characters, and not blank.</summary>
    public static string? SafeModelLabel(string? model) =>
        model != null && ModelPattern().IsMatch(model) && !string.IsNullOrWhiteSpace(model) ? model : null;

    [GeneratedRegex("^https://github\\.com/([A-Za-z0-9-]{1,39})/([A-Za-z0-9._-]{1,100})$", RegexOptions.CultureInvariant)]
    private static partial Regex RepoPattern();

    /// <summary>https://github.com/{owner}/{repo} with no query, fragment, or extra path.</summary>
    public static bool IsValidPublicRepoUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        var match = RepoPattern().Match(url);
        if (!match.Success) return false;
        string repo = match.Groups[2].Value;
        return repo is not ("." or "..");
    }

    /// <summary>Cuts a field to the 128-character Discord limit, ending in an ellipsis.</summary>
    public static string? Cap(string? text, int max = HardCap)
    {
        if (text == null || text.Length <= max) return text;
        int cut = max - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "…";
    }
}
