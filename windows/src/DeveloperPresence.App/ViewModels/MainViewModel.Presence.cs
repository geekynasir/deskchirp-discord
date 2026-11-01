using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;
using DeveloperPresence.Core.Services;
using DeveloperPresence.Core.T3;
using DeveloperPresence.Core.T3.Usage;

namespace DeveloperPresence.App.ViewModels;

public abstract class ObservableItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One T3 environment row. The label is local and never published.</summary>
public sealed class EnvironmentItem : ObservableItem
{
    private string _label;
    private T3ConnectionStatus _status;
    private string _detail = string.Empty;
    private bool _usageAllowed;
    private string? _usageStatus;
    private bool _showPairing;
    private string _pairingMessage = string.Empty;
    private bool _isPairing;

    public EnvironmentItem(string id, string address, string label)
    {
        Id = id;
        Address = address;
        _label = label;
    }

    public string Id { get; }
    public string Address { get; }
    internal Action<EnvironmentItem>? LabelEdited { get; set; }
    /// <summary>The pasted pairing code or link. Kept in memory only until pairing finishes.</summary>
    public string PairingInput { get; set; } = string.Empty;

    public string Label
    {
        get => _label;
        set
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (Set(ref _label, trimmed))
            {
                Raise(nameof(AllowAutomationName)); Raise(nameof(RemoveAutomationName)); Raise(nameof(UsageAutomationName));
                LabelEdited?.Invoke(this);
            }
        }
    }

    public T3ConnectionStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(StatusText)); Raise(nameof(AllowButtonText)); Raise(nameof(IsAllowVisible)); Raise(nameof(IsConnected));
            }
        }
    }

    public string Detail { get => _detail; set => Set(ref _detail, value); }

    public string StatusText => _status switch
    {
        T3ConnectionStatus.OpeningSignIn => "Opening sign-in…",
        T3ConnectionStatus.Connected => "Connected",
        T3ConnectionStatus.SignInAgain => "Sign in again",
        T3ConnectionStatus.CouldNotReach => "Couldn’t reach T3 Code",
        _ => "Not connected"
    };

    public bool IsConnected => _status == T3ConnectionStatus.Connected;
    public bool IsAllowVisible => _status != T3ConnectionStatus.Connected;
    public string AllowButtonText => _status == T3ConnectionStatus.OpeningSignIn ? "Cancel" : "Allow";
    public string AllowAutomationName => _status == T3ConnectionStatus.OpeningSignIn ? $"Cancel sign-in for {DisplayLabel}" : $"Allow T3 activity for {DisplayLabel}";
    public string RemoveAutomationName => $"Remove {DisplayLabel}";
    public string UsageAutomationName => _usageAllowed ? $"Remove token access for {DisplayLabel}" : $"Allow token usage for {DisplayLabel}";
    public string PairAutomationName => $"Pair token usage for {DisplayLabel}";
    public string DisplayLabel => string.IsNullOrWhiteSpace(_label) ? PresenceSettings.DefaultLabel(Address) : _label;

    public bool UsageAllowed
    {
        get => _usageAllowed;
        set
        {
            if (Set(ref _usageAllowed, value))
            {
                Raise(nameof(UsageButtonText)); Raise(nameof(UsageAutomationName)); Raise(nameof(UsageStatusText));
                if (value) ShowPairing = false;
            }
        }
    }

    public string? UsageStatus
    {
        get => _usageStatus;
        set { if (Set(ref _usageStatus, value)) Raise(nameof(UsageStatusText)); }
    }

    public string UsageStatusText => _usageStatus ?? (_usageAllowed ? T3UsageText.Allowed : T3UsageText.NotAllowed);
    public string UsageButtonText => _usageAllowed ? "Remove token access" : "Allow token usage";
    public bool ShowPairing { get => _showPairing; set => Set(ref _showPairing, value); }
    public string PairingMessage { get => _pairingMessage; set { if (Set(ref _pairingMessage, value)) Raise(nameof(HasPairingMessage)); } }
    public bool HasPairingMessage => !string.IsNullOrEmpty(_pairingMessage);
    public bool IsPairing { get => _isPairing; set { if (Set(ref _isPairing, value)) Raise(nameof(CanPair)); } }
    public bool CanPair => !_isPairing;

    // Fixed copy from spec/presence-v3.md §6 and spec/t3-usage-transport.md, for the item template.
    public string UsageExplanation => T3UsageText.Explanation;
    public string PairCommandText => T3UsageText.PairCommand;
    public string PermissionWording => T3UsageText.PermissionWording;
}

