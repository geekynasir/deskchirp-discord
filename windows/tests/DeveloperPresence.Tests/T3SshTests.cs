using System.Diagnostics;
using System.Text.Json;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.T3.Desktop;
using Xunit;

namespace DeveloperPresence.Tests;

/// <summary>Reaching T3 environments over the user's own SSH. Every machine, code and runtime file here is fake.</summary>
public class T3SshTests : IDisposable
{
    private const string EnvA = "11111111-2222-3333-4444-555555555555";
    private const string EnvB = "66666666-7777-8888-9999-000000000000";
    private const string PrettyPairing = """
        {
          "id": "pairing-1",
          "credential": "SYNTHETICCODE42",
          "label": "Deskchirp",
          "scopes": [
            "orchestration:read"
          ],
          "expiresAt": "2026-11-02T12:05:00.000Z"
        }
        """;

    private static readonly string UsagePairing = PrettyPairing.Replace("SYNTHETICCODE42", "SYNTHETICUSAGE7", StringComparison.Ordinal);

    private readonly string _dir;

    public T3SshTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "deskchirp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- Targets and ssh arguments ----

    [Theory]
    [InlineData("mumbai-vps", null, null, true)]
    [InlineData("box.tail1234.ts.net", "ubuntu", 2222, true)]
    [InlineData("fd7a:115c:a1e0::1", null, null, true)]
    [InlineData("-oProxyCommand=evil", null, null, false)]
    [InlineData("box", "-l", null, false)]
    [InlineData("box name", null, null, false)]
    [InlineData("box;rm", null, null, false)]
    [InlineData("box", "user@x", null, false)]
    [InlineData("box", null, 70000, false)]
    [InlineData("", null, null, false)]
    public void TargetsAreKeptToPlainNames(string host, string? user, int? port, bool valid) =>
        Assert.Equal(valid, new T3SshTarget(host, user, port).IsValid);

    [Fact]
    public void SshNeverPromptsAndForwardsOnlyOnLoopback()
    {
        var target = new T3SshTarget("box", "ubuntu", 2222);
        var script = T3SshRunner.ScriptArgs(target);
        Assert.Equal(new[] { "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", "-o", "ControlMaster=no", "-o", "ControlPath=none", "-p", "2222", "-T", "--", "ubuntu@box", "sh -s" }, script);

        var forward = T3SshRunner.ForwardArgs(target, 41234, "::1", 3773);
        Assert.Contains("ExitOnForwardFailure=yes", forward);
        Assert.Contains("BatchMode=yes", forward);
        Assert.Equal("127.0.0.1:41234:[::1]:3773", forward[forward.IndexOf("-L") + 1]);
        Assert.Equal(new[] { "--", "ubuntu@box" }, forward.TakeLast(2));
    }

    [Theory]
    [InlineData("Host key verification failed.", T3SshFailure.HostKey)]
    [InlineData("@@@ WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED! @@@", T3SshFailure.HostKey)]
    [InlineData("ubuntu@box: Permission denied (publickey).", T3SshFailure.Denied)]
    [InlineData("ssh: connect to host box port 22: Connection timed out", T3SshFailure.Unreachable)]
    [InlineData("", T3SshFailure.Unreachable)]
    public void SaysWhySshCouldntGetIn(string stderr, T3SshFailure expected) =>
        Assert.Equal(expected, T3SshRunner.Classify(stderr));

    // ---- SSH config and tailnet ----

    [Fact]
    public void ConfigGivesNamedHostsOnly()
    {
        const string config = """
            # Machines
            Host mumbai-vps india-vps
              HostName 10.0.0.5
              User ubuntu
              User ignored
            Host *.internal
              User nobody
            Host github.com
              User git
            Host gh
              HostName github.com
            Host "quoted"
              HostName=quoted.example
            Match host foo
              User matched
            Include extra
            Host after-include
            """;
        var hosts = T3SshHostSource.ParseConfig(config, name => name == "extra" ? new[] { "Host included\n  User me" } : Array.Empty<string>());

        Assert.Equal(new[]
        {
            new T3SshConfigHost("mumbai-vps", "10.0.0.5", "ubuntu"),
            new T3SshConfigHost("india-vps", "10.0.0.5", "ubuntu"),
            new T3SshConfigHost("quoted", "quoted.example", null),
            new T3SshConfigHost("included", null, "me"),
            new T3SshConfigHost("after-include", null, null),
        }, hosts);
    }

    [Fact]
    public void ConfigHostNamesNameEachAliasesOwnMachine()
    {
        var hosts = T3SshHostSource.ParseConfig("Host vps laptop\n  HostName %h.tail.ts.net\n", _ => Array.Empty<string>());
        Assert.Equal(new[] { new T3SshConfigHost("vps", "vps.tail.ts.net", null), new T3SshConfigHost("laptop", "laptop.tail.ts.net", null) }, hosts);
    }

    [Theory]
    [InlineData("id_ed25519", true)]
    [InlineData("id_rsa.pub", true)]
    [InlineData("known_hosts", true)]
    [InlineData("authorized_keys", true)]
    [InlineData("work.pem", true)]
    [InlineData("deploy.key", true)]
    [InlineData("putty.PPK", true)]
    [InlineData("work.conf", false)]
    [InlineData("config", false)]
    public void IncludesNeverReadKeyFiles(string name, bool key) =>
        Assert.Equal(key, T3SshHostSource.LooksLikeKeyFile(Path.Combine("conf.d", name)));

