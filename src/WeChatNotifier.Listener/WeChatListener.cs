using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using WeChatNotifier.Core;
using WeChatNotifier.Core.Imaging;
using WeChatNotifier.Core.Notifications;
using WeChatNotifier.Diagnostics;
using WeChatNotifier.Listener.Discovery;
using WeChatNotifier.Listener.Hooks;
using WeChatNotifier.Listener.Interop;
using WeChatNotifier.Listener.Profile;
using WeChatNotifier.Listener.Reading;
using WeChatNotifier.Listener.Tracking;

namespace WeChatNotifier.Listener;

/// <summary>
/// Detects new WeChat messages without polling (architecture: docs/wechat-ui-analysis.md §9).
///
/// Trigger: WeChat flashes its taskbar button for every incoming message while it is not in the foreground
/// (visible, minimized or hidden in the tray) — received as HSHELL_FLASH through the shell hook. Each flash
/// starts a short burst of targeted UIA reads of the conversation list; <see cref="SessionTracker"/> turns the
/// differences into <see cref="MessageEvent"/>s. Window and process lifecycle arrive as WinEvents / a process
/// wait handle. With no WeChat activity the listener thread is blocked and uses no CPU.
///
/// The listener only reads WeChat's UI, with one exception: after the user clicks a notification,
/// <see cref="OpenWeChat"/> brings WeChat forward and may open that conversation (spec §17).
///
/// All UIA work happens on one MTA thread; OS hooks live on their own thread and only post work items.
/// </summary>
public sealed class WeChatListener : IDisposable
{
    /// <summary>After this long without any change in a burst, report a generic "new message".</summary>
    internal const int UnexplainedAfterMs = 1000;

    /// <summary>After a notification click, how long to wait for WeChat's window before giving up on the chat.</summary>
    internal const int OpenTimeoutMs = 5000;
    private const int OpenRetryMs = 200;
    private const int OpenVerifyMs = 350;
    private const int ClickRestoreMs = 120;
    private const int TrayStepMs = 100;
    private const int TrayFlyoutTimeoutMs = 1500;
    private const int TrayFlyoutCloseCheckMs = 500;

    /// <summary>Avatar copies of visible conversations: when the user has been in WeChat this long, at most this often.</summary>
    private const int AvatarSweepDelayMs = 1500;
    private const int AvatarSweepIntervalMs = 30_000;

    /// <summary>
    /// No avatar copies this long after the PC woke up or the session was unlocked: WeChat's window picture and UIA's
    /// positions disagreed then (2026-10-02 10:39, every copy of a sweep showed the cell below its avatar).
    /// </summary>
    private const int AvatarQuietAfterWakeMs = 5000;

    /// <summary>
    /// After an image message whose conversation is open on screen: how long to wait for WeChat to show the new
    /// bubble and load the thumbnail (it shows a grey placeholder first) before notifying without it, and how often to
    /// look meanwhile. Only during such a wait — never otherwise.
    /// </summary>
    internal const int PictureWaitMs = 1500;
    private const int PictureRetryMs = 200;

    private ListenerOptions _options; // replaced only on the listener thread (OptionsItem)
    private readonly DiagnosticLog _log;
    private readonly BlockingCollection<ListenerItem> _inbox = new(new ConcurrentQueue<ListenerItem>(), 4096);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly SessionTracker _tracker = new();
    private readonly ReadScheduler _scheduler = new();
    private readonly DedupCache _dedup = new(256);
    private readonly Dictionary<(int Pid, long Created), bool> _verdicts = [];
    private readonly Dictionary<IntPtr, string?> _chatWindows = [];          // detached chat window -> newest message seen
    private readonly HashSet<IntPtr> _burstChatWindows = [];
    private readonly HashSet<string> _burstHandled = new(StringComparer.Ordinal);
    private readonly List<string> _readConversations = [];
    private readonly List<(long DueMs, Action Action)> _deferred = [];     // only while a click or a sweep is pending
    private readonly AvatarCache _avatars;
    private readonly GroupEvidence _groups = new();
    private PendingPicture? _pendingPicture; // an image message held back until its thumbnail has loaded
    private int _pendingPictureId;
    private (double X, double Y, double Side)? _avatarFit; // where the last found avatar sat, relative to the cell height
    private long _lastAvatarSweepMs = long.MinValue / 2;
    private long _avatarsQuietUntilMs;
    private bool _sweepPending;

    /// <summary>
    /// Once WeChat activity (reads, window copies, opening a chat) has been quiet this long: one full garbage
    /// collection. UI Automation elements and notification objects hold native memory that is freed only when their
    /// wrappers are collected, and this process allocates too little for the GC to run by itself — private memory went
    /// 13 -> 29 MB over a morning of messages and stayed there (2026-10-02). A few ms, at most once per quiet period.
    /// </summary>
    internal const int SettleAfterMs = 30_000;
    private long _lastActivityMs;
    private bool _settlePending;
    private Thread? _thread;
    private HookThread? _hooks;
    private UiaSession? _uia;
    private WeChatUiReader? _reader;
    private ShellTray? _tray;
    private long _trayDeadlineMs;
    private AttachedWeChat? _wechat;
    private string? _lastImagePath;
    private PendingOpen? _pendingOpen;
    private long _lastFlashMs;
    private bool _wechatHadFocus;
    private bool _burstSynthetic;
    private bool _burstExplained;
    private bool _stopping;
    private ListenerStatus _status = new(ListenerState.Starting, null, null, WeChatWindowState.NotFound, false, 0, null, false, 0, 0);

    public WeChatListener(ListenerOptions? options = null, DiagnosticLog? log = null)
    {
        _options = options ?? new ListenerOptions();
        _log = log ?? DiagnosticLog.Disabled;
        _avatars = new AvatarCache(_options.AvatarFolder);
    }

    /// <summary>Raised on the listener thread; handlers must return quickly.</summary>
    public event Action<MessageEvent>? MessageDetected;

    /// <summary>Raised on the listener thread whenever a component state changes.</summary>
    public event Action<ListenerStatus>? StatusChanged;

    /// <summary>
    /// Raised on the listener thread when a conversation's unread counter went back to 0 (read in WeChat or on
    /// the phone), e.g. to withdraw its notification. Seen at the next read of the list, not instantly.
    /// </summary>
    public event Action<string>? ConversationRead;

    public ListenerMetrics Metrics { get; } = new();

    public ListenerStatus Status => Volatile.Read(ref _status);

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "WeChatNotifier.Listener" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Diagnostics / self-test: run a read burst as if WeChat's main window had flashed.</summary>
    public void InjectSyntheticFlash() => Post(new SyntheticFlashItem(Now));

    /// <summary>
    /// The user clicked a notification (spec §17): bring WeChat to the front and, when
    /// <paramref name="conversationKey"/> is given (<see cref="ConversationKey"/> of the conversation), open that
    /// conversation. Call right away: Windows lets the clicked app move the foreground only briefly.
    /// </summary>
    public void OpenWeChat(string? conversationKey) => Post(new OpenRequestItem(conversationKey, Now));

    /// <summary>Re-checks WeChat (process, window, references) — e.g. after the session was unlocked. Cheap; no polling.</summary>
    public void Refresh() => Post(new RefreshItem(Now));

    /// <summary>
    /// Settings changed: applied between two work items, without a restart — a restart would drop the references
    /// to WeChat's conversation list, which cannot be re-captured while WeChat sits in the tray.
    /// </summary>
    public void UpdateOptions(ListenerOptions options) => Post(new OptionsItem(options, Now));

    /// <summary>The user cleared the image cache: forget the copied avatars, also the ones kept on disk.</summary>
    public void ClearAvatars() => Post(new ClearAvatarsItem(Now));

    /// <summary>Deletes the avatars kept in <paramref name="folder"/> (uninstall, cleanup) — for when no listener runs.</summary>
    public static bool DeleteSavedAvatars(string folder) => AvatarCache.DeleteFolder(folder);

