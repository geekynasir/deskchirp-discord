using System.Text;
using DeveloperPresence.Core.T3;
using Xunit;

namespace DeveloperPresence.Tests;

public class T3LocalDiscoveryTests
{
    [Theory]
    [InlineData("{\"version\":1,\"pid\":4,\"port\":4123,\"origin\":\"https://untrusted.example\"}", "http://127.0.0.1:4123/mcp")]
    [InlineData("{\"version\":1,\"pid\":4,\"port\":4123,\"host\":\"::1\"}", "http://[::1]:4123/mcp")]
    [InlineData("{\"version\":2,\"pid\":4,\"port\":4123}", null)]
    [InlineData("{\"version\":1,\"pid\":0,\"port\":4123}", null)]
    [InlineData("{\"version\":1,\"pid\":4,\"port\":65536}", null)]
    [InlineData("[]", null)]
    public void RuntimeNeverSuppliesRemoteUrlsOrCredentials(string json, string? expected) =>
        Assert.Equal(expected, T3LocalDiscovery.ParseRuntime(Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData("http://127.0.0.1:3773/mcp", "http://127.0.0.1:3773", "orchestration:read", true)]
    [InlineData("http://127.0.0.1:4000/mcp", "http://127.0.0.1:3773", "orchestration:read", false)]
    [InlineData("http://127.0.0.1:3773/mcp", "https://other.example", "orchestration:read", false)]
    [InlineData("http://127.0.0.1:3773/mcp", "http://127.0.0.1:3773", "orchestration:operate", false)]
    public void MetadataMustMatchLocalResourceIssuerAndReadScope(string resource, string issuer, string scope, bool expected)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { resource, authorization_servers = new[] { issuer }, scopes_supported = new[] { scope } });
        Assert.Equal(expected, T3LocalDiscovery.MatchesMetadata(bytes, new Uri("http://127.0.0.1:3773/mcp")));
    }

    [Fact]
    public async Task MissingFileCanFindDefaultButDoesNotGuessWhenOffline()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "absent.json");
        Assert.Equal("http://127.0.0.1:3773/mcp", await new T3LocalDiscovery(path, (_, _) => Task.FromResult(true)).FindAsync());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new T3LocalDiscovery(path, (_, _) => Task.FromResult(false)).FindAsync());
        Assert.Equal(T3LocalDiscovery.NotFound, ex.Message);
    }

    [Fact]
    public async Task MultipleEnvironmentsRequireChoiceAndCancellationStopsDiscovery()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{\"version\":1,\"pid\":7,\"port\":4123}");
            var discovery = new T3LocalDiscovery(path, (_, _) => Task.FromResult(true));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => discovery.FindAsync());
            Assert.Equal(T3LocalDiscovery.Ambiguous, ex.Message);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.FindAsync(cts.Token));
        }
        finally { File.Delete(path); }
    }
}
