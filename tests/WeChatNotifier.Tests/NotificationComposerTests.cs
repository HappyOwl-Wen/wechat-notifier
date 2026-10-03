using WeChatNotifier.Core;
using WeChatNotifier.Core.Notifications;

namespace WeChatNotifier.Tests;

public class NotificationComposerTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 20, 15, 3, TimeSpan.FromHours(2));
    private static readonly NotificationOptions Defaults = new();

    private static MessageEvent Event(string? conversation = "Alice", string? text = "晚上一起吃饭？", int unread = 1, int newMessages = 1,
        ContentSource source = ContentSource.SessionPreview, bool mentioned = false, string? hint = null) => new()
    {
        Conversation = conversation,
        ConversationHint = hint,
        Kind = text is null ? MessageKind.Unknown : MessageKind.Text,
        Text = text,
        Source = source,
        UnreadCount = unread,
        NewMessages = newMessages,
        Mentioned = mentioned,
        DetectedAt = At,
        TriggerToDetect = TimeSpan.FromMilliseconds(280),
        Fingerprint = "fp",
        Reason = "unread-increase",
    };

    [Fact]
    public void Text_message_shows_conversation_and_text()
    {
        var n = NotificationComposer.Compose(Event(), Defaults);
        Assert.Equal(NotificationKind.Message, n.Kind);
        Assert.Equal("Alice", n.Title);
        Assert.Equal("晚上一起吃饭？", n.Body);
        Assert.Null(n.Attribution);
        Assert.Equal(ConversationKey.Of("Alice"), n.Tag); // also the click argument: no name in the payload's arguments
        Assert.Equal(NotificationComposer.ChatGroup, n.Group);
        Assert.Equal(At, n.Timestamp);
        Assert.Equal(At + Defaults.Expiration, n.Expires);
        Assert.False(n.Silent);
        Assert.Equal(TimeSpan.FromMilliseconds(280), n.TriggerToDetect);
    }

    [Fact]
    public void Unread_count_above_one_goes_into_the_attribution()
    {
        Assert.Equal("3 条未读", NotificationComposer.Compose(Event(unread: 3), Defaults).Attribution);
    }

    [Theory]
    [InlineData(1, "新消息")]
    [InlineData(3, "3 条新消息")]
    public void Tray_state_without_text_says_new_messages(int newMessages, string body)
    {
        var n = NotificationComposer.Compose(Event(text: null, unread: 4, newMessages: newMessages, source: ContentSource.SessionMetadata), Defaults);
        Assert.Equal("Alice", n.Title);
        Assert.Equal(body, n.Body);
        Assert.Equal("4 条未读", n.Attribution);
    }

    [Fact]
    public void Preview_off_hides_the_text_but_keeps_the_conversation()
    {
        var n = NotificationComposer.Compose(Event(text: "secret"), new NotificationOptions { ShowMessagePreview = false });
        Assert.Equal("Alice", n.Title);
        Assert.Equal("新消息", n.Body);
        Assert.DoesNotContain("secret", n.Body);
    }

    [Fact]
    public void Mention_is_marked_like_WeChat_does()
    {
        Assert.Equal("[有人@我] Tom: 3 点开会？", NotificationComposer.Compose(Event(text: "Tom: 3 点开会？", mentioned: true), Defaults).Body);
    }

    [Fact]
    public void Placeholder_of_non_text_messages_is_kept()
    {
        Assert.Equal("[图片]", NotificationComposer.Compose(Event(text: "[图片]"), Defaults).Body);
    }

    [Fact]
    public void Generic_event_is_a_WeChat_notification_with_the_hint_as_a_guess()
    {
        var n = NotificationComposer.Compose(Event(conversation: null, text: null, unread: 0, source: ContentSource.None, hint: "Bob"), Defaults);
        Assert.Equal(NotificationKind.Generic, n.Kind);
        Assert.Equal(NotificationComposer.WeChatTitle, n.Title);
        Assert.Equal("收到新消息", n.Body);
        Assert.Equal("可能来自「Bob」", n.Attribution);
        // Not a conversation key: a guess never decides which chat a click opens.
        Assert.Equal(NotificationComposer.GenericTag, n.Tag);
    }

    [Fact]
    public void Unusable_conversation_name_falls_back_to_generic()
    {
        Assert.Equal(NotificationKind.Generic, NotificationComposer.Compose(Event(conversation: " \u0001 "), Defaults).Kind);
    }

    [Fact]
    public void Options_for_sound_and_duration_are_carried()
    {
        var n = NotificationComposer.Compose(Event(), new NotificationOptions { PlaySound = false, LongDuration = true });
        Assert.True(n.Silent);
        Assert.True(n.LongDuration);
    }

    [Fact]
    public void Test_notification_is_labelled_and_opens_no_conversation()
    {
        var n = NotificationComposer.Test(At, Defaults);
        Assert.Equal(NotificationKind.Test, n.Kind);
        Assert.Equal(NotificationComposer.TestTag, n.Tag);
    }

    [Fact]
    public void Conversation_key_is_stable_short_and_carries_no_name()
    {
        var a = ConversationKey.Of("张三");
        Assert.Equal(a, ConversationKey.Of("张三"));
        Assert.Equal(a, NotificationComposer.ConversationTag("张三"));
        Assert.NotEqual(a, ConversationKey.Of("李四"));
        Assert.True(a.Length <= 64); // Windows' limit for tags
        Assert.Matches("^c[0-9a-f]{24}$", a);
    }

    [Fact]
    public void Clean_removes_what_XML_cannot_carry_and_keeps_text()
    {
        Assert.Equal("a\nb\tc", NotificationComposer.Clean("  a\r\nb\tc\u0001\u000B  ", 100));
        Assert.Equal("x\ny", NotificationComposer.Clean("x\ry", 100));
        Assert.Equal("ok", NotificationComposer.Clean("o\uD800k￿", 100));   // lone surrogate, non-character
        Assert.Equal("👍🏽 好的 🇨🇳", NotificationComposer.Clean("👍🏽 好的 🇨🇳", 100)); // pairs and sequences survive
    }

    [Fact]
    public void Long_text_is_shortened_without_splitting_an_emoji()
    {
        var text = new string('a', 9) + "👨‍👩‍👧" + "tail";
        var s = NotificationComposer.Clean(text, 12);
        Assert.True(s.Length <= 12);
        Assert.EndsWith("…", s);
        Assert.Equal(new string('a', 9) + "…", s); // the family emoji does not fit and is not cut in half
    }

    [Theory]
    [InlineData("张三", "张")]
    [InlineData("tom", "T")]
    [InlineData("😀 Bob", "B")]
    [InlineData("😀🎉", "#")]
    [InlineData("  123abc", "1")]
    [InlineData("𠮷野家", "𠮷")]          // a CJK character outside the BMP stays whole
    public void Initial_for_generated_avatars(string name, string initial) =>
        Assert.Equal(initial, NotificationComposer.Initial(name));

    [Fact]
    public void Avatar_and_picture_follow_the_options()
    {
        var avatar = new WeChatNotifier.Core.Imaging.Bitmap32(2, 2);
        var picture = new WeChatNotifier.Core.Imaging.Bitmap32(4, 3);
        var ev = Event(text: "[图片]") with { Avatar = avatar, Image = picture };

        var all = NotificationComposer.Compose(ev, Defaults);
        Assert.Same(avatar, all.Avatar);
        Assert.Equal("Alice", all.AvatarText);
        Assert.Same(picture, all.Image);

        var noCache = NotificationComposer.Compose(ev, new NotificationOptions { StoreImageCache = false });
        Assert.Null(noCache.Avatar);              // no copied image may reach the disk...
        Assert.Equal("Alice", noCache.AvatarText); // ...generated initials still can
        Assert.Null(noCache.Image);

        var noPreview = NotificationComposer.Compose(ev, new NotificationOptions { ShowMessagePreview = false });
        Assert.Null(noPreview.Image);             // a picture is message content

        var noAvatar = NotificationComposer.Compose(ev, new NotificationOptions { ShowAvatars = false });
        Assert.Null(noAvatar.Avatar);
        Assert.Null(noAvatar.AvatarText);
    }

    [Fact]
    public void Nothing_readable_explains_how_to_get_names()
    {
        var ev = Event(conversation: null, text: null, unread: 0, source: ContentSource.None) with { Reason = EventReasons.NothingReadable };
        Assert.Contains("打开一次微信", NotificationComposer.Compose(ev, Defaults).Attribution);
    }

    [Fact]
    public void Long_message_body_is_capped()
    {
        var n = NotificationComposer.Compose(Event(text: new string('长', 2000)), Defaults);
        Assert.True(n.Body.Length <= NotificationComposer.MaxBodyLength);
        Assert.EndsWith("…", n.Body);
    }
}
