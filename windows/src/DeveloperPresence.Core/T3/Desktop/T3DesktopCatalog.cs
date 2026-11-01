using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeveloperPresence.Core.T3.Desktop;

/// <summary>One way to reach a saved T3 environment, with the T3 Code desktop app's own sign-in for it.</summary>
public sealed class T3DesktopRoute
{
    public T3DesktopRoute(string mcpAddress, string desktopToken)
    {
        McpAddress = mcpAddress;
        DesktopToken = desktopToken;
    }

    /// <summary>Normalized MCP address, for example https://host/mcp.</summary>
    public string McpAddress { get; }
    /// <summary>T3 Code's bearer token for this route. Only ever sent back to this route's own origin.</summary>
    public string DesktopToken { get; }
    public Uri Origin => T3UrlValidator.GetOriginUri(new Uri(McpAddress));

    public override string ToString() => McpAddress; // Never the token.
}

/// <summary>An environment saved in the T3 Code desktop app.</summary>
public sealed class T3DesktopEnvironment
{
    public required string EnvironmentId { get; init; }
    public required string Label { get; init; }
    /// <summary>Routes Deskchirp can use, in T3 Code's order of preference.</summary>
    public required IReadOnlyList<T3DesktopRoute> Routes { get; init; }
    /// <summary>Why no route is usable, when <see cref="Routes"/> is empty.</summary>
    public string? UnsupportedReason { get; init; }

    public override string ToString() => $"{Label} ({EnvironmentId}, {Routes.Count} routes)";
}

public enum T3DesktopCatalogStatus
{
    /// <summary>The catalog was read. The list is every enabled environment T3 Code has saved.</summary>
    Read,
    /// <summary>T3 Code has no saved connections on this computer, or it isn't installed.</summary>
    NotFound,
    /// <summary>The catalog exists but couldn't be read, so nothing is known about it.</summary>
    Unreadable
}

public sealed record T3DesktopCatalogResult(T3DesktopCatalogStatus Status, IReadOnlyList<T3DesktopEnvironment> Environments)
{
    public static readonly T3DesktopCatalogResult NotFound = new(T3DesktopCatalogStatus.NotFound, Array.Empty<T3DesktopEnvironment>());
    public static readonly T3DesktopCatalogResult Unreadable = new(T3DesktopCatalogStatus.Unreadable, Array.Empty<T3DesktopEnvironment>());
}

/// <summary>
/// Reads the environments saved in the T3 Code desktop app on this Windows account.
/// T3 Code keeps them in userdata\connection-catalog.json, encrypted with Electron safeStorage:
/// "v10", a 12-byte nonce, then AES-256-GCM ciphertext and tag. The key sits in the app's
/// Local State file, protected with DPAPI for the current user. Decrypted contents are parsed
/// once and dropped; tokens are kept in memory only for the routes Deskchirp can use.
/// </summary>
public sealed class T3DesktopCatalogReader
{
    public const string MsgRelayOnly = "connects through T3 Connect, which Deskchirp can’t use yet";
    public const string MsgSshOnly = "connects over SSH, which Deskchirp can’t use yet";
    public const string MsgInsecureOnly = "uses a plain http address on your network, which Deskchirp doesn’t send sign-ins to";
    public const string MsgNoSignIn = "has no saved sign-in in T3 Code";

    private const int MaxCatalogBytes = 4 * 1024 * 1024;
    private const int MaxLocalStateBytes = 4 * 1024 * 1024;
    private static readonly byte[] V10Prefix = "v10"u8.ToArray();
    private static readonly byte[] DpapiPrefix = "DPAPI"u8.ToArray();

    private readonly string _catalogPath;
    private readonly IReadOnlyList<string> _localStatePaths;
    private readonly Func<byte[], byte[]?> _unprotect;

    public T3DesktopCatalogReader() : this(DefaultCatalogPath(), DefaultLocalStatePaths(), WindowsDpapi.Unprotect)
    {
    }

    public T3DesktopCatalogReader(string catalogPath, IReadOnlyList<string> localStatePaths, Func<byte[], byte[]?> unprotect)
    {
        _catalogPath = catalogPath;
        _localStatePaths = localStatePaths;
        _unprotect = unprotect;
    }

    public string CatalogPath => _catalogPath;

