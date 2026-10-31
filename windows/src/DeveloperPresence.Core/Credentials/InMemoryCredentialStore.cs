using System.Collections.Concurrent;

namespace DeveloperPresence.Core.Credentials;

/// <summary>
/// Thread-safe in-memory credential store for tests and portable non-Windows environments.
/// </summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<(string Target, string Username), string> _store = new();

    public Task SaveCredentialAsync(string target, string username, string secret, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(secret);

        _store[(target, username)] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> ReadCredentialAsync(string target, string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        _store.TryGetValue((target, username), out string? secret);
        return Task.FromResult(secret);
    }

    public Task DeleteCredentialAsync(string target, string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        _store.TryRemove((target, username), out _);
        return Task.CompletedTask;
    }

    public void Clear() => _store.Clear();
}
