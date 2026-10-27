namespace TailControl.Client;

public partial class App : Application
{
    private readonly AppShell _shell;

    public App(AppShell shell)
    {
        InitializeComponent();
        _shell = shell;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_shell) { Title = "TailControl" };

        // Pe Windows, o fereastra de dimensiunea unui telefon mare.
        if (DeviceInfo.Current.Platform == DevicePlatform.WinUI)
        {
            window.Width = 460;
            window.Height = 860;
            window.MinimumWidth = 380;
            window.MinimumHeight = 560;
        }

        return window;
    }
}
