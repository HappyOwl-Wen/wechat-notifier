using System.Text.RegularExpressions;
using WeChatNotifier.Core.Imaging;

namespace WeChatNotifier.Listener.Profile;

/// <summary>
/// Every WeChat-specific assumption lives here (spec §23): process hints, window classes, AutomationIds,
/// cell classes and the accessible-name format. After a WeChat update, run <c>WeChatInspector locate</c>;
/// if something moved, only this file (and <see cref="PreviewText"/>) should need changes.
/// Deliberately contains no tree paths or child indexes. Verified on Weixin 4.1.13.65.
/// </summary>
public static class WeChatUiProfile
{
    // ---- processes / windows ----

    /// <summary>Cheap first-pass process-name hints. Identity is always confirmed via the version resource.</summary>
    public static readonly string[] ProcessNameHints = ["Weixin", "WeChat", "WeChatApp"];

    /// <summary>Tencent executables that ship with WeChat but never host the chat UI.</summary>
    public static readonly string[] HelperProcessNames =
    [
        "WeChatAppEx", "WeChatOCR", "WeChatPlayer", "WeChatUtility", "WeChatBrowser", "WeChatUpdate",
        "WeChatWeb", "WeChatStore", "WeixinUpdate", "crashpad_handler",
    ];

    public const string CompanyName = "Tencent";

    /// <summary>ProductName values of the UI executable's version resource.</summary>
    public static readonly string[] ProductNames = ["Weixin", "WeChat", "微信"];

    /// <summary>Localized captions of the main window (never personal, safe to log).</summary>
    public static readonly string[] MainWindowTitles = ["微信", "WeChat", "Weixin"];

    /// <summary>Captions of WeChat's technical windows (never personal).</summary>
    public static readonly string[] TechnicalWindowTitles = ["WxTrayIconMessageWindow", "Default IME", "MSCTFIME UI"];

    /// <summary>
    /// UIA ClassName of the main window. Qt reports the QObject class, which is independent of the Qt version
    /// and UI language (the Win32 class embeds the Qt version, e.g. "Qt51514QWindowIcon").
    /// "WeChatMainWndForPC" is the WeChat 3.x Win32 class, kept only for detection.
    /// </summary>
    public static readonly string[] MainWindowUiaClassNames = ["mmui::MainWindow", "WeChatMainWndForPC"];

    /// <summary>Detached chat window; its UIA Name is the conversation name.</summary>
    public const string ChatSingleWindowUiaClass = "mmui::ChatSingleWindow";

    /// <summary>
    /// AutomationId prefixes followed by a conversation identity: "session_item_" + display name (session
    /// cells), "ChatSingleWindow" + account id (detached chat windows). Must be redacted in any output.
    /// </summary>
    public static readonly string[] AutomationIdPrefixesWithIdentity = ["session_item_", "ChatSingleWindow"];

    /// <summary>Qt top-level windows that can be a main or chat window (Win32 class "Qt&lt;ver&gt;QWindowIcon").</summary>
    public static bool IsQtTopLevelClass(string win32Class) =>
        win32Class.StartsWith("Qt", StringComparison.Ordinal) && win32Class.EndsWith("QWindowIcon", StringComparison.Ordinal);

    /// <summary>Any Qt-created window (QWindowIcon, QWindowToolSaveBits, QWindowPopup...).</summary>
    public static bool IsQtWindowClass(string win32Class) =>
        win32Class.StartsWith("Qt", StringComparison.Ordinal) && win32Class.Contains("QWindow", StringComparison.Ordinal);

    /// <summary>Win32 classes worth a UIA class lookup when looking for the main / chat windows.</summary>
    public static bool IsCandidateTopLevelClass(string win32Class) =>
        IsQtTopLevelClass(win32Class) || win32Class == "WeChatMainWndForPC";

    // ---- notification-area (tray) icon ----

    /// <summary>
    /// Tooltip of WeChat's notification-area icon, which the Windows 11 shell exposes as the UIA Name of the icon's
    /// button (observed: " 微信", with a leading space). Compared trimmed and exactly, so 企业微信 never matches.
    /// Clicking this icon is how a hidden WeChat is shown: starting Weixin.exe again opens a second WeChat with a
    /// login screen, because WeChat 4 supports several instances.
    /// </summary>
    public static readonly string[] TrayIconNames = ["微信", "Weixin", "WeChat"];

