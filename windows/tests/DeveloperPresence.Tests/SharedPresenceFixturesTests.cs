using System.Text;
using System.Text.Json;
using DeveloperPresence.Core.Discord;
using DeveloperPresence.Core.Foreground;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;
using DeveloperPresence.Core.Services;
using Xunit;

namespace DeveloperPresence.Tests;

public class SharedPresenceFixturesTests
{
    private static string FindSpecPath(string filename)
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "spec", filename);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir)
            {
                break;
            }
            dir = parent;
        }
        throw new FileNotFoundException($"Spec file '{filename}' could not be located.");
    }

    [Fact]
    public async Task PresenceCases_ExerciseActualCoordinatorAndFormatterGuards()
    {
        string path = FindSpecPath("presence-cases.json");
        string json = await File.ReadAllTextAsync(path);
        using var doc = JsonDocument.Parse(json);

        var cases = doc.RootElement.GetProperty("cases");

        foreach (var c in cases.EnumerateArray())
        {
            string caseId = c.GetProperty("id").GetString()!;
            var input = c.GetProperty("input");
            bool enabled = input.GetProperty("enabled").GetBoolean();
            bool paused = input.GetProperty("paused").GetBoolean();
            bool suspended = input.GetProperty("suspended").GetBoolean();
            string applicationId = input.GetProperty("applicationId").GetString()!;
            string? focusedAppString = input.TryGetProperty("focusedApp", out var fa) && fa.ValueKind == JsonValueKind.String
                ? fa.GetString()
                : null;

            SupportedApp app = focusedAppString switch
            {
                "t3" => SupportedApp.T3Code,
                "claude" => SupportedApp.Claude,
                "codex" => SupportedApp.Codex,
                _ => SupportedApp.None
            };

            string? exeName = app switch
            {
                SupportedApp.T3Code => "t3code.exe",
                SupportedApp.Claude => "claude.exe",
                SupportedApp.Codex => "codex.exe",
                _ => "notepad.exe"
            };

            var expectedProp = c.GetProperty("expected");
            string? expectedDetails = expectedProp.ValueKind == JsonValueKind.Object &&
                                      expectedProp.TryGetProperty("details", out var detProp)
                ? detProp.GetString()
                : null;

            // 1. Test ActivityFormatter guard
            var formattedActivity = ActivityFormatter.FormatActivity(app, enabled, paused);
            if (!enabled || paused || app == SupportedApp.None)
            {
                Assert.Null(formattedActivity);
            }
            else if (expectedDetails != null)
            {
                Assert.NotNull(formattedActivity);
                Assert.Equal(expectedDetails, formattedActivity.Details);
                string serializedActivity = JsonSerializer.Serialize(formattedActivity);
                Assert.Equal($"{{\"details\":\"{expectedDetails}\"}}", serializedActivity);
            }

            // 2. Test PresenceCoordinator end-to-end gating
            var settingsService = new MockSettingsService();
            settingsService.Settings.EnableSharing = enabled;
            settingsService.Settings.IsPaused = paused;
            settingsService.Settings.DiscordClientId = applicationId;

            var discord = new MockDiscordClient();
            var detector = new ForegroundDetector(() => exeName);

            var coordinator = new PresenceCoordinator(settingsService, discord, detector);

            if (suspended)
            {
                coordinator.OnSessionLock();
            }

            await coordinator.EvaluateAndDispatchAsync(CancellationToken.None);

            if (expectedDetails == null)
            {
                Assert.True(
                    discord.SendActivityCallCount == 0,
                    $"Case {caseId}: expected no activity sent, but got {discord.SendActivityCallCount}");
                Assert.Equal("None / Cleared", coordinator.CurrentState.OutgoingHeadline);
            }
            else
            {
                Assert.True(
                    discord.SendActivityCallCount == 1,
                    $"Case {caseId}: expected 1 activity sent, but got {discord.SendActivityCallCount}");
                Assert.Equal(expectedDetails, coordinator.CurrentState.OutgoingHeadline);
                Assert.Equal(app, discord.LastSentApp);
            }
        }
    }

    [Fact]
    public async Task IpcFrames_MatchesSpecificationWireFormat()
    {
        string path = FindSpecPath("ipc-frames.json");
        string json = await File.ReadAllTextAsync(path);
        using var doc = JsonDocument.Parse(json);

        var frames = doc.RootElement.GetProperty("frames");

        foreach (var frameElem in frames.EnumerateArray())
        {
            string frameId = frameElem.GetProperty("id").GetString()!;
            int opcode = frameElem.GetProperty("opcode").GetInt32();
            string frameJson = frameElem.GetProperty("json").GetString()!;
            string expectedHex = frameElem.GetProperty("wireHex").GetString()!;

            // 1. Validate Packing wire representation
            byte[] packed = DiscordFrameParser.Pack(opcode, frameJson);
            string actualHex = Convert.ToHexString(packed).ToLowerInvariant();
            Assert.True(
                string.Equals(expectedHex, actualHex, StringComparison.OrdinalIgnoreCase),
                $"Frame {frameId}: wire hex mismatch.\nExpected: {expectedHex}\nActual:   {actualHex}");

            // 2. Validate Unpacking / Parsing incoming bytes
            var parser = new DiscordFrameParser();
            byte[] wireBytes = Convert.FromHexString(expectedHex);
            var parsedFrames = parser.ProcessIncomingBytes(wireBytes, wireBytes.Length);

            Assert.Single(parsedFrames);
            Assert.Equal(opcode, parsedFrames[0].Opcode);
            Assert.Equal(frameJson, parsedFrames[0].GetPayloadAsString());
        }
    }
}
