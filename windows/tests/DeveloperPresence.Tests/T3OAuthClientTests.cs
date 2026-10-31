using System.Security.Cryptography;
using System.Text;
using DeveloperPresence.Core.T3;
using Xunit;

namespace DeveloperPresence.Tests;

public class T3OAuthClientTests
{
    [Fact]
    public void Pkce_GeneratesValidS256ChallengeAndVerifier()
    {
        var (verifier, challenge) = PkceUtility.GeneratePkce();

        Assert.NotNull(verifier);
        Assert.NotNull(challenge);
        Assert.Equal(43, verifier.Length); // 32 bytes base64url without padding = 43 chars

        // Verify S256 derivation
        byte[] verifierAscii = Encoding.ASCII.GetBytes(verifier);
        byte[] expectedHash = SHA256.HashData(verifierAscii);
        string expectedChallenge = PkceUtility.Base64UrlEncode(expectedHash);

        Assert.Equal(expectedChallenge, challenge);
    }

    [Fact]
    public void Pkce_GenerateState_ReturnsCryptographicRandomString()
    {
        string state1 = PkceUtility.GenerateState();
        string state2 = PkceUtility.GenerateState();

        Assert.NotEmpty(state1);
        Assert.NotEmpty(state2);
        Assert.NotEqual(state1, state2);
    }

    [Fact]
    public async Task LoopbackListener_RejectsDuplicateQueryParameters()
    {
        await using var listener = TcpLoopbackCallbackListener.Create();
        int port = listener.Port;

        var waitTask = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        // Connect a TCP client and send a GET with duplicate query parameters
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
        using var stream = client.GetStream();

        string rawHttp = "GET /callback?code=abc&state=xyz&code=duplicate HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(rawHttp);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => waitTask);
        Assert.Contains("Duplicate query parameter", ex.Message);
    }

    [Fact]
    public async Task LoopbackListener_ParsesValidCallbackRequest()
    {
        await using var listener = TcpLoopbackCallbackListener.Create();
        int port = listener.Port;

        var waitTask = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
        using var stream = client.GetStream();

        string rawHttp = "GET /callback?code=auth_code_123&state=state_xyz&iss=http%3A%2F%2F127.0.0.1%3A8080 HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(rawHttp);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();

        var callback = await waitTask;
        Assert.Equal("auth_code_123", callback.Code);
        Assert.Equal("state_xyz", callback.State);
        Assert.Equal("http://127.0.0.1:8080", callback.Issuer);
        Assert.Null(callback.Error);
    }

    [Fact]
    public async Task LoopbackListener_RejectsInvalidPath()
    {
        await using var listener = TcpLoopbackCallbackListener.Create();
        int port = listener.Port;

        var waitTask = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
        using var stream = client.GetStream();

        string rawHttp = "GET /other-path?code=123 HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(rawHttp);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => waitTask);
        Assert.Contains("Invalid callback path", ex.Message);
    }
}
