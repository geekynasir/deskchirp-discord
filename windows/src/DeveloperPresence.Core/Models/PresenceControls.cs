using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeveloperPresence.Core.Models;

// Presence controls v3 (spec/presence-v3.md §3). Enum values are written as the camelCase
// names used by the spec so settings files stay readable; unknown or malformed values decode
// to the privacy-preserving default instead of failing the whole settings file.

[JsonConverter(typeof(SettingsEnumConverter<CardStyle>))]
public enum CardStyle { Workload = 0, AppOnly = 1, Usage = 2 }

[JsonConverter(typeof(SettingsEnumConverter<UsageMetric>))]
public enum UsageMetric { Tokens = 0, Context = 1 }

[JsonConverter(typeof(SettingsEnumConverter<UsageScope>))]
public enum UsageScope { Today = 0, Session = 1, PinnedTask = 2 }

[JsonConverter(typeof(SettingsEnumConverter<TokenCategory>))]
public enum TokenCategory { Total = 0, Input = 1, Output = 2 }

[JsonConverter(typeof(SettingsEnumConverter<TokenRounding>))]
public enum TokenRounding { Compact = 0, Exact = 1 }

[JsonConverter(typeof(SettingsEnumConverter<SmallBadge>))]
public enum SmallBadge { Status = 0, Provider = 1, None = 2 }

[JsonConverter(typeof(SettingsEnumConverter<PresenceButtons>))]
public enum PresenceButtons { None = 0, Companion = 1, CompanionAndRepo = 2 }

[JsonConverter(typeof(SettingsEnumConverter<SelectionMode>))]
public enum SelectionMode { KeepBackgroundVisible = 0, FollowActiveApp = 1 }

/// <summary>
/// One T3 environment. Only the normalized address and a local label are saved; the label never
/// leaves this computer. Credentials live in Windows Credential Manager, never here.
/// </summary>
public sealed class T3Environment
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Address { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    public T3Environment Clone() => new() { Id = Id, Address = Address, Label = Label };
}

/// <summary>Reads strings case-insensitively (or legacy integers); writes camelCase names.</summary>
public sealed class SettingsEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? text = reader.GetString();
            if (!string.IsNullOrEmpty(text) && !char.IsDigit(text[0]) && text[0] != '-' &&
                Enum.TryParse<T>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                return parsed;
            return default;
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number))
        {
            var value = (T)Enum.ToObject(typeof(T), number);
            return Enum.IsDefined(value) ? value : default;
        }
        reader.Skip();
        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        string name = value.ToString();
        writer.WriteStringValue(char.ToLowerInvariant(name[0]) + name[1..]);
    }
}
