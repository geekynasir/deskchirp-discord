using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DeveloperPresence.Core.T3;

public sealed class T3OAuthClient : IT3OAuthClient
{
    private readonly HttpClient _httpClient;
    private readonly IBrowserLauncher _browserLauncher;
    private readonly Func<ILoopbackCallbackListener> _listenerFactory;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public const int MaxMetadataBytes = 65536; // 64 KiB cap for metadata responses

    public T3OAuthClient() : this(CreateDefaultHttpClient(), new SystemBrowserLauncher(), TcpLoopbackCallbackListener.Create)
    {
    }

    public T3OAuthClient(
        HttpClient httpClient,
        IBrowserLauncher browserLauncher,
        Func<ILoopbackCallbackListener>? listenerFactory = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _browserLauncher = browserLauncher ?? throw new ArgumentNullException(nameof(browserLauncher));
        _listenerFactory = listenerFactory ?? TcpLoopbackCallbackListener.Create;
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false // Strict requirement: do not follow redirects silently
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<T3StoredToken> SignInAsync(Uri normalizedMcpUri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedMcpUri);

        Uri origin = T3UrlValidator.GetOriginUri(normalizedMcpUri);
        string originStr = origin.GetLeftPart(UriPartial.Authority);

        // 1. Discovery: Protected Resource Metadata
        var resourceMetadata = await DiscoverProtectedResourceAsync(origin, ct).ConfigureAwait(false);
        ValidateProtectedResource(resourceMetadata, normalizedMcpUri, originStr);

        // 2. Discovery: Authorization Server Metadata
        var authServerMetadata = await DiscoverAuthServerAsync(origin, ct).ConfigureAwait(false);
        ValidateAuthServer(authServerMetadata, originStr);

        // 3. Start ephemeral loopback listener bound to port 0
        await using var listener = _listenerFactory();
        string redirectUri = listener.CallbackUriString;

        // 4. Dynamic Client Registration
        string clientId = await RegisterClientAsync(authServerMetadata.RegistrationEndpoint, redirectUri, ct).ConfigureAwait(false);

        // 5. Generate PKCE and State
        var (codeVerifier, codeChallenge) = PkceUtility.GeneratePkce();
        string state = PkceUtility.GenerateState();

        // 6. Build authorization URL
        string authUrl = $"{authServerMetadata.AuthorizationEndpoint}" +
            $"?response_type=code" +
            $"&client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            $"&code_challenge_method=S256" +
            $"&scope=orchestration%3Aread" +
            $"&resource={Uri.EscapeDataString(normalizedMcpUri.AbsoluteUri)}";

        // 7. Open default browser
        if (!_browserLauncher.OpenBrowser(new Uri(authUrl)))
        {
            throw new InvalidOperationException("Couldn’t open your browser. Check that you have a default browser, then try again.");
        }

        // 8. Wait for authorization code via loopback callback
        OAuthCallbackRequest callbackRequest;
        try
        {
            callbackRequest = await listener.WaitForCallbackAsync(TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Sign-in timed out. Try connecting again.");
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("Sign-in was cancelled.");
        }

        // Order of checks: verify state before error and issuer!
        if (string.IsNullOrEmpty(callbackRequest.State) || !string.Equals(callbackRequest.State, state, StringComparison.Ordinal))
        {
            await listener.RespondAsync(success: false, exchangeCompleted: false, ct).ConfigureAwait(false);
            throw new InvalidOperationException("Sign-in didn’t finish. Try connecting again.");
        }

        // T3 advertises RFC 9207: every authorization response must identify its issuer.
        if (!string.Equals(callbackRequest.Issuer, authServerMetadata.Issuer, StringComparison.Ordinal))
        {
            await listener.RespondAsync(success: false, exchangeCompleted: false, ct).ConfigureAwait(false);
            throw new InvalidOperationException("Sign-in didn’t finish. Try connecting again.");
        }

        // Check for user cancellation or denial
        if (!string.IsNullOrEmpty(callbackRequest.Error))
        {
            await listener.RespondAsync(success: false, exchangeCompleted: false, ct).ConfigureAwait(false);
            throw new InvalidOperationException("Sign-in was cancelled.");
        }

        if (string.IsNullOrEmpty(callbackRequest.Code))
        {
            await listener.RespondAsync(success: false, exchangeCompleted: false, ct).ConfigureAwait(false);
            throw new InvalidOperationException("Sign-in didn’t finish. Try connecting again.");
        }

        string authorizationCode = callbackRequest.Code;

        // 9. Exchange authorization code for bearer token
        T3TokenResponse tokenResponse;
        try
        {
            tokenResponse = await ExchangeCodeAsync(
                authServerMetadata.TokenEndpoint,
                authorizationCode,
                redirectUri,
                clientId,
                codeVerifier,
                normalizedMcpUri.AbsoluteUri,
                ct).ConfigureAwait(false);

            ValidateTokenResponse(tokenResponse);

            // Response page indicates approval and connection complete
            await listener.RespondAsync(success: true, exchangeCompleted: false, ct).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await listener.RespondAsync(success: false, exchangeCompleted: false, ct).ConfigureAwait(false);
            }
            catch { }
            throw;
        }

        DateTimeOffset? expiresAt = tokenResponse.ExpiresIn > 0
            ? DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn)
            : null;

