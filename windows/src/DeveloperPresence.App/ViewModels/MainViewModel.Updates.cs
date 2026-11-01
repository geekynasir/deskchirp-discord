using System.Windows.Input;
using DeveloperPresence.Core.Services;

namespace DeveloperPresence.App.ViewModels;

/// <summary>Updates section: version, background download progress and Restart to update.</summary>
public sealed partial class MainViewModel
{
    private AppUpdateService? _updates;
    private bool _automaticUpdates = true;

    /// <summary>Raised after the update is staged; the window quits so the updater can replace the app.</summary>
    public event Action? RestartForUpdateRequested;

    public ICommand CheckForUpdatesCommand => _checkForUpdatesCommand ??= new RelayCommand(() => { if (_updates != null) _ = _updates.CheckAsync(); });
    public ICommand RestartToUpdateCommand => _restartToUpdateCommand ??= new RelayCommand(RestartToUpdate);
    private RelayCommand? _checkForUpdatesCommand;
    private RelayCommand? _restartToUpdateCommand;

    public string UpdateStatusText => _updates?.StatusText ?? "";
    public string AppVersionText => _updates == null ? "" : $"Deskchirp {_updates.CurrentVersion}";
    public bool CanCheckForUpdates => _updates?.CanCheck == true;
    public bool IsUpdateReady => _updates?.IsReady == true;
    public bool IsDownloadingUpdate => _updates?.IsDownloading == true;
    public double UpdateProgress => _updates?.Progress ?? 0;
    public bool CanUpdateAutomatically => _updates != null && _updates.State != AppUpdateState.Unavailable;

    public bool AutomaticUpdates
    {
        get => _automaticUpdates;
        set
        {
            if (_automaticUpdates == value) return;
            _automaticUpdates = value;
            OnPropertyChanged();
            _ = UpdateSettingAsync(s => s.AutomaticUpdates = value);
            if (value && _updates != null) _ = _updates.CheckAsync();
        }
    }

    public void AttachUpdates(AppUpdateService updates)
    {
        _updates = updates;
        updates.Changed += OnUpdatesChanged;
        OnUpdatesChanged();
    }

    private void RestartToUpdate()
    {
        if (_updates?.RestartToUpdate() == true) RestartForUpdateRequested?.Invoke();
    }

    private void OnUpdatesChanged() => Dispatch(() =>
    {
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(AppVersionText));
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(IsUpdateReady));
        OnPropertyChanged(nameof(IsDownloadingUpdate));
        OnPropertyChanged(nameof(UpdateProgress));
        OnPropertyChanged(nameof(CanUpdateAutomatically));
    });

    private void DisposeUpdates()
    {
        if (_updates != null) _updates.Changed -= OnUpdatesChanged;
    }
}
