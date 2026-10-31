namespace DeveloperPresence.Core.T3;

public interface IT3OAuthClient
{
    Task<T3StoredToken> SignInAsync(Uri normalizedMcpUri, CancellationToken ct = default);
}
