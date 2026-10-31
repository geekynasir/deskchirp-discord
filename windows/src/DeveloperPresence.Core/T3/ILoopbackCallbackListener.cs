namespace DeveloperPresence.Core.T3;

public sealed class OAuthCallbackRequest
{
    public string? Code { get; set; }
    public string? State { get; set; }
    public string? Issuer { get; set; }
    public string? Error { get; set; }
    public string? ErrorDescription { get; set; }
}

public interface ILoopbackCallbackListener : IAsyncDisposable
{
    int Port { get; }
    string CallbackUriString { get; }
    Task<OAuthCallbackRequest> WaitForCallbackAsync(TimeSpan timeout, CancellationToken ct);
    Task RespondAsync(bool success, bool exchangeCompleted, CancellationToken ct = default);
}
