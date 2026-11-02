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

/// <summary>Reaches T3 environments over the user's own SSH, for the ones T3 Code reaches some other way.</summary>
public interface IT3SshEnvironments : IAsyncDisposable
{
    /// <summary>The local address this environment's tunnel listens on. It stays the same while Deskchirp runs.</summary>
    string AddressFor(string environmentId);

    /// <summary>Asks the user's SSH hosts which T3 environment each runs, and returns the machine for each one wanted that turned up.</summary>
    Task<IReadOnlyDictionary<string, T3SshTarget>> DiscoverAsync(IReadOnlyCollection<string> environmentIds, CancellationToken ct = default);

    /// <summary>
    /// Keeps tunnels open for exactly these environments, each to the first of its machines that runs it,
    /// reopening them when they drop, and closes the rest.
    /// </summary>
    void Retain(IReadOnlyDictionary<string, IReadOnlyList<T3SshTarget>> tunnels);

    /// <summary>Environments whose tunnel found its machine running another environment since the last call, so they can be looked for again.</summary>
    IReadOnlyCollection<string> TakeMovedEnvironments();

    /// <summary>
    /// Environments whose tunnel hasn't reached any of its machines for a while, once per stretch, so other
    /// machines can be asked whether they run it now.
    /// </summary>
    IReadOnlyCollection<string> TakeUnreachableEnvironments();

    /// <summary>Opens the environment's tunnel if needed, then has the machine's own t3 command mint a one-time pairing code.</summary>
    Task<T3DesktopPairingResult> MintCodeAsync(string environmentId, IReadOnlyList<T3SshTarget> targets, IReadOnlyList<string> scopes, CancellationToken ct = default);
}

/// <summary>
/// Over SSH, Deskchirp only reads two small files in ~/.t3/userdata (the environment id and the
/// running server's port), runs "t3 auth pairing create" for a five-minute read-only code, and
/// forwards a local port to the server. It never starts or stops T3 Code there, and never reads
/// its credentials or conversations. Each machine is checked to run the expected environment first.
/// </summary>
public sealed partial class T3SshEnvironments : IT3SshEnvironments
{
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
    public static readonly TimeSpan MinTunnelRetry = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxTunnelRetry = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RecheckOpenTunnel = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SearchAfterUnreachable = TimeSpan.FromMinutes(15);
    internal const string ProbeMarker = "deskchirp-t3-probe-1";
    private const int MaxParallelProbes = 4;
    private static readonly string[] AllowedScopes = { T3DesktopPairingClient.ReadScope, T3DesktopPairingClient.DiagnosticsScope };

    private readonly IT3SshRunner _runner;
    private readonly IT3SshHostSource _hosts;
    private readonly Func<int, bool> _portFree;
    private readonly Func<int, CancellationToken, Task<bool>> _listening;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _recheck;
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _ports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tunnel> _tunnels = new(StringComparer.Ordinal);
    private readonly Dictionary<T3SshTarget, HostProbe> _probed = new();
    private readonly HashSet<string> _moved = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreachable = new(StringComparer.Ordinal);
    private bool _disposed;

