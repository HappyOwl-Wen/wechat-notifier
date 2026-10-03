using WeChatNotifier.Core;
using WeChatNotifier.Core.Notifications;

namespace WeChatNotifier.Tests;

public class ConversationLinesTests
{
    private static readonly NotificationOptions Defaults = new();
    private static readonly DateTimeOffset At = new(2026, 10, 2, 15, 0, 0, TimeSpan.FromHours(2));

    private static string[] Texts(IReadOnlyList<ConversationLines.Line> lines) =>
        lines.Select(l => l.Text ?? $"?{l.Count}").ToArray();

    private static MessageEvent Event(string? text, int unread, int newMessages = 1, string conversation = "Alice", bool mentioned = false) => new()
    {
        Conversation = conversation,
        Kind = text is null ? MessageKind.Unknown : MessageKind.Text,
        Text = text,
        Source = text is null ? ContentSource.SessionMetadata : ContentSource.SessionPreview,
        UnreadCount = unread,
        NewMessages = newMessages,
        Mentioned = mentioned,
        DetectedAt = At,
        Fingerprint = "fp",
    };

    [Fact]
    public void Messages_go_top_to_bottom_and_the_oldest_leaves_first()
    {
        var recent = new ConversationLines();
        Assert.Equal(["A"], Texts(recent.Add("k", "A", 1, 1)));
        Assert.Equal(["A", "B"], Texts(recent.Add("k", "B", 1, 2)));
        Assert.Equal(["A", "B", "C"], Texts(recent.Add("k", "C", 1, 3)));
        Assert.Equal(["B", "C", "D"], Texts(recent.Add("k", "D", 1, 4)));   // FIFO: at most three
        Assert.Equal(["C", "D", "E"], Texts(recent.Add("k", "E", 1, 5)));
    }

    [Fact]
    public void Conversations_are_kept_apart()
    {
        var recent = new ConversationLines();
        recent.Add("a", "A1", 1, 1);
        recent.Add("b", "B1", 1, 1);
        Assert.Equal(["A1", "A2"], Texts(recent.Add("a", "A2", 1, 2)));
        Assert.Equal(["B1", "B2"], Texts(recent.Add("b", "B2", 1, 2)));
    }

    [Fact]
    public void Unknown_messages_next_to_each_other_are_one_line()
    {
        var recent = new ConversationLines();
        recent.Add("k", "A", 1, 1);
        Assert.Equal(["A", "?1"], Texts(recent.Add("k", null, 1, 2)));      // WeChat went to the tray
        Assert.Equal(["A", "?3"], Texts(recent.Add("k", null, 2, 4)));
        Assert.Equal(["A", "?3", "B"], Texts(recent.Add("k", "B", 1, 5)));  // the gap stays visible
    }

    [Fact]
    public void A_burst_read_at_once_shows_its_unknown_part_before_the_newest()
    {
        var recent = new ConversationLines();
        Assert.Equal(["?2", "C"], Texts(recent.Add("k", "C", 3, 3)));
    }

    [Fact]
    public void Lines_beyond_the_unread_count_were_read_meanwhile()
    {
        var recent = new ConversationLines();
        recent.Add("k", "A", 1, 1);
        recent.Add("k", "B", 1, 2);
        Assert.Equal(["C"], Texts(recent.Add("k", "C", 1, 1)));            // read on the phone, then a new one
        recent.Add("k", null, 3, 4);                                        // C + 3 unknown
        Assert.Equal(["?2", "D"], Texts(recent.Add("k", "D", 1, 3)));      // only 2 of the earlier 4 are unread
    }

    [Fact]
    public void Unknown_unread_count_keeps_the_lines()
    {
        var recent = new ConversationLines();
        recent.Add("k", "A", 1, 0);
        Assert.Equal(["A", "B"], Texts(recent.Add("k", "B", 1, 0)));
    }

    [Fact]
    public void A_read_conversation_starts_over()
    {
        var recent = new ConversationLines();
        recent.Add("k", "A", 1, 1);
        recent.Clear("k");
        Assert.Equal(["B"], Texts(recent.Add("k", "B", 1, 0)));
    }

    [Fact]
    public void The_conversation_inactive_longest_is_forgotten_first()
    {
        var recent = new ConversationLines();
        for (int i = 0; i < ConversationLines.MaxConversations; i++) recent.Add($"k{i}", "x", 1, 0);
        recent.Add("k0", "y", 1, 0);                                        // k0 active again; k1 is now the oldest
        recent.Add("new", "z", 1, 0);
        Assert.Equal(ConversationLines.MaxConversations, recent.Count);
        Assert.Equal(["x", "y", "w"], Texts(recent.Add("k0", "w", 1, 0)));
        Assert.Equal(["v"], Texts(recent.Add("k1", "v", 1, 0)));
    }

