using Microsoft.UI.Xaml.Controls;

namespace WeChatNotifier.Settings.Pages;

public sealed partial class GeneralPage : Page
{
    public GeneralPage() => InitializeComponent();

    internal SettingsViewModel VM => App.Instance.Settings;
}
