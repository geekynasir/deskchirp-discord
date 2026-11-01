using System.Runtime.InteropServices;
using DeveloperPresence.App.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace DeveloperPresence.App;

/// <summary>
/// Replaces the XAML-generated entry point so Velopack's install, update and uninstall
/// hooks run (and exit) before any window or single-instance lock is created.
/// </summary>
public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => LegacyInstall.Remove())
            .Run();

        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
