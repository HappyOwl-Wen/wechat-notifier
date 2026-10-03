using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatNotifier.Core.Configuration;

/// <summary>
/// The notifier's state for the diagnostics page (spec §24): component states, times, counters and latencies.
/// Never message text, conversation names or senders — "last message" is a time only.
/// </summary>
public sealed record StatusSnapshot
{
    public string Version { get; init; } = "";

    // Windows notifications
    public bool NotificationsReady { get; init; }
    public string? NotificationsDetail { get; init; }
    public string? WindowsSetting { get; init; }
    public DateTime? PausedUntilUtc { get; init; }

    // Listener and WeChat
    public string ListenerState { get; init; } = "";
    public int? WeChatPid { get; init; }
    public string? WeChatVersion { get; init; }
    public string WindowState { get; init; } = "";
    public bool UiAutomationConnected { get; init; }
    public int SessionCells { get; init; }
    public bool ShellHookActive { get; init; }

    // Activity
    public DateTime? LastTriggerUtc { get; init; }
    public DateTime? LastEventUtc { get; init; }
    public double? LastLatencyMs { get; init; }
    public double? AverageLatencyMs { get; init; }
    public double? MaxLatencyMs { get; init; }
    public long DuplicatesDropped { get; init; }
    public long Shown { get; init; }
    public long Failed { get; init; }
    public long Withdrawn { get; init; }
    public long Clicks { get; init; }
    public long HeldBack { get; init; }
    public long PicturesAfterWait { get; init; }
    public long PicturesNotLoaded { get; init; }

    // Process
    public double WorkingSetMb { get; init; }
    public double PeakWorkingSetMb { get; init; }
    public double CpuSeconds { get; init; }
    public double UptimeSeconds { get; init; }
    public int ListenerRestarts { get; init; }
    public string? LogFile { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, StatusSnapshotJson.Default.StatusSnapshot);

    public static StatusSnapshot? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, StatusSnapshotJson.Default.StatusSnapshot);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string WindowStateText => WindowState switch
    {
        "Visible" => "可见",
        "Minimized" => "最小化",
        "Hidden" => "在托盘中（隐藏）",
        _ => "未找到",
    };

    public string ListenerStateText => ListenerState switch
    {
        "Attached" => "运行中",
        "WaitingForWeChat" => "等待微信启动",
        "Stopped" => "已停止（稍后自动重试）",
        "Restarting" => "正在重新启动",
        _ => "正在启动",
    };

    public string WindowsSettingText => (NotificationsReady, WindowsSetting) switch
    {
        (false, _) => "不可用：" + (NotificationsDetail ?? "未知原因"),
        (true, "Enabled") => "已开启",
        (true, "DisabledForApplication") => "已在 Windows 设置中关闭",
        (true, "DisabledForUser") => "Windows 通知已全部关闭",
        (true, "DisabledByGroupPolicy") => "被组策略禁止",
        (true, var other) => other ?? "未知",
    };

    /// <summary>Plain-text form for the tray's diagnostics box and "copy" in the settings window.</summary>
    public string ToText()
    {
        static string Time(DateTime? utc) => utc is { } u ? u.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "—";
        var sb = new StringBuilder();
        sb.AppendLine($"WeChat Notifier {Version}");
        sb.AppendLine($"微信进程：{(WeChatPid is { } pid ? $"已检测到（pid {pid}，版本 {WeChatVersion}）" : "未检测到")}");
        sb.AppendLine($"微信主窗口：{WindowStateText}");
        sb.AppendLine($"UI Automation：{(UiAutomationConnected ? $"已连接（会话列表 {SessionCells} 项）" : "尚未连接会话列表（打开一次微信窗口即可）")}");
        sb.AppendLine($"消息监听：{ListenerStateText}；Shell hook {(ShellHookActive ? "已注册" : "未注册")}");
        sb.AppendLine($"通知：{(PausedUntilUtc is { } until ? (until == DateTime.MaxValue ? "已暂停" : $"暂停到 {Time(until)}") : "开启")}；Windows 设置 {WindowsSettingText}");
        sb.AppendLine($"最后一次触发：{Time(LastTriggerUtc)}");
        sb.AppendLine($"最后一条消息：{Time(LastEventUtc)}（不显示内容）");
        sb.AppendLine($"最后通知延迟：{(LastLatencyMs is { } last ? $"{last:0} ms（平均 {AverageLatencyMs:0} ms，最大 {MaxLatencyMs:0} ms）" : "—")}");
        sb.AppendLine($"丢弃的重复消息：{DuplicatesDropped}");
        sb.AppendLine($"通知数：显示 {Shown}，失败 {Failed}，撤回 {Withdrawn}，点击 {Clicks}；暂停期间未显示 {HeldBack}");
        sb.AppendLine($"图片预览：等缩略图加载后显示 {PicturesAfterWait} 次，未及时加载 {PicturesNotLoaded} 次");
        sb.AppendLine($"当前内存：{WorkingSetMb:0} MB（峰值 {PeakWorkingSetMb:0} MB）；CPU 累计 {CpuSeconds:0.0} s");
        sb.AppendLine($"已运行：{TimeSpan.FromSeconds(UptimeSeconds):d\\.hh\\:mm\\:ss}；监听自动重启 {ListenerRestarts} 次");
        sb.AppendLine($"日志（不含聊天内容）：{LogFile ?? "关闭"}");
        return sb.ToString();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StatusSnapshot))]
internal sealed partial class StatusSnapshotJson : JsonSerializerContext;
