using WeChatNotifier.Core.Configuration;

namespace WeChatNotifier.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WeChatNotifier.Tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_folder, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var s = SettingsStore.Load(FilePath);
        Assert.Equal(new AppSettings(), s);
        Assert.True(s.StartWithWindows);
        Assert.True(s.MessagePreview);
        Assert.False(s.NotifyWhileWeChatFocused);
        Assert.False(s.NotifyMutedConversations);
        Assert.False(s.DebugLogging);
    }

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var changed = new AppSettings
        {
            StartWithWindows = false,
            ShowTrayIcon = false,
            NotifyWhileWeChatFocused = true,
            MessagePreview = false,
            ImagePreview = false,
            Avatars = false,
            Sound = false,
            LongDuration = true,
            NotifyMutedConversations = true,
            OpenConversationOnClick = false,
            StoreImageCache = false,
            DebugLogging = true,
        };
        Assert.True(SettingsStore.Save(changed, FilePath));
        Assert.Equal(changed, SettingsStore.Load(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void FileUsesCamelCaseNames()
    {
        Assert.True(SettingsStore.Save(new AppSettings { Sound = false }, FilePath));
        var json = File.ReadAllText(FilePath);
        Assert.Contains("\"sound\": false", json);
        Assert.Contains("\"startWithWindows\": true", json);
    }

    [Fact]
    public void MissingValuesKeepTheirDefaultsAndUnknownOnesAreIgnored()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, """{ "sound": false, "somethingFromANewerVersion": 42 }""");
        var s = SettingsStore.Load(FilePath);
        Assert.False(s.Sound);
        Assert.True(s.MessagePreview);
        Assert.True(s.StartWithWindows);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "sound": "loud" }""")]
    [InlineData("")]
    public void BrokenFileGivesDefaultsAndIsKeptAside(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, content);
        Assert.Equal(new AppSettings(), SettingsStore.Load(FilePath));
        Assert.False(File.Exists(FilePath));
        Assert.True(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void NullDocumentGivesDefaults()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, "null");
        Assert.Equal(new AppSettings(), SettingsStore.Load(FilePath));
    }

    [Fact]
    public void SaveReplacesAnExistingFile()
    {
        Assert.True(SettingsStore.Save(new AppSettings { Sound = false }, FilePath));
        Assert.True(SettingsStore.Save(new AppSettings { Sound = true, LongDuration = true }, FilePath));
        var s = SettingsStore.Load(FilePath);
        Assert.True(s.Sound);
        Assert.True(s.LongDuration);
    }

    [Fact]
    public void NotificationOptionsFollowTheSettings()
    {
        var o = new AppSettings { MessagePreview = false, Sound = false, LongDuration = true, Avatars = false, ImagePreview = false, StoreImageCache = false }
            .ToNotificationOptions();
        Assert.False(o.ShowMessagePreview);
        Assert.False(o.PlaySound);
        Assert.True(o.LongDuration);
        Assert.False(o.ShowAvatars);
        Assert.False(o.ShowImagePreview);
        Assert.False(o.StoreImageCache);
    }

    [Fact]
    public void Notifications_appear_as_WeChat_unless_switched_off()
    {
        Assert.True(new AppSettings().ToNotificationOptions().AppearAsWeChat);   // also for a file from before 0.7.2
        Assert.False(new AppSettings { ShowAsWeChat = false }.ToNotificationOptions().AppearAsWeChat);
        Assert.True(new WeChatNotifier.Core.Notifications.NotificationOptions().AppearAsWeChat);
    }
}

public sealed class StartupCommandTests
{
    [Fact]
    public void CommandQuotesThePathAndMarksAutostart()
    {
        Assert.Equal("\"C:\\Program Files\\WeChat Notifier\\WeChatNotifier.exe\" --autostart",
            StartupCommand.For(@"C:\Program Files\WeChat Notifier\WeChatNotifier.exe"));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\WeChat Notifier\\WeChatNotifier.exe\" --autostart", @"C:\Program Files\WeChat Notifier\WeChatNotifier.exe")]
    [InlineData("  \"C:\\a\\b.exe\"", @"C:\a\b.exe")]
    [InlineData(@"C:\tools\WeChatNotifier.exe --autostart", @"C:\tools\WeChatNotifier.exe")]
    [InlineData(@"C:\Program Files\x\WeChatNotifier.EXE", @"C:\Program Files\x\WeChatNotifier.EXE")]
    [InlineData("notifier --autostart", "notifier")]
    public void ExecutableIsTakenFromTheCommand(string command, string expected) =>
        Assert.Equal(expected, StartupCommand.ExecutableOf(command));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"")]
    [InlineData("\"\" --autostart")]
    public void NoExecutableInAnEmptyOrBrokenCommand(string? command) => Assert.Null(StartupCommand.ExecutableOf(command));

    [Fact]
    public void RoundTrip()
    {
        var exe = @"D:\x y\WeChatNotifier.exe";
        Assert.Equal(exe, StartupCommand.ExecutableOf(StartupCommand.For(exe)));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(new byte[0], false)]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 3, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01 }, true)]
    [InlineData(new byte[] { 7, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01 }, true)]
    public void DisabledMarkIsAnOddFirstByte(byte[]? value, bool disabled) =>
        Assert.Equal(disabled, StartupCommand.IsDisabledByUser(value));
}
