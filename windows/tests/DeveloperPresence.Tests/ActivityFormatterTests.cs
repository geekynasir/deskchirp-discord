using System.Text.Json;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;
using Xunit;

namespace DeveloperPresence.Tests;

public class ActivityFormatterTests
{
    [Fact]
    public void FormatActivity_ReturnsNull_WhenSharingIsDisabled()
    {
        var activity = ActivityFormatter.FormatActivity(
            SupportedApp.T3Code,
            enableSharing: false,
            isPaused: false);

        Assert.Null(activity);
    }

    [Fact]
    public void FormatActivity_ReturnsNull_WhenPaused()
    {
        var activity = ActivityFormatter.FormatActivity(
            SupportedApp.T3Code,
            enableSharing: true,
            isPaused: true);

        Assert.Null(activity);
    }

    [Fact]
    public void FormatActivity_ReturnsNull_WhenAppIsNone()
    {
        var activity = ActivityFormatter.FormatActivity(
            SupportedApp.None,
            enableSharing: true,
            isPaused: false);

        Assert.Null(activity);
    }

    [Fact]
    public void FormatActivity_ReturnsExactHeadlineForT3Code_DetailsOnly()
    {
        var activity = ActivityFormatter.FormatActivity(
            SupportedApp.T3Code,
            enableSharing: true,
            isPaused: false);

        Assert.NotNull(activity);
        Assert.Equal("Using T3 Code", activity.Details);
        Assert.Null(activity.State);

        // Strict serialized details-only policy: no timestamps, no assets, no fake states
        string json = JsonSerializer.Serialize(activity);
        Assert.Equal("{\"details\":\"Using T3 Code\"}", json);
    }

    [Fact]
    public void FormatActivity_ReturnsExactHeadlineForClaude_DetailsOnly()
    {
        var activity = ActivityFormatter.FormatActivity(
            SupportedApp.Claude,
            enableSharing: true,
            isPaused: false);

        Assert.NotNull(activity);
        Assert.Equal("Using Claude", activity.Details);
        Assert.Null(activity.State);

        string json = JsonSerializer.Serialize(activity);
        Assert.Equal("{\"details\":\"Using Claude\"}", json);
    }

    [Fact]
    public void FormatActivity_ReturnsExactHeadlineForCodex_DetailsOnly()
    {
        var activity = ActivityFormatter.FormatActivity(
            SupportedApp.Codex,
            enableSharing: true,
            isPaused: false);

        Assert.NotNull(activity);
        Assert.Equal("Using Codex", activity.Details);
        Assert.Null(activity.State);

        string json = JsonSerializer.Serialize(activity);
        Assert.Equal("{\"details\":\"Using Codex\"}", json);
    }

    [Theory]
    [InlineData(SupportedApp.T3Code, "Using T3 Code")]
    [InlineData(SupportedApp.Claude, "Using Claude")]
    [InlineData(SupportedApp.Codex, "Using Codex")]
    [InlineData(SupportedApp.None, "None / Cleared")]
    public void GetHeadline_ReturnsStrictWhitelistedStringsOnly(SupportedApp app, string expected)
    {
        Assert.Equal(expected, ActivityFormatter.GetHeadline(app));
    }
}
