using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Services;

namespace DeveloperPresence.App.ViewModels;

public sealed class ProcessMappingItem : INotifyPropertyChanged
{
    private string _executableName;
    private SupportedApp _app;

    public string ExecutableName
    {
        get => _executableName;
        set { _executableName = value; OnPropertyChanged(); }
    }

    public SupportedApp App
    {
        get => _app;
        set { _app = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
    }

    public string DisplayName => App switch
    {
        SupportedApp.T3Code => "T3 Code",
        SupportedApp.Claude => "Claude Desktop",
        SupportedApp.Codex => "Codex Desktop",
        _ => "Unknown"
    };

    public ProcessMappingItem(string exeName, SupportedApp app)
    {
        _executableName = exeName;
        _app = app;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly PresenceCoordinator _coordinator;
    private readonly Action<Action>? _uiDispatcher;

    private bool _enableSharing;
    private bool _isPaused;
    private string _discordClientId = string.Empty;
    private bool _isClientIdValid = true;
    private string _connectionStatusText = "Disconnected";
    private string _foregroundAppText = "None / Unsupported";
    private string _outgoingHeadlineText = "None / Cleared";
    private string _lastUpdateText = "Never";
    private string _statusMessage = "Ready";

    private string _newExecutableName = string.Empty;
    private SupportedApp _selectedAppForNewMapping = SupportedApp.T3Code;

    public ObservableCollection<ProcessMappingItem> ProcessMappings { get; } = new();

    public bool EnableSharing
    {
        get => _enableSharing;
        set
        {
            if (_enableSharing != value)
            {
                _enableSharing = value;
                OnPropertyChanged();
                _ = UpdateSettingAsync(s => s.EnableSharing = value);
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (_isPaused != value)
            {
                _isPaused = value;
                OnPropertyChanged();
                _ = UpdateSettingAsync(s => s.IsPaused = value);
            }
        }
    }

    public string DiscordClientId
    {
        get => _discordClientId;
        set
        {
            string normalized = value.Trim();
            if (_discordClientId != normalized)
            {
                _discordClientId = normalized;
                OnPropertyChanged();
                ValidateClientId();
                _ = UpdateSettingAsync(s => s.DiscordClientId = normalized);
            }
        }
    }

    public bool IsClientIdValid
    {
        get => _isClientIdValid;
        private set { _isClientIdValid = value; OnPropertyChanged(); }
    }

    public string ConnectionStatusText
    {
        get => _connectionStatusText;
        private set { _connectionStatusText = value; OnPropertyChanged(); }
    }

    public string ForegroundAppText
    {
        get => _foregroundAppText;
        private set { _foregroundAppText = value; OnPropertyChanged(); }
    }

    public string OutgoingHeadlineText
    {
        get => _outgoingHeadlineText;
        private set { _outgoingHeadlineText = value; OnPropertyChanged(); }
    }

    public string LastUpdateText
    {
        get => _lastUpdateText;
        private set { _lastUpdateText = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public string NewExecutableName
    {
        get => _newExecutableName;
        set { _newExecutableName = value; OnPropertyChanged(); }
    }


    public int SelectedAppIndex
    {
        get => _selectedAppForNewMapping switch
        {
            SupportedApp.T3Code => 0,
            SupportedApp.Claude => 1,
            SupportedApp.Codex => 2,
            _ => 0
        };
        set
        {
            _selectedAppForNewMapping = value switch
            {
                0 => SupportedApp.T3Code,
                1 => SupportedApp.Claude,
                2 => SupportedApp.Codex,
                _ => SupportedApp.T3Code
            };
            OnPropertyChanged();
        }
    }

    public SupportedApp SelectedAppForNewMapping
    {
        get => _selectedAppForNewMapping;
        set { _selectedAppForNewMapping = value; OnPropertyChanged(); }
    }

    public ICommand TogglePauseCommand { get; }
    public ICommand AddMappingCommand { get; }
    public ICommand RemoveMappingCommand { get; }

    public MainViewModel(
        ISettingsService settingsService,
        PresenceCoordinator coordinator,
        Action<Action>? uiDispatcher = null)
    {
        _settingsService = settingsService;
        _coordinator = coordinator;
        _uiDispatcher = uiDispatcher;

        TogglePauseCommand = new RelayCommand(() => IsPaused = !IsPaused);
        AddMappingCommand = new RelayCommand(AddProcessMapping);
        RemoveMappingCommand = new RelayCommand<ProcessMappingItem>(RemoveProcessMapping);

        LoadInitialData();
        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    private void LoadInitialData()
    {
        var s = _settingsService.Settings;
        _enableSharing = s.EnableSharing;
        _isPaused = s.IsPaused;
        _discordClientId = s.DiscordClientId;
        OnPropertyChanged(nameof(EnableSharing));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(DiscordClientId));
        ValidateClientId();

        ProcessMappings.Clear();
        foreach (var (exe, app) in s.ProcessMappings)
        {
            ProcessMappings.Add(new ProcessMappingItem(exe, app));
        }

        UpdateFromPresenceState(_coordinator.CurrentState);
    }

    private void ValidateClientId()
    {
        if (string.IsNullOrWhiteSpace(_discordClientId))
        {
            IsClientIdValid = false;
            return;
        }

        string trimmed = _discordClientId.Trim();
        IsClientIdValid = trimmed.Length >= 15 && trimmed.Length <= 20 && trimmed.All(c => c >= '0' && c <= '9');
    }

    private void OnCoordinatorStateChanged(PresenceState state)
    {
        Dispatch(() => UpdateFromPresenceState(state));
    }

    private void UpdateFromPresenceState(PresenceState state)
    {
        ConnectionStatusText = state.ConnectionStatus.ToString();
        ForegroundAppText = state.ForegroundApp switch
        {
            SupportedApp.T3Code => "T3 Code",
            SupportedApp.Claude => "Claude Desktop",
            SupportedApp.Codex => "Codex Desktop",
            _ => "None / Unsupported"
        };
        OutgoingHeadlineText = state.OutgoingHeadline;
        LastUpdateText = state.LastUpdate.HasValue ? state.LastUpdate.Value.ToLocalTime().ToString("HH:mm:ss") : "Never";
        StatusMessage = state.StatusMessage;
    }

    private async Task UpdateSettingAsync(Action<PresenceSettings> action)
    {
        try
        {
            await _settingsService.UpdateSettingsAsync(action).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Dispatch(() => StatusMessage = $"Couldn't save settings: {ex.Message}");
        }
    }

    private void AddProcessMapping()
    {
        if (string.IsNullOrWhiteSpace(NewExecutableName))
        {
            return;
        }

        string exe = NewExecutableName.Trim().ToLowerInvariant();
        if (exe.IndexOfAny(new[] { '/', '\\', ':', '*', '?' }) >= 0)
        {
            StatusMessage = "Enter an executable name such as app.exe.";
            return;
        }
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            exe += ".exe";
        }

        var existing = ProcessMappings.FirstOrDefault(m => string.Equals(m.ExecutableName, exe, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.App = SelectedAppForNewMapping;
        }
        else
        {
            ProcessMappings.Add(new ProcessMappingItem(exe, SelectedAppForNewMapping));
        }

        NewExecutableName = string.Empty;
        SyncMappingsToSettings();
    }

    private void RemoveProcessMapping(ProcessMappingItem? item)
    {
        if (item != null && ProcessMappings.Contains(item))
        {
            ProcessMappings.Remove(item);
            SyncMappingsToSettings();
        }
    }

    private void SyncMappingsToSettings()
    {
        _ = UpdateSettingAsync(s =>
        {
            var dict = new Dictionary<string, SupportedApp>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in ProcessMappings)
            {
                dict[item.ExecutableName] = item.App;
            }
            s.ProcessMappings = dict;
        });
    }

    private void OnSettingsChanged(PresenceSettings settings) => Dispatch(LoadInitialData);

    public void ShowError(string message) => Dispatch(() => StatusMessage = message);

    public void Dispose()
    {
        _coordinator.StateChanged -= OnCoordinatorStateChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
    }

    private void Dispatch(Action action)
    {
        if (_uiDispatcher != null)
        {
            _uiDispatcher(action);
        }
        else
        {
            action();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
