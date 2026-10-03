using System.Text.RegularExpressions;
using WeChatNotifier.Core;

namespace WeChatNotifier.Listener.Profile;

/// <param name="Text">Preview without WeChat's markers ("[n条] ", "[有人@我]"); group previews keep "sender: text".</param>
/// <param name="EmbeddedUnread">Count that muted conversations / folders embed in the preview itself ("[5条] ...").</param>
/// <param name="IsDraft">The user's own unsent draft ("[草稿] ..."), never an incoming message.</param>
/// <param name="IsSystemNotice">A notice such as a recalled message, not new content.</param>
public sealed record PreviewInfo(string? Text, MessageKind Kind, bool Mentioned, int? EmbeddedUnread, bool IsDraft, bool IsSystemNotice);

/// <summary>Interprets WeChat's conversation-list previews and message-cell labels (zh-CN and en-US UI).</summary>
public static class PreviewText
{
    private static readonly Regex EmbeddedCount = new(@"^\[(?<n>\d+)条\]\s*", RegexOptions.CultureInvariant);

    /// <summary>"[图片]" or, in groups, "Tom: [图片]" — the bracketed tag decides the message kind.</summary>
    private static readonly Regex Placeholder = new(@"^(?:[^:\n\[]{1,40}: )?\[(?<tag>[^\[\]\n]{1,16})\]", RegexOptions.CultureInvariant);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>"Tom: text": the sender prefix WeChat puts on group previews (and on the 公众号 folder's).</summary>
    private static readonly Regex SenderPrefix = new(@"^(?<sender>[^:\n\[\]]{1,40}): (?<text>.+)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly string[] MentionMarkers = ["[有人@我]", "[@我]"];
    private const string DraftMarker = "[草稿]";
    private static readonly string[] SystemNoticeFragments = ["撤回了一条消息", "recalled a message"];

    private static readonly Dictionary<string, MessageKind> Tags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["图片"] = MessageKind.Image, ["Photo"] = MessageKind.Image, ["Image"] = MessageKind.Image,
        ["动画表情"] = MessageKind.Sticker, ["表情"] = MessageKind.Sticker, ["Sticker"] = MessageKind.Sticker,
        ["语音"] = MessageKind.Voice, ["Voice"] = MessageKind.Voice,
        ["视频"] = MessageKind.Video, ["Video"] = MessageKind.Video,
        ["文件"] = MessageKind.File, ["File"] = MessageKind.File,
        ["链接"] = MessageKind.Link, ["Link"] = MessageKind.Link, ["视频号"] = MessageKind.Link,
        ["位置"] = MessageKind.Location, ["Location"] = MessageKind.Location,
        ["名片"] = MessageKind.ContactCard, ["Contact Card"] = MessageKind.ContactCard,
        ["视频通话"] = MessageKind.Call, ["语音通话"] = MessageKind.Call, ["通话"] = MessageKind.Call,
        ["Video Call"] = MessageKind.Call, ["Voice Call"] = MessageKind.Call,
        ["红包"] = MessageKind.RedPacket, ["微信红包"] = MessageKind.RedPacket, ["Red Packet"] = MessageKind.RedPacket,
        ["转账"] = MessageKind.Transfer, ["Transfer"] = MessageKind.Transfer,
        ["小程序"] = MessageKind.MiniProgram, ["Mini Program"] = MessageKind.MiniProgram,
        ["聊天记录"] = MessageKind.ChatHistory, ["Chat History"] = MessageKind.ChatHistory,
        ["音乐"] = MessageKind.Music, ["Music"] = MessageKind.Music,
    };

    public static PreviewInfo Analyze(string? preview)
    {
        if (preview is null) return new PreviewInfo(null, MessageKind.Unknown, false, null, false, false);

        var text = preview.Trim();
        int? embedded = null;
        if (EmbeddedCount.Match(text) is { Success: true } m)
        {
            embedded = int.Parse(m.Groups["n"].Value);
            text = text[m.Length..];
        }

        bool mentioned = false;
        foreach (var marker in MentionMarkers)
        {
            if (!text.Contains(marker, StringComparison.Ordinal)) continue;
            mentioned = true;
            text = text.Replace(marker, "", StringComparison.Ordinal).Trim();
        }

        bool draft = text.StartsWith(DraftMarker, StringComparison.Ordinal);
        bool notice = SystemNoticeFragments.Any(f => text.Contains(f, StringComparison.Ordinal));

        var kind = text.Length == 0 ? MessageKind.Unknown : MessageKind.Text;
        if (Placeholder.Match(text) is { Success: true } p && Tags.TryGetValue(p.Groups["tag"].Value, out var tagged))
            kind = tagged;

        return new PreviewInfo(text, kind, mentioned, embedded, draft, notice);
    }

    /// <summary>Media cells name their content without brackets ("图片").</summary>
    public static MessageKind KindOfCellLabel(string? label) =>
        label is not null && Tags.TryGetValue(label.Trim(), out var kind) ? kind : MessageKind.Unknown;

    public static bool IsDraft(string? preview) => preview is not null && Analyze(preview).IsDraft;

    /// <summary>
    /// Splits a group preview "sender: text". A private message that itself starts with "word: " looks the same,
    /// so the sender is only used as a label; what is shown stays WeChat's own "sender: text".
    /// </summary>
    public static (string? Sender, string Text) SplitSender(string text) =>
        SenderPrefix.Match(text) is { Success: true } m ? (m.Groups["sender"].Value, m.Groups["text"].Value) : (null, text);

    /// <summary>
    /// True when <paramref name="fullText"/> is the message behind an (elided) preview, i.e. the preview minus
    /// a trailing "…" is a prefix of the full text after whitespace normalisation.
    /// </summary>
    public static bool IsCompatible(string? preview, string? fullText)
    {
        if (string.IsNullOrWhiteSpace(preview) || string.IsNullOrWhiteSpace(fullText)) return false;
        var p = Normalize(preview);
        if (p.EndsWith('…')) p = p[..^1].TrimEnd();
        if (p.EndsWith("...", StringComparison.Ordinal)) p = p[..^3].TrimEnd();
        return p.Length > 0 && Normalize(fullText).StartsWith(p, StringComparison.Ordinal);
    }

    private static string Normalize(string s) => Whitespace.Replace(s.Trim(), " ");
}
