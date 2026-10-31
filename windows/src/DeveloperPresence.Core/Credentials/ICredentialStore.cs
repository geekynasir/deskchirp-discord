namespace DeveloperPresence.Core.Credentials;

/// <summary>
/// Abstraction for platform secure credential storage (Windows Credential Manager / Keychain / Test in-memory store).
/// Securely stores sensitive authentication material (OAuth bearer tokens) away from settings files, logs, or presence.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Securely persists a credential secret for the specified target and username.
    /// </summary>
    Task SaveCredentialAsync(string target, string username, string secret, CancellationToken ct = default);

    /// <summary>
    /// Reads a previously saved credential secret, or null if not found.
    /// </summary>
    Task<string?> ReadCredentialAsync(string target, string username, CancellationToken ct = default);

    /// <summary>
    /// Removes a stored credential. Does not throw if the credential does not exist.
    /// </summary>
    Task DeleteCredentialAsync(string target, string username, CancellationToken ct = default);
}
