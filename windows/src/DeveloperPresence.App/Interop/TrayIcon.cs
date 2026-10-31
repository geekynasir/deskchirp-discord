using System.Runtime.InteropServices;

namespace DeveloperPresence.App.Interop;

/// <summary>
/// Manages the native Win32 system tray notification icon using Shell_NotifyIcon.
/// Provides native popup context menu (Open, Pause/Resume, Exit) and double-click restore.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint TrayIconId = 1001;
    private const uint CmdShow = 1;
    private const uint CmdTogglePause = 2;
    private const uint CmdQuit = 3;

    private readonly IntPtr _hWnd;
    private readonly Action _onShow;
    private readonly Action _onTogglePause;
    private readonly Action _onQuit;
    private readonly Func<bool> _isPausedProvider;
    private readonly Func<bool> _isSharingEnabledProvider;

    private Win32.NOTIFYICONDATA _nid;
    private IntPtr _ownedIcon;
    private bool _isCreated;
    private bool _isDisposed;

    public bool IsCreated => _isCreated;

    public void Recreate()
    {
        if (!_isDisposed) InitializeTrayIcon();
    }

    public TrayIcon(
        IntPtr hWnd,
        Action onShow,
        Action onTogglePause,
        Action onQuit,
        Func<bool> isPausedProvider,
        Func<bool>? isSharingEnabledProvider = null)
    {
        _hWnd = hWnd;
        _onShow = onShow;
        _onTogglePause = onTogglePause;
        _onQuit = onQuit;
        _isPausedProvider = isPausedProvider;
        _isSharingEnabledProvider = isSharingEnabledProvider ?? (() => true);

        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        if (_ownedIcon == IntPtr.Zero)
        {
            // IMAGE_ICON, LR_LOADFROMFILE | LR_DEFAULTSIZE. Retain through Explorer restarts.
            _ownedIcon = Win32.LoadImage(IntPtr.Zero,
                System.IO.Path.Combine(AppContext.BaseDirectory, "Discord-artwork", "deskchirp.ico"), 1, 0, 0, 0x10 | 0x40);
        }
        IntPtr hIcon = _ownedIcon != IntPtr.Zero ? _ownedIcon : Win32.LoadIcon(IntPtr.Zero, (IntPtr)32512);

        _nid = new Win32.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = TrayIconId,
            uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP,
            uCallbackMessage = Win32.WM_TRAYICON,
            hIcon = hIcon,
            szTip = "Deskchirp\nNot shown on Discord"
        };

        _isCreated = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref _nid);
    }

    /// <summary>
    /// Updates the tooltip displayed when hovering over the tray icon.
    /// Kept within Win32 szTip limit (128 characters max).
    /// </summary>
    public void UpdateTooltip(string text)
    {
        if (!_isCreated || _isDisposed)
        {
            return;
        }

        string safeText = text.Length > 127 ? text.Substring(0, 127) : text;
        _nid.szTip = safeText;
        _nid.uFlags = Win32.NIF_TIP;
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref _nid);
    }

    /// <summary>
    /// Processes tray window callback messages (WM_TRAYICON).
    /// </summary>
    public void HandleTrayMessage(IntPtr lParam)
    {
        int msg = lParam.ToInt32();

        switch (msg)
        {
            case Win32.WM_LBUTTONUP:
            case Win32.WM_LBUTTONDBLCLK:
                _onShow();
                break;

            case Win32.WM_RBUTTONUP:
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        IntPtr hMenu = Win32.CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            bool isPaused = _isPausedProvider();
            string pauseText = isPaused ? "Resume sharing" : "Pause sharing";
            // Drop MF_CHECKED per Windows UX conventions (a checked 'Resume' item contradicts itself)
            uint pauseFlags = Win32.MF_STRING | (_isSharingEnabledProvider() ? 0 : Win32.MF_GRAYED);

            Win32.AppendMenu(hMenu, Win32.MF_STRING, CmdShow, "Open Deskchirp");
            Win32.AppendMenu(hMenu, pauseFlags, CmdTogglePause, pauseText);
            Win32.AppendMenu(hMenu, Win32.MF_SEPARATOR, 0, string.Empty);
            Win32.AppendMenu(hMenu, Win32.MF_STRING, CmdQuit, "Exit");

            Win32.GetCursorPos(out Win32.POINT pt);
            Win32.SetForegroundWindow(_hWnd);

            uint selectedCmd = Win32.TrackPopupMenu(
                hMenu,
                Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD,
                pt.X,
                pt.Y,
                0,
                _hWnd,
                IntPtr.Zero);

            switch (selectedCmd)
            {
                case CmdShow:
                    _onShow();
                    break;
                case CmdTogglePause:
                    _onTogglePause();
                    break;
                case CmdQuit:
                    _onQuit();
                    break;
            }
        }
        finally
        {
            Win32.DestroyMenu(hMenu);
            Win32.PostMessage(_hWnd, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_isCreated)
        {
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref _nid);
            _isCreated = false;
        }
        if (_ownedIcon != IntPtr.Zero)
        {
            Win32.DestroyIcon(_ownedIcon);
            _ownedIcon = IntPtr.Zero;
        }
    }
}
