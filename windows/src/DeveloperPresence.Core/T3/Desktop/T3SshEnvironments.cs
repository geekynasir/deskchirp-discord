using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeveloperPresence.Core.T3.Usage;

namespace DeveloperPresence.Core.T3.Desktop;

/// <summary>What a machine said about its T3 Code over SSH.</summary>
public sealed record T3SshProbe(string? EnvironmentId, string? RemoteHost, int? RemotePort)
{
    /// <summary>T3 Code's server is running there.</summary>
    public bool Running => RemoteHost != null && RemotePort != null;
}

/// <summary>How an environment's tunnel is doing.</summary>
/// <param name="Open">The forward is up and T3 Code answered through it.</param>
/// <param name="Tried">At least one attempt to open it has finished.</param>
/// <param name="Problem">Why the last attempt failed; Success while open.</param>
/// <param name="Machine">The machine the open tunnel goes to.</param>
public sealed record T3SshTunnelState(bool Open, bool Tried, T3DesktopPairingOutcome Problem, T3SshTarget? Machine, int LocalPort);

/// <summary>Reaches T3 environments over the user's own SSH, for the ones T3 Code reaches some other way.</summary>
public interface IT3SshEnvironments : IAsyncDisposable
{
    /// <summary>The local address this environment's tunnel listens on. It stays the same while Deskchirp runs.</summary>
    string AddressFor(string environmentId);

    /// <summary>
    /// Gives the environment the tunnel address it had before, so the sign-in T3 bound to it keeps working.
    /// Ignored for addresses outside Deskchirp's tunnel ports, or a port another environment already has.
    /// </summary>
    void Reserve(string environmentId, string address);

    /// <summary>
    /// Whether a token may be sent to this address now. Deskchirp's tunnel ports on 127.0.0.1 only count while
    /// their tunnel is open and T3 Code answered through it, so a sign-in never goes to whatever else listens there.
    /// </summary>
    bool MaySend(string address);

    /// <summary>Asks the user's SSH hosts which T3 environment each runs, and returns the machine for each one wanted that turned up.</summary>
    Task<IReadOnlyDictionary<string, T3SshTarget>> DiscoverAsync(IReadOnlyCollection<string> environmentIds, CancellationToken ct = default);

    /// <summary>Forgets every machine's answer, so the next discovery asks them all again.</summary>
    void ResetDiscovery();

    /// <summary>
    /// Keeps tunnels open for exactly these environments, each to the first of its machines that runs it,
    /// reopening them when they drop, and closes the rest.
    /// </summary>
    void Retain(IReadOnlyDictionary<string, IReadOnlyList<T3SshTarget>> tunnels);

    /// <summary>The environment's tunnel, or null when none is retained.</summary>
    T3SshTunnelState? TunnelState(string environmentId);

    /// <summary>Environments whose tunnel found its machine running another environment since the last call, so they can be looked for again.</summary>
    IReadOnlyCollection<string> TakeMovedEnvironments();

    /// <summary>
    /// Environments whose tunnel hasn't reached any of its machines for a while, once per stretch, so other
    /// machines can be asked whether they run it now.
    /// </summary>
    IReadOnlyCollection<string> TakeUnreachableEnvironments();

    /// <summary>
    /// Opens the environment's tunnel if needed, then has the machine's own t3 command mint a one-time pairing code,
    /// and a second one with <paramref name="usageScopes"/> in the same ssh call when given.
    /// </summary>
    Task<T3DesktopPairingResult> MintCodeAsync(string environmentId, IReadOnlyList<T3SshTarget> targets, IReadOnlyList<string> scopes,
        IReadOnlyList<string>? usageScopes = null, CancellationToken ct = default);
}

