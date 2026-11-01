using System.Globalization;
using System.Text.Json;

namespace DeveloperPresence.Core.T3.Usage;

/// <summary>Usage numbers decoded from one thread projection. Conversation content is never read.</summary>
public sealed class T3ProjectionUsage
{
    public string? ActiveProviderThreadId { get; init; }
    public IReadOnlyList<T3ProviderThreadUsage> ProviderThreads { get; init; } = Array.Empty<T3ProviderThreadUsage>();
    public IReadOnlyList<T3TurnUsage> Turns { get; init; } = Array.Empty<T3TurnUsage>();
}

public sealed record T3ProviderThreadUsage(string Id, long? ContextUsedTokens, long? ContextMaxTokens);

public sealed record T3TurnUsage(
    string? ProviderThreadId,
    DateTimeOffset? StartedAt,
    long? ContextUsedTokens,
    long? ContextMaxTokens,
    DateTimeOffset? ContextUpdatedAt,
    bool Complete,
    T3TokenCounts? Tokens);

/// <summary>Pure decoding and arithmetic for spec/t3-usage-transport.md.</summary>
public static class T3UsageCalculator
{
    public const int SupportedContractVersion = 6;
    public const int MaxProjectionsPerPoll = 24;

    // ---- Today (server.getUsageSummary) ----

    /// <summary>
    /// Sums all decodable buckets of a contract-6 usage summary. Returns null when the contract
    /// version differs. Any non-ok source makes tokens partial; unpriced records or a pricing status
    /// other than fresh/cached make only the cost partial.
    /// </summary>
    public static T3UsageValue? ParseUsageSummary(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || ReadLong(value, "contractVersion") != SupportedContractVersion) return null;
        var tokens = new T3TokenCounts();
        decimal cost = 0m;
        bool partial = false, costPartial = false;

