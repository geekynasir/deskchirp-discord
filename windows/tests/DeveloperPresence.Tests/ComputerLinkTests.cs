using System.Net;
using System.Text;
using System.Text.Json;
using DeveloperPresence.Core.Computers;
using DeveloperPresence.Core.Credentials;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Services;
using Xunit;

namespace DeveloperPresence.Tests;

public sealed class ComputerLinkTests
{
    private sealed class TestSettings : ISettingsService
    {
        public PresenceSettings Settings { get; } = new();
        public event Action<PresenceSettings>? SettingsChanged;
        public Task LoadSettingsAsync() => Task.CompletedTask;
        public Task SaveSettingsAsync() => Task.CompletedTask;
        public Task UpdateSettingsAsync(Action<PresenceSettings> update) {
            update(Settings); SettingsChanged?.Invoke(Settings); return Task.CompletedTask;
        }
    }
    private sealed class Input : IInputAgeReader
    { public double? Age = 0; public double? ReadSeconds() => Age; }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += (long)(seconds * TimestampFrequency);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<(string Path, string Body, string? Token)> Calls = new();
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? Heartbeat;
        public double Validity = 15;
        public bool Allow = true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync(ct), request.Headers.Authorization?.Parameter));
            if (request.RequestUri.AbsolutePath == "/v1/join")
                return Reply(new { deviceId = "test_device_123456", groupId = "test_group_123456", token = "test_token_123456789012" });
            if (request.RequestUri.AbsolutePath == "/v1/heartbeat")
                return Heartbeat != null ? await Heartbeat(request) : Reply(new { allowed = Allow, generation = 1,
                    validFor = Validity, selectedName = "My Mac", switching = !Allow });
            return Reply(new { removed = true, code = "test-code" });
        }
        public static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK) {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }

    [Theory]
    [InlineData("https://link.example/", "https://link.example")]
    [InlineData("http://127.0.0.1:4859", "http://127.0.0.1:4859")]
    public void NormalizesSecureAddress(string input, string expected) => Assert.Equal(expected, ComputerAddress.Normalize(input));

    [Theory]
    [InlineData("http://link.example")]
    [InlineData("https://user:password@link.example")]
    [InlineData("https://link.example/mcp")]
    [InlineData("https://link.example?key=private")]
    public void RejectsInsecureOrCredentialAddresses(string input) => Assert.Throws<InvalidOperationException>(() => ComputerAddress.Normalize(input));

    [Fact]
    public async Task PermissionExpiresBeforeServerGrantAndNeverRestoresAfterPause()
    {
        var settings = new TestSettings(); var input = new Input(); var clock = new Clock(); var handler = new Handler();
        var service = new ComputerLinkService(settings, new InMemoryCredentialStore(), input, new HttpClient(handler), clock);
        Assert.True(service.CanPublish); // Existing single-computer mode.
        await service.LinkAsync("https://link.example", "test-code", "My PC");
        Assert.True(settings.Settings.FollowComputers);
        Assert.False(service.CanPublish);
        service.UpdateContext(true, "123456789012345678");
        await service.PollOnceAsync();
        Assert.True(service.CanPublish);
        clock.Advance(12);
        Assert.False(service.CanPublish);
        await service.PollOnceAsync();
        Assert.True(service.CanPublish);
        service.UpdateContext(false, null);
        Assert.False(service.CanPublish);
        await service.PollOnceAsync();
        Assert.False(service.CanPublish);
        var body = handler.Calls.First(x => x.Path == "/v1/heartbeat").Body;
        Assert.DoesNotContain("123456789012345678", body);
        Assert.DoesNotContain("T3", body);
    }

    [Fact]
    public async Task LateGrantCannotRestoreLostEligibility()
    {
        var handler = new Handler(); var input = new Input();
        var service = new ComputerLinkService(new TestSettings(), new InMemoryCredentialStore(), input, new HttpClient(handler));
        await service.LinkAsync("https://link.example", "test-code", "My PC");
        service.UpdateContext(true, "123456789012345678");
        var result = new TaskCompletionSource<HttpResponseMessage>();
        handler.Heartbeat = _ => result.Task;
        var pending = service.PollOnceAsync();
        service.UpdateContext(false, null);
        result.SetResult(Handler.Reply(new { allowed = true, validFor = 15, selectedName = "My PC", switching = false }));
        await pending;
        Assert.False(service.CanPublish);
    }

    [Fact]
    public async Task IdleUnknownInputAndDifferentAccountCannotReuseGrant()
    {
        var input = new Input(); var service = new ComputerLinkService(new TestSettings(), new InMemoryCredentialStore(), input, new HttpClient(new Handler()));
        await service.LinkAsync("https://link.example", "test-code", "My PC");
        service.UpdateContext(true, "123456789012345678");
        await service.PollOnceAsync(); Assert.True(service.CanPublish);
        input.Age = 300; Assert.False(service.CanPublish);
        input.Age = null; Assert.False(service.CanPublish);
        input.Age = 0; service.UpdateContext(true, "987654321098765432"); Assert.False(service.CanPublish);
    }

    [Fact]
    public async Task DelayedOrOversizedGrantFailsClosed()
    {
        var clock = new Clock(); var handler = new Handler();
        var service = new ComputerLinkService(new TestSettings(), new InMemoryCredentialStore(), new Input(), new HttpClient(handler), clock);
        await service.LinkAsync("https://link.example", "test-code", "My PC");
        service.UpdateContext(true, "123456789012345678");
        handler.Heartbeat = _ => { clock.Advance(13); return Task.FromResult(Handler.Reply(new {
            allowed = true, validFor = 15, selectedName = "My PC", switching = false })); };
        await service.PollOnceAsync(); Assert.False(service.CanPublish);
        handler.Heartbeat = null; handler.Validity = 200;
        await service.PollOnceAsync(); Assert.False(service.CanPublish);
    }

    [Fact]
    public async Task UnlinkDisablesSharingAndRemovesSavedCredential()
    {
        var settings = new TestSettings(); var store = new InMemoryCredentialStore();
        var service = new ComputerLinkService(settings, store, new Input(), new HttpClient(new Handler()));
        await service.LinkAsync("https://link.example", "test-code", "My PC");
        settings.Settings.EnableSharing = true;
        await service.UnlinkAsync();
        Assert.False(settings.Settings.EnableSharing); Assert.False(settings.Settings.FollowComputers);
        Assert.False(service.HasSavedLink);
        Assert.Null(await store.ReadCredentialAsync("DeveloperPresence:computers", "device"));
    }

    [Fact]
    public void LegacySettingsDoNotEnableDeviceLinking()
    {
        var settings = JsonSerializer.Deserialize<PresenceSettings>("{\"EnableSharing\":true}")!;
        Assert.False(settings.FollowComputers); Assert.Empty(settings.ComputerLinkAddress);
    }
}
