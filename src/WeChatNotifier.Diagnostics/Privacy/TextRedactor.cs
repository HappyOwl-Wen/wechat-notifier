using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WeChatNotifier.Diagnostics.Privacy;

/// <summary>
/// Masks chat content while keeping its *shape*: whitespace, digits, punctuation and a vocabulary of WeChat
/// UI words survive, every other letter becomes a placeholder ('x' = Latin letter, '□' = other script such
/// as CJK, '◊' = emoji/symbol). "张三: 你好 😀 [图片]" becomes "□□: □□ ◊ [图片]".
/// Text matching the reveal pattern (e.g. a test marker) is kept verbatim.
/// </summary>
public sealed class TextRedactor
{
    private static readonly string[] UiVocabulary = new[]
    {
        // zh-CN UI words / message-type placeholders
        "文件传输助手", "消息免打扰", "条新消息", "新消息", "条消息", "有人@我", "@所有人", "聊天记录", "动画表情",
        "语音通话", "视频通话", "公众号", "服务号", "订阅号", "视频号", "小程序", "通讯录", "朋友圈",
        "图片", "视频", "语音", "文件", "链接", "表情", "位置", "名片", "红包", "转账", "撤回", "通话", "群聊",
        "会话", "消息", "聊天", "微信", "收藏", "搜索", "发送", "设置", "更多", "未读", "已读", "置顶", "草稿",
        "折叠", "昨天", "前天", "星期", "今天", "上午", "下午", "晚上", "凌晨", "中午", "早上", "刚刚", "分钟前",
        "年", "月", "日", "周", "条", "我",
        // en-US UI words
        "File Transfer", "WeChat", "Weixin", "Photo", "Image", "Video", "Voice", "Sticker", "Link", "Location",
        "Chats", "Chat", "Contacts", "Favorites", "Moments", "Search", "Send", "Settings", "Unread", "Messages",
        "Message", "Mute", "Pinned", "Draft", "Yesterday", "Today", "new", "message", "messages", "unread",
    }.OrderByDescending(w => w.Length).ToArray();

    /// <summary>WeChat account / chat-room identifiers that can appear inside technical strings.</summary>
    private static readonly Regex AccountIds = new(@"wxid_[A-Za-z0-9_\-]+|[0-9]{5,}@chatroom|gh_[0-9a-f]{8,}", RegexOptions.CultureInvariant);

    /// <summary>Per-process key: tokens are stable within one run (so events can be correlated) but not across runs.</summary>
    private static readonly byte[] TokenKey = RandomNumberGenerator.GetBytes(32);

    private readonly Regex? _reveal;
    private readonly string[] _identityPrefixes;
    private readonly HashSet<string> _safeTitles;

    /// <param name="enabled">False passes text through unchanged (only escapes newlines).</param>
    /// <param name="revealPattern">Regex of text to keep verbatim, e.g. <c>WXN-\w+</c>.</param>
    /// <param name="identityPrefixes">Prefixes after which a technical string carries a name/id (masked by <see cref="Ids"/>).</param>
    /// <param name="safeTitles">Window captions that are never personal (kept by <see cref="Title"/>).</param>
    public TextRedactor(bool enabled, string? revealPattern, IEnumerable<string>? identityPrefixes = null, IEnumerable<string>? safeTitles = null)
    {
        Enabled = enabled;
        if (!string.IsNullOrEmpty(revealPattern))
            _reveal = new Regex(revealPattern, RegexOptions.CultureInvariant);
        _identityPrefixes = identityPrefixes?.ToArray() ?? [];
        _safeTitles = new HashSet<string>(safeTitles ?? [], StringComparer.Ordinal);
    }

    public bool Enabled { get; }

    private static string Token(string value)
    {
        var mac = HMACSHA256.HashData(TokenKey, Encoding.UTF8.GetBytes(value));
        return "#" + Convert.ToHexString(mac, 0, 4).ToLowerInvariant();
    }

    /// <summary>
    /// Masks identifiers inside technical strings such as AutomationIds: the suffix after a configured prefix
    /// (WeChat puts conversation names there) and wxid / chat-room ids anywhere become a per-run token.
    /// </summary>
    public string? Ids(string? s)
    {
        if (s is null || !Enabled) return s;
        foreach (var prefix in _identityPrefixes)
        {
            if (s.StartsWith(prefix, StringComparison.Ordinal) && s.Length > prefix.Length)
                return prefix + Token(s[prefix.Length..]);
        }
        return AccountIds.Replace(s, m => Token(m.Value));
    }

    /// <summary>Window captions: known safe captions stay readable, anything else (e.g. a contact name) is masked.</summary>
    public string Title(string title) =>
        title.Length == 0 || !Enabled || _safeTitles.Contains(title) ? title : Apply(title) ?? "";

    public string? Apply(string? s)
    {
        if (s is null) return null;
        if (!Enabled) return Escape(s);

        var keep = new bool[s.Length];
        if (_reveal is not null)
            foreach (Match m in _reveal.Matches(s))
                for (int i = m.Index; i < m.Index + m.Length; i++) keep[i] = true;

        var sb = new StringBuilder(s.Length);
        int pos = 0;
        while (pos < s.Length)
        {
            if (keep[pos]) { AppendEscaped(sb, s[pos]); pos++; continue; }

            string? word = null;
            foreach (var w in UiVocabulary)
            {
                if (w.Length <= s.Length - pos && string.CompareOrdinal(s, pos, w, 0, w.Length) == 0) { word = w; break; }
            }
            if (word is not null) { sb.Append(word); pos += word.Length; continue; }

            if (char.IsSurrogatePair(s, pos)) { sb.Append('◊'); pos += 2; continue; }

            char c = s[pos++];
            if (c == '\n') sb.Append('⏎');
            else if (c == '\r') { }
            else if (char.IsWhiteSpace(c)) sb.Append(' ');
            else if (c < 128 && char.IsAsciiDigit(c)) sb.Append(c);
            else if (c < 128 && char.IsAsciiLetter(c)) sb.Append('x');
            else if (c < 128) sb.Append(c); // ASCII punctuation
            else
            {
                switch (char.GetUnicodeCategory(c))
                {
                    case UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.OtherPunctuation
                        or UnicodeCategory.DashPunctuation or UnicodeCategory.InitialQuotePunctuation
                        or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.ConnectorPunctuation
                        or UnicodeCategory.DecimalDigitNumber:
                        sb.Append(c);
                        break;
                    case UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark:
                        break; // zero-width joiners, variation selectors, combining marks
                    case UnicodeCategory.OtherSymbol or UnicodeCategory.MathSymbol or UnicodeCategory.ModifierSymbol
                        or UnicodeCategory.CurrencySymbol:
                        sb.Append('◊');
                        break;
                    default:
                        sb.Append('□');
                        break;
                }
            }
        }
        return sb.ToString();
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) AppendEscaped(sb, c);
        return sb.ToString();
    }

    private static void AppendEscaped(StringBuilder sb, char c)
    {
        if (c == '\n') sb.Append('⏎');
        else if (c != '\r') sb.Append(c);
    }
}
