namespace DeveloperPresence.Core.T3;

/// <summary>
/// Strict URL validation and endpoint normalization for T3 Code environments.
/// Enforces HTTPS or localhost loopback HTTP, rejects userinfo/query/fragment, and normalizes to /mcp.
/// </summary>
public static class T3UrlValidator
{
    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "127.0.0.1",
        "::1",
        "[::1]"
    };

    public const string MsgEmpty = "Paste the address from T3 Code first.";
    public const string MsgNotHttpsOrLocalhost = "Use an address that starts with https://. Plain http:// works only for T3 Code on this computer.";
    public const string MsgInvalidUrl = "This doesn’t look like a T3 Code address. Copy it again from Settings → Environments → Copy MCP URL.";

    public static bool TryNormalizeT3Address(
        string? rawAddress,
        out Uri? normalizedMcpUri,
        out string? errorMessage)
    {
        normalizedMcpUri = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            errorMessage = MsgEmpty;
            return false;
        }

        string trimmed = rawAddress.Trim();

        // Reject fragment (RFC 6749 §3.1.2 forbids fragment components; spec disallows fragments)
        if (trimmed.Contains('#'))
        {
            errorMessage = MsgInvalidUrl;
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            errorMessage = MsgInvalidUrl;
            return false;
        }

        // Must reject userinfo
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            errorMessage = MsgInvalidUrl;
            return false;
        }

        // Must reject query string
        if (!string.IsNullOrEmpty(uri.Query))
        {
            errorMessage = MsgInvalidUrl;
            return false;
        }

        bool isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        bool isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        if (!isHttps && !isHttp)
        {
            errorMessage = MsgNotHttpsOrLocalhost;
            return false;
        }

        // If HTTP, host MUST be exact loopback (localhost / 127.0.0.1 / [::1])
        if (isHttp && !IsLoopbackHost(uri.IdnHost))
        {
            errorMessage = MsgNotHttpsOrLocalhost;
            return false;
        }

        // Path validation: allowed path is empty, "/", or "/mcp"
        string path = uri.AbsolutePath.TrimEnd('/');
        if (path != "" && !string.Equals(path, "/mcp", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = MsgInvalidUrl;
            return false;
        }

        // Normalize to ${origin}/mcp
        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.Port, "mcp");
        normalizedMcpUri = builder.Uri;
        return true;
    }

    public static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string clean = host.Trim('[', ']');
        return LoopbackHosts.Contains(clean) || LoopbackHosts.Contains(host);
    }

    public static Uri GetOriginUri(Uri mcpUri)
    {
        ArgumentNullException.ThrowIfNull(mcpUri);
        return new Uri(mcpUri.GetLeftPart(UriPartial.Authority));
    }
}
