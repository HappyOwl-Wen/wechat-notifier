using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WeChatNotifier.Settings.Pages;

public sealed partial class NotificationsPage : Page
{
    public NotificationsPage() => InitializeComponent();

    internal SettingsViewModel VM => App.Instance.Settings;

    /// <summary>Windows' own notification settings page (a local settings URI, nothing goes online).</summary>
    private async void OnOpenWindowsSettings(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:notifications"));
}
