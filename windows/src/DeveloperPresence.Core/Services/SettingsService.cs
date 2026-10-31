using System.Text.Json;
using DeveloperPresence.Core.Models;

namespace DeveloperPresence.Core.Services;

/// <summary>
/// Implements persistent settings storage in LocalAppData JSON format.
/// Guarantees that only non-sensitive configuration is stored (no secrets, tokens, or credentials).
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private PresenceSettings _settings = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public PresenceSettings Settings => _settings;
    public event Action<PresenceSettings>? SettingsChanged;

    public SettingsService(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            _filePath = customPath;
        }
        else
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _filePath = Path.Combine(localAppData, "DeveloperPresence", "settings.json");
        }
    }

    public async Task LoadSettingsAsync()
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(_filePath))
            {
                string json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
                var loaded = JsonSerializer.Deserialize<PresenceSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    // Ensure process mappings dictionary is case-insensitive
                    // Preserve user's intentional empty mapping if present in JSON
                    if (loaded.ProcessMappings != null)
                    {
                        var mappings = new Dictionary<string, SupportedApp>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kvp in loaded.ProcessMappings)
                        {
                            mappings[kvp.Key] = kvp.Value;
                        }
                        loaded.ProcessMappings = mappings;
                    }
                    else
                    {
                        loaded.ProcessMappings = PresenceSettings.CreateDefaultMappings();
                    }

                    if (string.IsNullOrEmpty(loaded.DiscordClientId))
                        loaded.DiscordClientId = PresenceSettings.DefaultDiscordApplicationId;
                    if (loaded.ArtworkDefaultsVersion < 1) { loaded.ShowAppIcons = true; loaded.ArtworkDefaultsVersion = 1; }
                    _settings = loaded;
                    SettingsChanged?.Invoke(_settings);
                    return;
                }
            }

            // Apply safe defaults if file does not exist or deserialized to null
            _settings = new PresenceSettings { ArtworkDefaultsVersion = 1 };
            SettingsChanged?.Invoke(_settings);
        }
        catch
        {
            // Fall back safely to default settings on read or parsing error
            _settings = new PresenceSettings { ArtworkDefaultsVersion = 1 };
            SettingsChanged?.Invoke(_settings);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveSettingsAsync()
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Atomic save via temp file replacement
            string tempFile = Path.Combine(dir ?? ".", $"{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
            string json = JsonSerializer.Serialize(_settings, JsonOptions);
            await File.WriteAllTextAsync(tempFile, json).ConfigureAwait(false);
            File.Move(tempFile, _filePath, overwrite: true);

        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task UpdateSettingsAsync(Action<PresenceSettings> updateAction)
    {
        updateAction(_settings);
        // Immediate privacy invalidation notification before disk I/O
        SettingsChanged?.Invoke(_settings);
        await SaveSettingsAsync().ConfigureAwait(false);
    }
}
