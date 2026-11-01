using System.Diagnostics;
using Microsoft.Win32;

namespace DeveloperPresence.App.Updates;

/// <summary>
/// Preview 3 and earlier used an Inno Setup wizard installed under
/// %LOCALAPPDATA%\Programs\Deskchirp. The one-click installer removes that copy so people
/// don't end up with two Start entries. Settings and saved sign-ins live elsewhere and stay.
/// </summary>
internal static class LegacyInstall
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{F5C10229-3D01-49BF-9655-BF2D2564B917}_is1";

    /// <summary>Runs inside Velopack's install hook, which must finish quickly and never throw.</summary>
    public static void Remove()
    {
        try
        {
            string? folder;
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                if (key?.GetValue("InstallLocation") is not string location || string.IsNullOrWhiteSpace(location)) return;
                folder = Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar);
            }
            string uninstaller = Path.Combine(folder, "unins000.exe");
            if (!File.Exists(uninstaller)) return;

            // The old copy holds the single-instance lock, so close it before the new one opens.
            foreach (var process in Process.GetProcessesByName("Deskchirp"))
            {
                using (process)
                {
                    try
                    {
                        string? path = process.MainModule?.FileName;
                        if (path == null || !string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase)) continue;
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                    catch (Exception) { /* Another user's or an exiting process. */ }
                }
            }

            using var remove = Process.Start(new ProcessStartInfo(uninstaller, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            remove?.WaitForExit(20000);
        }
        catch (Exception) { /* Leaving the old copy is better than failing setup. */ }
    }
}
