using System.Text.Json.Serialization;

namespace DeveloperPresence.Core.T3;

public sealed class T3ProviderCapability
{
    [JsonPropertyName("providerInstanceId")]
    public string ProviderInstanceId { get; set; } = string.Empty;

    [JsonPropertyName("driverKind")]
    public string DriverKind { get; set; } = string.Empty;

    // displayName, models, etc. are explicitly ignored for privacy and not exposed to public presence
}

public sealed class T3CapabilitiesResult
{
    [JsonPropertyName("providers")]
    public List<T3ProviderCapability> Providers { get; set; } = new();
}

public sealed class T3ThreadListItem
{
    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = string.Empty;

    [JsonPropertyName("providerInstanceId")]
    public string ProviderInstanceId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("relationshipToParent")]
    public string? RelationshipToParent { get; set; }

    [JsonPropertyName("parentThreadId")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? ParentThreadId { get; set; }

    // Presence v3 metadata (spec/presence-v3.md §2). Non-string values decode as null.

    [JsonPropertyName("projectId")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? ProjectId { get; set; }

    /// <summary>Only published after <see cref="Privacy.PresenceCardFormatter"/> validates it, and only when shareModel is on.</summary>
    [JsonPropertyName("model")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Model { get; set; }

    [JsonPropertyName("createdAt")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? UpdatedAt { get; set; }

    /// <summary>
    /// Local settings picker only. Never logged, persisted, or published. The formatter never reads it.
    /// </summary>
    [JsonPropertyName("title")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Title { get; set; }

    public DateTimeOffset? UpdatedAtValue => ParseTime(UpdatedAt);
    public DateTimeOffset? CreatedAtValue => ParseTime(CreatedAt);

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
}

/// <summary>A project from t3_project_list. The title is shown only in the local alias editor and pickers.</summary>
public sealed class T3ProjectListItem
{
    public string ProjectId { get; set; } = string.Empty;
    public string? Title { get; set; }
}

/// <summary>Decodes string values; any other JSON kind becomes null instead of failing the snapshot.</summary>
public sealed class LenientStringConverter : JsonConverter<string?>
{
    public override bool HandleNull => true;

    public override string? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.String) return reader.GetString();
        reader.Skip();
        return null;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, string? value, System.Text.Json.JsonSerializerOptions options)
    {
        if (value == null) writer.WriteNullValue(); else writer.WriteStringValue(value);
    }
}

public sealed class T3ThreadListResult
{
    [JsonPropertyName("threads")]
    public List<T3ThreadListItem> Threads { get; set; } = new();

    [JsonPropertyName("nextCursor")]
    public int? NextCursor { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}