        if (value.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in sources.EnumerateArray())
                if (ReadString(source, "status") != "ok") partial = true;
        }
        else partial = true; // Coverage can't be confirmed.

        string? pricing = value.TryGetProperty("pricing", out var p) ? ReadString(p, "status") : null;
        if (pricing is not ("fresh" or "cached")) costPartial = true;

        if (!value.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array) return null;
        foreach (var bucket in buckets.EnumerateArray())
        {
            if (bucket.ValueKind != JsonValueKind.Object) continue;
            var totals = bucket.TryGetProperty("totals", out var t) && t.ValueKind == JsonValueKind.Object ? t : bucket;
            var counts = ReadTokenCounts(totals);
            if (counts == null) continue; // Skip buckets that don't decode.
            try { tokens.Add(counts); } catch (OverflowException) { partial = true; continue; }
            decimal? bucketCost = ReadDecimal(bucket, "costUsd") ?? ReadDecimal(totals, "costUsd");
            if (bucketCost is { } c && c >= 0) cost += c; else costPartial = true;
            if ((ReadLong(bucket, "unpricedRecords") ?? ReadLong(totals, "unpricedRecords") ?? 0) > 0) costPartial = true;
        }
        return new T3UsageValue { Tokens = tokens, CostUsd = cost, Partial = partial, CostPartial = costPartial || partial };
    }

    /// <summary>Adds the today values from several environments. Any failure makes the result partial.</summary>
    public static T3UsageValue CombineToday(IEnumerable<T3UsageValue?> values)
    {
        var combined = new T3UsageValue { Tokens = new T3TokenCounts(), CostUsd = 0m };
        bool any = false;
        foreach (var value in values)
        {
            if (value?.Tokens == null) { combined.Partial = true; combined.CostPartial = true; continue; }
            any = true;
            try { combined.Tokens.Add(value.Tokens); } catch (OverflowException) { combined.Partial = true; }
            combined.Partial |= value.Partial;
            combined.CostPartial |= value.CostPartial;
            if (value.CostUsd is { } c) combined.CostUsd += c; else combined.CostPartial = true;
        }
        if (!any) combined.Partial = true;
        return combined;
    }

    // ---- Thread projections (orchestration.getThreadProjection) ----

    /// <summary>Decodes only the usage keys of a projection; every other key, including the timeline, is ignored.</summary>
    public static T3ProjectionUsage? ParseProjection(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        if (!value.TryGetProperty("providerTurns", out _) && !value.TryGetProperty("providerThreads", out _) &&
            value.TryGetProperty("thread", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
            value = wrapped;

        var threads = new List<T3ProviderThreadUsage>();
        if (value.TryGetProperty("providerThreads", out var pts) && pts.ValueKind == JsonValueKind.Array)
        {
            foreach (var pt in pts.EnumerateArray())
            {
                string? id = ReadString(pt, "id");
                if (id == null) continue;
                long? used = null, max = null;
                if (pt.TryGetProperty("contextUsage", out var cu) && cu.ValueKind == JsonValueKind.Object)
                {
                    used = ReadLong(cu, "usedTokens");
                    max = ReadLong(cu, "maxTokens");
                }
                threads.Add(new T3ProviderThreadUsage(id, used, max));
            }
        }

        var turns = new List<T3TurnUsage>();
        if (value.TryGetProperty("providerTurns", out var pturns) && pturns.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in pturns.EnumerateArray())
            {
                if (turn.ValueKind != JsonValueKind.Object) continue;
                long? used = null, max = null;
                DateTimeOffset? updated = null;
                if (turn.TryGetProperty("tokenUsage", out var tu) && tu.ValueKind == JsonValueKind.Object)
                {
                    used = ReadLong(tu, "usedTokens");
                    max = ReadLong(tu, "maxTokens");
                    updated = ReadTime(tu, "updatedAt");
                }
                bool complete = false;
                T3TokenCounts? counts = null;
                if (turn.TryGetProperty("turnTokenUsage", out var ttu) && ttu.ValueKind == JsonValueKind.Object &&
                    ReadString(ttu, "usageStatus") == "complete")
                {
                    long? input = ReadLong(ttu, "inputTokens");
                    long? output = ReadLong(ttu, "outputTokens");
                    if (input is >= 0 && output is >= 0)
                    {
                        // inputTokens already includes cache reads and writes.
                        long cached = Math.Clamp(ReadLong(ttu, "cachedInputTokens") ?? 0, 0, input.Value);
                        long creation = Math.Clamp(ReadLong(ttu, "cacheCreationTokens") ?? ReadLong(ttu, "cacheCreationInputTokens") ?? 0, 0, input.Value - cached);
                        counts = new T3TokenCounts
                        {
                            UncachedInputTokens = Math.Max(0, input.Value - cached - creation),
                            CachedInputTokens = cached,
                            CacheCreationTokens = creation,
                            OutputTokens = output.Value,
                            ReasoningTokens = Math.Max(0, ReadLong(ttu, "reasoningOutputTokens") ?? ReadLong(ttu, "reasoningTokens") ?? 0)
                        };
                        complete = true;
                    }
                }
                turns.Add(new T3TurnUsage(ReadString(turn, "providerThreadId"), ReadTime(turn, "startedAt"), used, max, updated, complete, counts));
            }
        }

        return new T3ProjectionUsage { ActiveProviderThreadId = ReadString(value, "activeProviderThreadId"), ProviderThreads = threads, Turns = turns };
    }

    /// <summary>Sums complete turns (optionally only those started at or after a time). Any other turn in scope → partial.</summary>
    public static T3UsageValue SumTurns(IEnumerable<T3ProjectionUsage?> projections, DateTimeOffset? since)
    {
        var tokens = new T3TokenCounts();
        bool partial = false;
        foreach (var projection in projections)
        {
            if (projection == null) { partial = true; continue; }
            foreach (var turn in projection.Turns)
            {
                if (since is { } start && (turn.StartedAt == null || turn.StartedAt < start))
                {
                    if (turn.StartedAt == null) partial = true; // Can't tell whether it belongs to the session.
                    continue;
                }
                if (!turn.Complete || turn.Tokens == null) { partial = true; continue; }
                try { tokens.Add(turn.Tokens); } catch (OverflowException) { partial = true; }
            }
        }
        return new T3UsageValue { Tokens = tokens, Partial = partial, CostPartial = true };
    }

    /// <summary>
    /// Context: the newest turn tokenUsage (by updatedAt) on the active provider thread, falling back to
    /// that provider thread's contextUsage. Missing or zero maxTokens → unavailable (null).
    /// </summary>
    public static (long Used, long Max)? Context(T3ProjectionUsage? projection)
    {
        if (projection?.ActiveProviderThreadId is not { } active) return null;
        var newest = projection.Turns
            .Where(t => t.ProviderThreadId == active && t.ContextUsedTokens != null)
            .OrderByDescending(t => t.ContextUpdatedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        if (newest != null && newest.ContextUsedTokens is >= 0 and var used && newest.ContextMaxTokens is > 0 and var max)
            return (used, max);
        var thread = projection.ProviderThreads.FirstOrDefault(t => t.Id == active);
        if (thread?.ContextUsedTokens is >= 0 and var u && thread.ContextMaxTokens is > 0 and var m) return (u, m);
        return null;
    }

    // ---- JSON helpers: numbers may be JSON numbers or numeric strings. ----

    private static T3TokenCounts? ReadTokenCounts(JsonElement e)
    {
        long? uncached = ReadLong(e, "uncachedInputTokens");
        long? output = ReadLong(e, "outputTokens");
        if (uncached == null || output == null) return null;
        var counts = new T3TokenCounts
        {
            UncachedInputTokens = uncached.Value,
            CachedInputTokens = ReadLong(e, "cachedInputTokens") ?? 0,
            CacheCreationTokens = ReadLong(e, "cacheCreationTokens") ?? ReadLong(e, "cacheCreationInputTokens") ?? 0,
            OutputTokens = output.Value,
            ReasoningTokens = ReadLong(e, "reasoningTokens") ?? ReadLong(e, "reasoningOutputTokens") ?? 0
        };
        return counts.IsValid ? counts : null;
    }

    internal static long? ReadLong(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out n)) return n;
        return null;
    }

    private static decimal? ReadDecimal(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out decimal d)) return d;
        return null;
    }

    private static string? ReadString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? ReadTime(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) return t;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long ms)) return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return null;
    }
}
