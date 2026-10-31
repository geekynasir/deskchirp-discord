using Microsoft.UI.Xaml;

namespace DeveloperPresence.App;

public partial class App : Application
{
    private Window? m_window;
    private readonly Mutex _instanceMutex;
    private readonly bool _ownsInstance;

    public App()
    {
        _instanceMutex = new Mutex(true, @"Local\DeveloperPresence", out _ownsInstance);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!_ownsInstance)
        {
            Exit();
            return;
        }
        m_window = new MainWindow();
        m_window.Activate();
    }
}
