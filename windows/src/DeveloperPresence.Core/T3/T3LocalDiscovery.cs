using System.Text.Json;

namespace DeveloperPresence.Core.T3;

public interface IT3LocalDiscovery
{
    Task<string> FindAsync(CancellationToken ct = default);
}

/// Reads only T3's public runtime address. Never opens its catalog, credentials or conversations;
/// following the desktop app's connections is T3DesktopFollower's job and has its own switch.
public sealed class T3LocalDiscovery : IT3LocalDiscovery
{
    public const string NotFound = "Open T3 Code on this computer, then try again. If you use a remote environment, enter its address under Advanced.";
    public const string Ambiguous = "More than one T3 Code environment was found. Choose an address under Advanced.";
    private readonly string _runtimePath;
    private readonly Func<Uri, CancellationToken, Task<bool>> _probe;

    public T3LocalDiscovery(string? runtimePath = null, Func<Uri, CancellationToken, Task<bool>>? probe = null)
    {
        string home = Environment.GetEnvironmentVariable("T3CODE_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".t3");
        _runtimePath = runtimePath ?? Path.Combine(home, "userdata", "server-runtime.json");
        _probe = probe ?? ProbeAsync;
    }

    public async Task<string> FindAsync(CancellationToken ct = default)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal) { "http://127.0.0.1:3773/mcp" };
        try
        {
            await using var file = File.OpenRead(_runtimePath);
            if (file.Length <= 16_384)
            {
                byte[] bytes = new byte[16_385];
                int count = 0, read;
                while (count < bytes.Length && (read = await file.ReadAsync(bytes.AsMemory(count), ct)) > 0) count += read;
                var candidate = count <= 16_384 ? ParseRuntime(bytes.AsSpan(0, count).ToArray()) : null;
                if (candidate != null) candidates.Add(candidate);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        var found = new List<string>();
        foreach (string candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (await _probe(new Uri(candidate), ct)) found.Add(candidate);
        }
        ct.ThrowIfCancellationRequested();
        return found.Count switch { 1 => found[0], 0 => throw new InvalidOperationException(NotFound), _ => throw new InvalidOperationException(Ambiguous) };
    }

    public static string? ParseRuntime(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("pid").GetInt32() <= 0) return null;
            int port = root.GetProperty("port").GetInt32();
            if (port < 1 || port > 65535) return null;
            string host = root.TryGetProperty("host", out var h) && h.GetString() == "::1" ? "[::1]" : "127.0.0.1";
            return $"http://{host}:{port}/mcp";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    public static bool MatchesMetadata(byte[] bytes, Uri endpoint)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            return root.GetProperty("resource").GetString() == endpoint.AbsoluteUri &&
                root.GetProperty("authorization_servers").EnumerateArray().Any(x => x.GetString() == endpoint.GetLeftPart(UriPartial.Authority)) &&
                root.GetProperty("scopes_supported").EnumerateArray().Any(x => x.GetString() == "orchestration:read");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }

    private static async Task<bool> ProbeAsync(Uri endpoint, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        using var client = new HttpClient(handler);
        try
        {
            using var response = await client.GetAsync(new Uri(endpoint, "/.well-known/oauth-protected-resource/mcp"), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 16_384) return false;
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            byte[] bytes = new byte[16_385];
            int count = 0, read;
            while (count < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(count), deadline.Token)) > 0) count += read;
            return count <= 16_384 && MatchesMetadata(bytes.AsSpan(0, count).ToArray(), endpoint);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; }
        catch (IOException) { return false; }
    }
}