        return new T3StoredToken
        {
            AccessToken = tokenResponse.AccessToken,
            TokenType = tokenResponse.TokenType,
            ExpiresAt = expiresAt,
            McpUri = normalizedMcpUri.AbsoluteUri
        };
    }

    private static void ValidateTokenResponse(T3TokenResponse token)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("Sign-in didn’t finish. Try connecting again.");
        }

        if (!string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Sign-in didn’t finish. Try connecting again.");
        }

        if (string.IsNullOrWhiteSpace(token.Scope))
        {
            throw new InvalidOperationException("Sign-in didn’t finish. Try connecting again.");
        }

        var scopes = token.Scope.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (scopes.Length != 1 || scopes[0] != "orchestration:read")
        {
            throw new InvalidOperationException("Sign-in didn’t finish. Try connecting again.");
        }

        // Validate positive bounded expiry (between 1 second and 1 year)
        if (token.ExpiresIn <= 0 || token.ExpiresIn > 31536000)
        {
            throw new InvalidOperationException("Sign-in didn’t finish. Try connecting again.");
        }
    }

    private async Task<T3ProtectedResourceMetadata> DiscoverProtectedResourceAsync(Uri origin, CancellationToken ct)
    {
        string url = $"{origin.GetLeftPart(UriPartial.Authority)}/.well-known/oauth-protected-resource/mcp";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendWithoutRedirectAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.");
        }

        string json = await ReadBoundedStringAsync(response, MaxMetadataBytes, ct).ConfigureAwait(false);
        var meta = JsonSerializer.Deserialize<T3ProtectedResourceMetadata>(json, JsonOptions);
        if (meta == null)
        {
            throw new InvalidOperationException("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.");
        }
        return meta;
    }

    private static void ValidateProtectedResource(T3ProtectedResourceMetadata meta, Uri normalizedMcpUri, string origin)
    {
        string expectedResource = normalizedMcpUri.AbsoluteUri.TrimEnd('/');
        string actualResource = meta.Resource?.TrimEnd('/') ?? "";

        if (!string.Equals(expectedResource, actualResource, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.");
        }

        bool hasMatchingAuthServer = false;
        foreach (var server in meta.AuthorizationServers)
        {
            ValidateEndpointUrl(server, origin, "authorization server");
            if (string.Equals(server.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                hasMatchingAuthServer = true;
            }
        }

        if (!hasMatchingAuthServer)
        {
            throw new InvalidOperationException("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.");
        }

        if (!meta.ScopesSupported.Any(s => string.Equals(s, "orchestration:read", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.");
        }
    }

    private async Task<T3AuthorizationServerMetadata> DiscoverAuthServerAsync(Uri origin, CancellationToken ct)
    {
        string url = $"{origin.GetLeftPart(UriPartial.Authority)}/.well-known/oauth-authorization-server";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendWithoutRedirectAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.");
        }

        string json = await ReadBoundedStringAsync(response, MaxMetadataBytes, ct).ConfigureAwait(false);
        var meta = JsonSerializer.Deserialize<T3AuthorizationServerMetadata>(json, JsonOptions);
        if (meta == null)
        {
            throw new InvalidOperationException("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.");
        }
        return meta;
    }

    private static void ValidateAuthServer(T3AuthorizationServerMetadata meta, string origin)
    {
        string cleanIssuer = meta.Issuer?.TrimEnd('/') ?? "";
        string cleanOrigin = origin.TrimEnd('/');

        if (!string.Equals(cleanIssuer, cleanOrigin, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.");
        }

        ValidateEndpointUrl(meta.AuthorizationEndpoint, origin, "authorization_endpoint");
        ValidateEndpointUrl(meta.TokenEndpoint, origin, "token_endpoint");
        ValidateEndpointUrl(meta.RegistrationEndpoint, origin, "registration_endpoint");

        if (!meta.CodeChallengeMethodsSupported.Contains("S256", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.");
        }
    }

    private static void ValidateEndpointUrl(string? url, string expectedOrigin, string endpointName)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Contains('#'))
        {
            throw new InvalidOperationException("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.");
        }

        // Must reject userinfo and query in metadata endpoints
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query))
        {
            throw new InvalidOperationException("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.");
        }

        string cleanOrigin = expectedOrigin.TrimEnd('/');
        string endpointOrigin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');

        if (!string.Equals(cleanOrigin, endpointOrigin, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.");
        }
    }

    private async Task<string> RegisterClientAsync(string registrationEndpoint, string redirectUri, CancellationToken ct)
    {
        var regRequest = new T3ClientRegistrationRequest
        {
            ClientName = "Developer Presence",
            RedirectUris = new List<string> { redirectUri },
            TokenEndpointAuthMethod = "none"
        };

        string json = JsonSerializer.Serialize(regRequest);
        using var request = new HttpRequestMessage(HttpMethod.Post, registrationEndpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await SendWithoutRedirectAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.");
        }

        string respJson = await ReadBoundedStringAsync(response, MaxMetadataBytes, ct).ConfigureAwait(false);
        var regResp = JsonSerializer.Deserialize<T3ClientRegistrationResponse>(respJson, JsonOptions);
        if (regResp == null || string.IsNullOrWhiteSpace(regResp.ClientId))
        {
            throw new InvalidOperationException("This address didn’t respond like T3 Code. Check that you copied it with Copy MCP URL.");
        }

        return regResp.ClientId;
    }

    private async Task<T3TokenResponse> ExchangeCodeAsync(
        string tokenEndpoint,
        string code,
        string redirectUri,
        string clientId,
        string codeVerifier,
        string resource,
        CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = codeVerifier,
            ["resource"] = resource
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await SendWithoutRedirectAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Sign-in didn’t finish. Try connecting again.");
        }

        string json = await ReadBoundedStringAsync(response, MaxMetadataBytes, ct).ConfigureAwait(false);
        var tokenResp = JsonSerializer.Deserialize<T3TokenResponse>(json, JsonOptions);
        if (tokenResp == null || string.IsNullOrWhiteSpace(tokenResp.AccessToken))
        {
            throw new InvalidOperationException("Sign-in didn’t finish. Try connecting again.");
        }

        return tokenResp;
    }

    private async Task<HttpResponseMessage> SendWithoutRedirectAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Enforce ResponseHeadersRead to avoid buffering unbounded response bytes into memory
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
        {
            throw new InvalidOperationException("T3 Code sent Developer Presence to a different address. Copy the address again from T3 Code.");
        }
        return response;
    }

    private static async Task<string> ReadBoundedStringAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] buffer = new byte[4096];
        int totalRead = 0;

        while (true)
        {
            int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
            if (read == 0) break;

            totalRead += read;
            if (totalRead > maxBytes)
            {
                throw new InvalidOperationException($"Response exceeded maximum allowed size of {maxBytes} bytes.");
            }
            ms.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
