using System.Text.Json.Serialization;

namespace DeveloperPresence.Core.Models;

/// <summary>
/// Opcode 0 Handshake payload sent when opening Discord IPC connection.
/// </summary>
public sealed class DiscordHandshakePayload
{
    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;
}

/// <summary>
/// Opcode 1 SET_ACTIVITY request payload sent over Discord IPC.
/// </summary>
public sealed class DiscordSetActivityPayload
{
    [JsonPropertyName("cmd")]
    public string Command { get; set; } = "SET_ACTIVITY";

    [JsonPropertyName("args")]
    public DiscordSetActivityArgs Args { get; set; } = new();

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = Guid.NewGuid().ToString();
}

public sealed class DiscordSetActivityArgs
{
    [JsonPropertyName("pid")]
    public int ProcessId { get; set; }

    [JsonPropertyName("activity")]
    public DiscordActivity? Activity { get; set; }
}

/// <summary>
/// Discord Activity artwork and overlay icons.
/// </summary>
public sealed class DiscordActivityAssets : IEquatable<DiscordActivityAssets>
{
    [JsonPropertyName("large_image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LargeImage { get; set; }

    [JsonPropertyName("large_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LargeText { get; set; }

    [JsonPropertyName("small_image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SmallImage { get; set; }

    [JsonPropertyName("small_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SmallText { get; set; }

    public bool Equals(DiscordActivityAssets? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(LargeImage, other.LargeImage, StringComparison.Ordinal) &&
               string.Equals(LargeText, other.LargeText, StringComparison.Ordinal) &&
               string.Equals(SmallImage, other.SmallImage, StringComparison.Ordinal) &&
               string.Equals(SmallText, other.SmallText, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => Equals(obj as DiscordActivityAssets);

    public override int GetHashCode() =>
        HashCode.Combine(LargeImage, LargeText, SmallImage, SmallText);
}

/// <summary>Discord activity timestamps. Only <c>start</c> (Unix seconds) is ever sent.</summary>
public sealed class DiscordActivityTimestamps : IEquatable<DiscordActivityTimestamps>
{
    [JsonPropertyName("start")]
    public long Start { get; set; }

    public bool Equals(DiscordActivityTimestamps? other) => other is not null && Start == other.Start;
    public override bool Equals(object? obj) => Equals(obj as DiscordActivityTimestamps);
    public override int GetHashCode() => Start.GetHashCode();
}

/// <summary>A Discord activity button. Discord IPC accepts at most two, as {label, url}.</summary>
public sealed class DiscordActivityButton : IEquatable<DiscordActivityButton>
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    public bool Equals(DiscordActivityButton? other) => other is not null &&
        string.Equals(Label, other.Label, StringComparison.Ordinal) && string.Equals(Url, other.Url, StringComparison.Ordinal);
    public override bool Equals(object? obj) => Equals(obj as DiscordActivityButton);
    public override int GetHashCode() => HashCode.Combine(Label, Url);
}

/// <summary>
/// Discord Activity payload.
/// Supports details, state, artwork assets, an elapsed timer start, and up to two fixed buttons.
/// Every value comes from the allowlisted presence formatter; nothing here is free-form server text.
/// </summary>
public sealed class DiscordActivity : IEquatable<DiscordActivity>
{
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Details { get; set; }

    [JsonPropertyName("state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? State { get; set; }

    [JsonPropertyName("timestamps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiscordActivityTimestamps? Timestamps { get; set; }

    [JsonPropertyName("assets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiscordActivityAssets? Assets { get; set; }

    [JsonPropertyName("buttons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DiscordActivityButton>? Buttons { get; set; }

    public bool Equals(DiscordActivity? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Details, other.Details, StringComparison.Ordinal) &&
               string.Equals(State, other.State, StringComparison.Ordinal) &&
               Equals(Timestamps, other.Timestamps) &&
               Equals(Assets, other.Assets) &&
               ButtonsEqual(Buttons, other.Buttons);
    }

    private static bool ButtonsEqual(List<DiscordActivityButton>? a, List<DiscordActivityButton>? b)
    {
        if (a is null || a.Count == 0) return b is null || b.Count == 0;
        return b is not null && a.SequenceEqual(b);
    }

    public override bool Equals(object? obj) => Equals(obj as DiscordActivity);

    public override int GetHashCode() =>
        HashCode.Combine(Details, State, Timestamps, Assets, Buttons?.Count ?? 0);
}

/// <summary>
/// Minimal structure for parsing incoming Discord IPC responses and events.
/// </summary>
public sealed class DiscordGenericMessage
{
    [JsonPropertyName("cmd")]
    public string? Command { get; set; }

    [JsonPropertyName("evt")]
    public string? Event { get; set; }

    [JsonPropertyName("nonce")]
    public string? Nonce { get; set; }
}
