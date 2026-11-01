using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DeveloperPresence.Core.T3.Usage;

/// <summary>Token usage credential (spec/t3-usage-transport.md). Stored only in secure storage.</summary>
public sealed class T3UsageCredential
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } at && now >= at;
}

public enum T3PairingOutcome { Success, InvalidInput, CodeRejected, MissingPermission, Failed }

public sealed record T3PairingResult(T3PairingOutcome Outcome, string Message, T3UsageCredential? Credential = null);

/// <summary>Product copy for token usage pairing (docs/product-language.md voice).</summary>
public static class T3UsageText
{
    public const string Explanation = "T3 needs a one-time pairing code from that computer. Run the command below there, then paste the code. ";
    public const string PairCommand = "t3 pair --scope orchestration:read --scope diagnostics:read --label Deskchirp";
    public const string PermissionWording = "Token usage needs a second permission from T3. It also allows reading diagnostics and task details. Deskchirp uses it only for token counts and never stores or sends conversation content.";
    public const string CodeRejected = "That code didn’t work. Codes work once and expire after 5 minutes.";
    public const string MissingPermission = "That code doesn’t include token usage. Create a new code with the command below, then paste it.";
    public const string InvalidInput = "Paste the pairing code or the pairing link from that computer.";
    public const string OtherOrigin = "That pairing link is for a different computer. Use a code from the computer that runs this environment.";
    public const string Failed = "Pairing didn’t finish. Check that T3 Code is running there, then try again.";
    public const string PairingDiscarded = "Token access was removed while pairing, so that code wasn’t saved.";
    public const string PairAgain = "Pair again to keep showing tokens";
    public const string Unsupported = "This T3 version isn’t supported for token usage.";
    public const string Allowed = "Token usage allowed. Your access is saved in Windows Credential Manager.";
    public const string NotAllowed = "Token usage isn’t allowed for this environment.";
    public const string Unavailable = "Token usage isn’t available right now, so the card shows work instead.";
}

public static partial class T3UsagePairingInput
{
    [GeneratedRegex("^[A-Za-z0-9]{6,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    /// <summary>Normalized origin used for keys and requests: scheme://host[:port], no trailing slash.</summary>
    public static string OriginOf(Uri uri) => uri.GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// Accepts a bare code or a pairing URL on the environment's own origin whose fragment is token=CODE.
    /// This is not an access-token paste field.
    /// </summary>
    public static bool TryParse(string? raw, Uri environmentOrigin, out string code, out string error)
    {
        code = string.Empty;
        error = T3UsageText.InvalidInput;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string text = raw.Trim();
        if (CodePattern().IsMatch(text)) { code = text; error = string.Empty; return true; }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo))
            return false;
        if (!string.Equals(OriginOf(uri), OriginOf(environmentOrigin), StringComparison.OrdinalIgnoreCase))
        {
            error = T3UsageText.OtherOrigin;
            return false;
        }
        if (!string.IsNullOrEmpty(uri.Query)) return false;
        string fragment = uri.Fragment.StartsWith('#') ? uri.Fragment[1..] : uri.Fragment;
        if (!fragment.StartsWith("token=", StringComparison.Ordinal)) return false;
        string candidate = fragment["token=".Length..];
        if (!CodePattern().IsMatch(candidate)) return false;
        code = candidate;
        error = string.Empty;
        return true;
    }
}

/// <summary>Exchanges a one-time pairing code for a 30-day usage token. No DPoP, no redirects.</summary>
public sealed class T3UsagePairingClient
{
    public const int MaxResponseBytes = 64 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _clock;

    public T3UsagePairingClient(HttpClient? http = null, Func<DateTimeOffset>? clock = null)
    {
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout };
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<T3PairingResult> ExchangeAsync(Uri origin, string code, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(T3UsagePairingInput.OriginOf(origin) + "/"), "oauth/token"))
            {
                Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange"),
                    new KeyValuePair<string, string>("subject_token", code),
                    new KeyValuePair<string, string>("subject_token_type", "urn:t3:params:oauth:token-type:environment-bootstrap"),
                    new KeyValuePair<string, string>("requested_token_type", "urn:ietf:params:oauth:token-type:access_token"),
                    new KeyValuePair<string, string>("scope", "orchestration:read diagnostics:read"),
                    new KeyValuePair<string, string>("client_label", "Deskchirp"),
                    new KeyValuePair<string, string>("client_device_type", "desktop"),
                    new KeyValuePair<string, string>("client_os", "Windows"),
                })
            };
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(T3PairingOutcome.CodeRejected, T3UsageText.CodeRejected);
            if (!response.IsSuccessStatusCode) return new(T3PairingOutcome.Failed, T3UsageText.Failed);
            string body = await ReadBoundedAsync(response, MaxResponseBytes, deadline.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(at.GetString()) ||
                !root.TryGetProperty("token_type", out var tt) || tt.ValueKind != JsonValueKind.String ||
                !string.Equals(tt.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
                return new(T3PairingOutcome.Failed, T3UsageText.Failed);
            // T3 drops scopes the code did not grant instead of failing, so check what came back.
            string scope = root.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String ? sc.GetString()! : string.Empty;
            if (!scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("diagnostics:read", StringComparer.Ordinal))
                return new(T3PairingOutcome.MissingPermission, T3UsageText.MissingPermission);
            DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number &&
                                        ei.TryGetInt64(out long seconds) && seconds > 0
                ? _clock().AddSeconds(seconds) : null;
            return new(T3PairingOutcome.Success, T3UsageText.Allowed, new T3UsageCredential { AccessToken = at.GetString()!, ExpiresAt = expiresAt });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(T3PairingOutcome.Failed, T3UsageText.Failed); }
    }

    internal static async Task<string> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Response too large.");
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes) throw new InvalidDataException("Response too large.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
