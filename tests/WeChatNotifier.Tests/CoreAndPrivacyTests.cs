using WeChatNotifier.Core;
using WeChatNotifier.Diagnostics;
using WeChatNotifier.Diagnostics.Privacy;
using WeChatNotifier.Listener.Profile;

namespace WeChatNotifier.Tests;

public class DedupCacheTests
{
    [Fact]
    public void Second_add_is_a_duplicate()
    {
        var d = new DedupCache(4);
        Assert.True(d.TryAdd("a"));
        Assert.False(d.TryAdd("a"));
    }

    [Fact]
    public void Least_recently_used_entry_is_evicted()
    {
        var d = new DedupCache(2);
        d.TryAdd("a");
        d.TryAdd("b");
        Assert.False(d.TryAdd("a")); // touch a -> b is now the oldest
        d.TryAdd("c");               // evicts b
        Assert.Equal(2, d.Count);
        Assert.False(d.TryAdd("a"));
        Assert.True(d.TryAdd("b"));
    }
}

public class MessageFingerprintTests
{
    [Fact]
    public void Stable_and_sensitive_to_every_part()
    {
        var a = MessageFingerprint.Create("Alice", "1", "hi", "19:00");
        Assert.Equal(a, MessageFingerprint.Create("Alice", "1", "hi", "19:00"));
        Assert.NotEqual(a, MessageFingerprint.Create("Alice", "2", "hi", "19:00"));
        Assert.NotEqual(MessageFingerprint.Create("a", null), MessageFingerprint.Create("a", ""));
    }
}

public class TextRedactorTests
{
    private static TextRedactor Redactor(string? reveal = null) =>
        new(true, reveal, WeChatUiProfile.AutomationIdPrefixesWithIdentity, WeChatUiProfile.MainWindowTitles);

    [Fact]
    public void Keeps_shape_and_ui_words_only()
    {
        Assert.Equal("□□: □□ ◊ [图片]", Redactor().Apply("张三: 你好 😀 [图片]"));
        Assert.Equal("xxxxx xxxxx 123", Redactor().Apply("Hello World 123"));
    }

    [Fact]
    public void Reveal_pattern_keeps_test_markers()
    {
        Assert.Equal("WXN-1 □□", Redactor(@"WXN-\d+").Apply("WXN-1 你好"));
    }

    [Fact]
    public void Conversation_names_inside_automation_ids_are_tokenised()
    {
        var r = Redactor();
        var masked = r.Ids("session_item_Alice Smith")!;
        Assert.StartsWith("session_item_#", masked);
        Assert.DoesNotContain("Alice", masked);
        Assert.Equal(masked, r.Ids("session_item_Alice Smith")); // stable within a run
        Assert.DoesNotContain("wxid_abc123", r.Ids("ChatSingleWindowwxid_abc123")!);
        Assert.DoesNotContain("12345678@chatroom", r.Ids("x_12345678@chatroom")!);
    }

    [Fact]
    public void Only_known_window_titles_stay_readable()
    {
        var r = Redactor();
        Assert.Equal("微信", r.Title("微信"));
        Assert.Equal("xxxxx", r.Title("Alice"));
    }

    [Fact]
    public void Disabled_redactor_passes_text_through()
    {
        Assert.Equal("你好⏎hi", new TextRedactor(false, null).Apply("你好\nhi"));
    }
}

public class DiagnosticLogTests
{
    [Fact]
    public void Files_are_size_capped_and_rotated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wxn-log-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var log = new DiagnosticLog(dir, LogLevel.Debug, maxFileBytes: 2_000, maxFiles: 3))
            {
                for (int i = 0; i < 500; i++) log.Info("test", $"line {i} state=ok latency=12ms");
            }
            var files = Directory.GetFiles(dir);
            Assert.InRange(files.Length, 1, 3);
            Assert.All(files, f => Assert.True(new FileInfo(f).Length < 4_000));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Below_min_level_is_not_written()
    {
        var lines = new List<string>();
        using var log = new DiagnosticLog(null, LogLevel.Info, lines.Add);
        log.Debug("t", "hidden");
        log.Info("t", "shown");
        Assert.Single(lines);
    }
}
