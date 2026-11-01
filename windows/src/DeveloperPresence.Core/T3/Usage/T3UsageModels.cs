using System.Text.Json.Serialization;

namespace DeveloperPresence.Core.T3.Usage;

/// <summary>
/// Normalized token categories. Input = uncached + cached + cache creation. Output already
/// contains reasoning, so reasoning is kept only for completeness and never added.
/// </summary>
public sealed class T3TokenCounts
{
    public long UncachedInputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningTokens { get; set; }

    [JsonIgnore] public long Input => UncachedInputTokens + CachedInputTokens + CacheCreationTokens;
    [JsonIgnore] public long Output => OutputTokens;
    [JsonIgnore] public long Total => Input + Output;

    [JsonIgnore]
    public bool IsValid => UncachedInputTokens >= 0 && CachedInputTokens >= 0 && CacheCreationTokens >= 0 &&
                           OutputTokens >= 0 && ReasoningTokens >= 0;

    public void Add(T3TokenCounts other)
    {
        UncachedInputTokens = checked(UncachedInputTokens + other.UncachedInputTokens);
        CachedInputTokens = checked(CachedInputTokens + other.CachedInputTokens);
        CacheCreationTokens = checked(CacheCreationTokens + other.CacheCreationTokens);
        OutputTokens = checked(OutputTokens + other.OutputTokens);
        ReasoningTokens = checked(ReasoningTokens + other.ReasoningTokens);
    }

    public T3TokenCounts Clone() => new()
    {
        UncachedInputTokens = UncachedInputTokens, CachedInputTokens = CachedInputTokens,
        CacheCreationTokens = CacheCreationTokens, OutputTokens = OutputTokens, ReasoningTokens = ReasoningTokens
    };
}

/// <summary>One usage scope's value. Partial values are never shown.</summary>
public sealed class T3UsageValue
{
    public T3TokenCounts? Tokens { get; set; }
    public decimal? CostUsd { get; set; }
    /// <summary>Token data is incomplete: hide the whole usage line.</summary>
    public bool Partial { get; set; }
    /// <summary>Cost data is incomplete: hide only the cost suffix.</summary>
    public bool CostPartial { get; set; }
    public long? ContextUsedTokens { get; set; }
    public long? ContextMaxTokens { get; set; }
}

/// <summary>The latest token usage answer, combined across environments with usage permission.</summary>
public sealed class T3UsageSnapshot
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(180);

    /// <summary>When the answer arrived. Null means "now" (shared fixtures mark everything fresh).</summary>
    public DateTimeOffset? Timestamp { get; set; }
    public T3UsageValue? Today { get; set; }
    public T3UsageValue? Session { get; set; }
    public T3UsageValue? PinnedTask { get; set; }
    /// <summary>The pinned task the <see cref="PinnedTask"/> value was computed for.</summary>
    public string? PinnedTaskKey { get; set; }
    /// <summary>The work-session start the <see cref="Session"/> value was computed for.</summary>
    public DateTimeOffset? SessionStart { get; set; }

    public bool IsFresh(DateTimeOffset now)
    {
        if (Timestamp is not { } at) return true;
        var age = now - at;
        return age >= TimeSpan.FromSeconds(-5) && age <= Ttl;
    }
}
