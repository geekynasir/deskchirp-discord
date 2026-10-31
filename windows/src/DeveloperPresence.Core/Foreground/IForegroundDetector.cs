using DeveloperPresence.Core.Models;

namespace DeveloperPresence.Core.Foreground;

/// <summary>
/// Abstraction for detecting the currently focused foreground developer application.
/// </summary>
public interface IForegroundDetector
{
    /// <summary>
    /// Inspects the current foreground process identity and matches it against conservative mappings.
    /// Returns SupportedApp.None if the foreground window is not a recognized developer application.
    /// Strictly NEVER queries window titles, command-line arguments, or screen pixels.
    /// </summary>
    SupportedApp DetectForegroundApp(IReadOnlyDictionary<string, SupportedApp> mappings);

    /// <summary>
    /// Returns the exact base executable file name (e.g. "t3code.exe") of the foreground window, or null.
    /// </summary>
    string? GetActiveProcessImageName();
}
