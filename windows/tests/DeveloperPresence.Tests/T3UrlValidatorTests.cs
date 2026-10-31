using DeveloperPresence.Core.T3;
using Xunit;

namespace DeveloperPresence.Tests;

public class T3UrlValidatorTests
{
    [Theory]
    [InlineData("https://t3.example.com/mcp", "https://t3.example.com/mcp")]
    [InlineData("https://t3.example.com", "https://t3.example.com/mcp")]
    [InlineData("http://127.0.0.1:8080/mcp", "http://127.0.0.1:8080/mcp")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080/mcp")]
    [InlineData("http://localhost:3000", "http://localhost:3000/mcp")]
    [InlineData("http://localhost:3000/mcp", "http://localhost:3000/mcp")]
    public void NormalizeAndValidate_AcceptsHttpsAndLoopbackHttp(string input, string expected)
    {
        bool success = T3UrlValidator.TryNormalizeT3Address(input, out var normalized, out string? error);
        Assert.True(success, error);
        Assert.NotNull(normalized);
        Assert.Equal(expected, normalized.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://remote-server.com/mcp")]
    [InlineData("http://192.168.1.100:8080/mcp")]
    [InlineData("ftp://127.0.0.1/mcp")]
    public void NormalizeAndValidate_RejectsRemoteNonHttps(string input)
    {
        bool success = T3UrlValidator.TryNormalizeT3Address(input, out _, out string? error);
        Assert.False(success);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("https://user:pass@example.com/mcp")]
    [InlineData("https://example.com/mcp?query=secret")]
    [InlineData("https://example.com/mcp#frag")]
    [InlineData("http://127.0.0.1:8080/mcp?foo=bar")]
    public void NormalizeAndValidate_RejectsUserinfoQueryFragment(string input)
    {
        bool success = T3UrlValidator.TryNormalizeT3Address(input, out _, out string? error);
        Assert.False(success);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeAndValidate_RejectsMalformedUrls(string input)
    {
        bool success = T3UrlValidator.TryNormalizeT3Address(input, out _, out string? error);
        Assert.False(success);
        Assert.NotNull(error);
    }
}
