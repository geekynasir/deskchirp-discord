namespace DeveloperPresence.Core.Services;

public enum AppUpdateState { Unavailable, Idle, Checking, Downloading, Ready, UpToDate, Failed }

/// <summary>
/// The installer's update feed. Implemented with Velopack in the app; faked in tests.
/// </summary>
public interface IAppUpdateSource
{
    /// <summary>False for portable or developer copies, which cannot replace themselves.</summary>
    bool IsInstalled { get; }
    string CurrentVersion { get; }
    /// <summary>A version already downloaded and waiting for a restart, if any.</summary>
    string? PendingVersion { get; }
    /// <summary>Returns the newer version on offer, or null when up to date.</summary>
    Task<string?> FindUpdateAsync(CancellationToken cancellationToken);
    /// <summary>Downloads the update returned by the last <see cref="FindUpdateAsync"/>.</summary>
    Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken);
    /// <summary>Asks the updater to install the downloaded version after this process exits, then reopen it.</summary>
    void ApplyAfterExit();
}

/// <summary>
/// Checks for, downloads and stages app updates. A downloaded update installs on the next
/// launch, or immediately through <see cref="RestartToUpdate"/>. Checks never interrupt sharing.
/// </summary>
public sealed class AppUpdateService : IDisposable
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly IAppUpdateSource _source;
    private readonly Func<bool> _automaticChecks;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private Task? _schedule;
    private int _progress;
    private string? _offeredVersion;

    public AppUpdateService(IAppUpdateSource source, Func<bool> automaticChecks)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _automaticChecks = automaticChecks ?? throw new ArgumentNullException(nameof(automaticChecks));
        if (!_source.IsInstalled) State = AppUpdateState.Unavailable;
        else if (_source.PendingVersion is { } pending) { State = AppUpdateState.Ready; _offeredVersion = pending; }
    }

    public event Action? Changed;

    public AppUpdateState State { get; private set; } = AppUpdateState.Idle;
    public string CurrentVersion => _source.CurrentVersion;
    public bool CanCheck => State is AppUpdateState.Idle or AppUpdateState.UpToDate or AppUpdateState.Failed;
    public bool IsReady => State == AppUpdateState.Ready;
    public bool IsDownloading => State == AppUpdateState.Downloading;
    public int Progress => _progress;

    public string StatusText => State switch
    {
        AppUpdateState.Unavailable => "This copy doesn’t update itself. Install Deskchirp with its setup app to get updates automatically.",
        AppUpdateState.Checking => "Checking for updates…",
        AppUpdateState.Downloading => $"Downloading version {_offeredVersion}… {_progress}%",
        AppUpdateState.Ready => $"Version {_offeredVersion} is ready. Restart Deskchirp to finish updating, or it updates the next time it opens.",
        AppUpdateState.UpToDate => "Deskchirp is up to date.",
        AppUpdateState.Failed => "Couldn’t check for updates. Check your internet connection and try again.",
        _ => $"Version {CurrentVersion}",
    };

    /// <summary>Starts background checks: shortly after launch, then every few hours.</summary>
    public void Start(TimeSpan? firstDelay = null, TimeSpan? interval = null)
    {
        if (State == AppUpdateState.Unavailable || _schedule != null) return;
        var token = _lifetime.Token;
        _schedule = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(firstDelay ?? FirstCheckDelay, token).ConfigureAwait(false);
                while (!token.IsCancellationRequested)
                {
                    if (_automaticChecks()) await CheckAsync().ConfigureAwait(false);
                    await Task.Delay(interval ?? CheckInterval, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Checks once and downloads any newer version. Safe to call while a check runs.</summary>
    public async Task CheckAsync()
    {
        if (!CanCheck || !await _busy.WaitAsync(0).ConfigureAwait(false)) return;
        var token = _lifetime.Token;
        try
        {
            if (!CanCheck) return;
            Set(AppUpdateState.Checking);
            string? version = await _source.FindUpdateAsync(token).ConfigureAwait(false);
            if (version == null) { Set(AppUpdateState.UpToDate); return; }
            _offeredVersion = version;
            _progress = 0;
            Set(AppUpdateState.Downloading);
            await _source.DownloadAsync(percent =>
            {
                int clamped = Math.Clamp(percent, 0, 100);
                if (clamped == _progress) return;
                _progress = clamped;
                Changed?.Invoke();
            }, token).ConfigureAwait(false);
            Set(AppUpdateState.Ready);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { Set(AppUpdateState.Failed); }
        finally { _busy.Release(); }
    }

    /// <summary>
    /// Stages the downloaded version to install once the app exits. The caller then quits
    /// normally, so Discord is cleared before the updater replaces the files.
    /// </summary>
    public bool RestartToUpdate()
    {
        if (State != AppUpdateState.Ready) return false;
        try { _source.ApplyAfterExit(); return true; }
        catch (Exception) { Set(AppUpdateState.Failed); return false; }
    }

    private void Set(AppUpdateState state)
    {
        State = state;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        // Not disposed: a check that is finishing may still read the token.
        _lifetime.Cancel();
    }
}