    [Fact]
    public void TailnetGivesTheUsersOwnOnlinePosixMachines()
    {
        const string status = """
            {
              "Self": { "UserID": 7, "DNSName": "pc.tail.ts.net.", "OS": "windows" },
              "Peer": {
                "a": { "UserID": 7, "Online": true, "OS": "linux", "DNSName": "vps.tail.ts.net.", "HostName": "vps", "TailscaleIPs": ["100.64.0.2"] },
                "b": { "UserID": 7, "Online": false, "OS": "linux", "DNSName": "offline.tail.ts.net." },
                "c": { "UserID": 8, "Online": true, "OS": "linux", "DNSName": "someone-else.tail.ts.net." },
                "d": { "UserID": 9, "Online": true, "OS": "linux", "DNSName": "server.tail.ts.net.", "Tags": ["tag:server"] },
                "e": { "UserID": 7, "Online": true, "OS": "windows", "DNSName": "other-pc.tail.ts.net." },
                "f": { "UserID": 7, "Online": true, "OS": "macOS", "DNSName": "mac.tail.ts.net.", "HostName": "mac" },
                "g": { "UserID": 7, "Online": true, "OS": "linux", "DNSName": "shared.tail.ts.net.", "ShareeNode": true }
              }
            }
            """;
        Assert.Equal(new[] { "mac.tail.ts.net", "server.tail.ts.net", "vps.tail.ts.net" }, T3SshHostSource.ParseTailscalePeers(status).Select(p => p.DnsName));
        Assert.Empty(T3SshHostSource.ParseTailscalePeers("not json"));
    }

    [Fact]
    public void ConfigAliasesComeFirstAndTailnetMachinesTheConfigNamesAreSkipped()
    {
        var hosts = new[] { new T3SshConfigHost("mumbai-vps", "100.64.0.2", "ubuntu"), new T3SshConfigHost("laptop", null, null) };
        var peers = new[]
        {
            new T3TailnetPeer("vps.tail.ts.net", "vps", new[] { "100.64.0.2" }),
            new T3TailnetPeer("mac.tail.ts.net", "mac", new[] { "100.64.0.3" }),
        };
        var targets = T3SshHostSource.Merge(hosts, peers);
        Assert.Equal(new[] { "mumbai-vps", "laptop", "ubuntu@mac.tail.ts.net", "mac.tail.ts.net" }, targets.Select(t => t.Destination));
    }

    [Fact]
    public async Task HostSourceReadsTheConfigAndToleratesMissingTailscale()
    {
        File.WriteAllText(Path.Combine(_dir, "config"), "Host box\n  User me\nInclude conf.d/*.conf\n");
        Directory.CreateDirectory(Path.Combine(_dir, "conf.d"));
        File.WriteAllText(Path.Combine(_dir, "conf.d", "a.conf"), "Host from-include\n");
        // A key that happens to match the pattern is never read.
        File.WriteAllText(Path.Combine(_dir, "conf.d", "stray.conf"), "-----BEGIN OPENSSH PRIVATE KEY-----\nHost from-key\n");
        var source = new T3SshHostSource(_dir, _ => Task.FromResult<string?>(null));
        Assert.Equal(new[] { "box", "from-include" }, (await source.CandidatesAsync()).Select(t => t.Destination));
    }

    // ---- What the machine says ----

    [Fact]
    public void ProbeSkipsLoginNoiseAndReadsTheRuntime()
    {
        var probe = T3SshEnvironments.ParseProbe($"Welcome!\r\nid=not-this\n{T3SshEnvironments.ProbeMarker}\nid={EnvA}\nruntime={{\"version\":1,\"pid\":42,\"port\":3773,\"host\":\"127.0.0.1\"}}\n");
        Assert.Equal(new T3SshProbe(EnvA, "127.0.0.1", 3773), probe);
        Assert.True(probe!.Running);

        var ipv6 = T3SshEnvironments.ParseProbe($"{T3SshEnvironments.ProbeMarker}\nid={EnvA}\nruntime={{\"version\":1,\"pid\":42,\"port\":3900,\"host\":\"::1\"}}\n");
        Assert.Equal(new T3SshProbe(EnvA, "::1", 3900), ipv6);

        var stopped = T3SshEnvironments.ParseProbe($"{T3SshEnvironments.ProbeMarker}\nid={EnvA}\nruntime=\n");
        Assert.Equal(new T3SshProbe(EnvA, null, null), stopped);
        Assert.False(stopped!.Running);

        Assert.Null(T3SshEnvironments.ParseProbe($"{T3SshEnvironments.ProbeMarker}\nid=bad id;\nruntime=\n")!.EnvironmentId);
        Assert.Null(T3SshEnvironments.ParseProbe("no marker here"));
    }

    [Theory]
    [InlineData("""{"version":1,"pid":42,"port":3773}""", "127.0.0.1", 3773)]
    [InlineData("""{"version":1,"pid":42,"port":3773,"host":"0.0.0.0"}""", "127.0.0.1", 3773)]
    [InlineData("""{"version":1,"pid":42,"port":3773,"host":"[::]"}""", "::1", 3773)]
    [InlineData("""{"version":1,"pid":42,"port":3773,"host":"100.64.0.2"}""", "100.64.0.2", 3773)]
    [InlineData("""{"version":1,"pid":42,"port":3773,"host":"fd7a:115c:a1e0::2"}""", "fd7a:115c:a1e0::2", 3773)]
    [InlineData("""{"version":1,"pid":42,"port":3773,"host":"box.tail.ts.net"}""", "box.tail.ts.net", 3773)]
    [InlineData("""{"version":1,"pid":42,"port":3773,"host":"x:1:[::1]:22 -oProxyCommand=evil"}""", "127.0.0.1", 3773)]
    [InlineData("""{"version":1,"pid":42,"port":3773,"host":"fe80::1%eth0"}""", "127.0.0.1", 3773)]
    [InlineData("""{"version":2,"pid":42,"port":3773}""", null, null)]
    [InlineData("""{"version":1,"pid":0,"port":3773}""", null, null)]
    [InlineData("""{"version":1,"pid":42,"port":70000}""", null, null)]
    [InlineData("[1,2]", null, null)]
    [InlineData("{not json", null, null)]
    public void RuntimeFilesGiveOnlyPlainHostsAndRealPorts(string runtime, string? host, int? port)
    {
        var parsed = T3SshEnvironments.ParseRemoteRuntime(runtime);
        Assert.Equal(host, parsed?.Host);
        Assert.Equal(port, parsed?.Port);
    }

