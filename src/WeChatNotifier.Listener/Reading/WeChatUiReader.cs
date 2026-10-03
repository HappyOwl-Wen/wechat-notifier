using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using WeChatNotifier.Core;
using WeChatNotifier.Core.Imaging;
using WeChatNotifier.Diagnostics;
using WeChatNotifier.Listener.Profile;

namespace WeChatNotifier.Listener.Reading;

/// <summary>Newest readable message of an open conversation.</summary>
/// <param name="Rect">The cell on screen (physical px) and the list's visible area around it, for picture capture.</param>
internal sealed record MessageCell(MessageKind Kind, string Text, PixelRect? Rect = null, PixelRect? ListRect = null);

internal enum OpenResult
{
    /// <summary>A UIA action was sent to the session cell; the caller verifies that the chat opened.</summary>
    Requested,
    AlreadyOpen,

    /// <summary>The session list is not on screen (yet): WeChat is still showing, or another tab is open.</summary>
    NotReadable,
    NotFound,

    /// <summary>Two visible conversations have this name; opening one of them could be the wrong chat.</summary>
    Ambiguous,
    NotSupported,
    Failed,
}

/// <summary>Session cells from one read.</summary>
/// <param name="Fresh">
/// Found through navigation, i.e. the list is visible and the names carry previews. False for reads through
/// held references (window in the tray, or another tab shown), whose names lack preview and time.
/// </param>
internal sealed record SessionRead(IReadOnlyList<SessionCell> Cells, bool Fresh);

/// <summary>
/// Targeted UIA reads of WeChat's main window (spec: never rescan the whole window on every event).
///
/// Qt 5.15's UIA bridge skips invisible children during navigation, so nothing can be *found* while WeChat is
/// hidden in the tray — but element references captured while the window was visible stay readable. Hence:
///  - window visible or minimized: fresh reads (one FindAll on the held session list, ~3–15 ms);
///  - window hidden: read the held session cells one by one (~1 ms each).
/// Owned by the listener thread.
/// </summary>
internal sealed class WeChatUiReader
{
    private readonly UiaSession _uia;
    private readonly DiagnosticLog _log;
    private IUIAutomationElement? _sessionList;
    private IUIAutomationElement? _messageList;
    private IUIAutomationElement? _chatTitle;
    private List<IUIAutomationElement> _cells = [];

    public WeChatUiReader(UiaSession uia, DiagnosticLog log)
    {
        _uia = uia;
        _log = log;
    }

    public IntPtr CapturedFor { get; private set; }

    public DateTime? CapturedAtUtc { get; private set; }

    /// <summary>Title of the conversation open in the main window at the last visible read.</summary>
    public string? OpenConversation { get; private set; }

    public int HeldCells => _cells.Count;

    public bool HasReferences => _sessionList is not null && _cells.Count > 0;

    public void Invalidate()
    {
        _sessionList = _messageList = _chatTitle = null;
        _cells = [];
        CapturedFor = IntPtr.Zero;
        CapturedAtUtc = null;
    }

    /// <summary>UIA ClassName of a top-level window (Qt reports the widget class, e.g. "mmui::MainWindow").</summary>
    public string? GetClassName(IntPtr hwnd)
    {
        try { return _uia.Automation.ElementFromHandleBuildCache(hwnd, _uia.Element).CachedClassName; }
        catch (COMException) { return null; }
    }