    /// <summary>Last write time of the catalog file, or null when it doesn't exist.</summary>
    public DateTime? CatalogWriteTimeUtc()
    {
        try { return File.Exists(_catalogPath) ? File.GetLastWriteTimeUtc(_catalogPath) : null; }
        catch { return null; }
    }

    public static string DefaultCatalogPath()
    {
        string? home = Environment.GetEnvironmentVariable("T3CODE_HOME")?.Trim();
        string baseDir = string.IsNullOrEmpty(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".t3")
            : home;
        return Path.Combine(baseDir, "userdata", "connection-catalog.json");
    }

    /// <summary>Profiles T3 Code has used on Windows, newest first. Its keys move between them on upgrade.</summary>
    public static IReadOnlyList<string> DefaultLocalStatePaths()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new[] { "t3code-v2", "T3 Code (Alpha)", "t3code" }
            .Select(name => Path.Combine(appData, name, "Local State"))
            .ToList();
    }

    public T3DesktopCatalogResult Read()
    {
        byte[] encrypted;
        try
        {
            if (!File.Exists(_catalogPath)) return T3DesktopCatalogResult.NotFound;
            string text = ReadBounded(_catalogPath, MaxCatalogBytes);
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("encryptedCatalog", out var value) || value.ValueKind != JsonValueKind.String)
                return T3DesktopCatalogResult.Unreadable;
            encrypted = Convert.FromBase64String(value.GetString()!);
        }
        catch { return T3DesktopCatalogResult.Unreadable; }

        byte[]? plain = Decrypt(encrypted);
        if (plain == null) return T3DesktopCatalogResult.Unreadable;
        try
        {
            return new T3DesktopCatalogResult(T3DesktopCatalogStatus.Read, Parse(plain));
        }
        catch { return T3DesktopCatalogResult.Unreadable; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private byte[]? Decrypt(byte[] encrypted)
    {
        if (!encrypted.AsSpan().StartsWith(V10Prefix))
        {
            // Older Electron builds protected the value with DPAPI directly.
            try { return _unprotect(encrypted); } catch { return null; }
        }

        const int nonceLength = 12, tagLength = 16;
        int offset = V10Prefix.Length;
        if (encrypted.Length < offset + nonceLength + tagLength) return null;
        var nonce = encrypted.AsSpan(offset, nonceLength);
        var cipher = encrypted.AsSpan(offset + nonceLength, encrypted.Length - offset - nonceLength - tagLength);
        var tag = encrypted.AsSpan(encrypted.Length - tagLength);

        foreach (var key in LocalStateKeys())
        {
            try
            {
                using var aes = new AesGcm(key, tagLength);
                var plain = new byte[cipher.Length];
                aes.Decrypt(nonce, cipher, tag, plain);
                return plain;
            }
            catch (CryptographicException) { /* Wrong profile's key; try the next one. */ }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        return null;
    }

    private IEnumerable<byte[]> LocalStateKeys()
    {
        foreach (string path in _localStatePaths)
        {
            byte[]? key = null;
            try
            {
                if (!File.Exists(path)) continue;
                using var doc = JsonDocument.Parse(ReadBounded(path, MaxLocalStateBytes));
                if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
                    osCrypt.ValueKind != JsonValueKind.Object ||
                    !osCrypt.TryGetProperty("encrypted_key", out var encryptedKey) ||
                    encryptedKey.ValueKind != JsonValueKind.String)
                    continue;
                byte[] wrapped = Convert.FromBase64String(encryptedKey.GetString()!);
                if (!wrapped.AsSpan().StartsWith(DpapiPrefix)) continue;
                key = _unprotect(wrapped[DpapiPrefix.Length..]);
            }
            catch { key = null; }
            if (key is { Length: 32 }) yield return key;
        }
    }

    /// <summary>Turns the decrypted catalog into environments and their usable routes.</summary>
    internal static IReadOnlyList<T3DesktopEnvironment> Parse(byte[] plain)
    {
        using var doc = JsonDocument.Parse(plain);
        var root = doc.RootElement;
        var disabled = new HashSet<string>(StringArray(root, "disabledEnvironmentIds"), StringComparer.Ordinal);

        var profiles = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var profile in Array(root, "profiles"))
            if (Str(profile, "connectionId") is { } id) profiles[id] = profile;

        var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Array(root, "credentials"))
        {
            if (Str(entry, "connectionId") is not { } id ||
                !entry.TryGetProperty("credential", out var credential) || credential.ValueKind != JsonValueKind.Object ||
                Str(credential, "_tag") != "BearerConnectionCredential" || Str(credential, "token") is not { Length: > 0 } token)
                continue;
            credentials[id] = token;
        }

        var order = new List<string>();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var routes = new Dictionary<string, List<T3DesktopRoute>>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var target in Array(root, "targets"))
        {
            if (Str(target, "environmentId") is not { Length: > 0 } envId || disabled.Contains(envId)) continue;
            if (!routes.ContainsKey(envId))
            {
                order.Add(envId);
                routes[envId] = new List<T3DesktopRoute>();
            }
            if (!labels.ContainsKey(envId) && Str(target, "label") is { Length: > 0 } label) labels[envId] = label.Trim();

            string? reason = null;
            switch (Str(target, "_tag"))
            {
                case "RelayConnectionTarget":
                    reason = MsgRelayOnly;
                    break;
                case "SshConnectionTarget":
                    reason = MsgSshOnly;
                    break;
                case "BearerConnectionTarget":
                    reason = BearerRoute(target, profiles, credentials, out var route);
                    if (route != null && !routes[envId].Any(r => r.McpAddress == route.McpAddress)) routes[envId].Add(route);
                    break;
            }
            // Report the most specific reason once nothing usable turns up.
            if (reason != null && (!reasons.TryGetValue(envId, out var existing) || existing == MsgNoSignIn)) reasons[envId] = reason;
        }

        return order.Select(envId => new T3DesktopEnvironment
        {
            EnvironmentId = envId,
            Label = labels.TryGetValue(envId, out var label) ? label : "T3 Code",
            Routes = routes[envId],
            UnsupportedReason = routes[envId].Count == 0 ? reasons.GetValueOrDefault(envId, MsgNoSignIn) : null
        }).ToList();
    }

    private static string? BearerRoute(
        JsonElement target,
        Dictionary<string, JsonElement> profiles,
        Dictionary<string, string> credentials,
        out T3DesktopRoute? route)
    {
        route = null;
        if (Str(target, "connectionId") is not { } connectionId ||
            !profiles.TryGetValue(connectionId, out var profile) ||
            Str(profile, "_tag") != "BearerConnectionProfile")
            return MsgNoSignIn;
        // T3 Connect routes use proof-of-possession tokens bound to T3 Code's own key.
        if (Str(profile, "authorization") == "t3-connect") return MsgRelayOnly;
        if (!credentials.TryGetValue(CredentialConnectionId(connectionId), out var token)) return MsgNoSignIn;
        if (Str(profile, "httpBaseUrl") is not { } httpBaseUrl ||
            !T3UrlValidator.TryNormalizeT3Address(httpBaseUrl, out var mcpUri, out _))
            return MsgInsecureOnly;
        route = new T3DesktopRoute(mcpUri!.AbsoluteUri, token);
        return null;
    }

    /// <summary>Learned routes share the credential of the route they were learned from (T3's routes.ts).</summary>
    public static string CredentialConnectionId(string connectionId)
    {
        int at = connectionId.IndexOf('@');
        return connectionId.StartsWith("learned:", StringComparison.Ordinal) && at != -1 ? connectionId[(at + 1)..] : connectionId;
    }

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList()
            : Enumerable.Empty<JsonElement>();

    private static IEnumerable<string> StringArray(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : Enumerable.Empty<string>();

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string ReadBounded(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maxBytes) throw new InvalidDataException("File too large.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>Windows DPAPI for the current user. Returns null anywhere else or when unprotecting fails.</summary>
public static class WindowsDpapi
{
    public static byte[]? Unprotect(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var input = new DataBlob();
        var output = new DataBlob();
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            input.cbData = data.Length;
            input.pbData = handle.AddrOfPinnedObject();
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output))
                return null;
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            handle.Free();
            if (output.pbData != IntPtr.Zero)
            {
                for (int i = 0; i < output.cbData; i++) Marshal.WriteByte(output.pbData, i, 0);
                LocalFree(output.pbData);
            }
        }
    }

    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