/// <summary>A choice in a local picker. Display text never leaves settings.</summary>
public sealed class PickerOption
{
    public PickerOption(string key, string display) { Key = key; Display = display; }
    public string Key { get; }
    public string Display { get; }
    public override string ToString() => Display;
}

/// <summary>A saved alias or exclusion row.</summary>
public sealed class KeyedEntry
{
    public KeyedEntry(string key, string display, string detail, string removeName)
    {
        Key = key; Display = display; Detail = detail; RemoveAutomationName = removeName;
    }
    public string Key { get; }
    public string Display { get; }
    public string Detail { get; }
    public string RemoveAutomationName { get; }
}

/// <summary>One outgoing Discord field in the preview.</summary>
public sealed class PreviewRowItem
{
    public PreviewRowItem(string label, string value, string sourceText, bool fitWarning)
    {
        Label = label; Value = value; SourceText = sourceText; HasFitWarning = fitWarning;
    }
    public string Label { get; }
    public string Value { get; }
    public string SourceText { get; }
    public bool HasFitWarning { get; }
    public string FitWarning => PresenceText.FitWarning;
    public string AutomationName => HasFitWarning ? $"{Label}: {Value}. {SourceText}. {FitWarning}" : $"{Label}: {Value}. {SourceText}.";
}

public sealed partial class MainViewModel
{
    private readonly IT3UsageService? _usageService;
    private bool _syncingPickers;
    private CardStyle _cardStyle;
    private UsageMetric _usageMetric;
    private UsageScope _usageScope;
    private TokenCategory _tokenCategory;
    private TokenRounding _tokenRounding;
    private SmallBadge _smallBadge;
    private bool _shareProjectAlias;
    private bool _shareModel;
    private bool _shareEstimatedCost;
    private bool _showElapsedTimer = true;
    private bool _playfulWording;
    private PresenceButtons _buttons;
    private string _publicRepoUrl = string.Empty;
    private SelectionMode _selectionMode;
    private int _aliasProjectIndex = -1;
    private string _newAliasText = string.Empty;
    private int _exclusionProjectIndex = -1;
    private int _pinnedTaskIndex;
    private int _pinnedProjectIndex;
    private string _newEnvironmentAddress = string.Empty;
    private string? _addEnvironmentError;

    public ObservableCollection<EnvironmentItem> Environments { get; } = new();
    public ObservableCollection<PickerOption> ProjectOptions { get; } = new();
    public ObservableCollection<PickerOption> PinnedProjectOptions { get; } = new();
    public ObservableCollection<PickerOption> TaskOptions { get; } = new();
    public ObservableCollection<KeyedEntry> Aliases { get; } = new();
    public ObservableCollection<KeyedEntry> Exclusions { get; } = new();
    public ObservableCollection<PreviewRowItem> PreviewRows { get; } = new();

    public ICommand AddEnvironmentCommand { get; private set; } = null!;
    public ICommand AddAliasCommand { get; private set; } = null!;
    public ICommand AddExclusionCommand { get; private set; } = null!;

    public string UsageExplanation => T3UsageText.Explanation;
    public string PairCommandText => T3UsageText.PairCommand;
    public string UsagePermissionWording => T3UsageText.PermissionWording;
    public string RepoUrlErrorText => PresenceText.RepoUrlInvalid;

    private void InitializePresenceControls()
    {
        AddEnvironmentCommand = new RelayCommand(() => _ = AddEnvironmentAsync());
        AddAliasCommand = new RelayCommand(AddAlias);
        AddExclusionCommand = new RelayCommand(AddExclusion);
        if (_t3ActivityService != null) _t3ActivityService.EnvironmentsChanged += OnEnvironmentsChanged;
        if (_usageService != null) _usageService.Changed += OnEnvironmentsChanged;
    }

    private void DisposePresenceControls()
    {
        if (_t3ActivityService != null) _t3ActivityService.EnvironmentsChanged -= OnEnvironmentsChanged;
        if (_usageService != null) _usageService.Changed -= OnEnvironmentsChanged;
    }

    private void OnEnvironmentsChanged() => Dispatch(() =>
    {
        if (_disposed) return;
        RefreshEnvironmentStatus();
        RefreshPickers(_settingsService.Settings);
        NotifyAvailability();
    });

