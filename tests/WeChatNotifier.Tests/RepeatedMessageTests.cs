using WeChatNotifier.Core;
using WeChatNotifier.Core.Imaging;
using WeChatNotifier.Listener.Profile;
using WeChatNotifier.Listener.Tracking;

namespace WeChatNotifier.Tests;

/// <summary>Messages that look alike (same preview, same minute) must still each be notified (spec §8 vs. §27 "消息不能漏").</summary>
public class RepeatedMessageTests
{
    private static SessionCell Cell(string title, int unread, string? preview = "[图片]", string? time = "23:12") =>
        new(title, false, unread, preview, time, false);

    /// <summary>The listener's fingerprint for a session change (see WeChatListener.BuildEvent).</summary>
    private static string Fingerprint(SessionChange c) =>
        MessageFingerprint.Create(c.Cell.Title, c.ReadEpoch.ToString(), c.Cell.Unread.ToString(), c.Cell.Preview, c.Cell.Time, c.PreviousPreview, null);

    [Fact]
    public void A_picture_after_a_read_is_not_taken_for_the_one_before_it()
    {
        // 2026-10-01 23:12: picture → unread 2 (chat not open); the user reads the chat; two more pictures in the same
        // minute → unread 1, then 2 again — the second looked exactly like the first one and was dropped.
        var dedup = new DedupCache();
        var t = new SessionTracker();
        t.Apply([Cell("Bob", 1)], true, null, detect: true);                       // baseline

        var first = Assert.Single(t.Apply([Cell("Bob", 2)], true, null, true));
        Assert.True(dedup.TryAdd(Fingerprint(first)));

        var read = new List<string>();
        Assert.Empty(t.Apply([Cell("Bob", 0)], true, "Bob", true, read));    // read in WeChat
        Assert.Equal(["Bob"], read);

        var second = Assert.Single(t.Apply([Cell("Bob", 1)], true, null, true));
        Assert.True(dedup.TryAdd(Fingerprint(second)));
        var third = Assert.Single(t.Apply([Cell("Bob", 2)], true, null, true));
        Assert.Equal(1, third.ReadEpoch);
        Assert.True(dedup.TryAdd(Fingerprint(third)));                                  // was false before 0.6.3
    }

    [Fact]
    public void The_same_observation_is_still_a_duplicate()
    {
        var t = new SessionTracker();
        t.Apply([Cell("A", 0)], true, null, true);
        var change = Assert.Single(t.Apply([Cell("A", 1)], true, null, true));
        var dedup = new DedupCache();
        Assert.True(dedup.TryAdd(Fingerprint(change)));
        Assert.False(dedup.TryAdd(Fingerprint(change with { })));
    }

    [Fact]
    public void Open_chat_previews_A_B_A_in_one_minute_are_three_messages()
    {
        var t = new SessionTracker();
        t.Apply([Cell("A", 0, "earlier")], true, "A", true);
        var dedup = new DedupCache();
        foreach (var preview in new[] { "哈哈", "好的", "哈哈" })
        {
            var c = Assert.Single(t.Apply([Cell("A", 0, preview)], true, "A", true));
            Assert.Equal("open-chat-preview", c.Reason);
            Assert.True(dedup.TryAdd(Fingerprint(c)), preview);
        }
    }

    [Fact]
    public void A_message_after_a_read_on_the_phone_is_reported_with_its_conversation()
    {
        // WeChat in the tray: 3 unread; read on the phone (no flash, so no read of the list sees the 0); one more message:
        // the counter shows 1. Before 0.6.4 this was "3 -> 1, no increase" and only a generic notification appeared.
        var t = new SessionTracker();
        t.Apply([Cell("Alice", 3, preview: null, time: null)], contentVisible: false, null, detect: true);
        var c = Assert.Single(t.Apply([Cell("Alice", 1, preview: null, time: null)], false, null, true));
        Assert.Equal("unread-after-read", c.Reason);
        Assert.Equal(1, c.NewMessages);
        Assert.Equal(1, c.ReadEpoch);

        // Then counted up as usual.
        var next = Assert.Single(t.Apply([Cell("Alice", 2, preview: null, time: null)], false, null, true));
        Assert.Equal(("unread-increase", 1, 1), (next.Reason, next.NewMessages, next.ReadEpoch));
    }

    [Fact]
    public void A_recalled_unread_message_or_a_read_is_not_a_new_message()
    {
        var t = new SessionTracker();
        t.Apply([Cell("Alice", 3, preview: "hi")], true, null, true);
        Assert.Empty(t.Apply([Cell("Alice", 2, preview: "Alice撤回了一条消息")], true, null, true)); // visible: the notice tells
        var read = new List<string>();
        Assert.Empty(t.Apply([Cell("Alice", 0, preview: "Alice撤回了一条消息")], true, null, true, read));
        Assert.Equal(["Alice"], read);
    }

    [Fact]
    public void Session_rows_are_clicked_on_the_text_and_only_when_visible()
    {
        var list = new PixelRect(1356, 140, 263, 700);
        var row = new PixelRect(1356, 181, 263, 81);
        Assert.Equal((1356 + 157, 181 + 40), WeChatUiProfile.SessionClickPoint(row, list));     // right of the avatar
        Assert.Null(WeChatUiProfile.SessionClickPoint(row with { Y = 800 }, list));             // cut off at the bottom
        Assert.Null(WeChatUiProfile.SessionClickPoint(row with { Y = 100 }, list));             // cut off at the top
        Assert.NotNull(WeChatUiProfile.SessionClickPoint(row with { Y = 140 + 700 - 81 + 10 }, list)); // a few px cut: fine
        Assert.Null(WeChatUiProfile.SessionClickPoint(default, list));
    }
}
