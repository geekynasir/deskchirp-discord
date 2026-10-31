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
/// Discord Activity payload.
/// Strictly limited to details only in version 1. No timestamps, assets, secrets, or fake states.
/// </summary>
public sealed class DiscordActivity
{
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Details { get; set; }

    [JsonPropertyName("state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? State { get; set; }
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