    private void LoadPresenceControls(PresenceSettings s)
    {
        _cardStyle = s.CardStyle;
        _usageMetric = s.UsageMetric;
        _usageScope = s.UsageScope;
        _tokenCategory = s.TokenCategory;
        _tokenRounding = s.TokenRounding;
        _smallBadge = s.SmallBadge;
        _shareProjectAlias = s.ShareProjectAlias;
        _shareModel = s.ShareModel;
        _shareEstimatedCost = s.ShareEstimatedCost;
        _showElapsedTimer = s.ShowElapsedTimer;
        _playfulWording = s.PlayfulWording;
        _buttons = s.Buttons;
        if (!string.Equals(_publicRepoUrl, s.PublicRepoUrl, StringComparison.Ordinal)) _publicRepoUrl = s.PublicRepoUrl;
        _selectionMode = s.SelectionMode;
        foreach (var name in new[] { nameof(CardStyleIndex), nameof(UsageMetricIndex), nameof(UsageScopeIndex), nameof(TokenCategoryIndex),
            nameof(TokenRoundingIndex), nameof(SmallBadgeIndex), nameof(ShareProjectAlias), nameof(ShareModel), nameof(ShareEstimatedCost),
            nameof(ShowElapsedTimer), nameof(PlayfulWording), nameof(ButtonsIndex), nameof(PublicRepoUrl), nameof(SelectionModeIndex) })
            OnPropertyChanged(name);
        SyncEnvironmentItems(s);
        RefreshPickers(s);
        NotifyAvailability();
    }

    // ---- Card style and usage options ----

    private bool AnyEnvironmentConnected => _t3ActivityService?.Environments.Any(e => e.Status == T3ConnectionStatus.Connected) == true;
    private bool AnyUsagePermission => _usageService?.HasAnyPermission == true;

    private OptionAvailability StyleAvailability(CardStyle style) =>
        PresenceAvailability.CardStyleOption(style, _settingsService.Settings, AnyEnvironmentConnected, AnyUsagePermission);

    public int CardStyleIndex
    {
        get => _cardStyle switch { CardStyle.AppOnly => 0, CardStyle.Usage => 2, _ => 1 };
        set
        {
            if (value < 0) return;
            var style = value switch { 0 => CardStyle.AppOnly, 2 => CardStyle.Usage, _ => CardStyle.Workload };
            if (style == _cardStyle) return;
            _cardStyle = style;
            OnPropertyChanged();
            NotifyAvailability();
            _ = UpdateSettingAsync(s => s.CardStyle = style);
        }
    }

    public bool IsWorkloadStyleEnabled => StyleAvailability(CardStyle.Workload).Enabled;
    public bool IsUsageStyleEnabled => StyleAvailability(CardStyle.Usage).Enabled;
    public string? CardStyleReason => StyleAvailability(CardStyle.Workload).Reason ?? StyleAvailability(CardStyle.Usage).Reason;
    public bool HasCardStyleReason => !string.IsNullOrEmpty(CardStyleReason);
    public bool IsUsageStyle => _cardStyle == CardStyle.Usage;

