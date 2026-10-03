using WeChatNotifier.Listener.Profile;
using WeChatNotifier.Listener.Tracking;

namespace WeChatNotifier.Tests;

public class SessionTrackerTests
{
    private static SessionCell Cell(string title, int unread = 0, string? preview = "hi", string? time = "19:00", bool muted = false, bool pinned = false) =>
        new(title, pinned, unread, preview, time, muted);

    private static SessionTracker Baseline(params SessionCell[] cells)
    {
        var t = new SessionTracker();
        Assert.Empty(t.Apply(cells, contentVisible: true, openConversation: null, detect: true)); // first read never reports
        return t;
    }

    [Fact]
    public void First_read_is_baseline_only()
    {
        var t = new SessionTracker();
        Assert.Empty(t.Apply([Cell("Alice", unread: 3)], true, null, detect: true));
        Assert.True(t.HasBaseline);
    }

    [Fact]
    public void Unread_increase_is_a_new_message()
    {
        var t = Baseline(Cell("Alice"), Cell("Bob"));
        var changes = t.Apply([Cell("Alice", unread: 1, preview: "WXN-14", time: "19:44"), Cell("Bob")], true, null, true);
        var c = Assert.Single(changes);
        Assert.Equal("Alice", c.Cell.Title);
        Assert.Equal(1, c.NewMessages);
        Assert.Equal("unread-increase", c.Reason);
        Assert.Equal("WXN-14", c.Cell.Preview);
    }

    [Fact]
    public void Several_messages_before_a_read_are_reported_once_with_the_delta()
    {
        var t = Baseline(Cell("Alice", unread: 1));
        var c = Assert.Single(t.Apply([Cell("Alice", unread: 4, preview: "last one")], true, null, true));
        Assert.Equal(3, c.NewMessages);
    }

    [Fact]
    public void Rows_changing_order_is_not_a_message()
    {
        var t = Baseline(Cell("Alice"), Cell("Bob"), Cell("Carol"));
        Assert.Empty(t.Apply([Cell("Carol"), Cell("Alice"), Cell("Bob")], true, null, true));
    }

    [Fact]
    public void Hidden_reads_detect_counts_and_keep_the_last_preview()
    {
        var t = Baseline(Cell("Alice", unread: 1, preview: "first"));
        // Tray: WeChat drops preview/time from the names, the unread line stays.
        var c = Assert.Single(t.Apply([Cell("Alice", unread: 2, preview: null, time: null)], contentVisible: false, null, true));
        Assert.Null(c.Cell.Preview);
        Assert.Equal(1, c.NewMessages);

        // Window shown again: preview reappears, count unchanged -> not a new message.
        Assert.Empty(t.Apply([Cell("Alice", unread: 2, preview: "second")], true, null, true));
    }

    [Fact]
    public void Open_conversation_preview_change_is_a_message()
    {
        var t = Baseline(Cell("Alice", preview: "old", time: "19:00"));
        var c = Assert.Single(t.Apply([Cell("Alice", preview: "new text", time: "19:01")], true, openConversation: "Alice", true));
        Assert.Equal("open-chat-preview", c.Reason);
    }

    [Fact]
    public void Preview_change_without_unread_elsewhere_is_ignored()
    {
        // e.g. a message the user sent from the phone: preview changes, counter does not.
        var t = Baseline(Cell("Alice", preview: "old"), Cell("Bob"));
        Assert.Empty(t.Apply([Cell("Alice", preview: "my reply", time: "19:05"), Cell("Bob")], true, openConversation: "Bob", true));
    }

    [Theory]
    [InlineData("[草稿] still typing")]
    [InlineData("你撤回了一条消息")]
    public void Drafts_and_recalls_in_the_open_conversation_are_ignored(string preview)
    {
        var t = Baseline(Cell("Alice", preview: "old"));
        Assert.Empty(t.Apply([Cell("Alice", preview: preview, time: "19:05")], true, "Alice", true));
    }

    [Fact]
    public void Hidden_reads_never_produce_preview_changes()
    {
        var t = Baseline(Cell("Alice", preview: "old"));
        Assert.Empty(t.Apply([Cell("Alice", preview: null, time: null)], contentVisible: false, "Alice", true));
    }

    [Fact]
    public void Counter_decrease_then_increase_reports_again()
    {
        var t = Baseline(Cell("Alice", unread: 2));
        Assert.Empty(t.Apply([Cell("Alice", unread: 0)], true, null, true)); // read on the phone
        Assert.Single(t.Apply([Cell("Alice", unread: 1, preview: "again")], true, null, true));
    }

    [Fact]
    public void Muted_flag_is_remembered_for_hidden_reads()
    {
        var t = Baseline(Cell("Team", muted: true));
        var c = Assert.Single(t.Apply([Cell("Team", unread: 3, preview: null, time: null, muted: false)], contentVisible: false, null, true));
        Assert.True(c.Muted);
    }

    [Fact]
    public void Conversation_scrolling_into_view_with_unread_is_reported()
    {
        var t = Baseline(Cell("Alice"), Cell("Bob"));
        var c = Assert.Single(t.Apply([Cell("Dave", unread: 2, preview: "hey"), Cell("Alice")], true, null, true));
        Assert.Equal("appeared-with-unread", c.Reason);
        Assert.Equal(2, c.NewMessages);
    }

    [Fact]
    public void Baseline_mode_never_reports()
    {
        var t = Baseline(Cell("Alice"));
        Assert.Empty(t.Apply([Cell("Alice", unread: 5, preview: "x")], true, null, detect: false));
        // ...and the new state is the baseline for the next detection.
        Assert.Empty(t.Apply([Cell("Alice", unread: 5, preview: "x")], true, null, detect: true));
    }

    [Fact]
    public void Duplicate_titles_in_one_read_use_the_first_row()
    {
        var t = Baseline(Cell("Alice"));
        var changes = t.Apply([Cell("Alice", unread: 1, preview: "a"), Cell("Alice", unread: 9, preview: "b")], true, null, true);
        Assert.Equal("a", Assert.Single(changes).Cell.Preview);
    }

    [Fact]
    public void Counter_back_to_zero_reports_the_conversation_as_read()
    {
        var t = Baseline(Cell("Alice", unread: 3), Cell("Bob", unread: 1), Cell("Carol"));
        var read = new List<string>();
        // Baseline-only reads (window shown, focus lost) must still report it: that is when the user read it.
        t.Apply([Cell("Alice", unread: 0), Cell("Bob", unread: 1), Cell("Carol")], true, null, detect: false, read);
        Assert.Equal(["Alice"], read);

        read.Clear();
        t.Apply([Cell("Alice", unread: 0), Cell("Bob", unread: 0, preview: null, time: null), Cell("Carol")], contentVisible: false, null, true, read);
        Assert.Equal(["Bob"], read); // read on the phone while WeChat sits in the tray
    }

    [Fact]
    public void Conversation_leaving_the_list_is_not_read()
    {
        var t = Baseline(Cell("Alice", unread: 3), Cell("Bob"));
        var read = new List<string>();
        t.Apply([Cell("Bob")], true, null, true, read);
        Assert.Empty(read);
    }

    [Fact]
    public void State_is_bounded()
    {
        var t = new SessionTracker();
        for (int batch = 0; batch < 30; batch++)
            t.Apply(Enumerable.Range(0, 30).Select(i => Cell($"c{batch}-{i}")).ToList(), true, null, false);
        Assert.True(t.Count <= SessionTracker.MaxConversations);
    }
}