    /// <summary>
    /// Captures references to the session list, its cells, the open conversation's title and message list with
    /// one BuildUpdatedCache(Subtree) call (~15–35 ms). Needs the window to be visible (not hidden).
    /// Returns null when no session cell could be found (held references are then kept, not replaced).
    /// </summary>
    public SessionRead? Capture(IntPtr mainHwnd)
    {
        var sw = Stopwatch.StartNew();
        var root = _uia.Automation.ElementFromHandleBuildCache(mainHwnd, _uia.Subtree);

        IUIAutomationElement? sessionList = null, messageList = null, chatTitle = null;
        var stack = new Stack<IUIAutomationElement>();
        stack.Push(root);
        while (stack.Count > 0 && (sessionList is null || messageList is null || chatTitle is null))
        {
            var el = stack.Pop();
            var aid = _uia.CachedString(el, UiaSession.AutomationIdProperty);
            if (aid == WeChatUiProfile.SessionListAutomationId) sessionList ??= el;
            else if (aid == WeChatUiProfile.MessageListAutomationId) messageList ??= el;
            else if (aid?.EndsWith(WeChatUiProfile.CurrentChatNameIdSuffix, StringComparison.Ordinal) == true) chatTitle ??= el;
            foreach (var child in UiaSession.CachedChildren(el)) stack.Push(child);
        }

        var cells = sessionList is null
            ? []
            : UiaSession.CachedChildren(sessionList)
                .Where(c => _uia.CachedString(c, UiaSession.ClassNameProperty) == WeChatUiProfile.SessionCellClass)
                .ToList();
        if (cells.Count == 0)
        {
            // Session list absent or not visible (e.g. the Contacts tab is open): keep what we hold.
            _log.Debug("reader", $"capture: no visible session cells under 0x{mainHwnd:X} ({sw.ElapsedMilliseconds} ms)");
            return null;
        }

        _sessionList = sessionList;
        _messageList = messageList;
        _chatTitle = chatTitle;
        _cells = cells;
        CapturedFor = mainHwnd;
        CapturedAtUtc = DateTime.UtcNow;
        OpenConversation = chatTitle is null ? null : _uia.CachedString(chatTitle, UiaSession.NameProperty);
        _log.Debug("reader", $"capture: {cells.Count} session cells, message list {(messageList is null ? "absent" : "found")}, {sw.ElapsedMilliseconds} ms");
        return new SessionRead(cells.Select(c => WeChatUiProfile.ParseSessionCell(_uia.CachedString(c, UiaSession.NameProperty) ?? "")).ToList(), Fresh: true);
    }

    /// <summary>Reads the session cells in the current window state. Null when nothing is readable.</summary>
    public SessionRead? ReadSessions(IntPtr mainHwnd, bool windowHidden)
    {
        if (!windowHidden)
        {
            if (_sessionList is not null && CapturedFor == mainHwnd)
            {
                try
                {
                    var arr = _sessionList.FindAllBuildCache(TreeScope.TreeScope_Children, _uia.True, _uia.Element);
                    var cells = UiaSession.Elements(arr)
                        .Where(c => _uia.CachedString(c, UiaSession.ClassNameProperty) == WeChatUiProfile.SessionCellClass)
                        .ToList();
                    // A gone chat title means WeChat replaced the chat page: capture everything again below.
                    if (cells.Count > 0 && RefreshOpenConversation())
                    {
                        _cells = cells;
                        return new SessionRead(cells.Select(c => WeChatUiProfile.ParseSessionCell(_uia.CachedString(c, UiaSession.NameProperty) ?? "")).ToList(), Fresh: true);
                    }
                }
                catch (COMException ex) when (UiaSession.IsGone(ex)) { }
            }
            if (Capture(mainHwnd) is { } captured) return captured; // references missing or stale: one full capture
        }

        // Hidden in the tray (or the session list is not on screen): navigation finds nothing,
        // but references held from the last visible read remain readable.
        if (_cells.Count == 0) return null;
        var parsed = new List<SessionCell>(_cells.Count);
        var alive = new List<IUIAutomationElement>(_cells.Count);
        foreach (var cell in _cells)
        {
            try
            {
                parsed.Add(WeChatUiProfile.ParseSessionCell(cell.CurrentName ?? ""));
                alive.Add(cell);
            }
            catch (COMException ex) when (UiaSession.IsGone(ex)) { }
        }
        if (alive.Count < _cells.Count) _log.Info("reader", $"{_cells.Count - alive.Count} held session cells are gone ({alive.Count} left)");
        _cells = alive;
        return new SessionRead(parsed, Fresh: false);
    }

