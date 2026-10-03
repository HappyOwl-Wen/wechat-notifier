using System.ComponentModel;
using System.Globalization;
using WeChatNotifier.Core.Configuration;

namespace WeChatNotifier.Settings;

/// <summary>The "Advanced" page (spec §18/§24): the notifier's state as text. States, times and numbers only.</summary>
internal sealed class StatusViewModel : INotifyPropertyChanged
{
    private const string None = "—";

    public event PropertyChangedEventHandler? PropertyChanged;

    public StatusSnapshot? Snapshot { get; private set; }

    public bool NotifierRunning => Snapshot is not null;

    public bool NotifierMissing => Snapshot is null;

    public string WeChatProcess => Snapshot is null ? None : Snapshot.WeChatPid is { } pid ? $"已检测到（pid {pid}，版本 {Snapshot.WeChatVersion}）" : "未检测到";

    public string WeChatWindow => Snapshot?.WindowStateText ?? None;

    public string UiAutomation => Snapshot is null ? None
        : Snapshot.UiAutomationConnected ? $"已连接（会话列表 {Snapshot.SessionCells} 项）" : "尚未连接（打开一次微信窗口即可）";

    public string Listener => Snapshot is null ? "WeChat Notifier 没有运行" : Snapshot.ListenerStateText + (Snapshot.ShellHookActive ? "" : "（Shell hook 未注册）");

    public string Latency => Snapshot?.LastLatencyMs is { } ms
        ? $"{ms:0} ms（平均 {Snapshot.AverageLatencyMs:0} ms，最大 {Snapshot.MaxLatencyMs:0} ms）"
        : None;

    public string LastTrigger => Time(Snapshot?.LastTriggerUtc);

    public string LastMessage => Snapshot?.LastEventUtc is { } t ? $"{Time(t)}（不显示内容）" : None;

    public string Duplicates => Snapshot?.DuplicatesDropped.ToString(CultureInfo.InvariantCulture) ?? None;

    public string Notifications => Snapshot is null ? None
        : $"显示 {Snapshot.Shown}，失败 {Snapshot.Failed}，撤回 {Snapshot.Withdrawn}，点击 {Snapshot.Clicks}" +
          (Snapshot.PausedUntilUtc is { } until ? (until == DateTime.MaxValue ? "；已暂停" : $"；暂停到 {Time(until)}") : "");

    public string WindowsNotifications => Snapshot?.WindowsSettingText ?? None;

    public string Memory => Snapshot is null ? None : $"{Snapshot.WorkingSetMb:0} MB（峰值 {Snapshot.PeakWorkingSetMb:0} MB），CPU 累计 {Snapshot.CpuSeconds:0.0} s";

    public string Uptime => Snapshot is null ? None : $"{TimeSpan.FromSeconds(Snapshot.UptimeSeconds):d\\.hh\\:mm\\:ss}，监听自动重启 {Snapshot.ListenerRestarts} 次";

    public string Version => Snapshot?.Version is { Length: > 0 } v ? v : typeof(StatusViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    public void Update(StatusSnapshot? snapshot)
    {
        Snapshot = snapshot;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private static string Time(DateTime? utc) => utc is { } u ? u.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : None;
}
