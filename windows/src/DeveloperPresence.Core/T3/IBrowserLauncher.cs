namespace DeveloperPresence.Core.T3;

/// <summary>
/// Abstraction for launching the system default browser.
/// </summary>
public interface IBrowserLauncher
{
    bool OpenBrowser(Uri url);
}

public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public bool OpenBrowser(Uri url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri)
            {
                UseShellExecute = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            return proc != null;
        }
        catch
        {
            return false;
        }
    }
}