    public void Dispose()
    {
        if (_thread is null) return;
        Post(new StopItem(Now));
        _thread.Join(5000);
        _thread = null;
    }

    private long Now => _clock.ElapsedMilliseconds;

    private void Post(ListenerItem item)
    {
        try
        {
            if (!_inbox.TryAdd(item)) _log.Warn("listener", "inbox full, item dropped");
        }
        catch (InvalidOperationException) { } // shutting down
    }

    // ------------------------------------------------------------------ loop

    private void Run()
    {
        try
        {
            // UIA rectangles, GetWindowRect and PrintWindow must all speak physical pixels for Level 3 capture.
            Native.SetThreadDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            _uia = new UiaSession();
            _reader = new WeChatUiReader(_uia, _log);
            _tray = new ShellTray(_uia);
            _hooks = new HookThread(Post, () => Now, _log);
            _hooks.Start();
            if (!_options.CaptureAvatars) _avatars.DeleteAll(); // switched off while not running: no copies may stay
            AttachAtStartup();

            while (true)
            {
                // Wake-ups exist only inside a read burst or while a notification click is being handled.
                long? next = _scheduler.NextDueMs;
                foreach (var (dueMs, _) in _deferred) next = next is { } n ? Math.Min(n, dueMs) : dueMs;
                int timeout = next is { } due ? (int)Math.Clamp(due - Now, 0, int.MaxValue) : Timeout.Infinite;
                if (_inbox.TryTake(out var item, timeout))
                {
                    if (item is StopItem) break;
                    Guard(item.GetType().Name, () => Handle(item));
                }
                if (_scheduler.TakeDue(Now)) Guard("read", ReadBurst);
                RunDeferred();
            }
        }
        catch (Exception ex)
        {
            _log.Error("listener", $"listener thread failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
        finally
        {
            _stopping = true;
            Detach("stopped");
            _hooks?.Dispose();
            _inbox.CompleteAdding();
            SetStatus(Status with { State = ListenerState.Stopped });
        }
    }

    private void Guard(string what, Action action)
    {
        try { action(); }
        catch (COMException ex)
        {
            _log.Warn("listener", $"{what}: UIA call failed 0x{ex.HResult:X8}");
            if (UiaSession.IsGone(ex)) _reader?.Invalidate();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("listener", $"{what}: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
    }

    private void Handle(ListenerItem item)
    {
        switch (item)
        {
            case FlashItem f:
                OnFlash(f.Hwnd, f.AtMs, synthetic: false);
                break;
            case SyntheticFlashItem s:
                if (_wechat is { MainHwnd: var main } && main != IntPtr.Zero) OnFlash(main, s.AtMs, synthetic: true);
                else _log.Info("listener", "self-test ignored: WeChat main window not known");
                break;
            case ShellWindowCreatedItem c:
                OnWindowAppeared(c.Hwnd);
                break;
            case WindowSignalItem w:
                OnWindowSignal(w);
                break;
            case ProcessExitedItem p:
                if (_wechat?.Info.Pid == p.Pid) Detach("WeChat exited");
                break;
            case ResumeItem:
                OnResume();
                break;
            case ShellRestartedItem:
                _log.Info("hooks", "Explorer restarted, shell hook re-registered");
                PublishStatus();
                break;
            case OpenRequestItem o:
                OnOpenRequest(o);
                break;
            case OptionsItem o:
                // Copies switched off: forget the ones in memory and on disk too.
                if (_options.CaptureAvatars && !o.Options.CaptureAvatars) _avatars.DeleteAll();
                _options = o.Options;
                _log.Info("listener", "options updated");
                break;
            case ClearAvatarsItem:
                _avatars.DeleteAll();
                _log.Info("capture", "copied avatars deleted");
                break;
            case RefreshItem:
                _avatarsQuietUntilMs = Now + AvatarQuietAfterWakeMs; // sent when the session was unlocked
                if (_wechat is null) AttachAtStartup();
                else
                {
                    CaptureIfVisible("refresh");
                    PublishStatus();
                }
                break;
        }
    }

    // ------------------------------------------------------------------ triggers

    private void OnFlash(IntPtr hwnd, long atMs, bool synthetic)
    {
        int pid = Native.GetPid(hwnd);
        if (!synthetic && _wechat?.Info.Pid != pid)
        {
            if (!IsWeChatProcess(pid)) { Metrics.ForeignFlash(); return; }
            TryAttach(pid, "flash"); // WeChat started while hidden in the tray: its first flash finds it
            if (_wechat?.Info.Pid != pid) return;
        }

        Metrics.Flash();
        if (!synthetic && !_options.NotifyWhileWeChatFocused && IsWeChatForeground())
        {
            Metrics.SuppressedFocused();
            return;
        }

        if (!_scheduler.BurstActive)
        {
            _burstChatWindows.Clear();
            _burstHandled.Clear();
            _burstSynthetic = synthetic;
            _burstExplained = false;
            Metrics.Burst();
            _log.Debug("burst", $"start ({WindowState()})");
        }
        if (hwnd != _wechat!.MainHwnd)
        {
            if (!_chatWindows.ContainsKey(hwnd)) ClassifyWindow(hwnd);
            if (_chatWindows.ContainsKey(hwnd)) _burstChatWindows.Add(hwnd);
        }
        _lastFlashMs = atMs;
        _scheduler.OnFlash(atMs);
    }

    private void ReadBurst()
    {
        Busy();
        long now = Now;
        bool changed = false;
        if (_wechat is not null)
        {
            changed |= ReadChatWindows();
            changed |= ReadMainWindow(detect: true, out bool readable);
            if (!readable && !_burstExplained)
            {
                // Nothing can be read (WeChat never shown since it started): report at once, don't wait.
                EmitGeneric(EventReasons.NothingReadable, now);
                changed = true;
            }
        }

        if (changed) _burstExplained = true;
        else if (!_burstExplained && now - _scheduler.BurstStartMs >= UnexplainedAfterMs)
        {
            EmitGeneric(_burstSynthetic ? EventReasons.SelfTest : "unexplained-flash", now);
        }

        if (_scheduler.OnReadCompleted(changed) is { } end)
        {
            _log.Debug("burst", $"end: flashes={end.Flashes} reads={end.Reads} change={end.ChangeSeen}");
            if (!_burstExplained) EmitGeneric(_burstSynthetic ? EventReasons.SelfTest : "unexplained-flash", Now);
        }
    }

    private bool ReadChatWindows()
    {
        bool changed = false;
        foreach (var hwnd in _burstChatWindows.ToList())
        {
            if (!Native.IsWindow(hwnd))
            {
                _burstChatWindows.Remove(hwnd);
                _chatWindows.Remove(hwnd);
                continue;
            }
            var (conversation, tail) = _reader!.ReadChatWindow(hwnd);
            if (conversation is null || tail.Newest is not { } newest) continue;
            _chatWindows.TryGetValue(hwnd, out var previous);
            if (newest.Text == previous) continue; // not updated yet: a later read of the burst will see it
            _chatWindows[hwnd] = newest.Text;
            _burstChatWindows.Remove(hwnd);
            changed = true;
            _burstHandled.Add(conversation);

            Bitmap32? picture = null;
            bool waitForPicture = false;
            if (newest.Kind == MessageKind.Image)
            {
                bool onScreen = WindowCapture.CanCapture(hwnd);
                string state = "";
                if (_options.CaptureImagePreviews && onScreen)
                {
                    picture = CapturePicture(hwnd, newest, out state);
                    waitForPicture = picture is null;
                }
                _log.Info("picture", PictureOutcome(onScreen, open: true, picture, waitForPicture, state) + " (chat window)");
            }
            var ev = new MessageEvent
            {
                Conversation = conversation,
                Kind = newest.Kind,
                Text = newest.Kind is MessageKind.Text or MessageKind.Unknown ? newest.Text : $"[{newest.Text}]",
                Avatar = _avatars.Get(ConversationKey.Of(conversation)),
                Image = picture,
                Source = ContentSource.MessageList,
                DetectedAt = DateTimeOffset.Now,
                TriggerToDetect = TriggerToDetect(),
                Fingerprint = MessageFingerprint.Create(conversation, "chat-window", newest.Text, _scheduler.BurstStartMs.ToString(CultureInfo.InvariantCulture)),
                Reason = "chat-window",
            };
            if (waitForPicture) HoldForPicture(ev, new PictureSource(hwnd, ChatWindow: true));
            else EmitInOrder(ev);
        }
        return changed;
    }

    /// <returns>True when the conversation list changed.</returns>
    private bool ReadMainWindow(bool detect, out bool readable)
    {
        readable = false;
        var main = EnsureMainWindow();
        if (main == IntPtr.Zero) return false;

        bool hidden = !Native.IsWindowVisible(main);
        var read = _reader!.ReadSessions(main, hidden);
        Metrics.Read(read is not null);
        if (read is null) return false;
        readable = true;

        _readConversations.Clear();
        ObserveGroups(read.Cells);
        var changes = _tracker.Apply(read.Cells, read.Fresh, _reader.OpenConversation, detect, _readConversations);
        foreach (var change in changes)
        {
            if (_burstHandled.Contains(change.Cell.Title)) continue;
            if (change.Muted && !_options.NotifyMutedConversations) { Metrics.SuppressedMuted(); continue; }
            var (ev, waitOn) = BuildEvent(change, read.Fresh, main);
            if (waitOn is { } source) HoldForPicture(ev, source);
            else EmitInOrder(ev);
        }
        ReportRead();
        return changes.Count > 0;
    }

    /// <summary>Learns group chats from the previews of all visible conversations ("sender: text").</summary>
    private void ObserveGroups(IReadOnlyList<SessionCell> cells)
    {
        foreach (var cell in cells)
        {
            if (cell.Preview is null || PreviewText.Analyze(cell.Preview).Text is not { } text) continue;
            _groups.Observe(ConversationKey.Of(cell.Title), PreviewText.SplitSender(text).Sender);
        }
    }

    /// <returns>The event, and where to look for the picture of an image message while holding it back (or null).</returns>
    private (MessageEvent Event, PictureSource? WaitOn) BuildEvent(SessionChange change, bool fresh, IntPtr main)
    {
        var cell = change.Cell;
        var info = PreviewText.Analyze(cell.Preview);
        var source = cell.Preview is null ? ContentSource.SessionMetadata : ContentSource.SessionPreview;
        var kind = cell.Preview is null ? MessageKind.Unknown : info.Kind;
        var text = info.Text;
        string? sender = null, body = null;
        if (text is not null) (sender, body) = PreviewText.SplitSender(text);

        // The open conversation shows the whole message (the preview is elided to the column width) and the
        // picture of an image message. Its title was read with this read of the list.
        bool open = fresh && cell.Title == _reader!.OpenConversation;
        bool onScreen = WindowCapture.CanCapture(main);
        bool wantPicture = kind == MessageKind.Image && _options.CaptureImagePreviews && fresh;
        MessageTail? tail = null;
        if (wantPicture && onScreen && (open || _reader!.OpenConversation is null))
        {
            // A picture in the open conversation (or no conversation known to be open): read through fresh
            // references (~20–35 ms) — WeChat may have replaced the chat page since they were taken, and an old list
            // shows nothing, or another conversation's pictures. Other conversations have no picture on screen.
            tail = _reader!.ReadMessageTail(main, refresh: true);
            open = cell.Title == _reader!.OpenConversation;
        }
        if (open && kind == MessageKind.Text && _options.ReadFullTextWhenOpen && FullText(main, text, sender, body) is { } full)
        {
            text = full;
            source = ContentSource.MessageList;
        }

        // Where the picture is on screen: the main window, or a detached chat window of this conversation.
        PictureSource? pictureSource = null;
        if (wantPicture && open && tail is not null) pictureSource = new PictureSource(main, ChatWindow: false);
        else if (wantPicture && !open && ChatWindowShowing(cell.Title) is { } chat)
        {
            (pictureSource, tail) = (new PictureSource(chat.Hwnd, ChatWindow: true), chat.Tail);
        }

        var key = ConversationKey.Of(cell.Title);
        var mainPictureCell = pictureSource is { ChatWindow: false } && tail?.Newest is { Kind: MessageKind.Image } c ? c : null;
        string state = tail?.Describe() ?? "";
        Bitmap32? picture = null;
        if (fresh)
        {
            picture = CaptureForEvent(key, cell.Title, mainPictureCell, out var captured);
            state = captured ?? state;
        }
        if (pictureSource is { ChatWindow: true } chatSource && tail?.Newest is { Kind: MessageKind.Image } chatCell)
            picture = CapturePicture(chatSource.Window, chatCell, out state);

        PictureSource? waitOn = null;
        if (kind == MessageKind.Image)
        {
            if (pictureSource is not null && picture is null) waitOn = pictureSource;
            _log.Info("picture", PictureOutcome(onScreen || pictureSource is not null, pictureSource is not null, picture, waitOn is not null, state)
                                 + (pictureSource is { ChatWindow: true } ? " (chat window)" : ""));
        }

        var ev = new MessageEvent
        {
            Conversation = cell.Title,
            Kind = kind,
            Text = text,
            Source = source,
            UnreadCount = cell.Unread,
            NewMessages = Math.Max(1, change.NewMessages),
            Mentioned = info.Mentioned,
            Sender = sender,
            IsGroup = _groups.IsGroup(key, sender),
            Avatar = _avatars.Get(key),
            Image = picture,
            DetectedAt = DateTimeOffset.Now,
            TriggerToDetect = TriggerToDetect(),
            // Same conversation, read epoch, counter, preview and minute = the same observation. Metadata-only
            // observations carry no content or time, so the burst makes them unique.
            Fingerprint = MessageFingerprint.Create(cell.Title, change.ReadEpoch.ToString(CultureInfo.InvariantCulture),
                cell.Unread.ToString(CultureInfo.InvariantCulture), cell.Preview, cell.Time, change.PreviousPreview,
                cell.Preview is null ? _scheduler.BurstStartMs.ToString(CultureInfo.InvariantCulture) : null),
            Reason = change.Reason,
        };
        return (ev, waitOn);
    }

    /// <summary>
    /// The open conversation's whole message for an elided preview; null when the end of its message list is not this
    /// message. A mismatch through held references is read once more through fresh ones (the chat page may be new).
    /// </summary>
    private string? FullText(IntPtr main, string? text, string? sender, string? body)
    {
        for (bool refresh = false; ; refresh = true)
        {
            var tail = _reader!.ReadMessageTail(main, refresh);
            if (tail.Newest is { Kind: MessageKind.Text } newest)
            {
                if (PreviewText.IsCompatible(text, newest.Text)) return newest.Text;
                if (sender is not null && PreviewText.IsCompatible(body, newest.Text)) return $"{sender}: {newest.Text}"; // group: cells carry no sender
            }
            if (refresh || tail.Refreshed) return null;
        }
    }

    /// <summary>A detached chat window (on screen) that shows this conversation, with the end of its message list.</summary>
    private (IntPtr Hwnd, MessageTail Tail)? ChatWindowShowing(string title)
    {
        foreach (var hwnd in _chatWindows.Keys)
        {
            if (!WindowCapture.CanCapture(hwnd)) continue;
            var (conversation, tail) = _reader!.ReadChatWindow(hwnd);
            if (conversation == title) return (hwnd, tail);
        }
        return null;
    }

    /// <summary>Why an image message's notification has (or has not) its picture — for the log; no content.</summary>
    private string PictureOutcome(bool onScreen, bool open, Bitmap32? picture, bool waiting, string state) =>
        !_options.CaptureImagePreviews ? "preview off (settings)"
        : !onScreen ? "no preview: WeChat's window is not on screen (minimized or in the tray)"
        : !open ? "no preview: the conversation is not open in WeChat"
        : picture is not null ? $"preview {picture.Width}x{picture.Height}"
        : waiting ? $"waiting up to {PictureWaitMs} ms: {state}" : $"no preview: {state}";

    // ------------------------------------------------------------------ Level 3: avatars and pictures

    /// <summary>Where a held image message's picture is on screen.</summary>
    private readonly record struct PictureSource(IntPtr Window, bool ChatWindow);

    /// <summary>
    /// One PrintWindow for what this event can use while the main window is on screen: the conversation's avatar
    /// (unless copied recently) and, for an image message in the open conversation, the picture.
    /// </summary>
    /// <param name="pictureState">Why there is no picture, when one was looked for (else null); for the log.</param>
    /// <returns>The picture, if one was found; the avatar goes into <see cref="_avatars"/>.</returns>
    private Bitmap32? CaptureForEvent(string key, string title, MessageCell? pictureCell, out string? pictureState)
    {
        pictureState = null;
        var main = _wechat?.MainHwnd ?? IntPtr.Zero;
        if (!WindowCapture.CanCapture(main)) return null;

        var regions = new List<PixelRect>(2);
        int avatarIndex = -1, pictureIndex = -1;
        PixelRect avatarCell = default;
        bool badge = false;
        if (_options.CaptureAvatars && _pendingOpen is null && Now >= _avatarsQuietUntilMs)
        {
            foreach (var (cellTitle, rect, unread) in _reader!.CellRects())
            {
                if (cellTitle != title) continue;
                if (!_avatars.NeedsCopy(key, unread > 0)) break;
                (avatarIndex, avatarCell, badge) = (regions.Count, rect, unread > 0);
                regions.Add(WeChatUiProfile.SessionAvatarArea(rect));
                break;
            }
        }
        double scale = DpiScale(main);
        if (pictureCell is not null)
        {
            if (PictureArea(pictureCell, scale) is { } area)
            {
                pictureIndex = regions.Count;
                regions.Add(area);
            }
            else pictureState = "the picture's bubble is not in the visible part of the list";
        }
        if (regions.Count == 0) return null;

        var sw = Stopwatch.StartNew();
        var crops = WindowCapture.CaptureRegions(main, regions);
        Bitmap32? picture = null;
        if (crops is not null)
        {
            if (avatarIndex >= 0 && ExtractAvatar(crops[avatarIndex], avatarCell) is { } avatar)
            {
                _avatars.Set(key, avatar, badge);
                Metrics.AvatarsCaptured(1);
            }
            if (pictureIndex >= 0) picture = ExtractPicture(crops[pictureIndex], scale, out pictureState);
        }
        else if (pictureIndex >= 0) pictureState = "copying WeChat's window failed";
        _log.Debug("capture", $"event: avatar={(avatarIndex < 0 ? "cached/off" : crops?[avatarIndex] is null ? "failed" : badge ? "copied (badge)" : "copied")} " +
                              $"picture={(pictureIndex < 0 ? "-" : picture is null ? "not found" : $"{picture.Width}x{picture.Height}")} {sw.ElapsedMilliseconds} ms");
        return picture;
    }

    /// <summary>
    /// The avatar inside a copied cell area, cropped exactly: found by its pixels; otherwise placed where the
    /// last found avatar sat (same layout, relative to the cell height); otherwise the profile's estimate. The
    /// outermost pixel ring (anti-aliased against the list) is left out. A flat crop is no avatar — the window was
    /// not painted, or the row was empty — and neither is one whose middle is the list's background (the copy did not
    /// line up with the row); generated initials are better than either.
    /// </summary>
    private Bitmap32? ExtractAvatar(Bitmap32? area, PixelRect cell)
    {
        if (area is null) return null;
        int h = cell.Height;
        PixelRect square;
        if (ImageAnalysis.FindAvatar(area, (int)(h * WeChatUiProfile.MinAvatarRatio), (int)(h * WeChatUiProfile.MaxAvatarRatio)) is { } found)
        {
            square = found;
            _avatarFit = (found.X / (double)h, found.Y / (double)h, found.Width / (double)h);
        }
        else if (_avatarFit is { } fit)
        {
            int side = (int)Math.Round(fit.Side * h);
            square = new PixelRect((int)Math.Round(fit.X * h), (int)Math.Round(fit.Y * h), side, side);
        }
        else
        {
            square = WeChatUiProfile.SessionAvatar(cell).Offset(-cell.X, -cell.Y);
        }
        int inset = Math.Max(1, square.Width / 40);
        square = new PixelRect(square.X + inset, square.Y + inset, square.Width - 2 * inset, square.Height - 2 * inset).Intersect(area.Bounds);
        if (square.IsEmpty) return null;
        var crop = area.Crop(square);
        return ImageAnalysis.IsNearlyUniform(crop, crop.Bounds, maxStdDev: 3) || !ImageAnalysis.LooksLikeAvatar(crop, area) ? null : crop;
    }

    /// <summary>The part of a picture message's cell to copy (screen px), or null when none of it is visible.</summary>
    private static PixelRect? PictureArea(MessageCell cell, double scale) =>
        cell is { Rect: { } cellRect, ListRect: { } listRect } && WeChatUiProfile.MessagePictureArea(cellRect, listRect, scale) is { IsEmpty: false } area
            ? area : null;

    /// <summary>Copies the picture of an image message's cell in <paramref name="window"/> (one PrintWindow).</summary>
    private Bitmap32? CapturePicture(IntPtr window, MessageCell cell, out string state)
    {
        double scale = DpiScale(window);
        if (PictureArea(cell, scale) is not { } area)
        {
            state = "the picture's bubble is not in the visible part of the list";
            return null;
        }
        if (WindowCapture.CaptureRegions(window, [area])?[0] is not { } crop)
        {
            state = "copying WeChat's window failed";
            return null;
        }
        return ExtractPicture(crop, scale, out state);
    }

    /// <summary>The picture inside a captured bubble area, shrunk for the notification; null while still loading.</summary>
    /// <param name="state">What was seen, for the log (no content).</param>
    private Bitmap32? ExtractPicture(Bitmap32? area, double scale, out string state)
    {
        if (area is null)
        {
            state = "copying WeChat's window failed";
            return null;
        }
        var found = ImageAnalysis.FindPicture(area, (int)Math.Round(WeChatUiProfile.MinPictureSideLogical * scale), out var background, out var search);
        Metrics.PictureCaptured(found is not null);
        state = search switch
        {
            PictureSearch.Found => "found",
            PictureSearch.AreaTooSmall => $"the bubble area is too small ({area.Width}x{area.Height})",
            PictureSearch.NoBlock => $"nothing drawn in the bubble area yet ({area.Width}x{area.Height})",
            PictureSearch.NotPictureShaped => $"no picture-shaped block in the bubble area ({area.Width}x{area.Height})",
            PictureSearch.Uniform => "the picture is still blank",
            _ => "the thumbnail is still loading (grey placeholder)",
        };
        return found is { } r
            ? PictureImage.ForNotification(area.Crop(r), background, ToastScale.PrimaryDpi())
            : null;
    }

    /// <summary>
    /// Copies the avatars of the visible conversations that have none yet, so that later notifications — mostly
    /// sent while WeChat sits in the tray — can show them. Runs after the user has been in WeChat a moment (the
    /// window is painted) and at most every 30 s; one PrintWindow for all of them. Not while a click opens a
    /// conversation or just after waking up, and the copies are dropped when the rows moved meanwhile (scrolling, a
    /// conversation moving to the top): a copy that does not line up with its row shows another row's pixels.
    /// </summary>
    private void SweepAvatars()
    {
        var main = _wechat?.MainHwnd ?? IntPtr.Zero;
        if (!_options.CaptureAvatars || Now - _lastAvatarSweepMs < AvatarSweepIntervalMs || !WindowCapture.CanCapture(main)) return;
        if (_reader!.CapturedFor != main || _pendingOpen is not null || Now < _avatarsQuietUntilMs) return;

        var missing = _reader.CellRects().Where(c => _avatars.NeedsCopy(ConversationKey.Of(c.Title), c.Unread > 0)).ToList();
        if (missing.Count == 0) return;
        _lastAvatarSweepMs = Now;
        var sw = Stopwatch.StartNew();
        var crops = WindowCapture.CaptureRegions(main, missing.Select(c => WeChatUiProfile.SessionAvatarArea(c.Rect)).ToList());
        CaptureIfVisible("avatar sweep check");
        var rowsNow = _reader.CellRects().GroupBy(c => c.Title).ToDictionary(g => g.Key, g => g.First().Rect);
        if (missing.Any(c => !rowsNow.TryGetValue(c.Title, out var rect) || rect != c.Rect))
        {
            _log.Debug("capture", $"avatars not copied: the list moved during the copy, {sw.ElapsedMilliseconds} ms");
            return;
        }
        int copied = 0;
        for (int i = 0; crops is not null && i < missing.Count; i++)
        {
            if (ExtractAvatar(crops[i], missing[i].Rect) is not { } avatar) continue;
            _avatars.Set(ConversationKey.Of(missing[i].Title), avatar, withBadge: missing[i].Unread > 0);
            copied++;
        }
        Metrics.AvatarsCaptured(copied);
        _log.Debug("capture", $"avatars copied: {copied}/{missing.Count}, cached {_avatars.Count}, {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>WeChat got the focus: copy the avatars once the window has been on screen for a moment.</summary>
    private void ScheduleAvatarSweep()
    {
        if (_sweepPending || !_options.CaptureAvatars || Now - _lastAvatarSweepMs < AvatarSweepIntervalMs) return;
        _sweepPending = true;
        Defer((int)Math.Max(AvatarSweepDelayMs, _avatarsQuietUntilMs - Now), () =>
        {
            _sweepPending = false;
            if (!IsWeChatForeground()) return; // left again (maybe to the tray): nothing on screen to copy
            CaptureIfVisible("avatar sweep");   // current cell positions (and baseline: the user is looking)
            SweepAvatars();
        });
    }

    private static double DpiScale(IntPtr hwnd) => Native.GetDpiForWindow(hwnd) is var dpi and > 0 ? dpi / 96.0 : 1.0;

    private void EmitGeneric(string reason, long now)
    {
        _burstExplained = true;
        Metrics.UnexplainedFlash();
        bool hidden = _wechat is { MainHwnd: var h } && h != IntPtr.Zero && !Native.IsWindowVisible(h);
        Emit(new MessageEvent
        {
            Conversation = null,
            // The open conversation's counter does not grow and its preview is hidden in the tray,
            // so it is the most likely source then — offered only as a hint.
            ConversationHint = hidden ? _reader?.OpenConversation : null,
            Kind = MessageKind.Unknown,
            Source = ContentSource.None,
            DetectedAt = DateTimeOffset.Now,
            TriggerToDetect = TimeSpan.FromMilliseconds(now - _scheduler.BurstStartMs),
            Fingerprint = MessageFingerprint.Create("generic", _scheduler.BurstStartMs.ToString(CultureInfo.InvariantCulture)),
            Reason = reason,
        });
    }

    private sealed record PendingPicture(int Id, MessageEvent Event, PictureSource Source, long StartMs);

    /// <summary>
    /// Holds an image message until its picture is on screen and loaded (or <see cref="PictureWaitMs"/> passed): WeChat
    /// may still be laying out or scrolling to the new bubble, and shows a grey placeholder until the thumbnail is in.
    /// </summary>
    private void HoldForPicture(MessageEvent ev, PictureSource source)
    {
        SettlePendingPicture(ev.Conversation);
        int id = ++_pendingPictureId;
        _pendingPicture = new PendingPicture(id, ev, source, Now);
        Defer(PictureRetryMs, () => RetryPicture(id));
    }

    /// <summary>Emits an event after any held image message, keeping the order of notifications.</summary>
    private void EmitInOrder(MessageEvent ev)
    {
        SettlePendingPicture(ev.Conversation);
        Emit(ev);
    }

    /// <summary>
    /// Releases a held image message without its picture — unless the next message is of the same conversation:
    /// its notification would replace this one at once, so this one is dropped instead of flashing by.
    /// </summary>
    private void SettlePendingPicture(string? nextConversation)
    {
        if (_pendingPicture is not { } p) return;
        _pendingPicture = null;
        if (nextConversation is not null && nextConversation == p.Event.Conversation)
        {
            _log.Info("picture", "superseded by a newer message of the same conversation");
            return;
        }
        _log.Info("picture", "notifying without the preview: the next message is of another conversation");
        Emit(p.Event);
    }

    private void RetryPicture(int id)
    {
        if (_pendingPicture is not { } p || p.Id != id) return;
        long waited = Now - p.StartMs;
        var (picture, state, giveUp) = TryCapturePicture(p);
        if (picture is not null)
        {
            _pendingPicture = null;
            Metrics.PictureWaited(found: true);
            _log.Info("picture", $"preview {picture.Width}x{picture.Height} after waiting {waited} ms");
            Emit(p.Event with { Image = picture });
        }
        else if (giveUp || waited >= PictureWaitMs)
        {
            _pendingPicture = null;
            Metrics.PictureWaited(found: false);
            _log.Info("picture", $"no preview after waiting {waited} ms: {state}");
            Emit(p.Event);
        }
        else
        {
            _log.Debug("picture", $"+{waited} ms: {state}");
            Defer(PictureRetryMs, () => RetryPicture(id));
        }
    }

    /// <summary>One more look at the held message's bubble: at the end of the open conversation, on screen, loaded?</summary>
    /// <returns>The picture, else what was seen (no content) and whether waiting longer is pointless.</returns>
    private (Bitmap32? Picture, string State, bool GiveUp) TryCapturePicture(PendingPicture p)
    {
        var window = p.Source.Window;
        if (!WindowCapture.CanCapture(window)) return (null, "WeChat's window left the screen (minimized, hidden or closed)", true);
        MessageTail tail;
        if (p.Source.ChatWindow)
        {
            (var conversation, tail) = _reader!.ReadChatWindow(window);
            if (conversation != p.Event.Conversation) return (null, "the chat window closed", true);
        }
        else
        {
            tail = _reader!.ReadMessageTail(window, refresh: false);
            if (_reader.OpenConversation != p.Event.Conversation) return (null, "another conversation was opened", true);
        }
        if (tail.Newest is not { Kind: MessageKind.Image } cell) return (null, tail.Describe(), false);
        var picture = CapturePicture(window, cell, out var state);
        return (picture, state, false);
    }

    private bool Emit(MessageEvent ev)
    {
        if (!_dedup.TryAdd(ev.Fingerprint))
        {
            Metrics.DuplicateDropped();
            _log.Debug("event", $"duplicate dropped ({ev.Reason})");
            return false;
        }
        Metrics.Event(ev.ContentAvailable, ev.TriggerToDetect);
        _log.Info("event", $"source={ev.Source} kind={ev.Kind} unread={ev.UnreadCount} new={ev.NewMessages} " +
                           $"latency={ev.TriggerToDetect?.TotalMilliseconds:0}ms reason={ev.Reason} window={WindowState()}");
        try { MessageDetected?.Invoke(ev); }
        catch (Exception ex) { _log.Error("event", $"handler threw {ex.GetType().Name}"); }
        return true;
    }

    /// <summary>
    /// Latency from the latest taskbar flash. WeChat flashes again for every message, so in a stream of messages
    /// each one is measured from its own flash (Phase 1 measured from the burst's first flash, which made later
    /// messages of a long burst look seconds late).
    /// </summary>
    private TimeSpan TriggerToDetect() => TimeSpan.FromMilliseconds(Now - _lastFlashMs);

    private void ReportRead()
    {
        foreach (var conversation in _readConversations)
        {
            _log.Debug("tracker", "a conversation was read (unread counter back to 0)");
            try { ConversationRead?.Invoke(conversation); }
            catch (Exception ex) { _log.Error("event", $"read handler threw {ex.GetType().Name}"); }
        }
        _readConversations.Clear();
    }

    // ------------------------------------------------------------------ windows

    private void OnWindowSignal(WindowSignalItem w)
    {
        if (w.Signal == WindowSignal.Foreground)
        {
            OnForeground(w.Hwnd);
            return;
        }
        if (_wechat is null) return;

        switch (w.Signal)
        {
            case WindowSignal.Created or WindowSignal.Shown:
                if (w.Hwnd == _wechat.MainHwnd) CaptureIfVisible("shown");
                else ClassifyWindow(w.Hwnd);
                break;
            case WindowSignal.Restored or WindowSignal.MoveSizeEnded:
                if (w.Hwnd == _wechat.MainHwnd) CaptureIfVisible(w.Signal == WindowSignal.Restored ? "restored" : "resized");
                break;
            case WindowSignal.Hidden or WindowSignal.Minimized:
                if (w.Hwnd == _wechat.MainHwnd) PublishStatus();
                break;
            case WindowSignal.Destroyed:
                if (w.Hwnd == _wechat.MainHwnd)
                {
                    _log.Info("window", "main window destroyed");
                    _wechat.MainHwnd = IntPtr.Zero;
                    _reader!.Invalidate();
                    PublishStatus();
                }
                _chatWindows.Remove(w.Hwnd);
                break;
        }
    }

    /// <summary>HSHELL_WINDOWCREATED: a window got a taskbar button (also when WeChat is restored from the tray).</summary>
    private void OnWindowAppeared(IntPtr hwnd)
    {
        int pid = Native.GetPid(hwnd);
        if (_wechat is null)
        {
            if (IsWeChatProcess(pid)) TryAttach(pid, "window created");
            return;
        }
        if (pid != _wechat.Info.Pid || !WeChatUiProfile.IsCandidateTopLevelClass(Native.GetClassName(hwnd))) return;
        if (hwnd == _wechat.MainHwnd) CaptureIfVisible("shown");
        else ClassifyWindow(hwnd);
    }

    private void OnForeground(IntPtr hwnd)
    {
        if (_wechat is null) return;
        bool isWeChat = hwnd != IntPtr.Zero && Native.GetPid(hwnd) == _wechat.Info.Pid;
        if (isWeChat)
        {
            _wechatHadFocus = true;
            ScheduleAvatarSweep();
            return;
        }
        if (!_wechatHadFocus) return;
        // The user just left WeChat while it is still shown: refresh references and the baseline now,
        // because nothing can be captured any more once the window is hidden.
        _wechatHadFocus = false;
        CaptureIfVisible("focus-lost");
    }

    private void ClassifyWindow(IntPtr hwnd)
    {
        var uiaClass = _reader!.GetClassName(hwnd);
        if (uiaClass is null) return;
        if (WeChatUiProfile.MainWindowUiaClassNames.Contains(uiaClass) && hwnd != _wechat!.MainHwnd)
        {
            _log.Info("window", $"main window is now 0x{hwnd:X}");
            _wechat.MainHwnd = hwnd;
            _reader.Invalidate();
            CaptureIfVisible("main window created");
        }
        else if (uiaClass == WeChatUiProfile.ChatSingleWindowUiaClass && !_chatWindows.ContainsKey(hwnd))
        {
            _chatWindows[hwnd] = _reader.ReadChatWindow(hwnd).Tail.Newest?.Text; // baseline
            PublishStatus();
        }
    }

    private void CaptureIfVisible(string why)
    {
        Busy();
        var main = EnsureMainWindow();
        if (main == IntPtr.Zero || !Native.IsWindowVisible(main)) return;
        var read = _reader!.Capture(main);
        Metrics.Capture(read is not null);
        if (read is null) return;

        // Outside a burst this only refreshes the baseline (the user is looking at WeChat or just was).
        _readConversations.Clear();
        ObserveGroups(read.Cells);
        var changes = _tracker.Apply(read.Cells, read.Fresh, _reader.OpenConversation, detect: _scheduler.BurstActive, _readConversations);
        foreach (var change in changes)
        {
            if (_burstHandled.Contains(change.Cell.Title)) continue;
            if (change.Muted && !_options.NotifyMutedConversations) { Metrics.SuppressedMuted(); continue; }
            var (ev, waitOn) = BuildEvent(change, read.Fresh, main);
            if (waitOn is { } source) HoldForPicture(ev, source);
            else EmitInOrder(ev);
        }
        ReportRead();
        if (changes.Count > 0) _burstExplained = true;
        _log.Debug("reader", $"captured ({why}): {read.Cells.Count} cells");
        PublishStatus();
    }

    private IntPtr EnsureMainWindow()
    {
        if (_wechat is null) return IntPtr.Zero;
        if (_wechat.MainHwnd != IntPtr.Zero && Native.IsWindow(_wechat.MainHwnd)) return _wechat.MainHwnd;
        _wechat.MainHwnd = FindMainWindow(_wechat.Info.Pid);
        if (_wechat.MainHwnd != IntPtr.Zero) _log.Info("window", $"main window found 0x{_wechat.MainHwnd:X}");
        return _wechat.MainHwnd;
    }

    private IntPtr FindMainWindow(int pid)
    {
        foreach (var hwnd in Native.TopLevelWindows(pid))
        {
            if (!WeChatUiProfile.IsCandidateTopLevelClass(Native.GetClassName(hwnd))) continue;
            if (_reader!.GetClassName(hwnd) is { } cls && WeChatUiProfile.MainWindowUiaClassNames.Contains(cls)) return hwnd;
        }
        return IntPtr.Zero;
    }

    private bool IsWeChatForeground()
    {
        var fg = Native.GetForegroundWindow();
        return fg != IntPtr.Zero && _wechat is not null && Native.GetPid(fg) == _wechat.Info.Pid;
    }

    // ------------------------------------------------------------------ process lifecycle

    private void AttachAtStartup()
    {
        foreach (var candidate in WeChatProcessLocator.FindCandidates())
        {
            if (TryAttach(candidate.Pid, "startup")) return;
        }
        _log.Info("attach", "WeChat not running; waiting for it (event-driven)");
        PublishStatus();
    }

    /// <summary>Verdict per (pid, creation time), so repeated flashes of other apps cost one lookup.</summary>
    private bool IsWeChatProcess(int pid)
    {
        var (_, created) = Native.GetProcessIdentity(pid);
        var key = (pid, created);
        if (_verdicts.TryGetValue(key, out var verdict)) return verdict;
        verdict = WeChatProcessLocator.Identify(pid) is not null;
        if (_verdicts.Count >= 256) _verdicts.Clear();
        _verdicts[key] = verdict;
        return verdict;
    }

    private bool TryAttach(int pid, string reason)
    {
        if (_wechat?.Info.Pid == pid) return true;
        var info = WeChatProcessLocator.Identify(pid);
        if (info is null) return false;

        // WeChat runs several processes from the same executable; only the UI host owns Qt top-level windows.
        IntPtr main = IntPtr.Zero;
        var chatWindows = new List<IntPtr>();
        bool uiHost = false;
        foreach (var hwnd in Native.TopLevelWindows(pid))
        {
            if (!WeChatUiProfile.IsCandidateTopLevelClass(Native.GetClassName(hwnd))) continue;
            uiHost = true;
            var cls = _reader!.GetClassName(hwnd);
            if (cls is not null && WeChatUiProfile.MainWindowUiaClassNames.Contains(cls)) main = hwnd;
            else if (cls == WeChatUiProfile.ChatSingleWindowUiaClass) chatWindows.Add(hwnd);
        }
        if (!uiHost) return false;

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
            process.EnableRaisingEvents = true; // kernel wait on the process handle, no polling
            process.Exited += (_, _) => Post(new ProcessExitedItem(pid, Now));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }

        Detach("switching process");
        _wechat = new AttachedWeChat(info, process) { MainHwnd = main };
        _lastImagePath = info.ImagePath;
        _hooks!.AttachProcess(pid);
        _tracker.Reset();
        _scheduler.Reset();
        _dedup.Clear();
        foreach (var hwnd in chatWindows) _chatWindows[hwnd] = _reader!.ReadChatWindow(hwnd).Tail.Newest?.Text;
        Metrics.Attached();
        _log.Info("attach", $"attached pid={pid} version={info.Version} main={(main == IntPtr.Zero ? "none" : $"0x{main:X}")} reason={reason}");
        CaptureIfVisible("attach");
        SweepAvatars();
        PublishStatus();
        return true;
    }

    private void Detach(string reason)
    {
        SettlePendingPicture(null); // its retries are dropped with the other deferred work below
        if (_wechat is null) return;
        _hooks?.AttachProcess(0);
        _wechat.Process.Dispose();
        _wechat = null;
        _reader?.Invalidate();
        _tracker.Reset();
        _scheduler.Reset();
        _chatWindows.Clear();
        _burstChatWindows.Clear();
        _dedup.Clear();
        _pendingOpen = null;
        _deferred.Clear();
        _sweepPending = false;
        _avatars.Clear();
        _groups.Clear();
        Metrics.Detached();
        _log.Info("attach", $"detached: {reason}");
        PublishStatus();
    }

    private void OnResume()
    {
        // Never trust UIA elements from before a sleep (spec §22): drop them and look again.
        _log.Info("listener", "resumed from sleep");
        _avatarsQuietUntilMs = Now + AvatarQuietAfterWakeMs;
        _reader?.Invalidate();
        _chatWindows.Clear();
        if (_wechat is null)
        {
            AttachAtStartup();
            return;
        }
        if (_wechat.Process.HasExited)
        {
            Detach("WeChat exited during sleep");
            AttachAtStartup();
            return;
        }
        _wechat.MainHwnd = IntPtr.Zero;
        CaptureIfVisible("resume");
        PublishStatus();
    }

    // ------------------------------------------------------------------ notification clicks (spec §17)

    /// <summary>
    /// Order of spec §17: WeChat's protocol has no documented "open chat" path, so (2) bring the window to the
    /// front, then (3) open the conversation through UIA only when that can be verified, else (4) leave WeChat
    /// just shown.
    ///
    /// Weixin.exe is started only when no WeChat runs at all: WeChat 4 supports several instances, so starting it
    /// while it runs opens a second WeChat with a login screen (seen in the PHASE 2 test).
    /// </summary>
    private void OnOpenRequest(OpenRequestItem request)
    {
        Busy();
        Metrics.OpenRequested();
        _pendingOpen = null;
        if (_wechat is null)
        {
            AttachAtStartup(); // make sure it really is not running (e.g. started in the tray, never flashed yet)
            if (_wechat is null)
            {
                // WeChat exited after the notification: start it the way its shortcut would — but only when no
                // WeChat process exists at all (one that is still starting must not get a second login).
                if (WeChatProcessLocator.FindCandidates().Count > 0) _log.Info("open", "WeChat is running but not ready yet; not starting another one");
                else if (_lastImagePath is { } path) LaunchWeChat(path);
                else _log.Info("open", "WeChat is not running and its location is unknown");
                return;
            }
        }

        var main = EnsureMainWindow();
        var state = WindowState();
        bool inFront = false;
        if (state is WeChatWindowState.Visible or WeChatWindowState.Minimized)
        {
            inFront = BringToFront(main);
        }
        else
        {
            // In the tray: click WeChat's tray icon like the user would; WeChat then shows its own window
            // (an outside ShowWindow would desynchronise Qt's idea of the window's visibility).
            Native.AllowSetForegroundWindow(_wechat.Info.Pid);
            ClickTrayIcon();
        }
        _log.Info("open", $"request: window={state} in-front={inFront} conversation={(request.ConversationKey is null ? "none" : "given")}");

        if (request.ConversationKey is null || !_options.OpenConversationOnClick) return;
        _pendingOpen = new PendingOpen(request.ConversationKey, Now + OpenTimeoutMs, OpenStep.Click);
        // A window that was minimized lays itself out again first.
        Defer(inFront && state == WeChatWindowState.Visible ? 0 : OpenRetryMs, TryOpenConversation);
    }

    private static bool BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (Native.IsIconic(hwnd)) Native.ShowWindowAsync(hwnd, Native.SW_RESTORE);
        return Native.SetForegroundWindow(hwnd);
    }

    private void LaunchWeChat(string path)
    {
        // Pass on the right to take the foreground that Windows gave us for the click.
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
            Metrics.WeChatLaunched();
            _log.Info("open", "WeChat was not running: started it");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _log.Warn("open", $"could not start WeChat: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
    }

    /// <summary>
    /// Clicks WeChat's notification-area icon through the shell's UI Automation tree: on the taskbar if it is
    /// pinned there, otherwise inside the "Show hidden icons" flyout (opened for this and closed again).
    /// </summary>
    private void ClickTrayIcon()
    {
        var icon = _tray!.FindWeChatIcon(inOverflow: false, out bool ambiguous);
        if (icon is not null || ambiguous)
        {
            InvokeTrayIcon(icon, ambiguous, "taskbar");
            return;
        }
        if (_tray.FindOverflowButton() is not { } chevron || !ShellTray.Invoke(chevron))
        {
            _log.Info("open", "WeChat's tray icon not found: no taskbar icon and no hidden-icons button");
            return;
        }
        _trayDeadlineMs = Now + TrayFlyoutTimeoutMs;
        Defer(TrayStepMs, ClickTrayIconInFlyout);
    }

    private void ClickTrayIconInFlyout()
    {
        if (!_tray!.OverflowOpen)
        {
            if (Now < _trayDeadlineMs) Defer(TrayStepMs, ClickTrayIconInFlyout);
            else _log.Info("open", "the hidden-icons flyout did not open");
            return;
        }
        var icon = _tray.FindWeChatIcon(inOverflow: true, out bool ambiguous);
        InvokeTrayIcon(icon, ambiguous, "hidden icons");
        Defer(TrayFlyoutCloseCheckMs, CloseTrayFlyout);
    }

    private void InvokeTrayIcon(IUIAutomationElement? icon, bool ambiguous, string where)
    {
        if (icon is null)
        {
            // Two "微信" icons mean two WeChat instances: clicking could open the other account's window.
            _log.Info("open", ambiguous ? $"several WeChat tray icons ({where}): not clicking" : $"WeChat's tray icon not found ({where})");
            return;
        }
        if (ShellTray.Invoke(icon))
        {
            Metrics.TrayIconClicked();
            _log.Info("open", $"clicked WeChat's tray icon ({where})");
        }
        else
        {
            _log.Info("open", $"WeChat's tray icon ({where}) cannot be invoked");
        }
    }

    /// <summary>Clicking an icon normally closes the flyout; if it stayed open, toggle it closed.</summary>
    private void CloseTrayFlyout()
    {
        if (_tray!.OverflowOpen && _tray.FindOverflowButton() is { } chevron) ShellTray.Invoke(chevron);
    }

    /// <summary>Retries (every 200 ms, at most 5 s after the click) until WeChat's conversation list is on screen.</summary>
    private void TryOpenConversation()
    {
        if (_pendingOpen is not { } p) return;
        if (_wechat is null || Now > p.DeadlineMs)
        {
            FinishOpen(false, _wechat is null ? "WeChat went away" : "the conversation list did not appear in time");
            return;
        }
        var main = EnsureMainWindow();
        if (main == IntPtr.Zero || !Native.IsWindowVisible(main) || Native.IsIconic(main))
        {
            WaitToOpen(p, "WeChat's window is not shown yet");
            return;
        }

        var result = p.Step == OpenStep.Click
            ? ClickSession(main, p.Key)
            : _reader!.OpenSession(main, p.Key, useSelect: p.Step == OpenStep.Select);
        switch (result)
        {
            case OpenResult.Requested:
                Defer(OpenVerifyMs, VerifyOpen);
                break;
            case OpenResult.AlreadyOpen:
                FinishOpen(true, "already open");
                break;
            case OpenResult.NotReadable:
                WaitToOpen(p, "the conversation list cannot be read yet");
                break;
            case OpenResult.NotSupported or OpenResult.Failed when p.Step != OpenStep.Select:
                _pendingOpen = p with { Step = p.Step + 1 };
                Defer(0, TryOpenConversation);
                break;
            default:
                // NotFound (scrolled away), Ambiguous (two chats with this name), no working action.
                FinishOpen(false, result.ToString());
                break;
        }
    }

    /// <summary>
    /// Opens the conversation the way the user would: one left click on its row in WeChat's conversation list (WeChat
    /// 4.1 ignores UIA Invoke and Select on the rows). Only right after the user clicked our notification, only while
    /// WeChat is the foreground window, and only when the row is visible and nothing covers that point; the cursor then
    /// goes back where it was. <see cref="VerifyOpen"/> checks the result through the chat title.
    /// </summary>
    /// <returns><see cref="OpenResult.Failed"/> when it could not click safely: the UIA actions are tried next.</returns>
    private OpenResult ClickSession(IntPtr main, string key)
    {
        var found = _reader!.FindSessionCell(main, key, out var cell, out var list);
        if (found != OpenResult.Requested) return found;

        var target = WeChatUiProfile.SessionClickPoint(cell, list);
        var point = new Native.POINT { X = target?.X ?? 0, Y = target?.Y ?? 0 };
        // Shown from the tray, WeChat activates itself; if it did not, it may still be brought forward (the click on our
        // notification allows that for a moment).
        if (Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOT) != main) Native.SetForegroundWindow(main);
        string? refused = Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOT) != main ? "WeChat is not the foreground window"
            : target is null ? "the conversation's row is only partly visible"
            : Native.GetAncestor(Native.WindowFromPoint(point), Native.GA_ROOT) != main ? "another window covers the row"
            : null;
        if (refused is not null)
        {
            _log.Info("open", $"not clicking: {refused}");
            return OpenResult.Failed;
        }

        if (!Native.GetCursorPos(out var before) || !Native.SetCursorPos(point.X, point.Y) || !Native.LeftClick())
        {
            _log.Info("open", "the click could not be sent");
            return OpenResult.Failed;
        }
        Defer(ClickRestoreMs, () =>
        {
            // Put the cursor back — unless the user has moved it meanwhile.
            if (Native.GetCursorPos(out var now) && now.X == point.X && now.Y == point.Y) Native.SetCursorPos(before.X, before.Y);
        });
        _log.Info("open", $"clicked the conversation's row after {Now - (_pendingOpen!.DeadlineMs - OpenTimeoutMs)} ms");
        return OpenResult.Requested;
    }

    private void VerifyOpen()
    {
        if (_pendingOpen is not { } p) return;
        CaptureIfVisible("open"); // refreshes the open conversation's title and the baseline
        if (_reader!.OpenConversation is { } open && ConversationKey.Of(open) == p.Key)
        {
            FinishOpen(true, p.Step switch { OpenStep.Click => "click", OpenStep.Invoke => "Invoke", _ => "SelectionItem.Select" });
        }
        else if (p.Step != OpenStep.Select && Now < p.DeadlineMs)
        {
            _log.Info("open", $"{p.Step} did not switch the conversation; trying {p.Step + 1}");
            _pendingOpen = p with { Step = p.Step + 1 };
            Defer(0, TryOpenConversation);
        }
        else
        {
            FinishOpen(false, "WeChat did not switch to the conversation");
        }
    }

    private void FinishOpen(bool opened, string how)
    {
        long elapsed = _pendingOpen is { } p ? Now - (p.DeadlineMs - OpenTimeoutMs) : 0;
        _pendingOpen = null;
        Metrics.ConversationOpened(opened);
        _log.Info("open", (opened ? $"conversation opened ({how})" : $"conversation not opened: {how}") + $" after {elapsed} ms");
    }

    /// <summary>Retries the open step soon; says once per click why it is waiting.</summary>
    private void WaitToOpen(PendingOpen p, string why)
    {
        if (!p.WaitLogged)
        {
            _log.Info("open", $"waiting: {why}");
            _pendingOpen = p with { WaitLogged = true };
        }
        Defer(OpenRetryMs, TryOpenConversation);
    }

    /// <summary>Notes WeChat activity (reads, copies, opening a chat); the memory is settled once it has stopped.</summary>
    private void Busy()
    {
        _lastActivityMs = Now;
        if (_settlePending) return;
        _settlePending = true;
        Defer(SettleAfterMs, Settle);
    }

    private void Settle()
    {
        long quiet = Now - _lastActivityMs;
        if (quiet < SettleAfterMs)
        {
            Defer((int)(SettleAfterMs - quiet), Settle);
            return;
        }
        _settlePending = false;
        var sw = Stopwatch.StartNew();
        // Collect, let the finalizer release the COM / WinRT wrappers (freeing their native memory), collect what they
        // left behind; Aggressive also hands the GC's own free memory back to Windows.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        _log.Debug("memory", $"settled after activity in {sw.ElapsedMilliseconds} ms; GC heap {GC.GetGCMemoryInfo().HeapSizeBytes / 1024} KB");
    }

    private void Defer(int delayMs, Action action) => _deferred.Add((Now + delayMs, action));

    private void RunDeferred()
    {
        if (_deferred.Count == 0) return;
        long now = Now;
        var due = _deferred.Where(d => d.DueMs <= now).ToList();
        if (due.Count == 0) return;
        _deferred.RemoveAll(d => d.DueMs <= now);
        foreach (var (_, action) in due) Guard("deferred", action);
    }

    /// <summary>Ways to open a conversation, tried in this order.</summary>
    private enum OpenStep { Click, Invoke, Select }

    private sealed record PendingOpen(string Key, long DeadlineMs, OpenStep Step, bool WaitLogged = false);

    // ------------------------------------------------------------------ status

    private WeChatWindowState WindowState()
    {
        if (_wechat is not { MainHwnd: var h } || h == IntPtr.Zero || !Native.IsWindow(h)) return WeChatWindowState.NotFound;
        if (!Native.IsWindowVisible(h)) return WeChatWindowState.Hidden;
        return Native.IsIconic(h) ? WeChatWindowState.Minimized : WeChatWindowState.Visible;
    }

    private void PublishStatus()
    {
        if (_stopping) return;
        SetStatus(new ListenerStatus(
            _wechat is null ? ListenerState.WaitingForWeChat : ListenerState.Attached,
            _wechat?.Info.Pid,
            _wechat?.Info.Version,
            WindowState(),
            _reader?.HasReferences ?? false,
            _reader?.HeldCells ?? 0,
            _reader?.CapturedAtUtc,
            _hooks?.ShellHookActive ?? false,
            _tracker.Count,
            _chatWindows.Count));
    }

    private void SetStatus(ListenerStatus status)
    {
        var previous = Interlocked.Exchange(ref _status, status);
        if (previous == status) return;
        try { StatusChanged?.Invoke(status); }
        catch (Exception ex) { _log.Error("status", $"handler threw {ex.GetType().Name}"); }
    }

    private sealed class AttachedWeChat(WeChatProcessInfo info, Process process)
    {
        public WeChatProcessInfo Info { get; } = info;

        public Process Process { get; } = process;

        public IntPtr MainHwnd { get; set; }
    }
}
