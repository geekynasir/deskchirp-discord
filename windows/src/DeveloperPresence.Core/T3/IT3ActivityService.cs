using DeveloperPresence.Core.Models;

namespace DeveloperPresence.Core.T3;

/// <summary>Plain-language state of one T3 environment for the settings list.</summary>
public sealed class T3EnvironmentStatus
{
    public string EnvironmentId { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    /// <summary>Local label. Shown only in settings; never published.</summary>
    public string Label { get; init; } = string.Empty;
    public T3ConnectionStatus Status { get; init; }
    public string Detail { get; init; } = string.Empty;
    /// <summary>True once any poll of this environment found running, waiting, or starting work.</summary>
    public bool HasReturnedWork { get; init; }
    /// <summary>True once t3_project_list has answered for this environment.</summary>
    public bool ProjectCatalogLoaded { get; init; }
}

/// <summary>Thread metadata for the local pinned-task picker. Titles never leave settings.</summary>
public sealed record T3PickerTask(string EnvironmentId, string ThreadId, string? Title, string? ProjectId, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt, bool IsHelper);

/// <summary>Project metadata for the local alias editor and pickers. Titles never leave settings.</summary>
public sealed record T3PickerProject(string EnvironmentId, string ProjectId, string? Title);

public interface IT3ActivityService : IAsyncDisposable
{
    /// <summary>Combined status: Opening sign-in, then Connected (any environment), then the first problem.</summary>
    T3ConnectionStatus ConnectionStatus { get; }
    string StatusDetail { get; }
    /// <summary>Combined snapshot of every environment with a fresh poll, or null.</summary>
    T3Snapshot? CurrentSnapshot { get; }
    bool IsSnapshotValid { get; }

    event Action<T3ConnectionStatus, string>? StatusChanged;
    event Action<T3Snapshot?>? SnapshotUpdated;

    Task InitializeAsync(CancellationToken ct = default);
    /// <summary>Finds or adds the environment for this address, then opens T3's own sign-in for it.</summary>
    Task<bool> ConnectAsync(string rawAddress, CancellationToken ct = default);
    /// <summary>Removes every saved T3 sign-in on this computer. Environments stay in the list.</summary>
    Task DisconnectAsync(CancellationToken ct = default);
    Task CancelSignInAsync();
    Task<T3Snapshot?> PollOnceAsync(CancellationToken ct = default);
    void ClearSnapshot();
    void Invalidate();

    // Presence v3 multi-environment surface. Defaults keep older fakes compiling.

    IReadOnlyList<T3EnvironmentStatus> Environments => Array.Empty<T3EnvironmentStatus>();
    /// <summary>
    /// True while any environment is connected, whatever the others are doing (for example signing in).
    /// Polling and publishing depend on this, never on the combined status line.
    /// </summary>
    bool AnyEnvironmentConnected => ConnectionStatus == T3ConnectionStatus.Connected;
    event Action? EnvironmentsChanged { add { } remove { } }
    /// <summary>Validates and adds an environment. Returns an error message, or null on success.</summary>
    Task<string?> AddEnvironmentAsync(string rawAddress, CancellationToken ct = default) => Task.FromResult<string?>("Not supported.");
    Task<bool> ConnectEnvironmentAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Cancels only this environment's pending browser sign-in.</summary>
    Task CancelSignInAsync(string environmentId) => CancelSignInAsync();
    /// <summary>Removes the environment and deletes its saved sign-in.</summary>
    Task RemoveEnvironmentAsync(string environmentId, CancellationToken ct = default) => Task.CompletedTask;
    IReadOnlyList<T3PickerTask> PickerTasks => Array.Empty<T3PickerTask>();
    IReadOnlyList<T3PickerProject> PickerProjects => Array.Empty<T3PickerProject>();
}
