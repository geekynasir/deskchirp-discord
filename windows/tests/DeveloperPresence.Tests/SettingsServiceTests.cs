using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Services;
using Xunit;

namespace DeveloperPresence.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void DefaultSettings_HaveSharingDisabledAndPublisherApplicationId()
    {
        var settings = new PresenceSettings();

        // Master sharing must be OFF by default
        Assert.False(settings.EnableSharing);
        Assert.False(settings.IsPaused);
        Assert.Equal(PresenceSettings.DefaultDiscordApplicationId, settings.DiscordClientId);
        Assert.True(settings.IsValidDiscordClientId());
        Assert.Equal(5, settings.UpdateIntervalSeconds);
    }

    [Fact]
    public async Task ArtworkUpgradeRunsOnceAndRespectsLaterOptOut()
    {
        string path = Path.GetTempFileName();
        try {
            await File.WriteAllTextAsync(path, "{\"ShowAppIcons\":false}");
            var service = new SettingsService(path);
            await service.LoadSettingsAsync();
            Assert.True(service.Settings.ShowAppIcons);
            await service.UpdateSettingsAsync(settings => settings.ShowAppIcons = false);
            await service.LoadSettingsAsync();
            Assert.False(service.Settings.ShowAppIcons);
        } finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("", PresenceSettings.DefaultDiscordApplicationId)]
    [InlineData("123456789012345678", "123456789012345678")]
    public async Task ExistingSettingsUpgradeBlankIdAndPreserveSharingChoices(string savedId, string expected)
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new PresenceSettings
                { DiscordClientId = savedId, EnableSharing = false, IsPaused = true }));
            var service = new SettingsService(path);
            await service.LoadSettingsAsync();
            Assert.Equal(expected, service.Settings.DiscordClientId);
            Assert.False(service.Settings.EnableSharing);
            Assert.True(service.Settings.IsPaused);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("1301234567890123456", true)]
    [InlineData("123456789012345", true)]
    [InlineData("12345678901234567890", true)]
    [InlineData("123456789012345678901", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("12345", false)] // too short
    [InlineData("12345678901234567890123", false)] // too long (>20)
    [InlineData("1301234567890123456abc", false)] // non-numeric
    [InlineData(" 1301234567890123456 ", false)] // leading/trailing whitespace rejected (no trimmed mismatch wire)
    [InlineData("invalid-id", false)]
    [InlineData("١٢٣٤٥٦٧٨٩٠١٢٣٤٥", false)] // Unicode Arabic-Indic digits rejected (must be ASCII '0'-'9')
    public void IsValidDiscordClientId_ValidatesProperAsciiNumericRange(string clientId, bool expected)
    {
        var settings = new PresenceSettings { DiscordClientId = clientId };
        Assert.Equal(expected, settings.IsValidDiscordClientId());
    }

    [Fact]
    public void ProcessMappings_DefaultMappingsAreCaseInsensitive_AndExcludeCli()
    {
        var mappings = PresenceSettings.CreateDefaultMappings();

        Assert.True(mappings.ContainsKey("t3code.exe"));
        Assert.True(mappings.ContainsKey("T3CODE.EXE"));
        Assert.True(mappings.ContainsKey("Claude.exe"));
        Assert.True(mappings.ContainsKey("codex.exe"));
        Assert.False(mappings.ContainsKey("t3.exe")); // CLI excluded

        Assert.Equal(SupportedApp.T3Code, mappings["T3CODE.EXE"]);
        Assert.Equal(SupportedApp.Claude, mappings["claude.exe"]);
        Assert.Equal(SupportedApp.Codex, mappings["CODEX.EXE"]);
    }

    [Fact]
    public async Task UpdateSettingsAsync_FiresSettingsChangedImmediatelyBeforeSave()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string settingsFile = Path.Combine(tempDir, "settings.json");

        try
        {
            var service = new SettingsService(settingsFile);
            bool eventFired = false;
            bool enabledValueInEvent = false;

            service.SettingsChanged += s =>
            {
                eventFired = true;
                enabledValueInEvent = s.EnableSharing;
            };

            await service.UpdateSettingsAsync(s => s.EnableSharing = true);

            Assert.True(eventFired);
            Assert.True(enabledValueInEvent);
            Assert.True(File.Exists(settingsFile));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task LoadSettingsAsync_PreservesIntentionallyEmptyProcessMappings()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string settingsFile = Path.Combine(tempDir, "settings.json");

        try
        {
            var service = new SettingsService(settingsFile);
            await service.LoadSettingsAsync();

            // Explicitly clear all process mappings
            await service.UpdateSettingsAsync(s =>
            {
                s.ProcessMappings.Clear();
            });

            // Reload from disk in a fresh service instance
            var service2 = new SettingsService(settingsFile);
            await service2.LoadSettingsAsync();

            // Must NOT replace empty dictionary with defaults
            Assert.Empty(service2.Settings.ProcessMappings);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task SaveAndLoad_RoundtripsSuccessfullyWithoutSecrets()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string settingsFile = Path.Combine(tempDir, "settings.json");

        try
        {
            var service = new SettingsService(settingsFile);
            await service.LoadSettingsAsync();

            Assert.False(service.Settings.EnableSharing);

            await service.UpdateSettingsAsync(s =>
            {
                s.EnableSharing = true;
                s.DiscordClientId = "999888777666555444";
                s.ProcessMappings["mycustomeditor.exe"] = SupportedApp.T3Code;
            });

            // Inspect written JSON content to verify no secrets are present
            string json = await File.ReadAllTextAsync(settingsFile);
            Assert.Contains("mycustomeditor.exe", json);
            Assert.Contains("999888777666555444", json);
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);

            // Re-read with a new service instance
            var service2 = new SettingsService(settingsFile);
            await service2.LoadSettingsAsync();

            Assert.True(service2.Settings.EnableSharing);
            Assert.Equal("999888777666555444", service2.Settings.DiscordClientId);
            Assert.Equal(SupportedApp.T3Code, service2.Settings.ProcessMappings["mycustomeditor.exe"]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
