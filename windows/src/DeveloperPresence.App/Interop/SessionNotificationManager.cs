namespace DeveloperPresence.App.Interop;

/// <summary>
/// Registers the application window handle with Windows Terminal Services
/// to receive session lock/unlock notifications (WM_WTSSESSION_CHANGE).
/// </summary>
public sealed class SessionNotificationManager : IDisposable
{
    private readonly IntPtr _hWnd;
    private bool _isRegistered;
    private bool _isDisposed;

    public SessionNotificationManager(IntPtr hWnd)
    {
        _hWnd = hWnd;
        Register();
    }

    private void Register()
    {
        if (_hWnd != IntPtr.Zero)
        {
            _isRegistered = Win32.WTSRegisterSessionNotification(_hWnd, Win32.NOTIFY_FOR_THIS_SESSION);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_isRegistered && _hWnd != IntPtr.Zero)
        {
            Win32.WTSUnRegisterSessionNotification(_hWnd);
            _isRegistered = false;
        }
    }
}