    /// <summary>
    /// Titles, on-screen rectangles (physical px) and unread counts (WeChat then draws its badge over the avatar) of
    /// the session cells as of the last visible read — current only right after a fresh read, which is when they
    /// are used (avatar capture).
    /// </summary>
    public List<(string Title, PixelRect Rect, int Unread)> CellRects()
    {
        var result = new List<(string, PixelRect, int)>(_cells.Count);
        foreach (var cell in _cells)
        {
            var parsed = WeChatUiProfile.ParseSessionCell(_uia.CachedString(cell, UiaSession.NameProperty) ?? "");
            if (parsed.Title.Length > 0 && PixelRect.FromUia(_uia.CachedRect(cell)) is { } rect) result.Add((parsed.Title, rect, parsed.Unread));
        }
        return result;
    }

    /// <summary>
    /// The one place where the listener acts on WeChat's UI, and only after the user clicked a notification
    /// (spec §17, option 3): asks the visible session cell whose <see cref="ConversationKey"/> is
    /// <paramref name="key"/> to open, like a click. <paramref name="useSelect"/> picks SelectionItem.Select
    /// instead of Invoke (Qt maps Invoke to the item's "press" action). Never acts on an ambiguous name. The
    /// caller verifies the result via the chat title.
    /// </summary>
    public OpenResult OpenSession(IntPtr mainHwnd, string key, bool useSelect)
    {
        var read = ReadSessions(mainHwnd, windowHidden: false);
        if (read is not { Fresh: true }) return OpenResult.NotReadable;
        if (OpenConversation is { } open && ConversationKey.Of(open) == key) return OpenResult.AlreadyOpen;

        IUIAutomationElement? match = null;
        int count = 0;
        for (int i = 0; i < read.Cells.Count && i < _cells.Count; i++)
        {
            if (read.Cells[i].Title.Length == 0 || ConversationKey.Of(read.Cells[i].Title) != key) continue;
            match = _cells[i];
            count++;
        }
        if (count == 0) return OpenResult.NotFound;
        if (count > 1) return OpenResult.Ambiguous;

        try
        {
            if (useSelect)
            {
                if (match!.GetCurrentPattern(UiaSession.SelectionItemPatternId) is not IUIAutomationSelectionItemPattern select) return OpenResult.NotSupported;
                select.Select();
            }
            else
            {
                if (match!.GetCurrentPattern(UiaSession.InvokePatternId) is not IUIAutomationInvokePattern invoke) return OpenResult.NotSupported;
                invoke.Invoke();
            }
            return OpenResult.Requested;
        }
        catch (COMException ex)
        {
            _log.Info("open", $"UIA {(useSelect ? "Select" : "Invoke")} failed 0x{ex.HResult:X8}");
            return OpenResult.Failed;
        }
    }

    /// <summary>
    /// For opening a conversation by a click (spec §17; WeChat 4.1 ignores the UIA actions): the on-screen rectangle of
    /// the visible session cell whose key is <paramref name="key"/>, and the session list's visible area — fresh read.
    /// </summary>
    /// <returns>
    /// <see cref="OpenResult.Requested"/> with both rectangles; otherwise <see cref="OpenResult.AlreadyOpen"/>,
    /// <see cref="OpenResult.NotReadable"/>, <see cref="OpenResult.NotFound"/> or <see cref="OpenResult.Ambiguous"/>.
    /// </returns>
    public OpenResult FindSessionCell(IntPtr mainHwnd, string key, out PixelRect cell, out PixelRect list)
    {
        cell = list = default;
        var read = ReadSessions(mainHwnd, windowHidden: false);
        if (read is not { Fresh: true } || _sessionList is null) return OpenResult.NotReadable;
        if (OpenConversation is { } open && ConversationKey.Of(open) == key) return OpenResult.AlreadyOpen;
        int count = 0;
        for (int i = 0; i < read.Cells.Count && i < _cells.Count; i++)
        {
            if (read.Cells[i].Title.Length == 0 || ConversationKey.Of(read.Cells[i].Title) != key) continue;
            if (PixelRect.FromUia(_uia.CachedRect(_cells[i])) is { } rect) cell = rect;
            count++;
        }
        if (count == 0 || cell.IsEmpty) return OpenResult.NotFound;
        if (count > 1) return OpenResult.Ambiguous;
        try
        {
            var listRect = (double[])_sessionList.GetCurrentPropertyValue(UiaSession.BoundingRectangleProperty);
            if (PixelRect.FromUia(listRect) is not { } visible) return OpenResult.NotReadable;
            list = visible;
            return OpenResult.Requested;
        }
        catch (COMException) { return OpenResult.NotReadable; }
        catch (InvalidCastException) { return OpenResult.NotReadable; }
    }

