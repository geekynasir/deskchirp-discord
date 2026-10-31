namespace DeveloperPresence.Core.Models;

/// <summary>
/// Supported developer applications for presence detection.
/// </summary>
public enum SupportedApp
{
    /// <summary>
    /// No supported developer application is currently active or focused.
    /// </summary>
    None = 0,

    /// <summary>
    /// T3 Code (desktop application).
    /// </summary>
    T3Code = 1,

    /// <summary>
    /// Claude Desktop.
    /// </summary>
    Claude = 2,

    /// <summary>
    /// Codex Desktop.
    /// </summary>
    Codex = 3
}