    public int UsageMetricIndex
    {
        get => _usageMetric == UsageMetric.Context ? 1 : 0;
        set
        {
            if (value < 0) return;
            var metric = value == 1 ? UsageMetric.Context : UsageMetric.Tokens;
            if (metric == _usageMetric) return;
            _usageMetric = metric;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UsageScopeIndex));
            NotifyAvailability();
            _ = UpdateSettingAsync(s => s.UsageMetric = metric);
        }
    }

    /// <summary>Shows the effective scope: context is always the pinned task.</summary>
    public int UsageScopeIndex
    {
        get => (_usageMetric == UsageMetric.Context ? UsageScope.PinnedTask : _usageScope) switch
        {
            UsageScope.Session => 1, UsageScope.PinnedTask => 2, _ => 0
        };
        set
        {
            if (value < 0 || _usageMetric == UsageMetric.Context) return;
            var scope = value switch { 1 => UsageScope.Session, 2 => UsageScope.PinnedTask, _ => UsageScope.Today };
            if (scope == _usageScope) return;
            _usageScope = scope;
            OnPropertyChanged();
            NotifyAvailability();
            _ = UpdateSettingAsync(s => s.UsageScope = scope);
        }
    }

    public bool IsScopeEditable => _usageMetric == UsageMetric.Tokens;
    public bool IsTodayScopeEnabled => PresenceAvailability.ScopeOption(UsageScope.Today, _settingsService.Settings).Enabled;
    public bool IsSessionScopeEnabled => PresenceAvailability.ScopeOption(UsageScope.Session, _settingsService.Settings).Enabled;
    public bool IsPinnedScopeEnabled => _usageMetric == UsageMetric.Context ||
        PresenceAvailability.ScopeOption(UsageScope.PinnedTask, _settingsService.Settings).Enabled;
    public string? ScopeReason => PresenceAvailability.ScopeReason(_settingsService.Settings);
    public bool HasScopeReason => !string.IsNullOrEmpty(ScopeReason);
    public bool ShowTokenOptions => IsUsageStyle && _usageMetric == UsageMetric.Tokens;

    public int TokenCategoryIndex
    {
        get => _tokenCategory switch { TokenCategory.Input => 1, TokenCategory.Output => 2, _ => 0 };
        set
        {
            if (value < 0) return;
            var category = value switch { 1 => TokenCategory.Input, 2 => TokenCategory.Output, _ => TokenCategory.Total };
            if (category == _tokenCategory) return;
            _tokenCategory = category;
            OnPropertyChanged();
            _ = UpdateSettingAsync(s => s.TokenCategory = category);
        }
    }

    public int TokenRoundingIndex
    {
        get => _tokenRounding == TokenRounding.Exact ? 1 : 0;
        set
        {
            if (value < 0) return;
            var rounding = value == 1 ? TokenRounding.Exact : TokenRounding.Compact;
            if (rounding == _tokenRounding) return;
            _tokenRounding = rounding;
            OnPropertyChanged();
            _ = UpdateSettingAsync(s => s.TokenRounding = rounding);
        }
    }

    public bool ShareEstimatedCost
    {
        get => _shareEstimatedCost;
        set
        {
            if (_shareEstimatedCost == value) return;
            _shareEstimatedCost = value;
            OnPropertyChanged();
            _ = UpdateSettingAsync(s => s.ShareEstimatedCost = value);
        }
    }

    public bool ShowCostToggle => PresenceAvailability.ShowEstimatedCost(_settingsService.Settings);

    public int SmallBadgeIndex
    {
        get => _smallBadge switch { SmallBadge.Provider => 1, SmallBadge.None => 2, _ => 0 };
        set
        {
            if (value < 0) return;
            var badge = value switch { 1 => SmallBadge.Provider, 2 => SmallBadge.None, _ => SmallBadge.Status };
            if (badge == _smallBadge) return;
            _smallBadge = badge;
            OnPropertyChanged();
            _ = UpdateSettingAsync(s => s.SmallBadge = badge);
        }
    }

    public bool ShareProjectAlias
    {
        get => _shareProjectAlias;
        set
        {
            if (_shareProjectAlias == value) return;
            _shareProjectAlias = value;
            OnPropertyChanged();
            NotifyAvailability();
            _ = UpdateSettingAsync(s => s.ShareProjectAlias = value);
        }
    }

    public bool ShareModel
    {
        get => _shareModel;
        set
        {
            if (_shareModel == value) return;
            _shareModel = value;
            OnPropertyChanged();
            _ = UpdateSettingAsync(s => s.ShareModel = value);
        }
    }

    public bool ShowElapsedTimer
    {
        get => _showElapsedTimer;
        set
        {
            if (_showElapsedTimer == value) return;
            _showElapsedTimer = value;
            OnPropertyChanged();
            _ = UpdateSettingAsync(s => s.ShowElapsedTimer = value);
        }
    }

    public bool PlayfulWording
    {
        get => _playfulWording;
        set
        {
            if (_playfulWording == value) return;
            _playfulWording = value;
            OnPropertyChanged();
            _ = UpdateSettingAsync(s => s.PlayfulWording = value);
        }
    }

    // ---- Buttons ----

    public int ButtonsIndex
    {
        get => _buttons switch { PresenceButtons.Companion => 1, PresenceButtons.CompanionAndRepo => 2, _ => 0 };
        set
        {
            if (value < 0) return;
            var buttons = value switch { 1 => PresenceButtons.Companion, 2 => PresenceButtons.CompanionAndRepo, _ => PresenceButtons.None };
            if (buttons == _buttons) return;
            _buttons = buttons;
            OnPropertyChanged();
            NotifyAvailability();
            _ = UpdateSettingAsync(s => s.Buttons = buttons);
        }
    }

    public bool ShowRepoUrlField => _buttons == PresenceButtons.CompanionAndRepo;
    public string? ButtonsReason => _buttons == PresenceButtons.CompanionAndRepo && !_shareProjectAlias ? PresenceAvailability.ReasonShareAlias : null;
    public bool HasButtonsReason => !string.IsNullOrEmpty(ButtonsReason);

    public string PublicRepoUrl
    {
        get => _publicRepoUrl;
        set
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (string.Equals(_publicRepoUrl, trimmed, StringComparison.Ordinal)) return;
            _publicRepoUrl = trimmed;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowRepoUrlError));
            _ = UpdateSettingAsync(s => s.PublicRepoUrl = trimmed);
        }
    }

    public bool ShowRepoUrlError => !string.IsNullOrEmpty(_publicRepoUrl) && !PresenceText.IsValidPublicRepoUrl(_publicRepoUrl);

    // ---- Selection ----

    public int SelectionModeIndex
    {
        get => _selectionMode == SelectionMode.FollowActiveApp ? 1 : 0;
        set
        {
            if (value < 0) return;
            var mode = value == 1 ? SelectionMode.FollowActiveApp : SelectionMode.KeepBackgroundVisible;
            if (mode == _selectionMode) return;
            _selectionMode = mode;
            OnPropertyChanged();
            _ = UpdateSettingAsync(s => s.SelectionMode = mode);
        }
    }

    private void NotifyAvailability()
    {
        foreach (var name in new[] { nameof(IsWorkloadStyleEnabled), nameof(IsUsageStyleEnabled), nameof(CardStyleReason), nameof(HasCardStyleReason),
            nameof(IsUsageStyle), nameof(IsScopeEditable), nameof(IsTodayScopeEnabled), nameof(IsSessionScopeEnabled), nameof(IsPinnedScopeEnabled),
            nameof(ScopeReason), nameof(HasScopeReason), nameof(ShowTokenOptions), nameof(ShowCostToggle), nameof(ShowRepoUrlField),
            nameof(ButtonsReason), nameof(HasButtonsReason), nameof(ShowRepoUrlError), nameof(UsageScopeIndex) })
            OnPropertyChanged(name);
    }

    // ---- Environments ----

    public string NewEnvironmentAddress
    {
        get => _newEnvironmentAddress;
        set { _newEnvironmentAddress = value ?? string.Empty; OnPropertyChanged(); }
    }

    public string? AddEnvironmentError
    {
        get => _addEnvironmentError;
        private set { _addEnvironmentError = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasAddEnvironmentError)); }
    }

    public bool HasAddEnvironmentError => !string.IsNullOrEmpty(_addEnvironmentError);
    public bool HasEnvironments => Environments.Count > 0;

    private async Task AddEnvironmentAsync()
    {
        if (_t3ActivityService == null) return;
        try
        {
            string? error = await _t3ActivityService.AddEnvironmentAsync(NewEnvironmentAddress);
            Dispatch(() =>
            {
                AddEnvironmentError = error;
                if (error == null) NewEnvironmentAddress = string.Empty;
            });
        }
        catch
        {
            Dispatch(() => AddEnvironmentError = "Couldn’t save your settings. Try again. If it keeps happening, quit and reopen Deskchirp.");
        }
    }

    private void SyncEnvironmentItems(PresenceSettings s)
    {
        var ids = s.T3Environments.Select(e => e.Id).ToList();
        for (int i = Environments.Count - 1; i >= 0; i--)
            if (!ids.Contains(Environments[i].Id)) Environments.RemoveAt(i);
        for (int i = 0; i < s.T3Environments.Count; i++)
        {
            var env = s.T3Environments[i];
            var item = Environments.FirstOrDefault(e => e.Id == env.Id);
            if (item == null || item.Address != env.Address)
            {
                if (item != null) Environments.Remove(item);
                item = new EnvironmentItem(env.Id, env.Address, env.Label) { LabelEdited = OnEnvironmentLabelEdited };
                Environments.Insert(Math.Min(i, Environments.Count), item);
            }
            else if (Environments.IndexOf(item) != i)
            {
                Environments.Move(Environments.IndexOf(item), Math.Min(i, Environments.Count - 1));
            }
        }
        OnPropertyChanged(nameof(HasEnvironments));
        RefreshEnvironmentStatus();
    }

    private void RefreshEnvironmentStatus()
    {
        var statuses = _t3ActivityService?.Environments ?? Array.Empty<T3EnvironmentStatus>();
        foreach (var item in Environments)
        {
            var status = statuses.FirstOrDefault(x => x.EnvironmentId == item.Id);
            item.Status = status?.Status ?? T3ConnectionStatus.NotConnected;
            item.Detail = status?.Detail ?? string.Empty;
            item.UsageAllowed = _usageService?.HasPermission(item.Id) == true;
            item.UsageStatus = _usageService?.StatusFor(item.Id);
        }
    }

    private void OnEnvironmentLabelEdited(EnvironmentItem item)
    {
        string label = item.Label;
        _ = UpdateSettingAsync(s =>
        {
            var env = s.T3Environments.FirstOrDefault(e => e.Id == item.Id);
            if (env != null) env.Label = label;
        });
    }

    public void AllowEnvironment(EnvironmentItem item)
    {
        if (_t3ActivityService == null) return;
        if (item.Status == T3ConnectionStatus.OpeningSignIn)
        {
            _ = _t3ActivityService.CancelSignInAsync();
            return;
        }
        _ = ConnectEnvironmentAsync(item.Id);
    }

    private async Task ConnectEnvironmentAsync(string id)
    {
        try
        {
            bool connected = await _t3ActivityService!.ConnectEnvironmentAsync(id);
            if (connected && !_disposed) Dispatch(() => ShowT3Activity = true);
        }
        catch { /* The service reports a plain status for this environment. */ }
    }

    public void RemoveEnvironment(EnvironmentItem item)
    {
        if (_t3ActivityService == null) return;
        _ = RemoveEnvironmentAsync(item);
    }

    private async Task RemoveEnvironmentAsync(EnvironmentItem item)
    {
        try
        {
            // Removing an environment deletes both its sign-in and its token access.
            if (_usageService != null) await _usageService.RemoveTokenAccessAsync(item.Id);
            await _t3ActivityService!.RemoveEnvironmentAsync(item.Id);
        }
        catch
        {
            Dispatch(() => StatusMessage = "Couldn’t remove that environment. Try again.");
        }
    }

    public void ToggleUsage(EnvironmentItem item)
    {
        if (_usageService == null) return;
        if (item.UsageAllowed)
        {
            _ = RemoveTokenAccessAsync(item);
            return;
        }
        item.ShowPairing = !item.ShowPairing;
        item.PairingMessage = string.Empty;
    }

    private async Task RemoveTokenAccessAsync(EnvironmentItem item)
    {
        try { await _usageService!.RemoveTokenAccessAsync(item.Id); }
        catch { }
        Dispatch(RefreshEnvironmentStatus);
    }

    public void PairEnvironment(EnvironmentItem item)
    {
        if (_usageService == null || item.IsPairing) return;
        _ = PairEnvironmentAsync(item);
    }

    private async Task PairEnvironmentAsync(EnvironmentItem item)
    {
        string input = item.PairingInput;
        item.IsPairing = true;
        item.PairingMessage = "Pairing…";
        T3PairingResult result;
        try { result = await _usageService!.PairAsync(item.Id, input); }
        catch { result = new T3PairingResult(T3PairingOutcome.Failed, T3UsageText.Failed); }
        Dispatch(() =>
        {
            item.IsPairing = false;
            item.PairingMessage = result.Outcome == T3PairingOutcome.Success ? string.Empty : result.Message;
            if (result.Outcome == T3PairingOutcome.Success) item.PairingInput = string.Empty;
            RefreshEnvironmentStatus();
            NotifyAvailability();
        });
    }

    // ---- Local pickers: aliases, exclusions, pins. Titles stay in this window. ----

    private string EnvironmentLabel(string environmentId)
    {
        var env = _settingsService.Settings.T3Environments.FirstOrDefault(e => e.Id == environmentId);
        return env == null ? "Removed environment" : string.IsNullOrWhiteSpace(env.Label) ? PresenceSettings.DefaultLabel(env.Address) : env.Label;
    }

    private Dictionary<string, string> ProjectTitles()
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_t3ActivityService == null) return titles;
        foreach (var p in _t3ActivityService.PickerProjects)
            titles[T3WorkloadView.Key(p.EnvironmentId, p.ProjectId)] = string.IsNullOrWhiteSpace(p.Title) ? "Untitled project" : p.Title!.Trim();
        foreach (var t in _t3ActivityService.PickerTasks)
            if (!string.IsNullOrEmpty(t.ProjectId)) titles.TryAdd(T3WorkloadView.Key(t.EnvironmentId, t.ProjectId), "Untitled project");
        return titles;
    }

    private string ProjectDisplay(string key, Dictionary<string, string> titles)
    {
        int slash = key.IndexOf('/');
        string env = slash > 0 ? EnvironmentLabel(key[..slash]) : "Unknown environment";
        return titles.TryGetValue(key, out var title) ? $"{title} · {env}" : $"Project not seen yet · {env}";
    }

    private void RefreshPickers(PresenceSettings s)
    {
        _syncingPickers = true;
        try
        {
            var titles = ProjectTitles();
            var projectKeys = titles.Keys.OrderBy(k => titles[k], StringComparer.CurrentCultureIgnoreCase).ToList();
            ReplaceIfChanged(ProjectOptions, projectKeys.Select(k => new PickerOption(k, ProjectDisplay(k, titles))).ToList());

            var pinnedProjects = new List<PickerOption> { new(string.Empty, "No pinned project") };
            pinnedProjects.AddRange(ProjectOptions);
            if (!string.IsNullOrEmpty(s.PinnedProject) && pinnedProjects.All(o => o.Key != s.PinnedProject))
                pinnedProjects.Add(new PickerOption(s.PinnedProject, ProjectDisplay(s.PinnedProject, titles)));
            ReplaceIfChanged(PinnedProjectOptions, pinnedProjects);

            var tasks = new List<PickerOption> { new(string.Empty, "No pinned task") };
            if (_t3ActivityService != null)
            {
                tasks.AddRange(_t3ActivityService.PickerTasks.Where(t => !t.IsHelper).Take(50).Select(t =>
                    new PickerOption(T3WorkloadView.Key(t.EnvironmentId, t.ThreadId),
                        $"{(string.IsNullOrWhiteSpace(t.Title) ? "Untitled task" : t.Title!.Trim())} · {EnvironmentLabel(t.EnvironmentId)}")));
            }
            if (!string.IsNullOrEmpty(s.PinnedTask) && tasks.All(o => o.Key != s.PinnedTask))
            {
                int slash = s.PinnedTask.IndexOf('/');
                tasks.Add(new PickerOption(s.PinnedTask, $"Pinned task not seen yet · {(slash > 0 ? EnvironmentLabel(s.PinnedTask[..slash]) : "Unknown environment")}"));
            }
            ReplaceIfChanged(TaskOptions, tasks);

            _pinnedTaskIndex = Math.Max(0, TaskOptions.ToList().FindIndex(o => o.Key == s.PinnedTask));
            _pinnedProjectIndex = Math.Max(0, PinnedProjectOptions.ToList().FindIndex(o => o.Key == s.PinnedProject));
            if (_aliasProjectIndex >= ProjectOptions.Count) _aliasProjectIndex = -1;
            if (_exclusionProjectIndex >= ProjectOptions.Count) _exclusionProjectIndex = -1;

            ReplaceIfChanged(Aliases, s.ProjectAliases.OrderBy(p => p.Value, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => new KeyedEntry(p.Key, p.Value, ProjectDisplay(p.Key, titles), $"Remove alias {p.Value}")).ToList());
            ReplaceIfChanged(Exclusions, s.ExcludedProjects.OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => { string d = ProjectDisplay(k, titles); return new KeyedEntry(k, d, string.Empty, $"Stop excluding {d}"); }).ToList());
        }
        finally { _syncingPickers = false; }
        foreach (var name in new[] { nameof(PinnedTaskIndex), nameof(PinnedProjectIndex), nameof(AliasProjectIndex), nameof(ExclusionProjectIndex),
            nameof(HasAliases), nameof(HasExclusions), nameof(HasProjects), nameof(ShowNoProjects) })
            OnPropertyChanged(name);
    }

    private static void ReplaceIfChanged(ObservableCollection<PickerOption> target, List<PickerOption> items)
    {
        if (target.Select(o => (o.Key, o.Display)).SequenceEqual(items.Select(o => (o.Key, o.Display)))) return;
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private static void ReplaceIfChanged(ObservableCollection<KeyedEntry> target, List<KeyedEntry> items)
    {
        if (target.Select(o => (o.Key, o.Display, o.Detail)).SequenceEqual(items.Select(o => (o.Key, o.Display, o.Detail)))) return;
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    public bool HasProjects => ProjectOptions.Count > 0;
    public bool ShowNoProjects => ProjectOptions.Count == 0;
    public bool HasAliases => Aliases.Count > 0;
    public bool HasExclusions => Exclusions.Count > 0;

    public int AliasProjectIndex
    {
        get => _aliasProjectIndex;
        set
        {
            if (_syncingPickers || value == _aliasProjectIndex) return;
            _aliasProjectIndex = value;
            OnPropertyChanged();
            if (value >= 0 && value < ProjectOptions.Count &&
                _settingsService.Settings.ProjectAliases.TryGetValue(ProjectOptions[value].Key, out var existing))
                NewAliasText = existing;
        }
    }

    public string NewAliasText
    {
        get => _newAliasText;
        set { _newAliasText = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(AliasPreviewText)); }
    }

    /// <summary>The alias exactly as Discord would receive it (cleaned and capped).</summary>
    public string AliasPreviewText => PresenceText.SanitizeAlias(_newAliasText) is { } alias ? $"Shown as: {alias}" : string.Empty;

    private void AddAlias()
    {
        if (_aliasProjectIndex < 0 || _aliasProjectIndex >= ProjectOptions.Count) return;
        string key = ProjectOptions[_aliasProjectIndex].Key;
        string? alias = PresenceText.SanitizeAlias(_newAliasText);
        _ = UpdateSettingAsync(s =>
        {
            if (alias == null) s.ProjectAliases.Remove(key);
            else s.ProjectAliases[key] = alias;
        });
        NewAliasText = string.Empty;
    }

    public void RemoveAlias(KeyedEntry entry) => _ = UpdateSettingAsync(s => s.ProjectAliases.Remove(entry.Key));

    public int ExclusionProjectIndex
    {
        get => _exclusionProjectIndex;
        set
        {
            if (_syncingPickers || value == _exclusionProjectIndex) return;
            _exclusionProjectIndex = value;
            OnPropertyChanged();
        }
    }

    private void AddExclusion()
    {
        if (_exclusionProjectIndex < 0 || _exclusionProjectIndex >= ProjectOptions.Count) return;
        string key = ProjectOptions[_exclusionProjectIndex].Key;
        _ = UpdateSettingAsync(s => s.ExcludedProjects.Add(key));
    }

    public void RemoveExclusion(KeyedEntry entry) => _ = UpdateSettingAsync(s => s.ExcludedProjects.Remove(entry.Key));

    public int PinnedTaskIndex
    {
        get => _pinnedTaskIndex;
        set
        {
            if (_syncingPickers || value < 0 || value >= TaskOptions.Count || value == _pinnedTaskIndex) return;
            _pinnedTaskIndex = value;
            string key = TaskOptions[value].Key;
            OnPropertyChanged();
            _ = UpdateSettingAsync(s => s.PinnedTask = key);
        }
    }

    public int PinnedProjectIndex
    {
        get => _pinnedProjectIndex;
        set
        {
            if (_syncingPickers || value < 0 || value >= PinnedProjectOptions.Count || value == _pinnedProjectIndex) return;
            _pinnedProjectIndex = value;
            string key = PinnedProjectOptions[value].Key;
            OnPropertyChanged();
            _ = UpdateSettingAsync(s => s.PinnedProject = key);
        }
    }

    // ---- Live preview of every outgoing field ----

    public bool HasPreviewRows => PreviewRows.Count > 0;

    private void UpdatePreview(PresenceState state)
    {
        var rows = state.PreviewRows.Select(r => new PreviewRowItem(FieldLabel(r.Field), FieldValue(r.Field, r.Value), SourceLabel(r.Source), r.FitWarning)).ToList();
        if (PreviewRows.Select(r => (r.Label, r.Value, r.SourceText, r.HasFitWarning))
            .SequenceEqual(rows.Select(r => (r.Label, r.Value, r.SourceText, r.HasFitWarning)))) return;
        PreviewRows.Clear();
        foreach (var row in rows) PreviewRows.Add(row);
        OnPropertyChanged(nameof(HasPreviewRows));
    }

    private static string FieldLabel(string field) => field switch
    {
        "details" => "First line",
        "state" => "Second line",
        "largeImage" => "Large image",
        "largeText" => "Large image hover text",
        "smallImage" => "Badge",
        "smallText" => "Badge hover text",
        "timestamps.start" => "Elapsed time",
        _ when field.StartsWith("buttons[", StringComparison.Ordinal) => field == "buttons[0]" ? "First button" : "Second button",
        _ => "Other"
    };

    private static string FieldValue(string field, string value) => field switch
    {
        "largeImage" or "smallImage" => value switch
        {
            "t3_code" => "T3 Code logo",
            "harness_claude" => "Claude logo",
            "harness_codex" => "Codex logo",
            "harness_antigravity" => "Antigravity logo",
            "harness_cursor" => "Cursor logo",
            "harness_opencode" => "OpenCode logo",
            "harness_grok" => "Grok logo",
            "status_working" => "Working badge",
            "status_attention" => "Waiting badge",
            "status_idle" => "Idle badge",
            _ => "Deskchirp logo"
        },
        "timestamps.start" => long.TryParse(value, out long seconds)
            ? $"Counting from {DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime():t}"
            : value,
        _ => value
    };

    private static string SourceLabel(string source) => source switch
    {
        PreviewSource.T3Code => "From T3 Code",
        PreviewSource.T3Usage => "From T3 token usage",
        _ => "From Deskchirp"
    };
}
