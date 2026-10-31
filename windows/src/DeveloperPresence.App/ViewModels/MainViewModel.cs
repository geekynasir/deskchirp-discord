using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;
using DeveloperPresence.Core.Services;
using DeveloperPresence.Core.T3;
using DeveloperPresence.Core.Computers;

namespace DeveloperPresence.App.ViewModels;

public sealed class ProcessMappingItem : INotifyPropertyChanged
{
    private string _executableName;
    private SupportedApp _app;

    public string ExecutableName
    {
        get => _executableName;
        set { _executableName = value; OnPropertyChanged(); OnPropertyChanged(nameof(RemoveAutomationName)); }
    }

    public SupportedApp App
    {
        get => _app;
        set { _app = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
    }

    public string RemoveAutomationName => $"Remove {ExecutableName}";

    public string DisplayName => App switch
    {
        SupportedApp.T3Code => "T3 Code",
        SupportedApp.Claude => "Claude Desktop",
        SupportedApp.Codex => "Codex Desktop",
        _ => "Unknown app"
    };

    public ProcessMappingItem(string executableName, SupportedApp app)
    {
        _executableName = executableName;
        _app = app;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Primary ViewModel driving the Developer Presence Settings Window.
/// Conforms to plain-language requirements and Windows system appearance guidelines.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly PresenceCoordinator _coordinator;
    private readonly IT3ActivityService? _t3ActivityService;
    private readonly Action<Action>? _uiDispatcher;
    private readonly IComputerLinkService? _computers;
    private bool _computerBusy;
    private string _linkingAddress = "";
    private string _linkingCode = "";
    public string ComputerName { get; set; } = Environment.MachineName;
    public string LinkingAddress { get => _linkingAddress; set { _linkingAddress = value; OnPropertyChanged(); } }
    public string LinkingCode { get => _linkingCode; set => _linkingCode = value; }
    public string ComputerStatus => _computers?.Status ?? "Not linked";
    public string ComputerPairingCode => _computers?.PairingCode ?? "";
    public bool HasComputerPairingCode => !string.IsNullOrEmpty(ComputerPairingCode);
    public bool HasComputerLink => _computers?.HasSavedLink == true;
    public bool ShowComputerLinkForm => !HasComputerLink;
    public bool ShowUnlinkComputer => _settingsService.Settings.FollowComputers;
    public bool CanChangeComputerLink => !_computerBusy;
    public ICommand LinkComputerCommand { get; }
    public ICommand CreateComputerCodeCommand { get; }
    public ICommand UnlinkComputerCommand { get; }

    private bool _enableSharing;
    private bool _isPaused;
    private string _discordClientId = string.Empty;
    private bool _isClientIdValid;
    private bool _showT3Activity;
    private string _t3Address = string.Empty;
    private bool _showAppIcons;

    private T3ConnectionStatus _t3ConnectionStatus = T3ConnectionStatus.NotConnected;
    private string _t3StatusDetail = "Paste the T3 address, then connect.";

    private string _connectionStatusText = "Disconnected";
    private string _foregroundAppText = "No supported app";
    private string _outgoingHeadlineText = "Not shown on Discord";
    private string? _outgoingStateText;
    private string? _activityNoteText;
    private string _lastUpdateText = string.Empty;
    private bool _hasLastUpdate;
    private string _statusMessage = "Sharing is off";
    private string? _statusRecoveryText;

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
                OnPropertyChanged(nameof(IsPauseEnabled));
                _ = UpdateSettingAsync(s => s.EnableSharing = value);
            }
        }
    }

    /// <summary>
    /// Pause is disabled while sharing is off.
    /// </summary>
    public bool IsPauseEnabled => _enableSharing;

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
        private set { _isClientIdValid = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowClientIdError)); }
    }

    public bool ShowClientIdError => !string.IsNullOrEmpty(_discordClientId) && !_isClientIdValid;

    public bool ShowT3Activity
    {
        get => _showT3Activity;
        set
        {
            if (_showT3Activity != value)
            {
                _showT3Activity = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PrivacyFooterText));
                _ = UpdateSettingAsync(s => s.ShowT3Activity = value);
            }
        }
    }

    public string T3Address
    {
        get => _t3Address;
        set
        {
            string normalized = value.Trim();
            if (_t3Address != normalized)
            {
                _t3Address = normalized;
                OnPropertyChanged();
                _ = UpdateSettingAsync(s => s.T3Address = normalized);
            }
        }
    }

    public bool ShowAppIcons
    {
        get => _showAppIcons;
        set
        {
            if (_showAppIcons != value)
            {
                _showAppIcons = value;
                OnPropertyChanged();
                _ = UpdateSettingAsync(s => s.ShowAppIcons = value);
            }
        }
    }

    public string T3StatusText => _t3ConnectionStatus switch
    {
        T3ConnectionStatus.OpeningSignIn => "Opening sign-in…",
        T3ConnectionStatus.Connected => "Connected",
        T3ConnectionStatus.SignInAgain => "Sign in again",
        T3ConnectionStatus.CouldNotReach => "Couldn’t reach T3 Code",
        _ => "Not connected"
    };

    public string T3StatusDetail
    {
        get => _t3StatusDetail;
        private set { _t3StatusDetail = value; OnPropertyChanged(); }
    }

    public string T3ConnectButtonText => _t3ConnectionStatus switch
    {
        T3ConnectionStatus.OpeningSignIn => "Cancel",
        T3ConnectionStatus.Connected => "Disconnect",
        _ => "Connect T3 Code"
    };

    public string T3ConnectButtonAutomationName => _t3ConnectionStatus switch
    {
        T3ConnectionStatus.OpeningSignIn => "Cancel T3 sign-in",
        T3ConnectionStatus.Connected => "Disconnect T3 Code",
        _ => "Connect T3 Code"
    };

    public bool ShowT3Consent => !IsT3Connected && !IsT3Connecting;
    public bool IsT3AddressEditable => !IsT3Connecting;
    public string PrivacyFooterText => ShowT3Activity
        ? "Discord sees the app name and, while T3 Code is active, an agent tool name with task and helper counts. Conversations, thread titles, projects, models, and files are never shared. Close this window to keep running in the notification area."
        : "Discord sees only the name of the app you’re using. Conversations, window titles, files, and projects are never shared. Close this window to keep running in the notification area.";

    public bool IsT3Connecting => _t3ConnectionStatus == T3ConnectionStatus.OpeningSignIn;
    public bool IsT3Connected => _t3ConnectionStatus == T3ConnectionStatus.Connected;

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

    public string? OutgoingStateText
    {
        get => _outgoingStateText;
        private set { _outgoingStateText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasOutgoingState)); }
    }

    public bool HasOutgoingState => !string.IsNullOrEmpty(_outgoingStateText);

    public string? ActivityNoteText
    {
        get => _activityNoteText;
        private set { _activityNoteText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasActivityNote)); }
    }

    public bool HasActivityNote => !string.IsNullOrEmpty(_activityNoteText);

    public string LastUpdateText
    {
        get => _lastUpdateText;
        private set { _lastUpdateText = value; OnPropertyChanged(); }
    }

    public bool HasLastUpdate
    {
        get => _hasLastUpdate;
        private set { _hasLastUpdate = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public string? StatusRecoveryText
    {
        get => _statusRecoveryText;
        private set { _statusRecoveryText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasStatusRecovery)); }
    }
    public bool HasStatusRecovery => !string.IsNullOrEmpty(_statusRecoveryText);

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
    public ICommand ConnectT3Command { get; }
    public ICommand DisconnectT3Command { get; }
    public ICommand CancelT3Command { get; }
    public ICommand AddMappingCommand { get; }
    public ICommand RemoveMappingCommand { get; }

    public MainViewModel(
        ISettingsService settingsService,
        PresenceCoordinator coordinator,
        Action<Action>? uiDispatcher = null,
        IT3ActivityService? t3ActivityService = null,
        IComputerLinkService? computers = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _uiDispatcher = uiDispatcher;
        _t3ActivityService = t3ActivityService;
        _computers = computers;
        LinkComputerCommand = new RelayCommand(() => _ = RunComputerActionAsync(async () => {
            if (_computers != null) await _computers.LinkAsync(LinkingAddress, LinkingCode, ComputerName);
            LinkingCode = "";
        }));
        CreateComputerCodeCommand = new RelayCommand(() => _ = RunComputerActionAsync(async () => {
            if (_computers != null) await _computers.CreateCodeAsync();
        }));
        UnlinkComputerCommand = new RelayCommand(() => _ = RunComputerActionAsync(async () => {
            if (_computers != null) await _computers.UnlinkAsync();
        }));
        if (_computers != null) _computers.Changed += OnComputerChanged;

        TogglePauseCommand = new RelayCommand(() => { if (IsPauseEnabled) IsPaused = !IsPaused; });
        ConnectT3Command = new RelayCommand(OnConnectT3Clicked);
        DisconnectT3Command = new RelayCommand(OnDisconnectT3Clicked);
        CancelT3Command = new RelayCommand(OnCancelT3Clicked);
        AddMappingCommand = new RelayCommand(AddProcessMapping);
        RemoveMappingCommand = new RelayCommand<ProcessMappingItem>(RemoveProcessMapping);

        LoadInitialData();
        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;

        if (_t3ActivityService != null)
        {
            _t3ActivityService.StatusChanged += OnT3StatusChanged;
        }
    }

    private void LoadInitialData()
    {
        var s = _settingsService.Settings;
        _enableSharing = s.EnableSharing;
        _isPaused = s.IsPaused;
        _discordClientId = s.DiscordClientId;
        _showT3Activity = s.ShowT3Activity;
        _t3Address = s.T3Address;
        _showAppIcons = s.ShowAppIcons;
        if (string.IsNullOrEmpty(_linkingAddress)) LinkingAddress = s.ComputerLinkAddress;

        OnPropertyChanged(nameof(EnableSharing));
        OnPropertyChanged(nameof(IsPauseEnabled));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(DiscordClientId));
        OnPropertyChanged(nameof(ShowT3Activity));
        OnPropertyChanged(nameof(PrivacyFooterText));
        OnPropertyChanged(nameof(T3Address));
        OnPropertyChanged(nameof(ShowAppIcons));
        ValidateClientId();

        ProcessMappings.Clear();
        foreach (var (exe, app) in s.ProcessMappings)
        {
            ProcessMappings.Add(new ProcessMappingItem(exe, app));
        }

        if (_t3ActivityService != null)
        {
            _t3ConnectionStatus = _t3ActivityService.ConnectionStatus;
            _t3StatusDetail = _t3ActivityService.StatusDetail;
            OnPropertyChanged(nameof(T3StatusText));
            OnPropertyChanged(nameof(T3StatusDetail));
            OnPropertyChanged(nameof(T3ConnectButtonText));
            OnPropertyChanged(nameof(IsT3Connecting));
            OnPropertyChanged(nameof(IsT3Connected));
            OnPropertyChanged(nameof(T3ConnectButtonAutomationName));
            OnPropertyChanged(nameof(ShowT3Consent));
            OnPropertyChanged(nameof(IsT3AddressEditable));
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

    private async Task RunComputerActionAsync(Func<Task> action)
    {
        if (_computerBusy) return;
        _computerBusy = true; OnPropertyChanged(nameof(CanChangeComputerLink));
        try { await action(); }
        catch { StatusMessage = "Couldn’t update your computer link. Check the connection and try again."; }
        finally { _computerBusy = false; OnComputerChanged(); }
    }

    private void OnComputerChanged() => Dispatch(() => {
        OnPropertyChanged(nameof(ComputerStatus)); OnPropertyChanged(nameof(ComputerPairingCode));
        OnPropertyChanged(nameof(HasComputerPairingCode)); OnPropertyChanged(nameof(HasComputerLink));
        OnPropertyChanged(nameof(ShowComputerLinkForm)); OnPropertyChanged(nameof(CanChangeComputerLink));
        OnPropertyChanged(nameof(ShowUnlinkComputer));
    });

    private void OnCoordinatorStateChanged(PresenceState state)
    {
        Dispatch(() => UpdateFromPresenceState(state));
    }

    private void OnT3StatusChanged(T3ConnectionStatus status, string detail)
    {
        Dispatch(() =>
        {
            _t3ConnectionStatus = status;
            _t3StatusDetail = detail;
            OnPropertyChanged(nameof(T3StatusText));
            OnPropertyChanged(nameof(T3StatusDetail));
            OnPropertyChanged(nameof(T3ConnectButtonText));
            OnPropertyChanged(nameof(IsT3Connecting));
            OnPropertyChanged(nameof(IsT3Connected));
            OnPropertyChanged(nameof(T3ConnectButtonAutomationName));
            OnPropertyChanged(nameof(ShowT3Consent));
            OnPropertyChanged(nameof(IsT3AddressEditable));
        });
    }

    private void UpdateFromPresenceState(PresenceState state)
    {
        ConnectionStatusText = state.ConnectionStatus switch
        {
            PresenceConnectionStatus.Connected => "Connected",
            PresenceConnectionStatus.Connecting => "Connecting…",
            PresenceConnectionStatus.Error => "Couldn’t connect",
            _ => "Not connected"
        };

        ForegroundAppText = state.ForegroundApp switch
        {
            SupportedApp.T3Code => "T3 Code",
            SupportedApp.Claude => "Claude Desktop",
            SupportedApp.Codex => "Codex Desktop",
            _ => "No supported app"
        };

        OutgoingHeadlineText = state.OutgoingHeadline == ActivityFormatter.HeadlineNone
            ? "Not shown on Discord"
            : state.OutgoingHeadline;

        OutgoingStateText = state.OutgoingState;
        ActivityNoteText = state.ActivityNote;

        if (state.LastUpdate.HasValue)
        {
            LastUpdateText = $"Last updated {state.LastUpdate.Value.ToLocalTime():t}";
            HasLastUpdate = true;
        }
        else
        {
            LastUpdateText = string.Empty;
            HasLastUpdate = false;
        }

        var status = UserFacingStatus.Describe(state, _settingsService.Settings,
            _coordinator.IsSessionLocked, _coordinator.IsSystemSuspended);
        StatusMessage = status.Title;
        StatusRecoveryText = status.Recovery;

        _t3ConnectionStatus = state.T3ConnectionStatus;
        _t3StatusDetail = state.T3StatusDetail;
        OnPropertyChanged(nameof(T3StatusText));
        OnPropertyChanged(nameof(T3StatusDetail));
        OnPropertyChanged(nameof(T3ConnectButtonText));
        OnPropertyChanged(nameof(IsT3Connecting));
        OnPropertyChanged(nameof(IsT3Connected));
            OnPropertyChanged(nameof(T3ConnectButtonAutomationName));
            OnPropertyChanged(nameof(ShowT3Consent));
            OnPropertyChanged(nameof(IsT3AddressEditable));
    }

    private void OnConnectT3Clicked()
    {
        if (_t3ConnectionStatus == T3ConnectionStatus.OpeningSignIn)
        {
            OnCancelT3Clicked();
            return;
        }

        if (_t3ConnectionStatus == T3ConnectionStatus.Connected)
        {
            OnDisconnectT3Clicked();
            return;
        }

        if (_t3ActivityService != null)
        {
            _ = _t3ActivityService.ConnectAsync(T3Address);
        }
    }

    private void OnDisconnectT3Clicked()
    {
        if (_t3ActivityService != null)
        {
            _ = _t3ActivityService.DisconnectAsync();
        }
    }

    private void OnCancelT3Clicked()
    {
        if (_t3ActivityService != null)
        {
            _ = _t3ActivityService.CancelSignInAsync();
        }
    }

    private async Task UpdateSettingAsync(Action<PresenceSettings> action)
    {
        try
        {
            await _settingsService.UpdateSettingsAsync(action).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Dispatch(() => StatusMessage = "Couldn’t save your settings. Try again. If it keeps happening, quit and reopen Developer Presence.");
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
            StatusMessage = "Enter only the file name, like example.exe, without a folder path.";
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
        if (_computers != null) _computers.Changed -= OnComputerChanged;

        if (_t3ActivityService != null)
        {
            _t3ActivityService.StatusChanged -= OnT3StatusChanged;
        }
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
