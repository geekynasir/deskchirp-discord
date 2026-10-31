using System.Text.Json.Serialization;

namespace DeveloperPresence.Core.T3;

public sealed class T3ProtectedResourceMetadata
{
    [JsonPropertyName("resource")]
    public string Resource { get; set; } = string.Empty;

    [JsonPropertyName("authorization_servers")]
    public List<string> AuthorizationServers { get; set; } = new();

    [JsonPropertyName("scopes_supported")]
    public List<string> ScopesSupported { get; set; } = new();

    [JsonPropertyName("resource_name")]
    public string? ResourceName { get; set; }
}

public sealed class T3AuthorizationServerMetadata
{
    [JsonPropertyName("issuer")]
    public string Issuer { get; set; } = string.Empty;

    [JsonPropertyName("authorization_endpoint")]
    public string AuthorizationEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("registration_endpoint")]
    public string RegistrationEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("response_types_supported")]
    public List<string> ResponseTypesSupported { get; set; } = new();

    [JsonPropertyName("grant_types_supported")]
    public List<string> GrantTypesSupported { get; set; } = new();

    [JsonPropertyName("code_challenge_methods_supported")]
    public List<string> CodeChallengeMethodsSupported { get; set; } = new();

    [JsonPropertyName("scopes_supported")]
    public List<string> ScopesSupported { get; set; } = new();
}

public sealed class T3ClientRegistrationRequest
{
    [JsonPropertyName("client_name")]
    public string ClientName { get; set; } = "Developer Presence";

    [JsonPropertyName("redirect_uris")]
    public List<string> RedirectUris { get; set; } = new();

    [JsonPropertyName("token_endpoint_auth_method")]
    public string TokenEndpointAuthMethod { get; set; } = "none";

    [JsonPropertyName("grant_types")]
    public List<string> GrantTypes { get; set; } = new() { "authorization_code" };

    [JsonPropertyName("response_types")]
    public List<string> ResponseTypes { get; set; } = new() { "code" };
}

public sealed class T3ClientRegistrationResponse
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("client_name")]
    public string? ClientName { get; set; }

    [JsonPropertyName("redirect_uris")]
    public List<string> RedirectUris { get; set; } = new();
}

public sealed class T3TokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

public sealed class T3StoredToken
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("mcp_uri")]
    public string McpUri { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsExpired => ExpiresAt.HasValue && DateTimeOffset.UtcNow >= ExpiresAt.Value;
}
