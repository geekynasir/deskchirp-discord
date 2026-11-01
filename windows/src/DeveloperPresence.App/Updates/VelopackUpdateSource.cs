using System.Reflection;
using DeveloperPresence.Core.Services;
using Velopack;
using Velopack.Sources;

namespace DeveloperPresence.App.Updates;

/// <summary>Velopack feed published with each GitHub release (releases.win.json and the full package).</summary>
internal sealed class VelopackUpdateSource : IAppUpdateSource
{
    public const string RepositoryUrl = "https://github.com/geekynasir/deskchirp-discord";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _offer;
    private VelopackAsset? _downloaded;

    public VelopackUpdateSource()
    {
        // Previews are GitHub pre-releases, so the feed must include them.
        try { _manager = new UpdateManager(new GithubSource(RepositoryUrl, null, true)); }
        catch (Exception) { _manager = null; }
    }

    public bool IsInstalled => _manager?.IsInstalled == true;

    public string CurrentVersion => _manager?.CurrentVersion?.ToString() ?? BuildVersion();

    public string? PendingVersion => (_downloaded ?? _manager?.UpdatePendingRestart)?.Version?.ToString();

    public async Task<string?> FindUpdateAsync(CancellationToken cancellationToken)
    {
        if (_manager == null) return null;
        _offer = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return _offer?.TargetFullRelease.Version.ToString();
    }

    public async Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
    {
        if (_manager == null || _offer == null) throw new InvalidOperationException("No update to download.");
        await _manager.DownloadUpdatesAsync(_offer, progress, cancellationToken).ConfigureAwait(false);
        _downloaded = _offer.TargetFullRelease;
    }

    public void ApplyAfterExit()
    {
        if (_manager == null) throw new InvalidOperationException("This copy isn't installed.");
        var asset = _downloaded ?? _manager.UpdatePendingRestart ?? throw new InvalidOperationException("No update is ready.");
        // Not silent: the updater shows its progress window while it replaces the files.
        _manager.WaitExitThenApplyUpdates(asset, false, true, Array.Empty<string>());
    }

    private static string BuildVersion()
    {
        string? version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(version)) return "development build";
        int metadata = version.IndexOf('+');
        return metadata < 0 ? version : version[..metadata];
    }
}
