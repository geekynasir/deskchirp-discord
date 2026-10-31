using System.Runtime.InteropServices;
using System.Text;
using DeveloperPresence.Core.Models;

namespace DeveloperPresence.Core.Foreground;

/// <summary>
/// Conservative foreground detector that identifies the active developer application
/// solely by its exact executable image name.
///
/// Privacy Guarantees:
/// - NEVER queries window titles (GetWindowText is omitted entirely).
/// - NEVER queries process command-line arguments.
/// - NEVER accesses conversation transcripts, file contents, or screen pixels.
/// - Conservative defaults: only matches explicitly mapped binaries (T3 Code, Claude Desktop, Codex Desktop).
/// </summary>
public sealed class ForegroundDetector : IForegroundDetector
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private readonly Func<string?>? _processNameOverride;
    private readonly Func<SupportedApp, bool>? _runningAppOverride;

    /// <summary>
    /// Default constructor for production use on Windows.
    /// </summary>
    public ForegroundDetector() : this(null)
    {
    }

    /// <summary>
    /// Test constructor allowing injection of simulated foreground process names.
    /// </summary>
    public ForegroundDetector(Func<string?>? processNameOverride, Func<SupportedApp, bool>? runningAppOverride = null)
    {
        _processNameOverride = processNameOverride;
        _runningAppOverride = runningAppOverride;
    }

    public SupportedApp DetectForegroundApp(IReadOnlyDictionary<string, SupportedApp> mappings)
    {
        string? exeName = GetActiveProcessImageName();
        if (string.IsNullOrWhiteSpace(exeName))
        {
            return SupportedApp.None;
        }

        string normalized = exeName.Trim().ToLowerInvariant();

        foreach (var (mappedExe, app) in mappings)
        {
            if (string.Equals(normalized, mappedExe.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
            {
                return app;
            }
        }

        return SupportedApp.None;
    }

    public bool IsAppRunning(SupportedApp app, IReadOnlyDictionary<string, SupportedApp> mappings)
    {
        if (_runningAppOverride != null) return _runningAppOverride(app);
        if (_processNameOverride != null) return DetectForegroundApp(mappings) == app;
        if (!OperatingSystem.IsWindows()) return false;
        // Only compare exact program names; no titles, arguments, paths or window contents.
        var processes = System.Diagnostics.Process.GetProcesses();
        try
        {
            return processes.Any(process => {
                try { return mappings.Any(pair => pair.Value == app && string.Equals(pair.Key, process.ProcessName + ".exe", StringComparison.OrdinalIgnoreCase)); }
                catch { return false; }
            });
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public string? GetActiveProcessImageName()
    {
        if (_processNameOverride != null)
        {
            return _processNameOverride();
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        IntPtr hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
        {
            return null;
        }

        uint threadId = GetWindowThreadProcessId(hWnd, out uint processId);
        if (processId == 0)
        {
            return null;
        }

        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (QueryFullProcessImageName(hProcess, 0, buffer, ref size))
            {
                string fullPath = buffer.ToString();
                return Path.GetFileName(fullPath);
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }

        return null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
