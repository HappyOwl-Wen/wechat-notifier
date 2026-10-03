using WeChatNotifier.Core.Configuration;
using WeChatNotifier.Listener.Tracking;

namespace WeChatNotifier.Tests;

public class Phase5Tests
{
    [Fact]
    public void One_sender_prefix_marks_only_that_message_as_group()
    {
        var groups = new GroupEvidence();
        groups.Observe("k", "PS");                     // could be a private message starting with "PS: "
        Assert.True(groups.IsGroup("k", "PS"));        // this message has the group form
        Assert.False(groups.IsGroup("k", null));       // but the conversation is not known as a group
        groups.Observe("k", "PS");
        Assert.False(groups.IsGroup("k", null));       // the same sender again proves nothing
    }

    [Fact]
    public void Two_different_senders_make_a_conversation_a_group()
    {
        var groups = new GroupEvidence();
        groups.Observe("g", "Tom");
        groups.Observe("g", "Anna");
        Assert.True(groups.IsGroup("g", null));        // also in the tray, where previews are hidden
        Assert.False(groups.IsGroup("other", null));
        groups.Observe("g", null);                     // own messages carry no prefix: nothing changes
        Assert.True(groups.IsGroup("g", null));
        groups.Clear();
        Assert.False(groups.IsGroup("g", null));
    }

    [Fact]
    public void Group_evidence_is_bounded()
    {
        var groups = new GroupEvidence();
        for (int i = 0; i < GroupEvidence.Capacity + 50; i++)
        {
            groups.Observe($"k{i}", "A");
            groups.Observe($"k{i}", "B");
        }
        Assert.Equal(GroupEvidence.Capacity, groups.Count);
    }

    [Fact]
    public void Group_messages_are_on_by_default_and_survive_a_save()
    {
        Assert.True(new AppSettings().GroupMessages);
        var folder = Path.Combine(Path.GetTempPath(), "WeChatNotifier.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(folder, "settings.json");
            Assert.True(SettingsStore.Save(new AppSettings { GroupMessages = false }, file));
            Assert.False(SettingsStore.Load(file).GroupMessages);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Uninstall_removes_the_program_folder_only_for_the_installed_copy()
    {
        var installed = Path.Combine(InstallLayout.InstallDirectory, InstallLayout.NotifierExecutable);
        Assert.True(InstallLayout.IsInstalledCopy(installed));
        Assert.True(InstallLayout.IsInstalledCopy(installed.ToUpperInvariant()));
        Assert.False(InstallLayout.IsInstalledCopy(@"C:\workspace\repo\src\WeChatNotifier.App\bin\Release\net10.0-windows10.0.19041.0\WeChatNotifier.exe"));
        Assert.False(InstallLayout.IsInstalledCopy(Path.Combine(InstallLayout.InstallDirectory, "sub", InstallLayout.NotifierExecutable)));
        Assert.EndsWith(@"Programs\WeChatNotifier", InstallLayout.InstallDirectory);
    }

    [Fact]
    public void Status_snapshot_round_trips_as_json()
    {
        var snapshot = new StatusSnapshot
        {
            Version = "0.5.0",
            NotificationsReady = true,
            WindowsSetting = "Enabled",
            ListenerState = "Attached",
            WeChatPid = 4764,
            WeChatVersion = "4.1.13.65",
            WindowState = "Hidden",
            UiAutomationConnected = true,
            SessionCells = 10,
            LastLatencyMs = 269,
            PausedUntilUtc = DateTime.MaxValue,
            Shown = 16,
        };
        Assert.Equal(snapshot, StatusSnapshot.FromJson(snapshot.ToJson()));
        Assert.Null(StatusSnapshot.FromJson("{ not json"));
    }

    [Fact]
    public void Diagnostics_text_names_every_layer_and_carries_no_content_fields()
    {
        var text = new StatusSnapshot { Version = "0.5.0", ListenerState = "WaitingForWeChat", WindowState = "NotFound" }.ToText();
        foreach (var label in new[] { "微信进程", "微信主窗口", "UI Automation", "消息监听", "最后一次触发", "最后一条消息", "最后通知延迟", "丢弃的重复消息", "当前内存" })
            Assert.Contains(label, text);
        Assert.Contains("等待微信启动", text);
        // The snapshot has no property that could hold a conversation name or message text.
        Assert.DoesNotContain(typeof(StatusSnapshot).GetProperties(), p => p.Name.Contains("Conversation") || p.Name.Contains("Text") && p.PropertyType == typeof(string) && !p.Name.EndsWith("Text", StringComparison.Ordinal));
    }
}
