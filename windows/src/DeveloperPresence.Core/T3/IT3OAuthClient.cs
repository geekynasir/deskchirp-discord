namespace DeveloperPresence.Core.T3;

public interface IT3OAuthClient
{
    Task<T3StoredToken> SignInAsync(Uri normalizedMcpUri, CancellationToken ct = default);

    /// <summary>Signs in without a browser using a one-time pairing code for this environment.</summary>
    Task<T3StoredToken> SignInWithPairingCodeAsync(Uri normalizedMcpUri, string pairingCode, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
