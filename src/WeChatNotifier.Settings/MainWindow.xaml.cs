using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WeChatNotifier.Core.Configuration;
using WeChatNotifier.Settings.Pages;

namespace WeChatNotifier.Settings;

public sealed partial class MainWindow : Window
{
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherQueueTimer _reload;

    public MainWindow()
    {
        InitializeComponent();
        // The notifier and a second start find this window by its title.
        Title = TitleText.Text = ControlProtocol.SettingsWindowTitle;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var icon = Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
            TitleIcon.Source = new BitmapImage(new Uri(icon));
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = Math.Max(1, Native.GetDpiForWindow(hwnd)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(980 * scale), (int)(720 * scale)));
        if (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary) is { } display)
        {
            var area = display.WorkArea;
            AppWindow.Move(new PointInt32(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (area.Height - AppWindow.Size.Height) / 2));
        }

        // Settings changed elsewhere (the tray menu): show them. The watcher is event driven, nothing polls.
        _reload = DispatcherQueue.CreateTimer();
        _reload.Interval = TimeSpan.FromMilliseconds(200);
        _reload.IsRepeating = false;
        _reload.Tick += (_, _) => App.Instance.Settings.Reload();
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            _watcher = new FileSystemWatcher(SettingsStore.Folder, Path.GetFileName(SettingsStore.FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            _watcher.Changed += (_, _) => ScheduleReload();
            _watcher.Created += (_, _) => ScheduleReload();
            _watcher.Renamed += (_, _) => ScheduleReload();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _watcher = null;
        }
        Closed += (_, _) => _watcher?.Dispose();

        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void ScheduleReload() => DispatcherQueue.TryEnqueue(() =>
    {
        _reload.Stop();
        _reload.Start();
    });

    private void OnNavigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = ((args.SelectedItem as NavigationViewItem)?.Tag as string) switch
        {
            "Notifications" => typeof(NotificationsPage),
            "Privacy" => typeof(PrivacyPage),
            "Advanced" => typeof(AdvancedPage),
            _ => typeof(GeneralPage),
        };
        ContentFrame.Navigate(page, null, new SuppressNavigationTransitionInfo());
    }
}