    public T3SshEnvironments(
        IT3SshRunner? runner = null,
        IT3SshHostSource? hosts = null,
        Func<int, bool>? portFree = null,
        Func<int, CancellationToken, Task<bool>>? listening = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? recheckOpenTunnel = null)
    {
        _runner = runner ?? new T3SshRunner();
        _hosts = hosts ?? new T3SshHostSource();
        _portFree = portFree ?? IsPortFree;
        _listening = listening ?? IsListeningAsync;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _recheck = recheckOpenTunnel ?? RecheckOpenTunnel;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentIdPattern();

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
        public volatile IT3SshForward? Forward;
        /// <summary>Open until ssh exits, even before the loop notices.</summary>
        public bool Open => Forward is { Exited.IsCompleted: false };
        /// <summary>Counts finished attempts, so a waiter can tell a fresh failure from an old one.</summary>
        public int Attempts;
        public volatile T3DesktopPairingOutcome Problem = T3DesktopPairingOutcome.SshUnreachable;
        /// <summary>When the attempts in a row that reached none of the machines began, and whether that was reported.</summary>
        public DateTimeOffset? UnreachableSince;
        public bool ReportedUnreachable;

        public void Wake()
        {
            try { if (Kick.CurrentCount == 0) Kick.Release(); }
            catch (SemaphoreFullException) { }
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
                port = FirstPort + start;
                for (int i = 0; i < 64; i++)
                {
                    int candidate = FirstPort + (start + i) % PortCount;
                    if (_ports.ContainsValue(candidate)) continue;
                    if (_portFree(candidate)) { port = candidate; break; }
                }
                _ports[environmentId] = port;
            }
            return Address(port);
        }
    }

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

        // Machines asked recently aren't asked again; ones that didn't answer wait longer each time.
        var now = _clock();
        var toProbe = new List<T3SshTarget>();
        lock (_lock)
        {
            foreach (var candidate in candidates)
            {
                if (!_probed.TryGetValue(candidate, out var known) || now >= known.RecheckAt) toProbe.Add(candidate);
            }
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
                        var (probe, reached) = await ProbeAsync(target, token).ConfigureAwait(false);
                        Record(target, reached ? probe?.EnvironmentId ?? string.Empty : null);
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* Took too long; keep what answered. */ }
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

    /// <summary>Remembers what a machine answered: an environment id, empty for no T3 Code, or null when it couldn't be reached.</summary>
    private void Record(T3SshTarget target, string? environmentId)
    {
        var now = _clock();
        lock (_lock)
        {
            if (environmentId != null)
            {
                _probed[target] = new HostProbe(environmentId, now + KnownHostRefresh, 0);
                return;
            }
            int failures = _probed.TryGetValue(target, out var previous) ? previous.Failures + 1 : 1;
            var wait = TimeSpan.FromTicks(Math.Min(MaxHostRetry.Ticks, HostRetry.Ticks * (1L << Math.Min(failures - 1, 10))));
            _probed[target] = new HostProbe(null, now + wait, failures);
        }
    }

    private void Forget(T3SshTarget target)
    {
        lock (_lock) _probed.Remove(target);
    }

    /// <summary>
    /// The environment left this machine. Machines that answered are asked again, as one of them may run it
    /// now; ones that didn't answer keep waiting.
    /// </summary>
    private void Moved(string environmentId, IEnumerable<T3SshTarget> targets)
    {
        lock (_lock)
        {
            foreach (var (host, probe) in _probed.ToList())
                if (probe.EnvironmentId != null) _probed.Remove(host);
            foreach (var target in targets) _probed.Remove(target);
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
    /// doesn't offer them from memory, and machines that answered are asked again, as one of them may run it now.
    /// </summary>
    private void LongUnreachable(Tunnel tunnel)
    {
        lock (_lock)
        {
            foreach (var (host, probe) in _probed.ToList())
                if (probe.EnvironmentId != null) _probed.Remove(host);
            foreach (var target in tunnel.Targets) _probed[target] = new HostProbe(null, _clock() + HostRetry, 1);
            _unreachable.Add(tunnel.EnvironmentId);
        }
    }

    // ---- Remote scripts ----

    /// <summary>Reads the environment id and, when its server is running, the runtime file. Nothing else.</summary>
    private const string ReadState = """
        T3_HOME="${T3CODE_HOME:-$HOME/.t3}"
        ID="$(head -c 200 "$T3_HOME/userdata/environment-id" 2>/dev/null | tr -d '\r\n')"
        RUNTIME="$(head -c 16384 "$T3_HOME/userdata/server-runtime.json" 2>/dev/null | tr -d '\r\n')"
        PID="$(printf '%s' "$RUNTIME" | sed -n 's/.*"pid"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p')"
        if [ -z "$PID" ] || ! { kill -0 "$PID" 2>/dev/null || [ -d "/proc/$PID" ]; }; then RUNTIME=""; PID=""; fi

        """;

    internal static string ProbeScript() => ReadState + $"""
        printf '%s\n' '{ProbeMarker}'
        printf 'id=%s\n' "$ID"
        printf 'runtime=%s\n' "$RUNTIME"

        """;

    /// <summary>
    /// Mints with the t3 the running server was started from, so the code lands in the database that
    /// server reads, falling back to t3 on PATH. Exit 3: no t3; 4: another environment; 5: not running.
    /// </summary>
    internal static string MintScript(string environmentId, IReadOnlyList<string> scopes)
    {
        if (!EnvironmentIdPattern().IsMatch(environmentId)) throw new ArgumentException("Not an environment id.", nameof(environmentId));
        if (scopes.Count == 0 || scopes.Any(s => !AllowedScopes.Contains(s))) throw new ArgumentException("Not a scope Deskchirp asks for.", nameof(scopes));
        string scopeArgs = string.Join(" ", scopes.Select(s => $"--scope {s}"));
        return ReadState + $$"""
            [ "$ID" = '{{environmentId}}' ] || exit 4
            [ -n "$PID" ] || exit 5
            T3=""
            EXE="$(readlink "/proc/$PID/exe" 2>/dev/null | sed 's/ (deleted)$//')"
            case "$EXE" in */t3) [ -x "$EXE" ] && T3="$EXE" ;; esac
            if [ -z "$T3" ]; then
              ARG0="$(ps -o args= -p "$PID" 2>/dev/null | awk '{print $1}')"
              case "$ARG0" in */t3) [ -x "$ARG0" ] && T3="$ARG0" ;; esac
            fi
            [ -n "$T3" ] || T3="$(command -v t3 2>/dev/null)" || T3=""
            if [ -z "$T3" ] && [ -x "$HOME/.local/bin/t3" ]; then T3="$HOME/.local/bin/t3"; fi
            [ -n "$T3" ] || exit 3
            exec "$T3" auth pairing create --base-dir "$T3_HOME" {{scopeArgs}} --ttl 5m --label Deskchirp --json </dev/null

            """;
    }

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

        string? host = null;
        int? port = null;
        if (!string.IsNullOrEmpty(runtime) &&
            T3LocalDiscovery.ParseRuntime(Encoding.UTF8.GetBytes(runtime)) is { } address &&
            Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            host = uri.Host == "[::1]" ? "::1" : uri.Host;
            port = uri.Port;
        }
        return new T3SshProbe(id, host, port);
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

    /// <summary>Runs the probe. Reached is false when ssh couldn't get in.</summary>
    private async Task<(T3SshProbe? Probe, bool Reached)> ProbeAsync(T3SshTarget target, CancellationToken ct)
    {
        var result = await _runner.RunScriptAsync(target, ProbeScript(), ProbeTimeout, ct).ConfigureAwait(false);
        if (result.Unreachable) return (null, false);
        return (result.ExitCode == 0 ? ParseProbe(result.Stdout) : null, true);
    }

    // ---- Tunnels ----

    public void Retain(IReadOnlyDictionary<string, IReadOnlyList<T3SshTarget>> tunnels)
    {
        lock (_lock)
        {
            if (_disposed) return;
            foreach (var (id, tunnel) in _tunnels.ToList())
            {
                if (tunnels.TryGetValue(id, out var targets) && targets.SequenceEqual(tunnel.Targets)) continue;
                tunnel.Stop.Cancel();
                _tunnels.Remove(id);
            }
            foreach (var (id, targets) in tunnels) EnsureTunnelLocked(id, targets);
        }
    }

    private Tunnel? EnsureTunnelLocked(string environmentId, IReadOnlyList<T3SshTarget> targets)
    {
        if (_disposed || targets.Count == 0) return null;
        if (_tunnels.TryGetValue(environmentId, out var existing))
        {
            if (existing.Targets.SequenceEqual(targets)) return existing;
            existing.Stop.Cancel();
        }
        if (!_ports.ContainsKey(environmentId)) AddressFor(environmentId);
        var tunnel = new Tunnel(environmentId, targets, _ports[environmentId]);
        _tunnels[environmentId] = tunnel;
        tunnel.Loop = Task.Run(() => RunTunnelAsync(tunnel));
        return tunnel;
    }

    /// <summary>Keeps one tunnel open until it is no longer retained, waiting longer after each failure.</summary>
    private async Task RunTunnelAsync(Tunnel tunnel)
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
                tunnel.Current = target;
                tunnel.Forward = forward;
                tunnel.UnreachableSince = null;
                tunnel.ReportedUnreachable = false;
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
                if (opened.Problem == T3DesktopPairingOutcome.SshUnreachable)
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
                Interlocked.Increment(ref tunnel.Attempts);
            }

            if (stop.IsCancellationRequested) break;
            try { await tunnel.Kick.WaitAsync(delay, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromTicks(Math.Min(MaxTunnelRetry.Ticks, delay.Ticks * 2));
        }
    }

    private sealed record Opened(T3DesktopPairingOutcome Problem, IT3SshForward? Forward, T3SshTarget? Target, T3SshProbe? Probe);

    /// <summary>
    /// Tries the machines in order until one runs this environment and forwards to it. Otherwise returns the
    /// most telling problem: not running beats another environment, which beats unreachable.
    /// </summary>
    private async Task<Opened> OpenAsync(Tunnel tunnel, CancellationToken ct)
    {
        var problem = T3DesktopPairingOutcome.SshUnreachable;
        var wrong = new List<T3SshTarget>();
        foreach (var target in tunnel.Targets)
        {
            var (probe, reached) = await ProbeAsync(target, ct).ConfigureAwait(false);
            if (!reached) continue;
            if (probe?.EnvironmentId != tunnel.EnvironmentId)
            {
                wrong.Add(target);
                if (problem == T3DesktopPairingOutcome.SshUnreachable) problem = T3DesktopPairingOutcome.WrongEnvironment;
                continue;
            }
            if (!probe.Running) { problem = T3DesktopPairingOutcome.NotRunning; continue; }
            // Something else listening here would get the sign-in meant for T3.
            if (!_portFree(tunnel.LocalPort)) return new(T3DesktopPairingOutcome.LocalPortBusy, null, null, null);
            var forward = await ForwardAsync(tunnel, target, probe, ct).ConfigureAwait(false);
            if (forward != null) return new(T3DesktopPairingOutcome.Success, forward, target, probe);
        }
        if (problem == T3DesktopPairingOutcome.WrongEnvironment) Moved(tunnel.EnvironmentId, wrong);
        return new(problem, null, null, null);
    }

    /// <summary>Starts the forward and returns it once the local port answers, or null if ssh gave up.</summary>
    private async Task<IT3SshForward?> ForwardAsync(Tunnel tunnel, T3SshTarget target, T3SshProbe probe, CancellationToken ct)
    {
        var forward = _runner.StartForward(target, tunnel.LocalPort, probe.RemoteHost!, probe.RemotePort!.Value);
        var waited = Stopwatch.StartNew();
        try
        {
            while (waited.Elapsed < ListenTimeout)
            {
                if (forward.Exited.IsCompleted) break;
                if (await _listening(tunnel.LocalPort, ct).ConfigureAwait(false) && !forward.Exited.IsCompleted)
                    return forward;
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            }
        }
        catch
        {
            forward.Dispose();
            throw;
        }
        forward.Dispose();
        return null;
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
            var (probe, reached) = await ProbeAsync(target, stop).ConfigureAwait(false);
            if (reached && (probe?.EnvironmentId != tunnel.EnvironmentId || !probe.Running ||
                            probe.RemoteHost != opened.RemoteHost || probe.RemotePort != opened.RemotePort)) return;
        }
    }

    // ---- Pairing codes ----

    public async Task<T3DesktopPairingResult> MintCodeAsync(string environmentId, IReadOnlyList<T3SshTarget> targets, IReadOnlyList<string> scopes, CancellationToken ct = default)
    {
        if (!EnvironmentIdPattern().IsMatch(environmentId) || scopes.Count == 0 || scopes.Any(s => !AllowedScopes.Contains(s)) ||
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

        var result = await _runner.RunScriptAsync(target, MintScript(environmentId, scopes), MintTimeout, ct).ConfigureAwait(false);
        if (result.Unreachable) return new(T3DesktopPairingOutcome.SshUnreachable);
        switch (result.ExitCode)
        {
            case 0:
                string? code = ParseCredential(result.Stdout, new Uri(AddressFor(environmentId)));
                return code == null ? new(T3DesktopPairingOutcome.Failed) : new(T3DesktopPairingOutcome.Success, code);
            case 3: return new(T3DesktopPairingOutcome.NoT3Command);
            case 4:
                Moved(environmentId, new[] { target });
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

    private static async Task<bool> IsListeningAsync(int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        List<Tunnel> tunnels;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            tunnels = _tunnels.Values.ToList();
            _tunnels.Clear();
        }
        foreach (var tunnel in tunnels) tunnel.Stop.Cancel();
        try { await Task.WhenAll(tunnels.Select(t => t.Loop)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { }
    }
}