    [Fact]
    public void CredentialIsReadFromT3sIndentedJson()
    {
        var origin = new Uri("http://127.0.0.1:41000/mcp");
        Assert.Equal("SYNTHETICCODE42", T3SshEnvironments.ParseCredential("motd {braces}\n" + PrettyPairing + "\n", origin));
        Assert.Null(T3SshEnvironments.ParseCredential("""{ "credential": "no spaces allowed" }""", origin));
        Assert.Null(T3SshEnvironments.ParseCredential("nothing", origin));
    }

    [Fact]
    public void BothCodesAreReadFromOneMint()
    {
        var origin = new Uri("http://127.0.0.1:41000/mcp");
        string marker = T3SshEnvironments.MintMarker;
        Assert.Equal(("SYNTHETICCODE42", "SYNTHETICUSAGE7"),
            T3SshEnvironments.ParseMint($"motd {{\"credential\":\"NOTTHISONE1\"}}\n{marker}\n{PrettyPairing}\n{marker}\n{UsagePairing}\n", origin));
        Assert.Equal(("SYNTHETICCODE42", (string?)null), T3SshEnvironments.ParseMint($"{marker}\r\n{PrettyPairing}\r\n", origin));
        // The usage code failing leaves the sign-in code.
        Assert.Equal(("SYNTHETICCODE42", (string?)null), T3SshEnvironments.ParseMint($"{marker}\n{PrettyPairing}\n{marker}\nerror\n", origin));
        Assert.Equal(((string?)null, (string?)null), T3SshEnvironments.ParseMint(PrettyPairing, origin));
    }

    [Fact]
    public void MintScriptOnlyTakesAnEnvironmentIdAndDeskchirpsScopes()
    {
        string script = T3SshEnvironments.MintScript(EnvA, new[] { "orchestration:read", "diagnostics:read" });
        Assert.Contains($"[ \"$ID\" = '{EnvA}' ] || exit 4", script);
        Assert.Contains("--scope orchestration:read --scope diagnostics:read --ttl 5m --label Deskchirp --json </dev/null", script);
        Assert.DoesNotContain("secrets", script);
        Assert.Throws<ArgumentException>(() => T3SshEnvironments.MintScript("x'; rm -rf ~; '", new[] { "orchestration:read" }));
        Assert.Throws<ArgumentException>(() => T3SshEnvironments.MintScript(EnvA, new[] { "orchestration:write" }));
        Assert.Throws<ArgumentException>(() => T3SshEnvironments.MintScript(EnvA, new[] { "orchestration:read" }, new[] { "orchestration:write" }));
    }

    /// <summary>Runs the real scripts with sh against a fake T3 home and a fake t3 command.</summary>
    [Fact]
    public async Task ScriptsWorkInARealShell()
    {
        if (OperatingSystem.IsWindows()) return;
        string home = Path.Combine(_dir, "home");
        string t3Home = Path.Combine(home, ".t3");
        string bin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(Path.Combine(t3Home, "userdata"));
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(t3Home, "userdata", "environment-id"), EnvA + "\n");
        WriteRuntime(t3Home, Environment.ProcessId);
        string fakeT3 = Path.Combine(bin, "t3");
        WriteFakeT3(fakeT3);

        var (code, output) = await RunShAsync(T3SshEnvironments.ProbeScript(), home, bin);
        Assert.Equal(0, code);
        Assert.Equal(new T3SshProbe(EnvA, "127.0.0.1", 3773), T3SshEnvironments.ParseProbe(output));

        var origin = new Uri("http://127.0.0.1:41000/mcp");
        (code, output) = await RunShAsync(T3SshEnvironments.MintScript(EnvA, new[] { "orchestration:read" }), home, bin);
        Assert.Equal(0, code);
        Assert.Equal(("SYNTHETICCODE42", (string?)null), T3SshEnvironments.ParseMint(output, origin));
        Assert.Equal($"auth pairing create --base-dir {t3Home} --scope orchestration:read --ttl 5m --label Deskchirp --json",
            File.ReadAllText(Path.Combine(home, "args")).Trim());

        // Both codes in one call.
        (code, output) = await RunShAsync(
            T3SshEnvironments.MintScript(EnvA, new[] { "orchestration:read" }, new[] { "orchestration:read", "diagnostics:read" }), home, bin);
        Assert.Equal(0, code);
        Assert.Equal(("SYNTHETICCODE42", "SYNTHETICCODE42"), T3SshEnvironments.ParseMint(output, origin));
        Assert.Contains("--scope orchestration:read --scope diagnostics:read", File.ReadAllText(Path.Combine(home, "args")));

        (code, _) = await RunShAsync(T3SshEnvironments.MintScript(EnvB, new[] { "orchestration:read" }), home, bin);
        Assert.Equal(4, code);

        // t3 failing says so.
        File.WriteAllText(fakeT3, "#!/bin/sh\necho broken >&2\nexit 1\n");
        (code, _) = await RunShAsync(T3SshEnvironments.MintScript(EnvA, new[] { "orchestration:read" }), home, bin);
        Assert.Equal(6, code);

        File.Delete(fakeT3);
        // Only meaningful where the usual install folders hold no t3 of their own.
        if (!File.Exists("/usr/local/bin/t3") && !File.Exists("/opt/homebrew/bin/t3"))
        {
            (code, _) = await RunShAsync(T3SshEnvironments.MintScript(EnvA, new[] { "orchestration:read" }), home, bin);
            Assert.Equal(3, code);
        }

