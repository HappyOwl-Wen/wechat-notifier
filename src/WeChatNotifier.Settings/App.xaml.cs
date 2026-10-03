using Microsoft.UI.Xaml;

namespace WeChatNotifier.Settings;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        Instance = this;
        InitializeComponent();
    }

    public static App Instance { get; private set; } = null!;

    internal NotifierChannel Channel { get; private set; } = null!;

    internal SettingsViewModel Settings { get; private set; } = null!;

    internal StatusViewModel Status { get; } = new();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Channel = new NotifierChannel();
        Settings = new SettingsViewModel(Channel);
        _window = new MainWindow();
        _window.Closed += (_, _) => Channel.Dispose();
        _window.Activate();
    }
}