/// <summary>
/// Over SSH, Deskchirp only reads two small files in ~/.t3/userdata (the environment id and the
/// running server's port), runs "t3 auth pairing create" for a five-minute read-only code, and
/// forwards a local port to the server. It never starts or stops T3 Code there, and never reads
/// its credentials or conversations. Each machine is checked to run the expected environment first.
/// </summary>
public sealed partial class T3SshEnvironments : IT3SshEnvironments
{
    /// <summary>Tunnels listen on 127.0.0.1 ports FirstPort to FirstPort + PortCount - 1, below Windows' ephemeral ports.</summary>
    public const int FirstPort = 41000;
    public const int PortCount = 8000;
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MintTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(40);
    public static readonly TimeSpan ListenTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan HostRetry = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxHostRetry = TimeSpan.FromHours(6);
    public static readonly TimeSpan KnownHostRefresh = TimeSpan.FromHours(1);
    /// <summary>A tunnel opened this soon after discovery asked its machine uses that answer instead of asking again.</summary>
    public static readonly TimeSpan FreshProbe = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MinTunnelRetry = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxTunnelRetry = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RecheckOpenTunnel = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SearchAfterUnreachable = TimeSpan.FromMinutes(15);
    /// <summary>Machines asked per discovery pass; the rest wait their turn, oldest answer first.</summary>
    public const int MaxProbesPerPass = 32;
    internal const string ProbeMarker = "deskchirp-t3-probe-1";
    internal const string MintMarker = "deskchirp-t3-mint-1";
    private const int MaxParallelProbes = 4;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] AllowedScopes = { T3DesktopPairingClient.ReadScope, T3DesktopPairingClient.DiagnosticsScope };

    private readonly IT3SshRunner _runner;
    private readonly IT3SshHostSource _hosts;
    private readonly Func<int, bool> _portFree;
    private readonly Func<int, CancellationToken, Task<bool>> _answersAsT3;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _recheck;
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _ports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tunnel> _tunnels = new(StringComparer.Ordinal);
    /// <summary>Loops of tunnels no longer retained, still shutting down; a new tunnel for the same environment waits for its old one.</summary>
    private readonly Dictionary<string, Task> _closing = new(StringComparer.Ordinal);
    private readonly Dictionary<T3SshTarget, HostProbe> _probed = new();
    private readonly Dictionary<T3SshTarget, (T3SshProbe Probe, DateTimeOffset At)> _discovered = new();
    private readonly HashSet<string> _moved = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreachable = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <param name="answersAsT3">Whether T3 Code answers on this local port, for exactly its address; by default its sign-in metadata is fetched.</param>
    public T3SshEnvironments(
        IT3SshRunner? runner = null,
        IT3SshHostSource? hosts = null,
        Func<int, bool>? portFree = null,
        Func<int, CancellationToken, Task<bool>>? answersAsT3 = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? recheckOpenTunnel = null)
    {
        _runner = runner ?? new T3SshRunner();
        _hosts = hosts ?? new T3SshHostSource();
        _portFree = portFree ?? IsPortFree;
        _answersAsT3 = answersAsT3 ?? AnswersAsT3Async;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _recheck = recheckOpenTunnel ?? RecheckOpenTunnel;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentIdPattern();

    [GeneratedRegex(@"^[0-9]{1,3}(\.[0-9]{1,3}){3}$", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$", RegexOptions.CultureInvariant)]
    private static partial Regex HostNamePattern();

    private sealed record HostProbe(string? EnvironmentId, DateTimeOffset RecheckAt, int Failures);

    private sealed class Tunnel
    {
        public Tunnel(string environmentId, IReadOnlyList<T3SshTarget> targets, int localPort)
        {
            EnvironmentId = environmentId;
            Targets = targets.ToList();
            LocalPort = localPort;
        }

        public string EnvironmentId { get; }
        public IReadOnlyList<T3SshTarget> Targets { get; }
        /// <summary>The machine the open forward goes to.</summary>
        public volatile T3SshTarget? Current;
        public int LocalPort { get; }
        public CancellationTokenSource Stop { get; } = new();
        public SemaphoreSlim Kick { get; } = new(0, 1);
        public Task Loop { get; set; } = Task.CompletedTask;
        /// <summary>Set only once T3 Code answered through it.</summary>
        public volatile IT3SshForward? Forward;
        /// <summary>Open until ssh exits, even before the loop notices.</summary>
        public bool Open => Forward is { Exited.IsCompleted: false };
        /// <summary>Counts finished attempts, so a waiter can tell a fresh failure from an old one.</summary>
        public int Attempts;
        public volatile T3DesktopPairingOutcome Problem = T3DesktopPairingOutcome.SshUnreachable;
        /// <summary>When the attempts in a row that reached none of the machines began, and whether that was reported.</summary>
        public DateTimeOffset? UnreachableSince;
        public bool ReportedUnreachable;
        /// <summary>Whether finding another environment on its machines was reported since the tunnel last opened.</summary>
        public bool ReportedMoved;

        public void Wake()
        {
            try { if (Kick.CurrentCount == 0) Kick.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        public void Close()
        {
            try { Stop.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    // ---- Addresses ----

    public string AddressFor(string environmentId)
    {
        lock (_lock)
        {
            if (!_ports.TryGetValue(environmentId, out int port))
            {
                // The same environment gets the same port each time, so its sign-in, which T3 binds to the address, keeps working.
                int start = PreferredOffset(environmentId);
                var taken = _ports.Values.ToHashSet();
                int? firstUnassigned = null, free = null;
                for (int i = 0, tried = 0; i < PortCount && tried < 64; i++)
                {
                    int candidate = FirstPort + (start + i) % PortCount;
                    if (taken.Contains(candidate)) continue;
                    firstUnassigned ??= candidate;
                    tried++;
                    if (_portFree(candidate)) { free = candidate; break; }
                }
                // Never another environment's port: its token would go to this one's tunnel.
                port = free ?? firstUnassigned ?? FirstPort + start;
                _ports[environmentId] = port;
            }
            return Address(port);
        }
    }

    public void Reserve(string environmentId, string address)
    {
        if (TunnelPort(address) is not { } port) return;
        lock (_lock)
        {
            if (_ports.ContainsKey(environmentId) || _ports.ContainsValue(port)) return;
            _ports[environmentId] = port;
        }
    }

    public bool MaySend(string address)
    {
        if (TunnelPort(address) is not { } port) return true;
        lock (_lock) return _tunnels.Values.Any(t => t.LocalPort == port && t.Open);
    }

    /// <summary>The port, when this is a loopback address on one of Deskchirp's tunnel ports.</summary>
    internal static int? TunnelPort(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.IsLoopback &&
        uri.Port is >= FirstPort and < FirstPort + PortCount ? uri.Port : null;

    internal static int PreferredOffset(string environmentId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(environmentId));
        return (int)(BitConverter.ToUInt32(hash, 0) % PortCount);
    }

    private static string Address(int port) => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/mcp");

    // ---- Discovery ----

    public async Task<IReadOnlyDictionary<string, T3SshTarget>> DiscoverAsync(IReadOnlyCollection<string> environmentIds, CancellationToken ct = default)
    {
        var wanted = new HashSet<string>(environmentIds, StringComparer.Ordinal);
        var found = new Dictionary<string, T3SshTarget>(StringComparer.Ordinal);
        if (wanted.Count == 0) return found;

        IReadOnlyList<T3SshTarget> candidates;
        try { candidates = await _hosts.CandidatesAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return found; }

        // Machines asked recently aren't asked again; ones that didn't answer wait longer each time. Never-asked
        // machines go first, then the longest-waiting, so a long list is worked through over a few passes.
        var now = _clock();
        List<T3SshTarget> toProbe;
        lock (_lock)
        {
            toProbe = candidates
                .Select((candidate, index) => (candidate, index, known: _probed.GetValueOrDefault(candidate)))
                .Where(c => c.known == null || now >= c.known.RecheckAt)
                .OrderBy(c => c.known == null ? 0 : 1).ThenBy(c => c.known?.RecheckAt ?? default).ThenBy(c => c.index)
                .Take(MaxProbesPerPass)
                .Select(c => c.candidate)
                .ToList();
        }

        if (toProbe.Count > 0 && !AllFound())
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(DiscoveryTimeout);
            try
            {
                await Parallel.ForEachAsync(toProbe, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelProbes, CancellationToken = deadline.Token },
                    async (target, token) =>
                    {
                        var (probe, failure) = await ProbeAsync(target, token).ConfigureAwait(false);
                        if (failure != T3SshFailure.None) { Record(target, null); return; }
                        string id = probe?.EnvironmentId ?? string.Empty;
                        // A machine with no T3, or one nobody is looking for, is asked again sooner, as T3 may start there.
                        Record(target, id, wanted.Contains(id) ? KnownHostRefresh : HostRetry);
                        if (probe != null) lock (_lock) _discovered[target] = (probe, _clock());
                        // Everything wanted turned up: the rest can wait.
                        if (AllFound()) deadline.Cancel();
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* Took too long, or found everything; keep what answered. */ }
        }

        // In the order the candidates came, so a config alias wins over the same machine's tailnet name.
        lock (_lock)
        {
            foreach (var candidate in candidates)
            {
                if (_probed.TryGetValue(candidate, out var known) && known.EnvironmentId is { Length: > 0 } id &&
                    wanted.Contains(id) && !found.ContainsKey(id))
                    found[id] = candidate;
            }
        }
        return found;

        bool AllFound()
        {
            lock (_lock)
                return wanted.All(id => candidates.Any(c => _probed.TryGetValue(c, out var k) && k.EnvironmentId == id));
        }
    }

    public void ResetDiscovery()
    {
        lock (_lock)
        {
            _probed.Clear();
            _discovered.Clear();
        }
    }

    /// <summary>
    /// Remembers what a machine answered: an environment id, empty for no T3 Code, or null when it couldn't be reached,
    /// which waits longer each time.
    /// </summary>
    private void Record(T3SshTarget target, string? environmentId, TimeSpan? recheckAfter = null)
    {
        var now = _clock();
        lock (_lock)
        {
            if (environmentId != null)
            {
                _probed[target] = new HostProbe(environmentId, now + (recheckAfter ?? KnownHostRefresh), 0);
                return;
            }
            int failures = _probed.TryGetValue(target, out var previous) ? previous.Failures + 1 : 1;
            var wait = TimeSpan.FromTicks(Math.Min(MaxHostRetry.Ticks, HostRetry.Ticks * (1L << Math.Min(failures - 1, 10))));
            _probed[target] = new HostProbe(null, now + wait, failures);
            _discovered.Remove(target);
        }
    }

    /// <summary>Discovery's answer from this machine, once, if it is only seconds old.</summary>
    private T3SshProbe? TakeFreshProbe(T3SshTarget target)
    {
        lock (_lock)
        {
            if (!_discovered.Remove(target, out var discovered)) return null;
            return _clock() - discovered.At <= FreshProbe ? discovered.Probe : null;
        }
    }

    /// <summary>
    /// The environment isn't on these machines any more. Their real answers are remembered, so discovery doesn't
    /// offer them for it, and the move is reported once until the tunnel opens again.
    /// </summary>
    private void Moved(Tunnel? tunnel, string environmentId, IEnumerable<(T3SshTarget Target, string? Answer)> machines)
    {
        lock (_lock)
        {
            foreach (var (target, answer) in machines)
            {
                if (answer == null) _probed.Remove(target);
                else _probed[target] = new HostProbe(answer, _clock() + HostRetry, 0);
                _discovered.Remove(target);
            }
            if (tunnel is { ReportedMoved: true }) return;
            if (tunnel != null) tunnel.ReportedMoved = true;
            _moved.Add(environmentId);
        }
    }

    public IReadOnlyCollection<string> TakeMovedEnvironments() => Take(_moved);

    public IReadOnlyCollection<string> TakeUnreachableEnvironments() => Take(_unreachable);

    private IReadOnlyCollection<string> Take(HashSet<string> environments)
    {
        lock (_lock)
        {
            var taken = environments.ToList();
            environments.Clear();
            return taken;
        }
    }

    /// <summary>
    /// None of this environment's machines has answered for a while. They count as not answering, so discovery
    /// doesn't offer them from memory, and the environment is reported so other machines can be asked.
    /// </summary>
    private void LongUnreachable(Tunnel tunnel)
    {
        lock (_lock)
        {
            foreach (var target in tunnel.Targets)
            {
                _probed[target] = new HostProbe(null, _clock() + HostRetry, 1);
                _discovered.Remove(target);
            }
            _unreachable.Add(tunnel.EnvironmentId);
        }
    }

    // ---- Remote scripts ----

    /// <summary>Reads the environment id and, when its server is running, the runtime file. Nothing else.</summary>
    private const string ReadState = """
        T3_HOME="${T3CODE_HOME:-$HOME/.t3}"
        ID="$(head -c 200 "$T3_HOME/userdata/environment-id" 2>/dev/null | tr -d '\r\n')"
        RUNTIME="$(head -c 16384 "$T3_HOME/userdata/server-runtime.json" 2>/dev/null | tr -d '\r\n')"
        PID="$(printf '%s' "$RUNTIME" | sed -n 's/.*"pid"[[:space:]]*:[[:space:]]*\([1-9][0-9]*\).*/\1/p')"
        if [ -z "$PID" ] || ! kill -0 "$PID" 2>/dev/null; then RUNTIME=""; PID=""; fi

        """;

    internal static string ProbeScript() => ReadState + $"""
        printf '%s\n' '{ProbeMarker}'
        printf 'id=%s\n' "$ID"
        printf 'runtime=%s\n' "$RUNTIME"

        """;

    /// <summary>
    /// Mints with the t3 the running server was started from, so the code lands in the database that
    /// server reads, falling back to t3 on PATH and in the usual install folders. Each code follows a marker line.
    /// Exit 3: no t3; 4: another environment; 5: not running; 6: t3 failed.
    /// </summary>
    internal static string MintScript(string environmentId, IReadOnlyList<string> scopes, IReadOnlyList<string>? usageScopes = null)
    {
        if (!EnvironmentIdPattern().IsMatch(environmentId)) throw new ArgumentException("Not an environment id.", nameof(environmentId));
        if (!AreAllowed(scopes) || (usageScopes != null && !AreAllowed(usageScopes)))
            throw new ArgumentException("Not a scope Deskchirp asks for.", nameof(scopes));
        string Create(IReadOnlyList<string> s) =>
            $"\"$T3\" auth pairing create --base-dir \"$T3_HOME\" {string.Join(" ", s.Select(x => $"--scope {x}"))} --ttl 5m --label Deskchirp --json </dev/null";
        string usage = usageScopes == null ? string.Empty : $"""
            printf '%s\n' '{MintMarker}'
            {Create(usageScopes)} || true

            """;
        return ReadState + $$"""
            [ "$ID" = '{{environmentId}}' ] || exit 4
            [ -n "$PID" ] || exit 5
            T3=""
            EXE="$(readlink "/proc/$PID/exe" 2>/dev/null | sed 's/ (deleted)$//')"
            case "$EXE" in */t3) [ -x "$EXE" ] && T3="$EXE" ;; esac
            if [ -z "$T3" ]; then
              # Usually node or bun runs the t3 script: look for it among the server's arguments.
              ARGS="$( { tr '\000' '\n' < "/proc/$PID/cmdline" || ps -o args= -p "$PID" | tr ' ' '\n'; } 2>/dev/null | head -n 64)"
              OLD_IFS="$IFS"
              IFS='
            '
              set -f
              for ARG in $ARGS; do
                case "$ARG" in */t3) if [ -x "$ARG" ]; then T3="$ARG"; break; fi ;; esac
              done
              set +f
              IFS="$OLD_IFS"
            fi
            PATH="$PATH:/opt/homebrew/bin:/usr/local/bin:$HOME/.local/bin:$HOME/.bun/bin:$HOME/.volta/bin"
            [ -n "$T3" ] || T3="$(command -v t3 2>/dev/null)" || T3=""
            [ -n "$T3" ] || exit 3
            # What t3 runs with, such as node or bun, is often installed beside it.
            PATH="$(dirname "$T3"):$PATH"
            printf '%s\n' '{{MintMarker}}'
            {{Create(scopes)}} || exit 6
            {{usage}}
            """;
    }

    private static bool AreAllowed(IReadOnlyList<string> scopes) => scopes.Count > 0 && scopes.All(s => AllowedScopes.Contains(s));

    /// <summary>Reads the probe's answer, skipping anything a login script printed before it.</summary>
    public static T3SshProbe? ParseProbe(string stdout)
    {
        var lines = stdout.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        int start = Array.IndexOf(lines, ProbeMarker);
        if (start < 0) return null;
        string? id = null, runtime = null;
        foreach (string line in lines.Skip(start + 1))
        {
            if (id == null && line.StartsWith("id=", StringComparison.Ordinal)) id = line[3..];
            else if (runtime == null && line.StartsWith("runtime=", StringComparison.Ordinal)) runtime = line[8..];
        }
        if (id != null && !EnvironmentIdPattern().IsMatch(id)) id = null;
        var server = string.IsNullOrEmpty(runtime) ? null : ParseRemoteRuntime(runtime);
        return new T3SshProbe(id, server?.Host, server?.Port);
    }

    /// <summary>Where the remote server listens, as seen from that machine, from its runtime file.</summary>
    internal static (string Host, int Port)? ParseRemoteRuntime(string runtime)
    {
        try
        {
            using var doc = JsonDocument.Parse(runtime);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out int v) || v != 1 ||
                !root.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out int p) || p <= 0 ||
                !root.TryGetProperty("port", out var portValue) || !portValue.TryGetInt32(out int port) || port is < 1 or > 65535)
                return null;
            string? host = root.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
            return (RemoteHost(host), port);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>
    /// The host for ssh to forward to. Wildcard and loopback binds map to loopback; an IP or plain host name is kept,
    /// and anything else falls back to 127.0.0.1, so nothing odd reaches ssh's -L argument.
    /// </summary>
    internal static string RemoteHost(string? host)
    {
        switch (host)
        {
            case null or "" or "localhost" or "0.0.0.0" or "127.0.0.1": return "127.0.0.1";
            case "::" or "::1" or "[::]" or "[::1]": return "::1";
        }
        if (Ipv4Pattern().IsMatch(host) && IPAddress.TryParse(host, out var v4)) return v4.ToString();
        if (host.Contains(':') && !host.Contains('%') && IPAddress.TryParse(host, out var v6) &&
            v6.AddressFamily == AddressFamily.InterNetworkV6)
            return v6.ToString();
        return HostNamePattern().IsMatch(host) ? host : "127.0.0.1";
    }

    /// <summary>The pairing code from "t3 auth pairing create --json", which prints indented JSON.</summary>
    public static string? ParseCredential(string stdout, Uri origin)
    {
        int end = stdout.LastIndexOf('}');
        int tries = 0;
        for (int start = stdout.IndexOf('{'); start >= 0 && start < end && tries++ < 16; start = stdout.IndexOf('{', start + 1))
        {
            try
            {
                using var doc = JsonDocument.Parse(stdout.AsMemory(start, end - start + 1));
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("credential", out var credential) || credential.ValueKind != JsonValueKind.String)
                    return null;
                return T3UsagePairingInput.TryParse(credential.GetString(), origin, out string code, out _) ? code : null;
            }
            catch (JsonException) { }
        }
        return null;
    }

    /// <summary>The code after the first marker, and the usage code after the second when there is one.</summary>
    public static (string? Code, string? UsageCode) ParseMint(string stdout, Uri origin)
    {
        var parts = stdout.Replace("\r", string.Empty, StringComparison.Ordinal).Split(MintMarker + "\n");
        if (parts.Length < 2) return (null, null);
        string? code = ParseCredential(parts[1], origin);
        string? usage = parts.Length > 2 ? ParseCredential(parts[2], origin) : null;
        return (code, usage);
    }

    /// <summary>Runs the probe. Failure is None when ssh got in.</summary>
    private async Task<(T3SshProbe? Probe, T3SshFailure Failure)> ProbeAsync(T3SshTarget target, CancellationToken ct)
    {
        var result = await _runner.RunScriptAsync(target, ProbeScript(), ProbeTimeout, ct).ConfigureAwait(false);
        if (result.Unreachable) return (null, result.Reason);
        return (result.ExitCode == 0 ? ParseProbe(result.Stdout) : null, T3SshFailure.None);
    }

    internal static T3DesktopPairingOutcome Outcome(T3SshFailure failure) => failure switch
    {
        T3SshFailure.HostKey => T3DesktopPairingOutcome.SshHostKey,
        T3SshFailure.Denied => T3DesktopPairingOutcome.SshDenied,
        T3SshFailure.NoSsh => T3DesktopPairingOutcome.SshMissing,
        _ => T3DesktopPairingOutcome.SshUnreachable,
    };

    /// <summary>ssh couldn't get in, so the environment may be on another machine now.</summary>
    internal static bool IsReachFailure(T3DesktopPairingOutcome outcome) =>
        outcome is T3DesktopPairingOutcome.SshUnreachable or T3DesktopPairingOutcome.SshHostKey or T3DesktopPairingOutcome.SshDenied;

    /// <summary>Which problem says most when no machine worked: the closer to working, the more telling.</summary>
    private static int Rank(T3DesktopPairingOutcome outcome) => outcome switch
    {
        T3DesktopPairingOutcome.NotRunning => 6,
        T3DesktopPairingOutcome.WrongEnvironment => 5,
        T3DesktopPairingOutcome.ForwardFailed => 4,
        T3DesktopPairingOutcome.SshHostKey or T3DesktopPairingOutcome.SshDenied => 3,
        T3DesktopPairingOutcome.SshMissing => 2,
        _ => 1,
    };

    // ---- Tunnels ----

    public void Retain(IReadOnlyDictionary<string, IReadOnlyList<T3SshTarget>> tunnels)
    {
        lock (_lock)
        {
            if (_disposed) return;
            foreach (var (id, task) in _closing.ToList())
                if (task.IsCompleted) _closing.Remove(id);
            foreach (var (id, tunnel) in _tunnels.ToList())
            {
                if (tunnels.TryGetValue(id, out var targets) && targets.SequenceEqual(tunnel.Targets)) continue;
                tunnel.Close();
                _tunnels.Remove(id);
                _closing[id] = tunnel.Loop;
            }
            foreach (var (id, targets) in tunnels) EnsureTunnelLocked(id, targets);
        }
    }

    public T3SshTunnelState? TunnelState(string environmentId)
    {
        lock (_lock)
        {
            if (!_tunnels.TryGetValue(environmentId, out var tunnel)) return null;
            bool open = tunnel.Open;
            return new T3SshTunnelState(open, Volatile.Read(ref tunnel.Attempts) > 0,
                open ? T3DesktopPairingOutcome.Success : tunnel.Problem, open ? tunnel.Current : null, tunnel.LocalPort);
        }
    }

    private Tunnel? EnsureTunnelLocked(string environmentId, IReadOnlyList<T3SshTarget> targets)
    {
        if (_disposed || targets.Count == 0) return null;
        Task previous = Task.CompletedTask;
        if (_tunnels.TryGetValue(environmentId, out var existing))
        {
            if (existing.Targets.SequenceEqual(targets)) return existing;
            existing.Close();
            previous = existing.Loop;
        }
        else if (_closing.Remove(environmentId, out var closing)) previous = closing;
        if (!_ports.ContainsKey(environmentId)) AddressFor(environmentId);
        var tunnel = new Tunnel(environmentId, targets, _ports[environmentId]);
        _tunnels[environmentId] = tunnel;
        // The old tunnel lets go of the port before the new one wants it.
        tunnel.Loop = Task.Run(async () =>
        {
            try { await previous.ConfigureAwait(false); } catch { }
            await RunTunnelAsync(tunnel).ConfigureAwait(false);
        });
        return tunnel;
    }

    /// <summary>Keeps one tunnel open until it is no longer retained, waiting longer after each failure.</summary>
    private async Task RunTunnelAsync(Tunnel tunnel)
    {
        try
        {
            var stop = tunnel.Stop.Token;
            var delay = MinTunnelRetry;
            while (!stop.IsCancellationRequested)
            {
                Opened opened = new(T3DesktopPairingOutcome.Failed, null, null, null);
                try { opened = await OpenAsync(tunnel, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch { }

                if (opened is { Forward: { } forward, Target: { } target, Probe: { } probe })
                {
                    var since = Stopwatch.StartNew();
                    // Opening answers any wake asked for meanwhile: a later drop waits like any other.
                    tunnel.Kick.Wait(0);
                    tunnel.Current = target;
                    tunnel.Forward = forward;
                    tunnel.UnreachableSince = null;
                    tunnel.ReportedUnreachable = false;
                    lock (_lock) tunnel.ReportedMoved = false;
                    Interlocked.Increment(ref tunnel.Attempts);
                    try { await WatchAsync(tunnel, forward, target, probe, stop).ConfigureAwait(false); }
                    catch { }
                    tunnel.Forward = null;
                    forward.Dispose();
                    if (since.Elapsed > TimeSpan.FromMinutes(1)) delay = MinTunnelRetry;
                }
                else
                {
                    tunnel.Problem = opened.Problem;
                    if (IsReachFailure(opened.Problem))
                    {
                        var now = _clock();
                        tunnel.UnreachableSince ??= now;
                        if (!tunnel.ReportedUnreachable && now - tunnel.UnreachableSince >= SearchAfterUnreachable)
                        {
                            tunnel.ReportedUnreachable = true;
                            LongUnreachable(tunnel);
                        }
                    }
                    else
                    {
                        tunnel.UnreachableSince = null;
                        tunnel.ReportedUnreachable = false;
                    }
                    if (opened.Problem != T3DesktopPairingOutcome.WrongEnvironment) lock (_lock) tunnel.ReportedMoved = false;
                    Interlocked.Increment(ref tunnel.Attempts);
                }

                if (stop.IsCancellationRequested) break;
                try { await tunnel.Kick.WaitAsync(delay, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromTicks(Math.Min(MaxTunnelRetry.Ticks, delay.Ticks * 2));
            }
        }
        finally
        {
            tunnel.Forward = null;
            tunnel.Stop.Dispose();
            tunnel.Kick.Dispose();
        }
    }

    private sealed record Opened(T3DesktopPairingOutcome Problem, IT3SshForward? Forward, T3SshTarget? Target, T3SshProbe? Probe);

    /// <summary>
    /// Tries the machines in order until one runs this environment and T3 Code answers through the forward.
    /// Otherwise returns the most telling problem, as ranked by <see cref="Rank"/>.
    /// </summary>
    private async Task<Opened> OpenAsync(Tunnel tunnel, CancellationToken ct)
    {
        var problem = T3DesktopPairingOutcome.SshUnreachable;
        var wrong = new List<(T3SshTarget, string?)>();
        void Consider(T3DesktopPairingOutcome outcome)
        {
            if (Rank(outcome) > Rank(problem)) problem = outcome;
        }

        foreach (var target in tunnel.Targets)
        {
            var (probe, failure) = TakeFreshProbe(target) is { } fresh ? (fresh, T3SshFailure.None) : await ProbeAsync(target, ct).ConfigureAwait(false);
            if (failure != T3SshFailure.None) { Consider(Outcome(failure)); continue; }
            if (probe?.EnvironmentId != tunnel.EnvironmentId)
            {
                wrong.Add((target, probe?.EnvironmentId ?? string.Empty));
                Consider(T3DesktopPairingOutcome.WrongEnvironment);
                continue;
            }
            if (!probe.Running) { Consider(T3DesktopPairingOutcome.NotRunning); continue; }
            // Something else listening here would get the sign-in meant for T3.
            if (!_portFree(tunnel.LocalPort)) return new(T3DesktopPairingOutcome.LocalPortBusy, null, null, null);
            var (forward, forwardProblem) = await ForwardAsync(tunnel, target, probe, ct).ConfigureAwait(false);
            if (forward != null) return new(T3DesktopPairingOutcome.Success, forward, target, probe);
            Consider(forwardProblem);
        }
        if (wrong.Count > 0)
        {
            // Another machine answering with another environment isn't news when one of them works.
            if (problem == T3DesktopPairingOutcome.WrongEnvironment) Moved(tunnel, tunnel.EnvironmentId, wrong);
            else lock (_lock) foreach (var (target, answer) in wrong) _probed[target] = new HostProbe(answer, _clock() + HostRetry, 0);
        }
        return new(problem, null, null, null);
    }

    /// <summary>Starts the forward and returns it once T3 Code answers through it, or why it didn't.</summary>
    private async Task<(IT3SshForward? Forward, T3DesktopPairingOutcome Problem)> ForwardAsync(Tunnel tunnel, T3SshTarget target, T3SshProbe probe, CancellationToken ct)
    {
        var forward = _runner.StartForward(target, tunnel.LocalPort, probe.RemoteHost!, probe.RemotePort!.Value);
        var waited = Stopwatch.StartNew();
        try
        {
            while (waited.Elapsed < ListenTimeout)
            {
                if (forward.Exited.IsCompleted) break;
                // Only T3 Code's own answer for this address counts, not just something accepting connections.
                if (await _answersAsT3(tunnel.LocalPort, ct).ConfigureAwait(false) && !forward.Exited.IsCompleted)
                    return (forward, T3DesktopPairingOutcome.Success);
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            }
        }
        catch
        {
            forward.Dispose();
            throw;
        }
        bool exited = forward.Exited.IsCompleted;
        forward.Dispose();
        if (!exited) return (null, T3DesktopPairingOutcome.ForwardFailed);
        return (null, forward.Failure switch
        {
            T3SshFailure.HostKey or T3SshFailure.Denied or T3SshFailure.NoSsh => Outcome(forward.Failure),
            _ => T3DesktopPairingOutcome.ForwardFailed,
        });
    }

    /// <summary>Returns once the forward ends, or once T3 Code there restarted on another address or port, or stopped.</summary>
    private async Task WatchAsync(Tunnel tunnel, IT3SshForward forward, T3SshTarget target, T3SshProbe opened, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var recheck = Task.Delay(_recheck, wait.Token);
            var done = await Task.WhenAny(forward.Exited, recheck).ConfigureAwait(false);
            wait.Cancel();
            if (done == forward.Exited || stop.IsCancellationRequested) return;
            var (probe, failure) = await ProbeAsync(target, stop).ConfigureAwait(false);
            if (failure == T3SshFailure.None && (probe?.EnvironmentId != tunnel.EnvironmentId || !probe.Running ||
                                                 probe.RemoteHost != opened.RemoteHost || probe.RemotePort != opened.RemotePort)) return;
        }
    }

    // ---- Pairing codes ----

    public async Task<T3DesktopPairingResult> MintCodeAsync(string environmentId, IReadOnlyList<T3SshTarget> targets, IReadOnlyList<string> scopes,
        IReadOnlyList<string>? usageScopes = null, CancellationToken ct = default)
    {
        if (!EnvironmentIdPattern().IsMatch(environmentId) || !AreAllowed(scopes) || (usageScopes != null && !AreAllowed(usageScopes)) ||
            targets.Count == 0 || targets.Any(t => !t.IsValid))
            return new(T3DesktopPairingOutcome.Failed);

        Tunnel? tunnel;
        lock (_lock) tunnel = EnsureTunnelLocked(environmentId, targets);
        if (tunnel == null) return new(T3DesktopPairingOutcome.Failed);

        // The code is only minted once the tunnel it will be used through is open.
        if (!tunnel.Open)
        {
            int seen = Volatile.Read(ref tunnel.Attempts);
            tunnel.Wake();
            var waited = Stopwatch.StartNew();
            while (!tunnel.Open && Volatile.Read(ref tunnel.Attempts) == seen && waited.Elapsed < OpenTimeout)
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            if (!tunnel.Open)
                return new(Volatile.Read(ref tunnel.Attempts) == seen ? T3DesktopPairingOutcome.SshUnreachable : tunnel.Problem);
        }
        // Minted on the machine the tunnel goes to, so the code lands where it will be used.
        if (tunnel.Current is not { } target) return new(T3DesktopPairingOutcome.SshUnreachable);

        var result = await _runner.RunScriptAsync(target, MintScript(environmentId, scopes, usageScopes), MintTimeout, ct).ConfigureAwait(false);
        if (result.Unreachable) return new(Outcome(result.Reason));
        switch (result.ExitCode)
        {
            case 0:
                var (code, usageCode) = ParseMint(result.Stdout, new Uri(Address(tunnel.LocalPort)));
                return code == null ? new(T3DesktopPairingOutcome.Failed) : new(T3DesktopPairingOutcome.Success, code, usageCode);
            case 3: return new(T3DesktopPairingOutcome.NoT3Command);
            case 4:
                Moved(tunnel, environmentId, new[] { (target, (string?)null) });
                return new(T3DesktopPairingOutcome.WrongEnvironment);
            case 5: return new(T3DesktopPairingOutcome.NotRunning);
            default: return new(T3DesktopPairingOutcome.Failed);
        }
    }

    // ---- Local ports ----

    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    private static Task<bool> AnswersAsT3Async(int port, CancellationToken ct) =>
        T3LocalDiscovery.ProbeAsync(new Uri(Address(port)), MetadataTimeout, ct);

    public async ValueTask DisposeAsync()
    {
        List<Task> loops;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var tunnel in _tunnels.Values) tunnel.Close();
            loops = _tunnels.Values.Select(t => t.Loop).Concat(_closing.Values).ToList();
            _tunnels.Clear();
            _closing.Clear();
        }
        try { await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { }
    }
}
