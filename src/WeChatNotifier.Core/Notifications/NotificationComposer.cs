using System.Globalization;
using System.Text;

namespace WeChatNotifier.Core.Notifications;

/// <summary>
/// Turns a <see cref="MessageEvent"/> into a notification (spec §6/§7): round avatar, conversation (contact or
/// group) name as title, the message as body — for groups WeChat's own "sender: text" — and a picture preview
/// when one could be copied. One notification per conversation: a newer message replaces the older notification,
/// so a burst of messages updates one banner instead of stacking ten, and Notification Center keeps one entry per
/// chat with WeChat's unread count; the replacement lists the conversation's latest messages, one per line, oldest on
/// top (<see cref="ConversationLines"/>). Pure logic apart from that list.
/// </summary>
public static class NotificationComposer
{
    public const string ChatGroup = "chat";
    public const string GenericTag = "wechat-generic";
    public const string TestTag = "self-test";
    public const string TestGroup = "test";
    public const string WeChatTitle = "微信";

    internal const int MaxTitleLength = 64;
    internal const int MaxBodyLength = 512;

    /// <param name="recent">
    /// The conversations' latest messages: this event is added, and its notification shows the earlier ones above
    /// it. Null: this message alone. Not used while message previews are off — they are message content.
    /// </param>
    public static NotificationContent Compose(MessageEvent ev, NotificationOptions options, ConversationLines? recent = null)
    {
        if (ev.Conversation is not { } conversation || Clean(conversation, MaxTitleLength) is not { Length: > 0 } title)
            return Generic(ev, options);

        string? text = options.ShowMessagePreview && ev.Text is { } raw && Clean(raw, MaxBodyLength) is { Length: > 0 } cleaned
            ? (ev.Mentioned ? "[有人@我] " + cleaned : cleaned)
            : null;
        string tag = ConversationTag(conversation);
        IReadOnlyList<ConversationLines.Line> lines = recent is not null && options.ShowMessagePreview
            ? recent.Add(tag, text, ev.NewMessages, ev.UnreadCount)
            : [new ConversationLines.Line(text, text is null ? Math.Max(1, ev.NewMessages) : 1)];

        var newest = lines[^1];
        string body = newest.Text ?? (ev.Mentioned ? "[有人@我] " : "") + NewMessagesText(newest.Count);
        var earlier = new string[lines.Count - 1];
        for (int i = 0; i < earlier.Length; i++) earlier[i] = lines[i].Text ?? $"[{NewMessagesText(lines[i].Count)}]";

        return new NotificationContent
        {
            Kind = NotificationKind.Message,
            Tag = tag,
            Group = ChatGroup,
            Title = title,
            Body = body,
            EarlierLines = earlier,
            Attribution = ev.UnreadCount > 1 ? $"{ev.UnreadCount} 条未读" : null,
            Timestamp = ev.DetectedAt,
            Expires = ev.DetectedAt + options.Expiration,
            Silent = !options.PlaySound,
            LongDuration = options.LongDuration,
            TriggerToDetect = ev.TriggerToDetect,
            DetectedAt = ev.DetectedAt,
            // Copied images reach the disk only through the temporary cache, so both need it.
            Avatar = options.ShowAvatars && options.StoreImageCache ? ev.Avatar : null,
            AvatarText = options.ShowAvatars ? title : null,
            Image = options.ShowImagePreview && options.StoreImageCache && options.ShowMessagePreview ? ev.Image : null,
        };
    }

    public static NotificationContent Test(DateTimeOffset now, NotificationOptions options) => new()
    {
        Kind = NotificationKind.Test,
        Tag = TestTag,
        Group = TestGroup,
        Title = "WeChat Notifier",
        Body = "测试通知：Windows 原生通知工作正常。点击它会打开微信。",
        Timestamp = now,
        Expires = now + TimeSpan.FromMinutes(10),
        Silent = !options.PlaySound,
        LongDuration = options.LongDuration,
        AvatarText = options.ShowAvatars ? "WeChat Notifier" : null,
    };

    /// <summary>A message from the notifier itself (started, already running…), shown with the app icon.</summary>
    public static NotificationContent AppMessage(string body, DateTimeOffset now) => new()
    {
        Kind = NotificationKind.Test,
        Tag = AppTag,
        Group = TestGroup,
        Title = "WeChat Notifier",
        Body = Clean(body, MaxBodyLength),
        Timestamp = now,
        Expires = now + TimeSpan.FromMinutes(30),
        Silent = true,
    };

    public const string AppTag = "app";

    /// <summary>
    /// The character drawn on a generated avatar: the first letter, digit or CJK character of the name (Latin
    /// upper-cased), "#" when there is none (names made only of emoji or symbols).
    /// </summary>
    public static string Initial(string name)
    {
        var e = StringInfo.GetTextElementEnumerator(name.Trim());
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            if (char.IsLetterOrDigit(element, 0)) return element.Length == 1 ? char.ToUpperInvariant(element[0]).ToString() : element[..(char.IsSurrogatePair(element, 0) ? 2 : 1)];
        }
        return "#";
    }

    /// <summary>Stable notification tag for a conversation: its <see cref="ConversationKey"/>, so it carries no name.</summary>
    public static string ConversationTag(string conversation) => ConversationKey.Of(conversation);

    /// <summary>
    /// Makes text safe for the notification payload (XML): drops characters XML 1.0 cannot carry (control
    /// characters other than tab/newline, unpaired surrogates, U+FFFE/U+FFFF), normalises newlines, trims, and
    /// shortens to <paramref name="maxLength"/> UTF-16 units without splitting an emoji or other grapheme.
    /// </summary>
    public static string Clean(string text, int maxLength)
    {
        var sb = new StringBuilder(Math.Min(text.Length, maxLength + 8));
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(c).Append(text[++i]);
                }
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') continue;
                sb.Append('\n');
                continue;
            }
            if (c is < ' ' and not '\n' and not '\t' or '￾' or '￿') continue;
            sb.Append(c);
        }

        var s = sb.ToString().Trim();
        if (s.Length <= maxLength) return s;

        int cut = 0;
        while (cut < s.Length)
        {
            int next = StringInfo.GetNextTextElementLength(s, cut);
            if (cut + next > maxLength - 1) break;
            cut += next;
        }
        return s[..cut].TrimEnd() + "…";
    }

    private static NotificationContent Generic(MessageEvent ev, NotificationOptions options) => new()
    {
        Kind = NotificationKind.Generic,
        Tag = GenericTag,
        Group = ChatGroup,
        Title = WeChatTitle,
        Body = "收到新消息",
        // Only a guess (the conversation open when WeChat went to the tray), so it is worded as one. With nothing
        // readable at all, say how to get names back: WeChat has not been on screen since the notifier started.
        Attribution = ev.ConversationHint is { } hint && Clean(hint, MaxTitleLength) is { Length: > 0 } h
            ? $"可能来自「{h}」"
            : ev.Reason == EventReasons.NothingReadable ? "打开一次微信窗口后，通知会显示会话名" : null,
        Timestamp = ev.DetectedAt,
        Expires = ev.DetectedAt + options.Expiration,
        Silent = !options.PlaySound,
        LongDuration = options.LongDuration,
        TriggerToDetect = ev.TriggerToDetect,
        DetectedAt = ev.DetectedAt,
    };

    private static string NewMessagesText(int count) => count > 1 ? $"{count} 条新消息" : "新消息";
}
