using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeveloperPresence.Core.T3.Usage;

namespace DeveloperPresence.Core.T3.Desktop;

public enum T3DesktopPairingOutcome { Success, TokenRejected, MissingPermission, Unreachable, Failed }

public sealed record T3DesktopPairingResult(T3DesktopPairingOutcome Outcome, string? Code = null)
{
    public override string ToString() => Outcome.ToString(); // Never the code.
}

public interface IT3DesktopPairingClient
{
    /// <summary>Asks T3 for a one-time pairing code with these scopes, using T3 Code's own sign-in.</summary>
    Task<T3DesktopPairingResult> MintCodeAsync(T3DesktopRoute route, IReadOnlyList<string> scopes, CancellationToken ct = default);
}

/// <summary>
/// Mints one-time pairing codes the way T3 Code's "Create pairing link" does. The desktop
/// token goes only to the route's own origin, never follows redirects, and is never stored.
/// </summary>
public sealed class T3DesktopPairingClient : IT3DesktopPairingClient
{
    public const string ReadScope = "orchestration:read";
    public const string DiagnosticsScope = "diagnostics:read";
    public const int MaxResponseBytes = 64 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;

    public T3DesktopPairingClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout };
    }

    public async Task<T3DesktopPairingResult> MintCodeAsync(T3DesktopRoute route, IReadOnlyList<string> scopes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        try
        {
            string origin = T3UsagePairingInput.OriginOf(route.Origin);
            string body = JsonSerializer.Serialize(new { label = "Deskchirp", scopes });
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(origin + "/"), "api/auth/pairing-token"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", route.DesktopToken);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(T3DesktopPairingOutcome.TokenRejected);
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(T3DesktopPairingOutcome.MissingPermission);
            if (!response.IsSuccessStatusCode) return new(T3DesktopPairingOutcome.Failed);

            string json = await T3UsagePairingClient.ReadBoundedAsync(response, MaxResponseBytes, deadline.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("credential", out var credential) ||
                credential.ValueKind != JsonValueKind.String ||
                !T3UsagePairingInput.TryParse(credential.GetString(), route.Origin, out string code, out _))
                return new(T3DesktopPairingOutcome.Failed);
            return new(T3DesktopPairingOutcome.Success, code);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { return new(T3DesktopPairingOutcome.Unreachable); }
        catch (Exception) { return new(T3DesktopPairingOutcome.Failed); }
    }
}
