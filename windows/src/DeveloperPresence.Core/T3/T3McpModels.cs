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
    public string? ParentThreadId { get; set; }
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
