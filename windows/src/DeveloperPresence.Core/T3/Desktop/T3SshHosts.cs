using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DeveloperPresence.Core.T3.Desktop;

/// <summary>Machines worth asking over SSH which T3 environment they run.</summary>
public interface IT3SshHostSource
{
    Task<IReadOnlyList<T3SshTarget>> CandidatesAsync(CancellationToken ct = default);
}

/// <summary>A Host entry from the user's SSH config with a name of its own, not a pattern.</summary>
public sealed record T3SshConfigHost(string Alias, string? HostName, string? User);

/// <summary>An online machine in the user's own tailnet.</summary>
public sealed record T3TailnetPeer(string DnsName, string HostName, IReadOnlyList<string> Addresses);

/// <summary>
/// Hosts named in ~/.ssh/config, then the user's own online Linux and macOS machines in Tailscale.
/// Only names are read: never keys, known hosts or anything else.
/// </summary>
public sealed class T3SshHostSource : IT3SshHostSource
{
    public const int MaxCandidates = 32;
    private const int MaxConfigBytes = 1024 * 1024;
    private const int MaxIncludeDepth = 4;
    private static readonly TimeSpan TailscaleTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] GitHosts = { "github.com", "gitlab.com", "bitbucket.org", "ssh.dev.azure.com", "vs-ssh.visualstudio.com", "codeberg.org" };
    private static readonly string[] PosixSystems = { "linux", "macos", "darwin", "freebsd", "openbsd", "illumos" };

    private readonly string _sshDir;
    private readonly Func<CancellationToken, Task<string?>> _tailscaleStatus;

    public T3SshHostSource(string? sshDir = null, Func<CancellationToken, Task<string?>>? tailscaleStatus = null)
    {
        _sshDir = sshDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        _tailscaleStatus = tailscaleStatus ?? ReadTailscaleStatusAsync;
    }

    public async Task<IReadOnlyList<T3SshTarget>> CandidatesAsync(CancellationToken ct = default)
    {
        var hosts = ParseConfig(ReadText(Path.Combine(_sshDir, "config")) ?? string.Empty, ReadInclude);
        string? status = null;
        try { status = await _tailscaleStatus(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        return Merge(hosts, status == null ? Array.Empty<T3TailnetPeer>() : ParseTailscalePeers(status));
    }

    /// <summary>Config aliases first, as ssh knows how to reach them, then tailnet machines the config doesn't name.</summary>
    public static IReadOnlyList<T3SshTarget> Merge(IReadOnlyList<T3SshConfigHost> hosts, IReadOnlyList<T3TailnetPeer> peers)
    {
        var targets = new List<T3SshTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts)
        {
            var target = new T3SshTarget(host.Alias);
            if (target.IsValid && seen.Add(target.Destination)) targets.Add(target);
        }

        var named = new HashSet<string>(hosts.SelectMany(h => new[] { h.Alias, h.HostName }).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        // The config's user names are the likeliest ones on other machines too; ssh's default comes last.
        var users = hosts.Select(h => h.User).OfType<string>().Distinct(StringComparer.Ordinal).Take(2).Append(null).ToList();
        foreach (var peer in peers)
        {
            if (named.Contains(peer.DnsName) || named.Contains(peer.HostName) || peer.Addresses.Any(named.Contains)) continue;
            foreach (string? user in users)
            {
                var target = new T3SshTarget(peer.DnsName, user);
                if (target.IsValid && seen.Add(target.Destination)) targets.Add(target);
            }
        }
        return targets.Take(MaxCandidates).ToList();
    }

    /// <summary>Host entries with plain names. Patterns, Match blocks and git hosting services are skipped.</summary>
    public static IReadOnlyList<T3SshConfigHost> ParseConfig(string text, Func<string, IReadOnlyList<string>> readInclude, int depth = 0)
    {
        var hosts = new List<T3SshConfigHost>();
        List<string>? aliases = null;
        string? hostName = null, user = null;
        // Hosts from an Include inside a block come after that block's own, in file order.
        var included = new List<T3SshConfigHost>();

        void Flush()
        {
            if (aliases != null)
                foreach (string alias in aliases)
                    if (!IsGitHost(alias) && !(hostName != null && IsGitHost(hostName)))
                        hosts.Add(new T3SshConfigHost(alias, hostName, user));
            hosts.AddRange(included);
            included.Clear();
            aliases = null;
            hostName = user = null;
        }

        foreach (string raw in text.Split('\n'))
        {
            var tokens = Tokens(raw);
            if (tokens.Count == 0) continue;
            string keyword = tokens[0].ToLowerInvariant();
            var args = tokens.Skip(1).ToList();
            switch (keyword)
            {
                case "host":
                    Flush();
                    aliases = args.Where(a => a.IndexOfAny(new[] { '*', '?', '!' }) < 0).ToList();
                    break;
                case "match":
                    Flush();
                    break;
                case "hostname" when aliases != null && hostName == null && args.Count > 0:
                    hostName = args[0];
                    break;
                case "user" when aliases != null && user == null && args.Count > 0:
                    user = args[0];
                    break;
                case "include" when depth < MaxIncludeDepth:
                    // Entries in an included file are read on their own; that is how most configs use Include.
                    foreach (string file in args.SelectMany(readInclude))
                        included.AddRange(ParseConfig(file, readInclude, depth + 1));
                    if (aliases == null) { hosts.AddRange(included); included.Clear(); }
                    break;
            }
        }
        Flush();
        return hosts;
    }

    private static bool IsGitHost(string name) =>
        GitHosts.Any(g => name.Equals(g, StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + g, StringComparison.OrdinalIgnoreCase));

    /// <summary>Keyword and arguments, split on spaces or one "=", with "double quoted" arguments kept whole.</summary>
    private static List<string> Tokens(string line)
    {
        var tokens = new List<string>();
        string text = line.Trim();
        if (text.Length == 0 || text[0] == '#') return tokens;
        var current = new StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in text)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (!quoted && (char.IsWhiteSpace(c) || (c == '=' && tokens.Count == 0)))
            {
                if (any) { tokens.Add(current.ToString()); current.Clear(); any = false; }
                continue;
            }
            current.Append(c);
            any = true;
        }
        if (any) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>The user's own online machines, and tagged ones, that run Linux, macOS or a BSD.</summary>
    public static IReadOnlyList<T3TailnetPeer> ParseTailscalePeers(string json)
    {
        var peers = new List<T3TailnetPeer>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Self", out var self) || self.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Peer", out var peerMap) || peerMap.ValueKind != JsonValueKind.Object)
                return peers;
            long? selfUser = self.TryGetProperty("UserID", out var su) && su.ValueKind == JsonValueKind.Number && su.TryGetInt64(out long s) ? s : null;
            foreach (var entry in peerMap.EnumerateObject())
            {
                var peer = entry.Value;
                if (peer.ValueKind != JsonValueKind.Object) continue;
                if (!(peer.TryGetProperty("Online", out var online) && online.ValueKind == JsonValueKind.True)) continue;
                if (peer.TryGetProperty("ShareeNode", out var sharee) && sharee.ValueKind == JsonValueKind.True) continue;
                bool tagged = peer.TryGetProperty("Tags", out var tags) && tags.ValueKind == JsonValueKind.Array && tags.GetArrayLength() > 0;
                bool mine = selfUser != null && peer.TryGetProperty("UserID", out var pu) && pu.ValueKind == JsonValueKind.Number &&
                            pu.TryGetInt64(out long p) && p == selfUser;
                if (!mine && !tagged) continue;
                if (!PosixSystems.Contains(Text(peer, "OS").ToLowerInvariant())) continue;
                string dns = Text(peer, "DNSName").TrimEnd('.');
                if (dns.Length == 0) continue;
                var addresses = peer.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array
                    ? ips.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).ToList()
                    : new List<string>();
                peers.Add(new T3TailnetPeer(dns, Text(peer, "HostName"), addresses));
            }
        }
        catch (JsonException) { }
        return peers.OrderBy(p => p.DnsName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;

    private IReadOnlyList<string> ReadInclude(string pattern)
    {
        try
        {
            string expanded = pattern.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), pattern[2..])
                : pattern;
            string full = Path.IsPathRooted(expanded) ? expanded : Path.Combine(_sshDir, expanded);
            string? dir = Path.GetDirectoryName(full);
            string name = Path.GetFileName(full);
            if (dir == null || !Directory.Exists(dir)) return Array.Empty<string>();
            var files = name.IndexOfAny(new[] { '*', '?' }) >= 0
                ? Directory.GetFiles(dir, name).OrderBy(f => f, StringComparer.Ordinal).Take(32)
                : new[] { full }.AsEnumerable();
            return files.Select(ReadText).OfType<string>().ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    private static string? ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxConfigBytes) return null;
            return File.ReadAllText(path);
        }
        catch { return null; }
    }

    private static async Task<string?> ReadTailscaleStatusAsync(CancellationToken ct)
    {
        string exe = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe")
            : "tailscale";
        if (OperatingSystem.IsWindows() && !File.Exists(exe)) return null;
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("status");
        info.ArgumentList.Add("--json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TailscaleTimeout);
        Process? process;
        try { process = Process.Start(info); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        if (process == null) return null;
        using (process)
        {
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
                _ = process.StandardError.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                string text = await stdout.ConfigureAwait(false);
                return process.ExitCode == 0 && text.Length <= 4 * MaxConfigBytes ? text : null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(); } catch { }
                return null;
            }
        }
    }
}
