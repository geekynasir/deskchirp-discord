using DeveloperPresence.Core.Models;

namespace DeveloperPresence.Core.T3;

public interface IT3ActivityService : IAsyncDisposable
{
    T3ConnectionStatus ConnectionStatus { get; }
    string StatusDetail { get; }
    T3Snapshot? CurrentSnapshot { get; }
    bool IsSnapshotValid { get; }

    event Action<T3ConnectionStatus, string>? StatusChanged;
    event Action<T3Snapshot?>? SnapshotUpdated;

    Task InitializeAsync(CancellationToken ct = default);
    Task<bool> ConnectAsync(string rawAddress, CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task CancelSignInAsync();
    Task<T3Snapshot?> PollOnceAsync(CancellationToken ct = default);
    void ClearSnapshot();
    void Invalidate();
}