    [Fact]
    public void Notification_lists_the_earlier_messages_above_the_newest()
    {
        var recent = new ConversationLines();
        NotificationComposer.Compose(Event("你好", 1), Defaults, recent);
        NotificationComposer.Compose(Event("在吗？", 2), Defaults, recent);
        var n = NotificationComposer.Compose(Event("晚上一起吃饭", 3), Defaults, recent);
        Assert.Equal(["你好", "在吗？"], n.EarlierLines);
        Assert.Equal("晚上一起吃饭", n.Body);
        Assert.Equal("3 条未读", n.Attribution);

        n = NotificationComposer.Compose(Event("七点", 4), Defaults, recent);
        Assert.Equal(["在吗？", "晚上一起吃饭"], n.EarlierLines);
        Assert.Equal("七点", n.Body);
    }

    [Fact]
    public void Unknown_content_is_one_line_of_new_messages()
    {
        var recent = new ConversationLines();
        NotificationComposer.Compose(Event("你好", 1), Defaults, recent);
        var n = NotificationComposer.Compose(Event(null, 3, newMessages: 2), Defaults, recent);
        Assert.Equal(["你好"], n.EarlierLines);
        Assert.Equal("2 条新消息", n.Body);
        n = NotificationComposer.Compose(Event("好的", 4), Defaults, recent);
        Assert.Equal(["你好", "[2 条新消息]"], n.EarlierLines);
        Assert.Equal("好的", n.Body);
    }

    [Fact]
    public void Mentions_stay_on_their_line()
    {
        var recent = new ConversationLines();
        NotificationComposer.Compose(Event("Tom: 3 点开会", 1, mentioned: true), Defaults, recent);
        var n = NotificationComposer.Compose(Event("Ann: 收到", 2), Defaults, recent);
        Assert.Equal(["[有人@我] Tom: 3 点开会"], n.EarlierLines);
        Assert.Equal("Ann: 收到", n.Body);
    }

    [Fact]
    public void Preview_off_keeps_no_lines()
    {
        var recent = new ConversationLines();
        var off = new NotificationOptions { ShowMessagePreview = false };
        NotificationComposer.Compose(Event("secret 1", 1), off, recent);
        var n = NotificationComposer.Compose(Event("secret 2", 2), off, recent);
        Assert.Empty(n.EarlierLines);
        Assert.Equal("新消息", n.Body);
        Assert.Equal(0, recent.Count);                                      // nothing of the content was kept
    }

    [Fact]
    public void Without_a_list_a_message_stands_alone()
    {
        var n = NotificationComposer.Compose(Event("你好", 2), Defaults);
        Assert.Empty(n.EarlierLines);
        Assert.Equal("你好", n.Body);
    }

    [Fact]
    public void Generic_events_are_not_listed()
    {
        var recent = new ConversationLines();
        var ev = Event(null, 0) with { Conversation = null, Source = ContentSource.None };
        Assert.Equal(NotificationKind.Generic, NotificationComposer.Compose(ev, Defaults, recent).Kind);
        Assert.Equal(0, recent.Count);
    }

    // ------------------------------------------------------------------ one line each

    [Fact]
    public void Short_text_is_kept_on_its_line()
    {
        Assert.Equal("你好", ToastText.FitOneLine("你好"));
        Assert.Equal(new string('字', 18), ToastText.FitOneLine(new string('字', 18)));   // 252 of 256 epx
    }

    [Fact]
    public void Text_that_would_wrap_is_cut_with_an_ellipsis()
    {
        Assert.Equal(new string('字', 17) + "…", ToastText.FitOneLine(new string('字', 19)));
        Assert.Equal(new string('a', 32) + "…", ToastText.FitOneLine(new string('a', 40)));
    }

    [Fact]
    public void Line_breaks_become_spaces()
    {
        Assert.Equal("第一行 第二行", ToastText.FitOneLine("第一行\n\n第二行\r\n"));
    }

    [Fact]
    public void Larger_text_size_fits_less()
    {
        Assert.Equal(new string('字', 11) + "…", ToastText.FitOneLine(new string('字', 13), textScale: 1.5));
    }

    [Fact]
    public void Emoji_are_not_split()
    {
        var s = ToastText.FitOneLine(string.Concat(Enumerable.Repeat("😀", 20)));
        Assert.EndsWith("…", s);
        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 13)) + "…", s);
    }

    [Fact]
    public void Newest_message_is_cut_with_an_ellipsis_where_Windows_would_drop_the_rest()
    {
        string forty = new string('字', 40);
        Assert.Equal(new string('字', 35) + "…", ToastText.Fit(forty, 2));         // 18 on the first line, 17 + "…"
        Assert.Equal(forty, ToastText.Fit(forty, 3));
        Assert.Equal("a\nb…", ToastText.Fit("a\nb\nc", 2));                        // a newline starts a line
        Assert.Equal("短消息", ToastText.Fit("短消息", 1));
    }

    [Fact]
    public void Width_estimate_matches_the_measured_line()
    {
        // 2026-10-02, 150 %: this line was drawn about 261 epx wide (the next character wrapped); the estimate stays
        // within a few percent of it, so lines cut to the estimate's 256 epx do not wrap.
        double width = "下的时候 Windows 怎样截断它，后面还有".EnumerateRunes().Sum(r => ToastText.Width(r.ToString()));
        Assert.InRange(width, 255, 285);
    }
}
