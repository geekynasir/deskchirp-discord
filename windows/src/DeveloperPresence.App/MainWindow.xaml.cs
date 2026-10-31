using System.Runtime.InteropServices;
using DeveloperPresence.App.Interop;
using DeveloperPresence.App.ViewModels;
using DeveloperPresence.Core.Credentials;
using DeveloperPresence.Core.Discord;
using DeveloperPresence.Core.Foreground;
using DeveloperPresence.Core.Models;
using DeveloperPresence.Core.Privacy;
using DeveloperPresence.Core.Services;
using DeveloperPresence.Core.T3;
using DeveloperPresence.Core.Computers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeveloperPresence.App;

public sealed partial class MainWindow : Window
{
    private readonly IntPtr _hWnd;
    private readonly ISettingsService _settingsService;
    private readonly ICredentialStore _credentialStore;
    private readonly IT3ActivityService _t3ActivityService;
    private readonly IDiscordIpcClient _discordClient;
    private readonly IForegroundDetector _foregroundDetector;
    private readonly PresenceCoordinator _coordinator;
    private readonly IComputerLinkService _computers;

    private readonly Win32.WndProcDelegate _wndProcDelegate;
    private readonly IntPtr _oldWndProc;
    private readonly TrayIcon _trayIcon;
    private readonly SessionNotificationManager _sessionNotifications;
    private bool _isExplicitExit;
    private readonly uint _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");

    public MainViewModel ViewModel { get; }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public MainWindow()
    {
        InitializeComponent();

        _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Discord-artwork", "deskchirp.ico");
        if (System.IO.File.Exists(iconPath))
        {
            try { AppWindow.SetIcon(iconPath); }
            catch { /* An unavailable icon must not prevent opening Settings. */ }
        }

        // Instantiate core architecture
        _settingsService = new SettingsService();
        _credentialStore = new WindowsCredentialManager();
        _t3ActivityService = new T3ActivityService(_settingsService, _credentialStore);
        _discordClient = new DiscordIpcClient();
        _foregroundDetector = new ForegroundDetector();
        _computers = new ComputerLinkService(_settingsService, _credentialStore);
        _coordinator = new PresenceCoordinator(_settingsService, _discordClient, _foregroundDetector, _t3ActivityService, _computers);

        // Instantiate ViewModel with UI thread dispatcher
        ViewModel = new MainViewModel(_settingsService, _coordinator, action =>
        {
            DispatcherQueue.TryEnqueue(() => action());
        }, _t3ActivityService, _computers);

        // Initialize Native Tray Icon
        _trayIcon = new TrayIcon(
            _hWnd,
            onShow: RestoreWindow,
            onTogglePause: () =>
            {
                if (ViewModel.IsPauseEnabled)
                {
                    ViewModel.IsPaused = !ViewModel.IsPaused;
                }
            },
            onQuit: ExplicitQuit,
            isPausedProvider: () => ViewModel.IsPaused,
            isSharingEnabledProvider: () => ViewModel.EnableSharing
        );

        // Register for Windows session lock/unlock notifications
        _sessionNotifications = new SessionNotificationManager(_hWnd);

        // Subclass window procedure to intercept WM_CLOSE, WM_WTSSESSION_CHANGE, WM_POWERBROADCAST, and WM_TRAYICON
        _wndProcDelegate = CustomWndProc;
        IntPtr delegatePtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _oldWndProc = Win32.SetWindowLongPtr64(_hWnd, Win32.GWLP_WNDPROC, delegatePtr);

        // Listen for coordinator state changes to update tray tooltip
        _coordinator.StateChanged += OnCoordinatorStateChanged;

        // Start coordinator background loop
        _ = StartCoordinatorAsync();

        // Handle window closed for resource cleanup
        Closed += OnWindowClosed;
    }

    private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            _trayIcon.Recreate();
            return IntPtr.Zero;
        }
        switch (msg)
        {
            case Win32.WM_CLOSE:
                if (!_isExplicitExit && _trayIcon.IsCreated)
                {
                    // Strict requirement: Close minimizes to tray rather than exiting
                    Win32.ShowWindow(hWnd, Win32.SW_HIDE);
                    return IntPtr.Zero;
                }
                break;

            case Win32.WM_TRAYICON:
                _trayIcon.HandleTrayMessage(lParam);
                return IntPtr.Zero;

            case Win32.WM_WTSSESSION_CHANGE:
                uint sessionEvent = (uint)wParam.ToInt64();
                if (sessionEvent == Win32.WTS_SESSION_LOCK ||
                    sessionEvent == Win32.WTS_REMOTE_DISCONNECT ||
                    sessionEvent == Win32.WTS_CONSOLE_DISCONNECT)
                {
                    _coordinator.OnSessionLock();
                }
                else if (sessionEvent == Win32.WTS_SESSION_UNLOCK)
                {
                    _coordinator.OnSessionUnlock();
                }
                break;

            case Win32.WM_POWERBROADCAST:
                uint powerEvent = (uint)wParam.ToInt64();
                if (powerEvent == Win32.PBT_APMSUSPEND)
                {
                    _coordinator.OnSystemSuspend();
                }
                else if (powerEvent == Win32.PBT_APMRESUMEAUTOMATIC || powerEvent == Win32.PBT_APMRESUMESUSPEND)
                {
                    _coordinator.OnSystemResume();
                }
                break;
        }

        return Win32.CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    private void RestoreWindow()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Win32.ShowWindow(_hWnd, Win32.SW_RESTORE);
            Win32.SetForegroundWindow(_hWnd);
        });
    }

    private void ExplicitQuit()
    {
        if (_isExplicitExit) return;
        _isExplicitExit = true;
        DispatcherQueue.TryEnqueue(async () =>
        {
            try { await _coordinator.StopAsync(); }
            catch { /* Socket closure during shutdown is best-effort. */ }
            finally { Close(); }
        });
    }

    private void OnCoordinatorStateChanged(PresenceState state)
    {
        string tooltip;
        if (state.OutgoingHeadline == ActivityFormatter.HeadlineNone)
        {
            tooltip = "Deskchirp\nNot shown on Discord";
        }
        else
        {
            string line = state.OutgoingHeadline;
            if (!string.IsNullOrEmpty(state.OutgoingState))
            {
                line += $" · {state.OutgoingState}";
            }
            tooltip = $"Deskchirp\n{line}";
        }

        if (tooltip.Length > 127)
        {
            tooltip = tooltip.Substring(0, 127);
        }

        DispatcherQueue.TryEnqueue(() => _trayIcon.UpdateTooltip(tooltip));
    }

    private void OnRemoveMappingClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ProcessMappingItem item)
        {
            ViewModel.RemoveMappingCommand.Execute(item);
        }
    }

    private void OnLinkingCodeChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null && sender is PasswordBox box) ViewModel.LinkingCode = box.Password;
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _coordinator.StateChanged -= OnCoordinatorStateChanged;
        ViewModel.Dispose();
        _trayIcon.Dispose();
        _sessionNotifications.Dispose();
        try
        {
            await _coordinator.DisposeAsync();
            await _discordClient.DisposeAsync();
        }
        catch { /* The process is exiting; native handles are already released. */ }
        finally { Application.Current.Exit(); }
    }

    private async Task StartCoordinatorAsync()
    {
        try { await _coordinator.StartAsync(); }
        catch (Exception) { ViewModel.ShowError("Couldn’t start sharing. Quit and reopen Deskchirp."); }
    }
}
