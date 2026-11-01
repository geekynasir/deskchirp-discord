using System.Text.Json;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;
using DeveloperPresence.Core.Services;
using DeveloperPresence.Core.T3;
using DeveloperPresence.Core.T3.Usage;
using Xunit;

namespace DeveloperPresence.Tests;

/// <summary>Loads spec/presence-v3-cases.json, shared with the macOS suite, and checks every case.</summary>
public class PresenceV3CasesTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    internal static string FindSpecPath(string filename)
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "spec", filename);
            if (File.Exists(candidate)) return candidate;
            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException($"Spec file '{filename}' could not be located.");
    }

    private static JsonElement Fixture()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindSpecPath("presence-v3-cases.json")));
        return doc.RootElement.Clone();
    }

    public static IEnumerable<object[]> CaseNames() =>
        Fixture().GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("name").GetString()! });

    [Fact]
    public void FixtureHasAllSharedCases()
    {
        Assert.Equal(31, Fixture().GetProperty("cases").GetArrayLength());
    }

    [Fact]
    public void CompactNumbersMatchFixture()
    {
        foreach (var pair in Fixture().GetProperty("compactNumbers").EnumerateArray())
            Assert.Equal(pair[1].GetString(), PresenceText.Compact(pair[0].GetInt64()));
    }

    [Fact]
    public void ExactNumbersMatchFixture()
    {
        foreach (var pair in Fixture().GetProperty("exactNumbers").EnumerateArray())
            Assert.Equal(pair[1].GetString(), PresenceText.Exact(pair[0].GetInt64()));
    }

    [Fact]
    public void CostsMatchFixture()
    {
        foreach (var pair in Fixture().GetProperty("costs").EnumerateArray())
            Assert.Equal(pair[1].GetString(), PresenceText.Cost(pair[0].GetDecimal()));
    }

    internal static PresenceCardResult Run(JsonElement fixture, JsonElement testCase)
    {
        long sessionStart = fixture.GetProperty("workSessionStart").GetInt64();
        long appStart = fixture.GetProperty("appShownStart").GetInt64();
        var now = DateTimeOffset.FromUnixTimeSeconds(sessionStart + 600);

        var settings = testCase.GetProperty("settings").Deserialize<PresenceSettings>(Options)!;
        settings.EnableSharing = true;
        settings.ShowT3Activity = true;
        settings.ShowAppIcons = true;
        settings.NormalizeAndMigrate();

        var drivers = fixture.GetProperty("providers").EnumerateArray().ToDictionary(
            p => p.GetProperty("providerInstanceId").GetString()!, p => p.GetProperty("driverKind").GetString()!);
        var environments = testCase.GetProperty("environments").EnumerateArray().Select(env => new T3EnvironmentSnapshot
        {
            EnvironmentId = env.GetProperty("id").GetString()!,
            Timestamp = now,
            Drivers = drivers,
            Threads = env.GetProperty("threads").Deserialize<List<T3ThreadListItem>>(Options)!,
            Projects = env.GetProperty("projects").Deserialize<List<T3ProjectListItem>>(Options)!
        }).ToList();
        var snapshot = new T3Snapshot { Timestamp = now, IsValid = true, Environments = environments };

        T3UsageSnapshot? usage = testCase.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object
            ? u.Deserialize<T3UsageSnapshot>(Options) : null;

        return PresenceCardFormatter.Format(new PresenceCardInput
        {
            Settings = settings,
            Snapshot = snapshot,
            Usage = usage,
            Timer = new PresenceTimerState(DateTimeOffset.FromUnixTimeSeconds(sessionStart), DateTimeOffset.FromUnixTimeSeconds(appStart)),
            App = SupportedApp.T3Code,
            Now = now
        });
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void SharedCase(string name)
    {
        var fixture = Fixture();
        var testCase = fixture.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        var result = Run(fixture, testCase);
        var activity = result.Activity;
        Assert.NotNull(activity);
        var expected = testCase.GetProperty("expected");

        void CheckString(string key, string? actual)
        {
            if (!expected.TryGetProperty(key, out var value)) return;
            Assert.Equal(value.ValueKind == JsonValueKind.Null ? null : value.GetString(), actual);
        }

        CheckString("details", activity!.Details);
        CheckString("state", activity.State);
        CheckString("largeImage", activity.Assets?.LargeImage);
        CheckString("largeText", activity.Assets?.LargeText);
        CheckString("smallImage", activity.Assets?.SmallImage);
        CheckString("smallText", activity.Assets?.SmallText);

        if (expected.TryGetProperty("timestampsStart", out var ts))
            Assert.Equal(ts.ValueKind == JsonValueKind.Null ? null : ts.GetInt64(), activity.Timestamps?.Start);

        if (expected.TryGetProperty("buttons", out var buttons))
        {
            var want = buttons.ValueKind == JsonValueKind.Null ? new List<(string, string)>()
                : buttons.EnumerateArray().Select(b => (b.GetProperty("label").GetString()!, b.GetProperty("url").GetString()!)).ToList();
            var got = (activity.Buttons ?? new()).Select(b => (b.Label, b.Url)).ToList();
            Assert.Equal(want, got);
        }

        if (expected.TryGetProperty("fitWarnings", out var warnings))
            Assert.Equal(warnings.EnumerateArray().Select(w => w.GetString()!).ToList(), result.FitWarnings.ToList());

        // Bait must never appear in any outgoing field or preview row.
        string wire = JsonSerializer.Serialize(activity) + "\n" + string.Join("\n", result.Rows.Select(r => r.Value));
        var forbidden = new List<string> { "PRIVATE" };
        if (expected.TryGetProperty("forbidden", out var f)) forbidden.AddRange(f.EnumerateArray().Select(x => x.GetString()!));
        foreach (string bait in forbidden) Assert.DoesNotContain(bait, wire, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewRowsListEveryOutgoingFieldWithASource()
    {
        var fixture = Fixture();
        var testCase = fixture.GetProperty("cases").EnumerateArray().First(c => c.GetProperty("name").GetString()!.StartsWith("Usage today with estimated cost"));
        var result = Run(fixture, testCase);
        var fields = result.Rows.Select(r => r.Field).ToList();
        Assert.Contains("details", fields);
        Assert.Contains("state", fields);
        Assert.Contains("largeImage", fields);
        Assert.Contains("largeText", fields);
        Assert.Contains("smallImage", fields);
        Assert.Contains("timestamps.start", fields);
        Assert.Equal(PreviewSource.T3Usage, result.Rows.Single(r => r.Field == "state").Source);
        Assert.All(result.Rows, r => Assert.Contains(r.Source, new[] { PreviewSource.Deskchirp, PreviewSource.T3Code, PreviewSource.T3Usage }));
    }

    [Fact]
    public void DiscordWireShapeIncludesTimestampsAndButtons()
    {
        var activity = new DiscordActivity
        {
            Details = "Using T3 Code",
            Timestamps = new DiscordActivityTimestamps { Start = 1793500600 },
            Buttons = new() { new DiscordActivityButton { Label = "Get Deskchirp", Url = PresenceCardFormatter.CompanionUrl } }
        };
        string json = JsonSerializer.Serialize(activity);
        Assert.Contains("\"timestamps\":{\"start\":1793500600}", json);
        Assert.Contains("\"buttons\":[{\"label\":\"Get Deskchirp\",\"url\":\"https://github.com/geekynasir/deskchirp-discord\"}]", json);
        Assert.DoesNotContain("\"assets\"", json);
    }

    [Fact]
    public void ArtworkUsesSingleTagAndStatusBadges()
    {
        Assert.Contains("/" + DiscordArtwork.ArtworkTag + "/", DiscordArtwork.BaseUrl);
        Assert.Equal("artwork-v2", DiscordArtwork.ArtworkTag);
        foreach (var key in new[] { "status_working", "status_attention", "status_idle" })
            Assert.Equal(DiscordArtwork.BaseUrl + key + ".png", DiscordArtwork.UrlFor(key));
    }
}
