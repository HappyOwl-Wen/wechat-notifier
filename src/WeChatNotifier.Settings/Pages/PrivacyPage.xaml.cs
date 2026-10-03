using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeChatNotifier.Core.Configuration;

namespace WeChatNotifier.Settings.Pages;

public sealed partial class PrivacyPage : Page
{
    public PrivacyPage() => InitializeComponent();

    internal SettingsViewModel VM => App.Instance.Settings;

    private void OnClearCache(object sender, RoutedEventArgs e)
    {
        // The notifier clears its own bookkeeping too; when it does not run, the folder is simply deleted.
        bool done = App.Instance.Channel.Send(ControlProtocol.ClearImageCache) || DeleteCacheFolder();
        Show(done ? "图片缓存已清除。" : "图片缓存没有完全清除（有文件正在使用），稍后再试。", done);
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        var logs = Path.Combine(SettingsStore.Folder, "logs");
        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Directory.Exists(logs) ? logs : SettingsStore.Folder }, UseShellExecute = false });
        }
        catch (Win32Exception)
        {
            Show("日志文件夹无法打开。", ok: false);
        }
    }

    private static bool DeleteCacheFolder()
    {
        var cache = Path.Combine(SettingsStore.Folder, "cache");
        try
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
            if (Directory.Exists(SettingsStore.AvatarFolder)) Directory.Delete(SettingsStore.AvatarFolder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Show(string message, bool ok)
    {
        Result.Message = message;
        Result.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        Result.IsOpen = true;
    }
}
