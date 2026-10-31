using DeveloperPresence.Core.Models;

namespace DeveloperPresence.Core.Services;

/// <summary>
/// Service managing persistence and retrieval of local settings in %LocalAppData%\DeveloperPresence\settings.json.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Current live settings in memory.
    /// </summary>
    PresenceSettings Settings { get; }

    /// <summary>
    /// Event raised when settings are modified and saved.
    /// </summary>
    event Action<PresenceSettings>? SettingsChanged;

    /// <summary>
    /// Loads settings from disk. If the file is missing or corrupt, applies safe defaults.
    /// Must be invoked before any Discord presence payload is transmitted.
    /// </summary>
    Task LoadSettingsAsync();

    /// <summary>
    /// Persists current settings to disk.
    /// </summary>
    Task SaveSettingsAsync();

    /// <summary>
    /// Updates live settings and persists them.
    /// </summary>
    Task UpdateSettingsAsync(Action<PresenceSettings> updateAction);
}