    public static bool IsTrayIconName(string? name) =>
        name is not null && TrayIconNames.Any(n => n.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    // ---- semantic elements (located by AutomationId / ClassName, never by index) ----

    /// <summary>Conversation list: List "会话", class mmui::XTableView. Holds only the rows currently in view.</summary>
    public const string SessionListAutomationId = "session_list";

    /// <summary>One conversation row. Name = "title\n[已置顶\n][[n条] \n]preview\ntime\n[消息免打扰\n]".</summary>
    public const string SessionCellClass = "mmui::ChatSessionCell";

    /// <summary>Messages of the open conversation: List "消息", class mmui::RecyclerListView (recycles cells).</summary>
    public const string MessageListAutomationId = "chat_message_list";

    /// <summary>Title of the open conversation (the AutomationId is a long dotted path ending with this).</summary>
    public const string CurrentChatNameIdSuffix = "current_chat_name_label";

    /// <summary>Message cell classes. Unknown mmui::*ItemView classes are treated as <see cref="MessageCellType.Other"/>.</summary>
    public static readonly IReadOnlyDictionary<string, MessageCellType> MessageCellTypes = new Dictionary<string, MessageCellType>(StringComparer.Ordinal)
    {
        ["mmui::ChatTextItemView"] = MessageCellType.Text,           // Name = full message text (emoji included)
        ["mmui::ChatBubbleReferItemView"] = MessageCellType.Media,   // Name = "图片" for pictures
        ["mmui::ChatItemView"] = MessageCellType.TimeSeparator,      // Name = "19:23" / "昨天 15:44"
    };

    // ---- pixel geometry for Level 3 capture (spec §4/§7: no UIA element exposes avatars or pictures) ----

    /// <summary>
    /// The part of a session cell (which has no child elements) that holds the conversation avatar: the square on
    /// its left. The avatar itself is then found in the copy by its pixels
    /// (<see cref="Core.Imaging.ImageAnalysis.FindAvatar"/>), so the crop fits it exactly at every DPI and layout.
    /// </summary>
    public static PixelRect SessionAvatarArea(PixelRect cell) => cell with { Width = Math.Min(cell.Width, cell.Height) };

    /// <summary>
    /// Where to click a session cell to open its conversation (WeChat 4.1 ignores UIA Invoke/Select on the cells): on
    /// the name/preview text, right of the avatar and left of the time column, at the middle of the row. Null unless
    /// the middle half of the row is inside the list's visible area (a row scrolled partly out of view is not clicked).
    /// </summary>
    public static (int X, int Y)? SessionClickPoint(PixelRect cell, PixelRect list)
    {
        if (cell.IsEmpty || list.IsEmpty) return null;
        int x = cell.X + cell.Width * 3 / 5, y = cell.Y + cell.Height / 2, quarter = cell.Height / 4;
        bool inside = x >= list.X && x < list.Right && y - quarter >= list.Y && y + quarter <= list.Bottom;
        return inside ? (x, y) : null;
    }

    /// <summary>Plausible avatar sides for the pixel search, relative to the cell height.</summary>
    public const double MinAvatarRatio = 0.4, MaxAvatarRatio = 0.8;

    /// <summary>
    /// Estimate of the avatar for when the pixel search fails before any avatar was found (e.g. an avatar whose
    /// edge matches the list background): a centred square of <see cref="AvatarRatio"/> × the cell height with the
    /// same margin on its left.
    /// </summary>
    public static PixelRect SessionAvatar(PixelRect cell)
    {
        int size = (int)Math.Round(cell.Height * AvatarRatio);
        int margin = (cell.Height - size) / 2;
        return new PixelRect(cell.X + margin, cell.Y + margin, size, size);
    }

    /// <summary>
    /// Avatar side / cell height in WeChat 4.1: about 0.56 (estimated from a notification that used 0.625 and
    /// showed list background around the avatar). <c>WeChatInspector captureprobe</c> prints the exact value.
    /// </summary>
    public const double AvatarRatio = 0.56;

    /// <summary>
    /// Message cells span the whole list width. A received picture lies right of the sender-avatar column
    /// (margin + 40 px avatar + gap, logical px); the picture itself is then found by
    /// <see cref="Core.Imaging.ImageAnalysis.FindPicture"/>.
    /// </summary>
    public const int MessageAvatarColumnLogical = 72;

    /// <summary>Smallest picture side worth a preview (logical px); WeChat's thumbnails are at least ~60.</summary>
    public const int MinPictureSideLogical = 32;

    /// <summary>The part of an image message cell where a received picture can be, in screen pixels.</summary>
    public static PixelRect MessagePictureArea(PixelRect cell, PixelRect messageList, double dpiScale)
    {
        var visible = cell.Intersect(messageList);
        int column = (int)Math.Round(MessageAvatarColumnLogical * dpiScale);
        return visible.IsEmpty || visible.Width <= column ? default : visible with { X = visible.X + column, Width = visible.Width - column };
    }

    // ---- session cell accessible-name format ----

    private static readonly Regex UnreadLine = new(@"^\[(?<n>\d+)条\]\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex PinnedLine = new(@"^.?置顶$", RegexOptions.CultureInvariant);
    private static readonly Regex TimeLine = new(
        @"^(\d{1,2}:\d{2}|昨天 \d{1,2}:\d{2}|前天 \d{1,2}:\d{2}|星期[一二三四五六日天]|\d{1,2}/\d{1,2}|\d{2,4}/\d{1,2}/\d{1,2})$",
        RegexOptions.CultureInvariant);
    private const string MutedLine = "消息免打扰";

    /// <summary>
    /// Parses a ChatSessionCell Name. While the main window is hidden WeChat drops the preview, time and
    /// muted lines ("title\n[n条] \n"); <see cref="SessionCell.Preview"/> is then null.
    /// </summary>
    public static SessionCell ParseSessionCell(string name)
    {
        var lines = name.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        string title = lines.Count > 0 ? lines[0] : "";
        bool pinned = false, muted = false;
        int unread = 0;
        string? time = null, preview = null;

        int i = 1;
        if (i < lines.Count && PinnedLine.IsMatch(lines[i])) { pinned = true; i++; }
        if (i < lines.Count && UnreadLine.Match(lines[i]) is { Success: true } m) { unread = int.Parse(m.Groups["n"].Value); i++; }
        int end = lines.Count;
        if (end > i && lines[end - 1] == MutedLine) { muted = true; end--; }
        if (end > i && TimeLine.IsMatch(lines[end - 1])) { time = lines[end - 1]; end--; }
        if (end > i) preview = string.Join("\n", lines.Skip(i).Take(end - i)).TrimEnd();

        return new SessionCell(title, pinned, unread, preview, time, muted);
    }
}

public enum MessageCellType
{
    Other = 0,
    Text,
    Media,
    TimeSeparator,
}

/// <summary>One parsed conversation row.</summary>
/// <param name="Preview">Last-message preview as WeChat shows it (elided); null while the window is hidden.</param>
public sealed record SessionCell(string Title, bool Pinned, int Unread, string? Preview, string? Time, bool Muted)
{
    public bool HasContent => Preview is not null;
}
