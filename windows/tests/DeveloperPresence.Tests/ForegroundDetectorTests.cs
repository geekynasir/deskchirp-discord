using DeveloperPresence.Core.Foreground;
using DeveloperPresence.Core.Models;
using Xunit;

namespace DeveloperPresence.Tests;

public class ForegroundDetectorTests
{
    private readonly Dictionary<string, SupportedApp> _defaultMappings = PresenceSettings.CreateDefaultMappings();

    [Theory]
    [InlineData("t3code.exe", SupportedApp.T3Code)]
    [InlineData("T3CODE.EXE", SupportedApp.T3Code)]
    [InlineData("claude.exe", SupportedApp.Claude)]
    [InlineData("Claude.EXE", SupportedApp.Claude)]
    [InlineData("codex.exe", SupportedApp.Codex)]
    [InlineData("Codex Desktop.exe", SupportedApp.Codex)]
    public void DetectForegroundApp_MatchesRecognizedBinaries(string processName, SupportedApp expected)
    {
        var detector = new ForegroundDetector(() => processName);
        var detected = detector.DetectForegroundApp(_defaultMappings);
        Assert.Equal(expected, detected);
    }

    [Theory]
    [InlineData("t3.exe")] // CLI binary excluded from default mappings
    [InlineData("chrome.exe")]
    [InlineData("code.exe")]
    [InlineData("notepad.exe")]
    [InlineData("explorer.exe")]
    [InlineData("powershell.exe")]
    public void DetectForegroundApp_ReturnsNoneForUnsupportedApps(string processName)
    {
        var detector = new ForegroundDetector(() => processName);
        var detected = detector.DetectForegroundApp(_defaultMappings);
        Assert.Equal(SupportedApp.None, detected);
    }

    [Theory]
    [InlineData("T3 Code (Beta).exe")]
    [InlineData("T3 Code (Nightly).exe")]
    public void RecognizesUpstreamDesktopProductExecutableNames(string executable)
    {
        var detector = new ForegroundDetector(() => executable);
        Assert.Equal(SupportedApp.T3Code, detector.DetectForegroundApp(PresenceSettings.CreateDefaultMappings()));
    }

    [Fact]
    public void DetectForegroundApp_ReturnsNoneWhenNoProcessIsActive()
    {
        var detector = new ForegroundDetector(() => null);
        var detected = detector.DetectForegroundApp(_defaultMappings);
        Assert.Equal(SupportedApp.None, detected);
    }
}