        // A runtime file left behind by a server that exited counts as not running.
        File.WriteAllText(Path.Combine(t3Home, "userdata", "server-runtime.json"), "{\"version\":1,\"pid\":999999999,\"port\":3773}");
        (code, output) = await RunShAsync(T3SshEnvironments.ProbeScript(), home, bin);
        Assert.False(T3SshEnvironments.ParseProbe(output)!.Running);
        (code, _) = await RunShAsync(T3SshEnvironments.MintScript(EnvA, new[] { "orchestration:read" }), home, bin);
        Assert.Equal(5, code);
    }

    /// <summary>The server was started from a t3 that isn't on PATH: the code is minted with that one.</summary>
    [Fact]
    public async Task MintsWithTheT3TheServerRunsFrom()
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists("/proc/self")) return;
        string home = Path.Combine(_dir, "home");
        string t3Home = Path.Combine(home, ".t3");
        string bin = Path.Combine(_dir, "bin");
        string install = Path.Combine(_dir, "install", "bin");
        Directory.CreateDirectory(Path.Combine(t3Home, "userdata"));
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(t3Home, "userdata", "environment-id"), EnvA + "\n");
        string serverT3 = Path.Combine(install, "t3");
        WriteFakeT3(serverT3);

        // Like node running the t3 script: the interpreter comes first in the server's arguments.
        var serverInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        serverInfo.ArgumentList.Add(serverT3);
        serverInfo.ArgumentList.Add("serve");
        using var server = Process.Start(serverInfo)!;
        try
        {
            WriteRuntime(t3Home, server.Id);
            var (code, output) = await RunShAsync(T3SshEnvironments.MintScript(EnvA, new[] { "orchestration:read" }), home, bin);
            Assert.Equal(0, code);
            Assert.Equal("SYNTHETICCODE42", T3SshEnvironments.ParseMint(output, new Uri("http://127.0.0.1:41000/mcp")).Code);
            Assert.StartsWith("auth pairing create", File.ReadAllText(Path.Combine(home, "args")));
        }
        finally
        {
            try { server.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static void WriteRuntime(string t3Home, int pid) =>
        File.WriteAllText(Path.Combine(t3Home, "userdata", "server-runtime.json"),
            $"{{\"version\":1,\"pid\":{pid},\"port\":3773,\"host\":\"127.0.0.1\",\"origin\":\"http://127.0.0.1:3773\"}}");

    /// <summary>A t3 that waits when served, and otherwise records its arguments and prints a pairing.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void WriteFakeT3(string path)
    {
        File.WriteAllText(path, "#!/bin/sh\nif [ \"$1\" = serve ]; then sleep 60; exit 0; fi\nprintf '%s\\n' \"$*\" > \"$HOME/args\"\ncat <<'JSON'\n" + PrettyPairing + "\nJSON\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task<(int Code, string Output)> RunShAsync(string script, string home, string bin)
    {
        var info = new ProcessStartInfo("/bin/sh") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("-s");
        info.Environment.Clear();
        info.Environment["HOME"] = home;
        info.Environment["PATH"] = bin + ":/usr/bin:/bin";
        using var process = Process.Start(info)!;
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output);
    }

    // ---- Discovery, tunnels and codes ----

    internal sealed class FakeMachine
    {
        public string? EnvironmentId { get; set; }
        public int? Port { get; set; } = 3773;
        public string? Host { get; set; }
        public bool Down { get; set; }
        /// <summary>Why ssh can't get in, when it can't.</summary>
        public T3SshFailure Failure { get; set; } = T3SshFailure.Unreachable;
        public int MintExit { get; set; }
        /// <summary>When set, the forward's ssh exits straight away for this reason.</summary>
        public T3SshFailure? ForwardFails { get; set; }
    }

    internal sealed class FakeForward : IT3SshForward
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeForward(string destination, int localPort, string remote) { Destination = destination; LocalPort = localPort; Remote = remote; }
        public string Destination { get; }
        public int LocalPort { get; }
        public string Remote { get; }
        public bool Disposed { get; private set; }
        public T3SshFailure Failure { get; set; } = T3SshFailure.Unreachable;
        public Task Exited => _exited.Task;
        public void Exit() => _exited.TrySetResult();
        public void Dispose() { Disposed = true; _exited.TrySetResult(); }
    }

    internal sealed class FakeRunner : IT3SshRunner
    {
        public Dictionary<string, FakeMachine> Machines { get; } = new();
        public List<(string Destination, string Kind)> Calls { get; } = new();
        public List<FakeForward> Forwards { get; } = new();

        public Task<T3SshResult> RunScriptAsync(T3SshTarget target, string script, TimeSpan timeout, CancellationToken ct = default)
        {
            bool mint = script.Contains("auth pairing create", StringComparison.Ordinal);
            lock (Calls) Calls.Add((target.Destination, mint ? "mint" : "probe"));
            if (!Machines.TryGetValue(target.Destination, out var machine)) return Task.FromResult(new T3SshResult(255, string.Empty));
            if (machine.Down) return Task.FromResult(new T3SshResult(255, string.Empty, machine.Failure));
            if (!mint)
            {
                string runtime = machine.Port is { } port ? machine.Host is { } host
                    ? $"{{\"version\":1,\"pid\":42,\"host\":\"{host}\",\"port\":{port}}}"
                    : $"{{\"version\":1,\"pid\":42,\"port\":{port}}}" : string.Empty;
                return Task.FromResult(new T3SshResult(0, $"motd\n{T3SshEnvironments.ProbeMarker}\nid={machine.EnvironmentId}\nruntime={runtime}\n"));
            }
            if (!script.Contains($"'{machine.EnvironmentId}' ]", StringComparison.Ordinal)) return Task.FromResult(new T3SshResult(4, string.Empty));
            if (machine.Port == null) return Task.FromResult(new T3SshResult(5, string.Empty));
            if (machine.MintExit != 0) return Task.FromResult(new T3SshResult(machine.MintExit, string.Empty));
            string output = $"{T3SshEnvironments.MintMarker}\n{PrettyPairing}\n";
            // A second create asks for the usage code.
            if (script.IndexOf("auth pairing create", StringComparison.Ordinal) != script.LastIndexOf("auth pairing create", StringComparison.Ordinal))
                output += $"{T3SshEnvironments.MintMarker}\n{UsagePairing}\n";
            return Task.FromResult(new T3SshResult(0, output));
        }

        public IT3SshForward StartForward(T3SshTarget target, int localPort, string remoteHost, int remotePort)
        {
            var forward = new FakeForward(target.Destination, localPort, $"{remoteHost}:{remotePort}");
            if (Machines.TryGetValue(target.Destination, out var machine) && machine.ForwardFails is { } failure)
            {
                forward.Failure = failure;
                forward.Exit();
            }
            lock (Forwards) Forwards.Add(forward);
            return forward;
        }

        public FakeForward? Live(int port)
        {
            lock (Forwards) return Forwards.LastOrDefault(f => f.LocalPort == port && !f.Disposed && !f.Exited.IsCompleted);
        }

        public int Probes(string destination)
        {
            lock (Calls) return Calls.Count(c => c.Destination == destination && c.Kind == "probe");
        }
    }

    internal sealed class FakeHosts : IT3SshHostSource
    {
        public List<T3SshTarget> Targets { get; } = new();
        public Task<IReadOnlyList<T3SshTarget>> CandidatesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<T3SshTarget>>(Targets.ToList());
    }

    internal sealed class Ssh : IAsyncDisposable
    {
        public FakeRunner Runner { get; } = new();
        public FakeHosts Hosts { get; } = new();
        public HashSet<int> Busy { get; } = new();
        public bool AllBusy { get; set; }
        public DateTimeOffset Now { get; set; } = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        public T3SshEnvironments Environments { get; }

        public Ssh(TimeSpan? recheck = null)
        {
            Environments = new T3SshEnvironments(Runner, Hosts,
                portFree: port => !AllBusy && !Busy.Contains(port) && Runner.Live(port) == null,
                answersAsT3: (port, _) => Task.FromResult(Runner.Live(port) != null),
                clock: () => Now,
                recheckOpenTunnel: recheck);
        }

        public FakeMachine Machine(string destination, string? environmentId)
        {
            var machine = new FakeMachine { EnvironmentId = environmentId };
            Runner.Machines[destination] = machine;
            Hosts.Targets.Add(new T3SshTarget(destination));
            return machine;
        }

        public int PortOf(string environmentId) => new Uri(Environments.AddressFor(environmentId)).Port;

        public ValueTask DisposeAsync() => Environments.DisposeAsync();
    }

    [Fact]
    public async Task EachEnvironmentKeepsItsOwnLocalPort()
    {
        await using var a = new Ssh();
        await using var b = new Ssh();
        Assert.Equal(a.Environments.AddressFor(EnvA), b.Environments.AddressFor(EnvA));
        Assert.Equal(a.Environments.AddressFor(EnvA), a.Environments.AddressFor(EnvA));
        Assert.InRange(a.PortOf(EnvA), T3SshEnvironments.FirstPort, T3SshEnvironments.FirstPort + T3SshEnvironments.PortCount - 1);
        Assert.Matches(@"^http://127\.0\.0\.1:\d+/mcp$", a.Environments.AddressFor(EnvB));

        await using var busy = new Ssh();
        busy.Busy.Add(T3SshEnvironments.FirstPort + T3SshEnvironments.PreferredOffset(EnvA));
        Assert.NotEqual(a.PortOf(EnvA), busy.PortOf(EnvA));
    }

    [Fact]
    public async Task DiscoveryFindsWhichMachineRunsEachEnvironmentAndRemembersIt()
    {
        await using var ssh = new Ssh();
        ssh.Machine("down", EnvA).Down = true;
        ssh.Machine("no-t3", null);
        ssh.Machine("vps", EnvA);
        ssh.Machine("vps-alias", EnvA);
        ssh.Machine("laptop", EnvB);

        var found = await ssh.Environments.DiscoverAsync(new[] { EnvA, EnvB });
        Assert.Equal(new Dictionary<string, T3SshTarget> { [EnvA] = new("vps"), [EnvB] = new("laptop") }, found);

        // Asked again soon, nothing is probed again.
        int probes = ssh.Runner.Calls.Count;
        Assert.Equal(new T3SshTarget("vps"), (await ssh.Environments.DiscoverAsync(new[] { EnvA }))[EnvA]);
        Assert.Equal(new T3SshTarget("laptop"), (await ssh.Environments.DiscoverAsync(new[] { EnvB }))[EnvB]);
        Assert.Equal(probes, ssh.Runner.Calls.Count);

        // A machine that didn't answer waits ten minutes, then twice as long.
        ssh.Now += T3SshEnvironments.HostRetry;
        await ssh.Environments.DiscoverAsync(new[] { "env-nowhere" });
        Assert.Equal(2, ssh.Runner.Probes("down"));
        Assert.Equal(1, ssh.Runner.Probes("vps"));
        ssh.Now += T3SshEnvironments.HostRetry;
        await ssh.Environments.DiscoverAsync(new[] { "env-nowhere" });
        Assert.Equal(2, ssh.Runner.Probes("down"));
    }

    [Fact]
    public async Task MintsOnlyOnceTheTunnelIsOpenAndForwardsToT3sPort()
    {
        await using var ssh = new Ssh();
        ssh.Machine("vps", EnvA).Port = 3900;

        var result = await ssh.Environments.MintCodeAsync(EnvA, new[] { new T3SshTarget("vps") }, new[] { "orchestration:read" });

        Assert.Equal(new T3DesktopPairingResult(T3DesktopPairingOutcome.Success, "SYNTHETICCODE42"), result);
        var forward = Assert.Single(ssh.Runner.Forwards);
        Assert.Equal(("vps", ssh.PortOf(EnvA), "127.0.0.1:3900"), (forward.Destination, forward.LocalPort, forward.Remote));
        Assert.Equal(new[] { "probe", "mint" }, ssh.Runner.Calls.Select(c => c.Kind));
        Assert.DoesNotContain("SYNTHETIC", result.ToString());

        // Once nothing retains it, the tunnel closes.
        ssh.Environments.Retain(new Dictionary<string, IReadOnlyList<T3SshTarget>>());
        Assert.True(await WaitAsync(() => forward.Disposed));
    }

    [Theory]
    [InlineData("down", T3DesktopPairingOutcome.SshUnreachable)]
    [InlineData("stopped", T3DesktopPairingOutcome.NotRunning)]
    [InlineData("other", T3DesktopPairingOutcome.WrongEnvironment)]
    [InlineData("busy", T3DesktopPairingOutcome.LocalPortBusy)]
    [InlineData("no-t3", T3DesktopPairingOutcome.NoT3Command)]
    public async Task SaysWhyItCouldntMintACode(string machine, T3DesktopPairingOutcome expected)
    {
        await using var ssh = new Ssh();
        ssh.Machine("down", EnvA).Down = true;
        ssh.Machine("stopped", EnvA).Port = null;
        ssh.Machine("other", EnvB);
        ssh.Machine("busy", EnvA);
        ssh.Machine("no-t3", EnvA).MintExit = 3;
        if (machine == "busy") ssh.Busy.Add(ssh.PortOf(EnvA));

        var result = await ssh.Environments.MintCodeAsync(EnvA, new[] { new T3SshTarget(machine) }, new[] { "orchestration:read" });

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Code);
        Assert.Equal(machine == "no-t3" ? 1 : 0, ssh.Runner.Calls.Count(c => c.Kind == "mint"));
    }

    [Fact]
    public async Task RefusesScopesDeskchirpDoesntAskFor()
    {
        await using var ssh = new Ssh();
        ssh.Machine("vps", EnvA);
        var result = await ssh.Environments.MintCodeAsync(EnvA, new[] { new T3SshTarget("vps") }, new[] { "orchestration:write" });
        Assert.Equal(T3DesktopPairingOutcome.Failed, result.Outcome);
        Assert.Empty(ssh.Runner.Calls);
    }

    [Fact]
    public async Task ReopensTheTunnelWhenSshDrops()
    {
        await using var ssh = new Ssh();
        ssh.Machine("vps", EnvA);
        ssh.Environments.Retain(new Dictionary<string, IReadOnlyList<T3SshTarget>> { [EnvA] = new[] { new T3SshTarget("vps") } });
        Assert.True(await WaitAsync(() => ssh.Runner.Live(ssh.PortOf(EnvA)) != null));

        ssh.Runner.Live(ssh.PortOf(EnvA))!.Exit();
        // The next attempt waits a few seconds; minting wakes it.
        var result = await ssh.Environments.MintCodeAsync(EnvA, new[] { new T3SshTarget("vps") }, new[] { "orchestration:read" });
        Assert.Equal(T3DesktopPairingOutcome.Success, result.Outcome);
        Assert.Equal(2, ssh.Runner.Forwards.Count);
    }

    [Fact]
    public async Task TriesEachSavedMachineAndMintsOnTheOneTheTunnelGoesTo()
    {
        await using var ssh = new Ssh();
        ssh.Machine("down", EnvA).Down = true;
        ssh.Machine("other", EnvB);
        ssh.Machine("vps", EnvA);
        var targets = new[] { new T3SshTarget("down"), new T3SshTarget("other"), new T3SshTarget("vps") };

        var result = await ssh.Environments.MintCodeAsync(EnvA, targets, new[] { "orchestration:read" });

        Assert.Equal(T3DesktopPairingOutcome.Success, result.Outcome);
        Assert.Equal("vps", Assert.Single(ssh.Runner.Forwards).Destination);
        Assert.Equal(new[] { "vps" }, ssh.Runner.Calls.Where(c => c.Kind == "mint").Select(c => c.Destination));
        // Another machine answering with another environment isn't news when one of them works.
        Assert.Empty(ssh.Environments.TakeMovedEnvironments());
    }

    [Theory]
    [InlineData(new[] { "down", "other" }, T3DesktopPairingOutcome.WrongEnvironment)]
    [InlineData(new[] { "other", "stopped", "down" }, T3DesktopPairingOutcome.NotRunning)]
    [InlineData(new[] { "down", "down-too" }, T3DesktopPairingOutcome.SshUnreachable)]
    public async Task WhenNoSavedMachineWorksSaysTheMostTellingReason(string[] machines, T3DesktopPairingOutcome expected)
    {
        await using var ssh = new Ssh();
        ssh.Machine("down", EnvA).Down = true;
        ssh.Machine("down-too", EnvA).Down = true;
        ssh.Machine("other", EnvB);
        ssh.Machine("stopped", EnvA).Port = null;

        var result = await ssh.Environments.MintCodeAsync(EnvA, machines.Select(m => new T3SshTarget(m)).ToArray(), new[] { "orchestration:read" });

        Assert.Equal(expected, result.Outcome);
        Assert.Empty(ssh.Runner.Forwards);
    }

    [Fact]
    public async Task ReopensTheTunnelWhenT3ThereMovesToAnotherLoopbackAddress()
    {
        await using var ssh = new Ssh(recheck: TimeSpan.FromMilliseconds(50));
        var vps = ssh.Machine("vps", EnvA);
        ssh.Environments.Retain(new Dictionary<string, IReadOnlyList<T3SshTarget>> { [EnvA] = new[] { new T3SshTarget("vps") } });
        Assert.True(await WaitAsync(() => ssh.Runner.Live(ssh.PortOf(EnvA)) != null));
        var first = ssh.Runner.Live(ssh.PortOf(EnvA))!;
        Assert.Equal("127.0.0.1:3773", first.Remote);

        // Same port, now on IPv6 loopback: the old forward would point at nothing.
        vps.Host = "::1";
        Assert.True(await WaitAsync(() => first.Disposed));
        var result = await ssh.Environments.MintCodeAsync(EnvA, new[] { new T3SshTarget("vps") }, new[] { "orchestration:read" });
        Assert.Equal(T3DesktopPairingOutcome.Success, result.Outcome);
        Assert.Equal("::1:3773", ssh.Runner.Live(ssh.PortOf(EnvA))!.Remote);
    }

    [Fact]
    public async Task ATunnelWhoseMachineNowRunsAnotherEnvironmentReportsItAndAsksAgain()
    {
        await using var ssh = new Ssh();
        var vps = ssh.Machine("vps", EnvA);
        var laptop = ssh.Machine("laptop", null);
        // Asked for something it doesn't find, discovery asks every machine.
        Assert.Equal(new T3SshTarget("vps"), (await ssh.Environments.DiscoverAsync(new[] { EnvA, "env-nowhere" }))[EnvA]);
        Assert.Equal(1, ssh.Runner.Probes("laptop"));

        vps.EnvironmentId = EnvB;
        laptop.EnvironmentId = EnvA;
        // Past the point where the tunnel would trust discovery's answer instead of asking.
        ssh.Now += T3SshEnvironments.FreshProbe + TimeSpan.FromSeconds(1);
        ssh.Environments.Retain(new Dictionary<string, IReadOnlyList<T3SshTarget>> { [EnvA] = new[] { new T3SshTarget("vps") } });
        Assert.True(await WaitAsync(() => ssh.Environments.TakeMovedEnvironments() is { Count: > 0 } moved && moved.Single() == EnvA));
        Assert.Empty(ssh.Environments.TakeMovedEnvironments());

        // vps isn't offered for it from memory; laptop, which ran nothing, is asked again in its turn.
        Assert.Empty(await ssh.Environments.DiscoverAsync(new[] { EnvA }));
        ssh.Now += T3SshEnvironments.HostRetry;
        Assert.Equal(new T3SshTarget("laptop"), (await ssh.Environments.DiscoverAsync(new[] { EnvA }))[EnvA]);
    }

    [Fact]
    public async Task ATunnelThatReachesNoMachineForAWhileAsksForADifferentOne()
    {
        await using var ssh = new Ssh();
        var vps = ssh.Machine("vps", EnvA);
        var laptop = ssh.Machine("laptop", null);
        Assert.Equal(new T3SshTarget("vps"), (await ssh.Environments.DiscoverAsync(new[] { EnvA }))[EnvA]);

        vps.Down = true;
        laptop.EnvironmentId = EnvA;
        ssh.Now += T3SshEnvironments.FreshProbe + TimeSpan.FromSeconds(1);
        var targets = new[] { new T3SshTarget("vps") };
        Assert.Equal(T3DesktopPairingOutcome.SshUnreachable, (await ssh.Environments.MintCodeAsync(EnvA, targets, new[] { "orchestration:read" })).Outcome);
        Assert.Empty(ssh.Environments.TakeUnreachableEnvironments());

        ssh.Now += T3SshEnvironments.SearchAfterUnreachable;
        Assert.Equal(T3DesktopPairingOutcome.SshUnreachable, (await ssh.Environments.MintCodeAsync(EnvA, targets, new[] { "orchestration:read" })).Outcome);
        Assert.Equal(new[] { EnvA }, ssh.Environments.TakeUnreachableEnvironments());
        // Once per stretch.
        Assert.Equal(T3DesktopPairingOutcome.SshUnreachable, (await ssh.Environments.MintCodeAsync(EnvA, targets, new[] { "orchestration:read" })).Outcome);
        Assert.Empty(ssh.Environments.TakeUnreachableEnvironments());

        // Discovery no longer offers the silent machine from memory and asks the others again.
        Assert.Equal(new T3SshTarget("laptop"), (await ssh.Environments.DiscoverAsync(new[] { EnvA }))[EnvA]);
    }

    [Fact]
    public async Task OneMintCanBringTheUsageCodeToo()
    {
        await using var ssh = new Ssh();
        ssh.Machine("vps", EnvA);

        var result = await ssh.Environments.MintCodeAsync(EnvA, new[] { new T3SshTarget("vps") }, new[] { "orchestration:read" },
            new[] { "orchestration:read", "diagnostics:read" });

        Assert.Equal(new T3DesktopPairingResult(T3DesktopPairingOutcome.Success, "SYNTHETICCODE42", "SYNTHETICUSAGE7"), result);
        Assert.Single(ssh.Runner.Calls, c => c.Kind == "mint");
        Assert.DoesNotContain("SYNTHETIC", result.ToString());
        Assert.Equal(T3DesktopPairingOutcome.Failed,
            (await ssh.Environments.MintCodeAsync(EnvA, new[] { new T3SshTarget("vps") }, new[] { "orchestration:read" }, new[] { "orchestration:write" })).Outcome);
    }

    [Theory]
    [InlineData(T3SshFailure.HostKey, T3DesktopPairingOutcome.SshHostKey)]
    [InlineData(T3SshFailure.Denied, T3DesktopPairingOutcome.SshDenied)]
    [InlineData(T3SshFailure.NoSsh, T3DesktopPairingOutcome.SshMissing)]
    [InlineData(T3SshFailure.Unreachable, T3DesktopPairingOutcome.SshUnreachable)]
    public async Task SaysWhySshCouldntGetInToTheMachine(T3SshFailure failure, T3DesktopPairingOutcome expected)
    {
        await using var ssh = new Ssh();
        var vps = ssh.Machine("vps", EnvA);
        vps.Down = true;
        vps.Failure = failure;
        Assert.Equal(expected, (await ssh.Environments.MintCodeAsync(EnvA, new[] { new T3SshTarget("vps") }, new[] { "orchestration:read" })).Outcome);
        var state = ssh.Environments.TunnelState(EnvA)!;
        Assert.Equal((false, true, expected), (state.Open, state.Tried, state.Problem));
    }

    [Theory]
    [InlineData(T3SshFailure.Unreachable, T3DesktopPairingOutcome.ForwardFailed)]
    [InlineData(T3SshFailure.HostKey, T3DesktopPairingOutcome.SshHostKey)]
    [InlineData(T3SshFailure.Denied, T3DesktopPairingOutcome.SshDenied)]
    public async Task SaysWhyTheForwardDidntWork(T3SshFailure failure, T3DesktopPairingOutcome expected)
    {
        await using var ssh = new Ssh();
        ssh.Machine("vps", EnvA).ForwardFails = failure;
        Assert.Equal(expected, (await ssh.Environments.MintCodeAsync(EnvA, new[] { new T3SshTarget("vps") }, new[] { "orchestration:read" })).Outcome);
        Assert.DoesNotContain(ssh.Runner.Calls, c => c.Kind == "mint");
    }

    [Fact]
    public async Task TokensGoToATunnelPortOnlyWhileItsTunnelIsOpen()
    {
        await using var ssh = new Ssh();
        ssh.Machine("vps", EnvA);
        string address = ssh.Environments.AddressFor(EnvA);

        // Anything outside the tunnel ports is up to the caller.
        Assert.True(ssh.Environments.MaySend("https://vps.tail.ts.net:3773/mcp"));
        Assert.True(ssh.Environments.MaySend("http://127.0.0.1:3773/mcp"));
        Assert.False(ssh.Environments.MaySend(address));
        Assert.False(ssh.Environments.MaySend(address.Replace("127.0.0.1", "localhost", StringComparison.Ordinal)));
        Assert.Null(ssh.Environments.TunnelState(EnvA));

        Assert.Equal(T3DesktopPairingOutcome.Success, (await ssh.Environments.MintCodeAsync(EnvA, new[] { new T3SshTarget("vps") }, new[] { "orchestration:read" })).Outcome);
        Assert.True(ssh.Environments.MaySend(address));
        var state = ssh.Environments.TunnelState(EnvA)!;
        Assert.Equal((true, new T3SshTarget("vps"), ssh.PortOf(EnvA)), (state.Open, state.Machine, state.LocalPort));

        ssh.Runner.Live(ssh.PortOf(EnvA))!.Exit();
        Assert.False(ssh.Environments.MaySend(address));
        ssh.Environments.Retain(new Dictionary<string, IReadOnlyList<T3SshTarget>>());
        Assert.False(ssh.Environments.MaySend(address));
    }

    [Fact]
    public async Task AnEnvironmentKeepsTheTunnelPortItWasSignedInOn()
    {
        await using var ssh = new Ssh();
        const string saved = "http://127.0.0.1:45678/mcp";
        ssh.Environments.Reserve(EnvA, saved);
        Assert.Equal(saved, ssh.Environments.AddressFor(EnvA));

        // Not another environment's port, nor one outside the range, nor a second one.
        ssh.Environments.Reserve(EnvB, saved);
        Assert.NotEqual(saved, ssh.Environments.AddressFor(EnvB));
        ssh.Environments.Reserve("env-c", "https://box.example/mcp");
        ssh.Environments.Reserve("env-c", "http://10.0.0.5:45000/mcp");
        ssh.Environments.Reserve(EnvA, "http://127.0.0.1:45679/mcp");
        Assert.Equal(saved, ssh.Environments.AddressFor(EnvA));
        Assert.NotEqual(45000, ssh.PortOf("env-c"));
    }

    [Fact]
    public async Task NeverHandsOutAPortAnotherEnvironmentHas()
    {
        await using var ssh = new Ssh { AllBusy = true };
        string preferred = $"http://127.0.0.1:{T3SshEnvironments.FirstPort + T3SshEnvironments.PreferredOffset(EnvA)}/mcp";
        ssh.Environments.Reserve(EnvB, preferred);
        // Every port looks busy: EnvA still doesn't get EnvB's.
        Assert.NotEqual(preferred, ssh.Environments.AddressFor(EnvA));
        Assert.Equal(preferred, ssh.Environments.AddressFor(EnvB));
    }

    [Fact]
    public async Task ALongListOfMachinesIsWorkedThroughOverAFewPasses()
    {
        await using var ssh = new Ssh();
        int count = T3SshEnvironments.MaxProbesPerPass + 8;
        for (int i = 0; i < count; i++) ssh.Machine($"box{i}", null);

        await ssh.Environments.DiscoverAsync(new[] { EnvA });
        Assert.Equal(T3SshEnvironments.MaxProbesPerPass, ssh.Runner.Calls.Count);
        await ssh.Environments.DiscoverAsync(new[] { EnvA });
        Assert.Equal(count, ssh.Runner.Calls.Count);
        Assert.All(Enumerable.Range(0, count), i => Assert.Equal(1, ssh.Runner.Probes($"box{i}")));
    }

    [Fact]
    public async Task DiscoveryStopsOnceEverythingWantedTurnedUp()
    {
        await using var ssh = new Ssh();
        ssh.Machine("vps", EnvA);
        for (int i = 0; i < 20; i++) ssh.Machine($"box{i}", null);

        Assert.Equal(new T3SshTarget("vps"), (await ssh.Environments.DiscoverAsync(new[] { EnvA }))[EnvA]);
        Assert.True(ssh.Runner.Calls.Count < 21, $"{ssh.Runner.Calls.Count} machines asked");
    }

    [Fact]
    public async Task ResettingDiscoveryAsksEveryMachineAgain()
    {
        await using var ssh = new Ssh();
        ssh.Machine("box", null);
        await ssh.Environments.DiscoverAsync(new[] { EnvA });
        await ssh.Environments.DiscoverAsync(new[] { EnvA });
        Assert.Equal(1, ssh.Runner.Probes("box"));

        ssh.Environments.ResetDiscovery();
        ssh.Runner.Machines["box"].EnvironmentId = EnvA;
        Assert.Equal(new T3SshTarget("box"), (await ssh.Environments.DiscoverAsync(new[] { EnvA }))[EnvA]);
        Assert.Equal(2, ssh.Runner.Probes("box"));
    }

    [Fact]
    public void TheMachineAnEnvironmentWasFoundOnIsSavedWithIt()
    {
        var env = new T3Environment { Id = "a", Address = "http://127.0.0.1:41000/mcp", Label = "A", T3SshMachine = new T3SshTarget("box", "me", 2222) };
        string json = JsonSerializer.Serialize(env);
        Assert.DoesNotContain("Destination", json);
        Assert.DoesNotContain("IsValid", json);
        Assert.Equal(new T3SshTarget("box", "me", 2222), JsonSerializer.Deserialize<T3Environment>(json)!.T3SshMachine);
        Assert.Equal(env.T3SshMachine, env.Clone().T3SshMachine);
        Assert.Null(JsonSerializer.Deserialize<T3Environment>("""{"Id":"b","Address":"https://b.example/mcp"}""")!.T3SshMachine);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(10)) return false;
            await Task.Delay(20);
        }
        return true;
    }
}
