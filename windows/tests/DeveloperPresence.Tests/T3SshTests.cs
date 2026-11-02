using System.Diagnostics;
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

    [Fact]
    public void CredentialIsReadFromT3sIndentedJson()
    {
        var origin = new Uri("http://127.0.0.1:41000/mcp");
        Assert.Equal("SYNTHETICCODE42", T3SshEnvironments.ParseCredential("motd {braces}\n" + PrettyPairing + "\n", origin));
        Assert.Null(T3SshEnvironments.ParseCredential("""{ "credential": "no spaces allowed" }""", origin));
        Assert.Null(T3SshEnvironments.ParseCredential("nothing", origin));
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
        File.WriteAllText(Path.Combine(t3Home, "userdata", "server-runtime.json"),
            $"{{\"version\":1,\"pid\":{Environment.ProcessId},\"port\":3773,\"host\":\"127.0.0.1\",\"origin\":\"http://127.0.0.1:3773\"}}");
        string fakeT3 = Path.Combine(bin, "t3");
        File.WriteAllText(fakeT3, "#!/bin/sh\nprintf '%s\\n' \"$*\" > \"$HOME/args\"\ncat <<'JSON'\n" + PrettyPairing + "\nJSON\n");
        File.SetUnixFileMode(fakeT3, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (code, output) = await RunShAsync(T3SshEnvironments.ProbeScript(), home, bin);
        Assert.Equal(0, code);
        Assert.Equal(new T3SshProbe(EnvA, "127.0.0.1", 3773), T3SshEnvironments.ParseProbe(output));

        (code, output) = await RunShAsync(T3SshEnvironments.MintScript(EnvA, new[] { "orchestration:read" }), home, bin);
        Assert.Equal(0, code);
        Assert.Equal("SYNTHETICCODE42", T3SshEnvironments.ParseCredential(output, new Uri("http://127.0.0.1:41000/mcp")));
        Assert.Equal($"auth pairing create --base-dir {t3Home} --scope orchestration:read --ttl 5m --label Deskchirp --json",
            File.ReadAllText(Path.Combine(home, "args")).Trim());

        (code, _) = await RunShAsync(T3SshEnvironments.MintScript(EnvB, new[] { "orchestration:read" }), home, bin);
        Assert.Equal(4, code);

        File.Delete(fakeT3);
        (code, _) = await RunShAsync(T3SshEnvironments.MintScript(EnvA, new[] { "orchestration:read" }), home, bin);
        Assert.Equal(3, code);

        // A runtime file left behind by a server that exited counts as not running.
        File.WriteAllText(Path.Combine(t3Home, "userdata", "server-runtime.json"), "{\"version\":1,\"pid\":999999999,\"port\":3773}");
        (code, output) = await RunShAsync(T3SshEnvironments.ProbeScript(), home, bin);
        Assert.False(T3SshEnvironments.ParseProbe(output)!.Running);
        (code, _) = await RunShAsync(T3SshEnvironments.MintScript(EnvA, new[] { "orchestration:read" }), home, bin);
        Assert.Equal(5, code);
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

    private sealed class FakeMachine
    {
        public string? EnvironmentId { get; set; }
        public int? Port { get; set; } = 3773;
        public bool Down { get; set; }
        public int MintExit { get; set; }
    }

    private sealed class FakeForward : IT3SshForward
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeForward(string destination, int localPort, string remote) { Destination = destination; LocalPort = localPort; Remote = remote; }
        public string Destination { get; }
        public int LocalPort { get; }
        public string Remote { get; }
        public bool Disposed { get; private set; }
        public Task Exited => _exited.Task;
        public void Exit() => _exited.TrySetResult();
        public void Dispose() { Disposed = true; _exited.TrySetResult(); }
    }

    private sealed class FakeRunner : IT3SshRunner
    {
        public Dictionary<string, FakeMachine> Machines { get; } = new();
        public List<(string Destination, string Kind)> Calls { get; } = new();
        public List<FakeForward> Forwards { get; } = new();

        public Task<T3SshResult> RunScriptAsync(T3SshTarget target, string script, TimeSpan timeout, CancellationToken ct = default)
        {
            bool mint = script.Contains("auth pairing create", StringComparison.Ordinal);
            lock (Calls) Calls.Add((target.Destination, mint ? "mint" : "probe"));
            if (!Machines.TryGetValue(target.Destination, out var machine) || machine.Down) return Task.FromResult(new T3SshResult(255, string.Empty));
            if (!mint)
            {
                string runtime = machine.Port is { } port ? $"{{\"version\":1,\"pid\":42,\"port\":{port}}}" : string.Empty;
                return Task.FromResult(new T3SshResult(0, $"motd\n{T3SshEnvironments.ProbeMarker}\nid={machine.EnvironmentId}\nruntime={runtime}\n"));
            }
            if (!script.Contains($"'{machine.EnvironmentId}' ]", StringComparison.Ordinal)) return Task.FromResult(new T3SshResult(4, string.Empty));
            if (machine.Port == null) return Task.FromResult(new T3SshResult(5, string.Empty));
            return Task.FromResult(new T3SshResult(machine.MintExit, machine.MintExit == 0 ? PrettyPairing : string.Empty));
        }

        public IT3SshForward StartForward(T3SshTarget target, int localPort, string remoteHost, int remotePort)
        {
            var forward = new FakeForward(target.Destination, localPort, $"{remoteHost}:{remotePort}");
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

    private sealed class FakeHosts : IT3SshHostSource
    {
        public List<T3SshTarget> Targets { get; } = new();
        public Task<IReadOnlyList<T3SshTarget>> CandidatesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<T3SshTarget>>(Targets.ToList());
    }

    private sealed class Ssh : IAsyncDisposable
    {
        public FakeRunner Runner { get; } = new();
        public FakeHosts Hosts { get; } = new();
        public HashSet<int> Busy { get; } = new();
        public DateTimeOffset Now { get; set; } = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        public T3SshEnvironments Environments { get; }

        public Ssh()
        {
            Environments = new T3SshEnvironments(Runner, Hosts,
                portFree: port => !Busy.Contains(port) && Runner.Live(port) == null,
                listening: (port, _) => Task.FromResult(Runner.Live(port) != null),
                clock: () => Now);
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

        var found = await ssh.Environments.DiscoverAsync(new[] { EnvA });
        Assert.Equal(new Dictionary<string, T3SshTarget> { [EnvA] = new("vps") }, found);

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

        var result = await ssh.Environments.MintCodeAsync(EnvA, new T3SshTarget("vps"), new[] { "orchestration:read" });

        Assert.Equal(new T3DesktopPairingResult(T3DesktopPairingOutcome.Success, "SYNTHETICCODE42"), result);
        var forward = Assert.Single(ssh.Runner.Forwards);
        Assert.Equal(("vps", ssh.PortOf(EnvA), "127.0.0.1:3900"), (forward.Destination, forward.LocalPort, forward.Remote));
        Assert.Equal(new[] { "probe", "mint" }, ssh.Runner.Calls.Select(c => c.Kind));
        Assert.DoesNotContain("SYNTHETIC", result.ToString());

        // Once nothing retains it, the tunnel closes.
        ssh.Environments.Retain(new Dictionary<string, T3SshTarget>());
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

        var result = await ssh.Environments.MintCodeAsync(EnvA, new T3SshTarget(machine), new[] { "orchestration:read" });

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Code);
        Assert.Equal(machine == "no-t3" ? 1 : 0, ssh.Runner.Calls.Count(c => c.Kind == "mint"));
    }

    [Fact]
    public async Task RefusesScopesDeskchirpDoesntAskFor()
    {
        await using var ssh = new Ssh();
        ssh.Machine("vps", EnvA);
        var result = await ssh.Environments.MintCodeAsync(EnvA, new T3SshTarget("vps"), new[] { "orchestration:write" });
        Assert.Equal(T3DesktopPairingOutcome.Failed, result.Outcome);
        Assert.Empty(ssh.Runner.Calls);
    }

    [Fact]
    public async Task ReopensTheTunnelWhenSshDrops()
    {
        await using var ssh = new Ssh();
        ssh.Machine("vps", EnvA);
        ssh.Environments.Retain(new Dictionary<string, T3SshTarget> { [EnvA] = new("vps") });
        Assert.True(await WaitAsync(() => ssh.Runner.Live(ssh.PortOf(EnvA)) != null));

        ssh.Runner.Live(ssh.PortOf(EnvA))!.Exit();
        // The next attempt waits a few seconds; minting wakes it.
        var result = await ssh.Environments.MintCodeAsync(EnvA, new T3SshTarget("vps"), new[] { "orchestration:read" });
        Assert.Equal(T3DesktopPairingOutcome.Success, result.Outcome);
        Assert.Equal(2, ssh.Runner.Forwards.Count);
    }

    [Fact]
    public async Task ATunnelWhoseMachineNowRunsAnotherEnvironmentReportsItAndAsksAgain()
    {
        await using var ssh = new Ssh();
        var vps = ssh.Machine("vps", EnvA);
        var laptop = ssh.Machine("laptop", null);
        Assert.Equal(new T3SshTarget("vps"), (await ssh.Environments.DiscoverAsync(new[] { EnvA }))[EnvA]);

        vps.EnvironmentId = EnvB;
        laptop.EnvironmentId = EnvA;
        ssh.Environments.Retain(new Dictionary<string, T3SshTarget> { [EnvA] = new("vps") });
        Assert.True(await WaitAsync(() => ssh.Environments.TakeMovedEnvironments() is { Count: > 0 } moved && moved.Single() == EnvA));
        Assert.Empty(ssh.Environments.TakeMovedEnvironments());

        // Machines that answered before are asked again straight away.
        Assert.Equal(new T3SshTarget("laptop"), (await ssh.Environments.DiscoverAsync(new[] { EnvA }))[EnvA]);
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