    /// <summary>
    /// The end of the main window's open conversation (visible or minimized window only), see <see cref="MessageTail"/>.
    /// With <paramref name="refresh"/> — and whenever the held list shows nothing — the references are captured again
    /// first (one Subtree cache, ~15–35 ms): WeChat can replace the chat page, and a list read through an old
    /// reference shows nothing, or not the open conversation. Also refreshes <see cref="OpenConversation"/>.
    /// </summary>
    public MessageTail ReadMessageTail(IntPtr mainHwnd, bool refresh)
    {
        if (!refresh)
        {
            var tail = RefreshOpenConversation() ? ReadTail() : MessageTail.NoList;
            if (tail.State is not (TailState.NoList or TailState.NoCells)) return tail;
        }
        // Without visible session cells the capture keeps the old references; they must not be read as the open chat.
        return Capture(mainHwnd) is null ? MessageTail.NoList with { Refreshed = true } : ReadTail() with { Refreshed = true };
    }

    private MessageTail ReadTail()
    {
        if (_messageList is null) return MessageTail.NoList;
        try
        {
            var listRect = (double[])_messageList.GetCurrentPropertyValue(UiaSession.BoundingRectangleProperty);
            var arr = _messageList.FindAllBuildCache(TreeScope.TreeScope_Children, _uia.True, _uia.Element);
            return MessageTail.Of(TailCells(UiaSession.Elements(arr)), PixelRect.FromUia(listRect));
        }
        catch (COMException ex) when (UiaSession.IsGone(ex)) { _messageList = null; return MessageTail.NoList; }
        catch (InvalidCastException) { return MessageTail.NoList; }
    }

    /// <summary>For a detached chat window: its conversation name (window Name) and the end of its message list.</summary>
    public (string? Conversation, MessageTail Tail) ReadChatWindow(IntPtr hwnd)
    {
        try
        {
            var root = _uia.Automation.ElementFromHandleBuildCache(hwnd, _uia.Subtree);
            if (_uia.CachedString(root, UiaSession.ClassNameProperty) != WeChatUiProfile.ChatSingleWindowUiaClass) return (null, MessageTail.NoList);
            var conversation = _uia.CachedString(root, UiaSession.NameProperty);

            var stack = new Stack<IUIAutomationElement>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var el = stack.Pop();
                if (_uia.CachedString(el, UiaSession.AutomationIdProperty) == WeChatUiProfile.MessageListAutomationId)
                    return (conversation, MessageTail.Of(TailCells(UiaSession.CachedChildren(el)), PixelRect.FromUia(_uia.CachedRect(el))));
                foreach (var child in UiaSession.CachedChildren(el)) stack.Push(child);
            }
            return (conversation, MessageTail.NoList);
        }
        catch (COMException) { return (null, MessageTail.NoList); }
    }

    private IEnumerable<TailCell> TailCells(IEnumerable<IUIAutomationElement> items)
    {
        foreach (var item in items)
        {
            if (PixelRect.FromUia(_uia.CachedRect(item)) is not { } rect) continue;
            yield return new TailCell(_uia.CachedString(item, UiaSession.ClassNameProperty), _uia.CachedString(item, UiaSession.NameProperty), rect);
        }
    }

    /// <returns>False when the title element is gone (WeChat replaced the chat page).</returns>
    private bool RefreshOpenConversation()
    {
        if (_chatTitle is null) return true;
        try
        {
            OpenConversation = _chatTitle.CurrentName;
            return true;
        }
        catch (COMException ex) when (UiaSession.IsGone(ex))
        {
            _chatTitle = null;
            return false;
        }
    }
}
