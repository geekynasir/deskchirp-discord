namespace DeveloperPresence.Core.T3;

public class T3AuthenticationException : Exception
{
    public T3AuthenticationException(string message) : base(message) { }
    public T3AuthenticationException(string message, Exception inner) : base(message, inner) { }
}

public interface IT3McpClient : IAsyncDisposable
{
    Task<T3CapabilitiesResult> GetCapabilitiesAsync(Uri mcpUri, string bearerToken, CancellationToken ct = default);
    Task<T3ThreadListResult> ListThreadsAsync(Uri mcpUri, string bearerToken, int? cursor = null, CancellationToken ct = default);
}
