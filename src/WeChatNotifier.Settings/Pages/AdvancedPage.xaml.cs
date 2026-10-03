using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using WeChatNotifier.Core.Configuration;

namespace WeChatNotifier.Settings.Pages;

/// <summary>Diagnostics (spec §18 "Advanced", §24). Refreshed every 2 s only while this page is shown.</summary>
public sealed partial class AdvancedPage : Page
{
    private readonly DispatcherQueueTimer _timer;

    public AdvancedPage()
    {
        InitializeComponent();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    internal StatusViewModel Status => App.Instance.Status;

    private void Refresh() => Status.Update(App.Instance.Channel.RequestStatus());

    private void OnTest(object sender, RoutedEventArgs e)
    {
        bool shown = App.Instance.Channel.Send(ControlProtocol.TestNotification);
        Show(shown ? "测试通知已发出。" : "测试通知没有发出：" + Status.WindowsNotifications, shown);
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        bool ok = App.Instance.Channel.Send(ControlProtocol.RestartListener);
        Show(ok ? "监听正在重新启动。" : "WeChat Notifier 没有响应。", ok);
        Refresh();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Status.Snapshot is not { } snapshot) return;
        var package = new DataPackage();
        package.SetText(snapshot.ToText());
        Clipboard.SetContent(package);
        Show("诊断信息已复制。", ok: true);
    }

    private void OnStartNotifier(object sender, RoutedEventArgs e)
    {
        bool started = NotifierChannel.StartNotifier();
        Show(started ? "WeChat Notifier 正在启动…" : "找不到 WeChatNotifier.exe。", started);
    }

    private void Show(string message, bool ok)
    {
        Result.Message = message;
        Result.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        Result.IsOpen = true;
    }
}
