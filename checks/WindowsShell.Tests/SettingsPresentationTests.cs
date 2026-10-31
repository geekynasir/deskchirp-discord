using DeveloperPresence.App.ViewModels;
using DeveloperPresence.Core.Discord;
using DeveloperPresence.Core.Foreground;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Services;
using Xunit;

public class SettingsPresentationTests
{
    [Fact]
    public async Task SavedPrivacyChoicesReachTheControlsAfterAsyncLoad()
    {
        var settings = new SettingsStub();
        await using var discord = new DiscordIpcClient();
        await using var coordinator = new PresenceCoordinator(settings, discord, new ForegroundDetector(() => null));
        using var model = new MainViewModel(settings, coordinator);

        Assert.False(model.EnableSharing);
        settings.Load(new PresenceSettings
        {
            EnableSharing = true, IsPaused = true, DiscordClientId = "123456789012345678",
            ProcessMappings = new() { ["custom.exe"] = SupportedApp.Claude }
        });

        Assert.True(model.EnableSharing);
        Assert.True(model.IsPaused);
        Assert.Equal("123456789012345678", model.DiscordClientId);
        Assert.Single(model.ProcessMappings);
        Assert.Equal("custom.exe", model.ProcessMappings[0].ExecutableName);
    }

    [Fact]
    public async Task MappingInputRejectsPathsAndPersistsExactExecutableNames()
    {
        var settings = new SettingsStub();
        await using var discord = new DiscordIpcClient();
        await using var coordinator = new PresenceCoordinator(settings, discord, new ForegroundDetector(() => null));
        using var model = new MainViewModel(settings, coordinator);
        int count = model.ProcessMappings.Count;

        model.NewExecutableName = @"C:\private\app.exe";
        model.AddMappingCommand.Execute(null);
        Assert.Equal(count, model.ProcessMappings.Count);

        model.NewExecutableName = "custom-app";
        model.SelectedAppIndex = 1;
        model.AddMappingCommand.Execute(null);
        Assert.Equal(SupportedApp.Claude, settings.Settings.ProcessMappings["custom-app.exe"]);
    }

    private sealed class SettingsStub : ISettingsService
    {
        public PresenceSettings Settings { get; private set; } = new();
        public event Action<PresenceSettings>? SettingsChanged;
        public void Load(PresenceSettings settings) { Settings = settings; SettingsChanged?.Invoke(settings); }
        public Task LoadSettingsAsync() => Task.CompletedTask;
        public Task SaveSettingsAsync() => Task.CompletedTask;
        public Task UpdateSettingsAsync(Action<PresenceSettings> update)
        {
            update(Settings);
            SettingsChanged?.Invoke(Settings);
            return Task.CompletedTask;
        }
    }
}
