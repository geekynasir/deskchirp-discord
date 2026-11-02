using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DeveloperPresence.Core.T3.Desktop;

/// <summary>A machine to reach with the user's own ssh, by a name from their SSH config or tailnet.</summary>
public sealed record T3SshTarget(string Host, string? User = null, int? Port = null)
{
    /// <summary>What ssh is given as the destination, for example ubuntu@mumbai-vps.</summary>
    public string Destination => string.IsNullOrEmpty(User) ? Host : $"{User}@{Host}";

    /// <summary>Destinations go to ssh after "--", and are still kept to plain names so none reads as an option.</summary>
    public bool IsValid =>
        IsPlainName(Host, allowColon: true) && (User == null || IsPlainName(User, allowColon: false)) && Port is null or (> 0 and < 65536);

    private static bool IsPlainName(string value, bool allowColon) =>
        value.Length is > 0 and <= 253 && value[0] is not ('-' or '.') &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' || (allowColon && c == ':'));

    public override string ToString() => Port is { } port ? $"{Destination}:{port}" : Destination;
}

public sealed record T3SshResult(int ExitCode, string Stdout)
{
    public const int TimedOut = -1;

    /// <summary>ssh itself failed: the host didn't answer, or it wanted a password or to trust a new host key.</summary>
    public bool Unreachable => ExitCode is 255 or TimedOut;
}

/// <summary>A running "ssh -N -L" forward.</summary>
public interface IT3SshForward : IDisposable
{
    /// <summary>Completes when ssh exits.</summary>
    Task Exited { get; }
}

/// <summary>Runs the user's own ssh. It never prompts: hosts must sign in with keys or an agent.</summary>
public interface IT3SshRunner
{
    /// <summary>Runs a POSIX shell script on the host with "sh -s". Stdout is cut at 64 KB.</summary>
    Task<T3SshResult> RunScriptAsync(T3SshTarget target, string script, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>Forwards 127.0.0.1:<paramref name="localPort"/> here to <paramref name="remoteHost"/>:<paramref name="remotePort"/> on the host.</summary>
    IT3SshForward StartForward(T3SshTarget target, int localPort, string remoteHost, int remotePort);
}

/// <summary>
/// Windows OpenSSH, or ssh on PATH, with the user's own config, keys and known hosts. BatchMode
/// means a host that would ask for a password, or to trust a new key, simply fails.
/// </summary>
public sealed class T3SshRunner : IT3SshRunner
{
    public const int MaxOutputChars = 64 * 1024;
    private static readonly Lazy<KillOnCloseJob?> Job = new(KillOnCloseJob.TryCreate);

    public static string SshPath()
    {
        if (!OperatingSystem.IsWindows()) return "ssh";
        string bundled = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
        return File.Exists(bundled) ? bundled : "ssh.exe";
    }

    internal static List<string> BaseArgs(T3SshTarget target)
    {
        var args = new List<string>
        {
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=8",
            "-o", "ControlMaster=no",
            "-o", "ControlPath=none",
        };
        if (target.Port is { } port) { args.Add("-p"); args.Add(port.ToString(CultureInfo.InvariantCulture)); }
        return args;
    }

    internal static List<string> ScriptArgs(T3SshTarget target)
    {
        var args = BaseArgs(target);
        args.AddRange(new[] { "-T", "--", target.Destination, "sh -s" });
        return args;
    }

    internal static List<string> ForwardArgs(T3SshTarget target, int localPort, string remoteHost, int remotePort)
    {
        string host = remoteHost.Contains(':') ? $"[{remoteHost}]" : remoteHost;
        var args = BaseArgs(target);
        args.AddRange(new[]
        {
            "-o", "ExitOnForwardFailure=yes",
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=3",
            "-n", "-N",
            "-L", string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{localPort}:{host}:{remotePort}"),
            "--", target.Destination
        });
        return args;
    }

    public async Task<T3SshResult> RunScriptAsync(T3SshTarget target, string script, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!target.IsValid) return new T3SshResult(255, string.Empty);
        using var process = Start(ScriptArgs(target), redirectInput: true);
        if (process == null) return new T3SshResult(255, string.Empty);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
            // Scripts use LF line endings whatever this computer uses.
            await process.StandardInput.WriteAsync(script.Replace("\r\n", "\n").AsMemory(), deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            string output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return new T3SshResult(process.ExitCode, output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            return new T3SshResult(T3SshResult.TimedOut, string.Empty);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        catch (IOException)
        {
            // ssh exited before reading the script.
            Kill(process);
            return new T3SshResult(255, string.Empty);
        }
    }

    public IT3SshForward StartForward(T3SshTarget target, int localPort, string remoteHost, int remotePort) =>
        new Forward(target.IsValid ? Start(ForwardArgs(target, localPort, remoteHost, remotePort), redirectInput: false) : null);

    private static Process? Start(List<string> args, bool redirectInput)
    {
        var info = new ProcessStartInfo(SshPath())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        try
        {
            var process = Process.Start(info);
            if (process != null) Job.Value?.Add(process);
            return process;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return null; // No ssh on this computer.
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            // Keep draining past the limit so ssh never blocks on a full pipe.
            if (text.Length < MaxOutputChars) text.Append(buffer, 0, Math.Min(read, MaxOutputChars - text.Length));
        }
        return text.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private sealed class Forward : IT3SshForward
    {
        private readonly Process? _process;

        public Forward(Process? process)
        {
            _process = process;
            if (process == null) { Exited = Task.CompletedTask; return; }
            // Nothing is read from a forward; drain both pipes so ssh never blocks writing to them.
            _ = DrainAsync(process.StandardOutput);
            _ = DrainAsync(process.StandardError);
            Exited = process.WaitForExitAsync();
        }

        public Task Exited { get; }

        private static async Task DrainAsync(StreamReader reader)
        {
            try { await ReadBoundedAsync(reader, CancellationToken.None).ConfigureAwait(false); } catch { }
        }

        public void Dispose()
        {
            if (_process == null) return;
            Kill(_process);
            _process.Dispose();
        }
    }

    /// <summary>A Windows job that ends every ssh Deskchirp started when Deskchirp exits, even if it crashes.</summary>
    private sealed class KillOnCloseJob
    {
        private const int ExtendedLimitInformationClass = 9;
        private const uint LimitKillOnJobClose = 0x2000;
        private readonly IntPtr _handle;

        private KillOnCloseJob(IntPtr handle) => _handle = handle;

        public static KillOnCloseJob? TryCreate()
        {
            if (!OperatingSystem.IsWindows()) return null;
            IntPtr handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) return null;
            var info = new ExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = LimitKillOnJobClose;
            int size = Marshal.SizeOf<ExtendedLimitInformation>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, buffer, (uint)size)) return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
            // The handle stays open for the life of Deskchirp; Windows closes it, and ends the job, on exit.
            return new KillOnCloseJob(handle);
        }

        public void Add(Process process)
        {
            try { AssignProcessToJobObject(_handle, process.Handle); } catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
    }
}
