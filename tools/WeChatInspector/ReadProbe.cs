using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WeChatInspector;

/// <summary>
/// Answers one question: after a new-message trigger, what can be *read* from WeChat, in each window state
/// (visible / minimized / hidden in the tray)?
///
/// Triggers are event-driven (taskbar flash via the shell hook, UIA RangeValue changes of the message-list
/// scrollbar, main window shown). References to the session cells and message cells are captured while the
/// main window is visible; on every trigger the probe re-reads
///   a) the held references (direct property reads, no navigation),
///   b) the session list via navigation (subject to Qt's "skip invisible children" rule),
///   c) SelectionPattern.GetCurrentSelection of both lists,
/// and reports what worked, plus the trigger -> read latency.
/// </summary>
internal sealed class ReadProbe
{
    private const string SessionListId = "session_list";
    private const string MessageListId = "chat_message_list";

    private readonly Uia _uia;
    private readonly Redactor _r;
    private readonly Sink _sink;
    private readonly int _pid;
    private readonly BlockingCollection<(string Reason, long Ts)> _triggers = new();
    private readonly long _t0 = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, string> _lastNames = new(StringComparer.Ordinal);

    private IntPtr _mainHwnd;
    private IUIAutomationElement? _sessionList;
    private IUIAutomationElement? _messageList;
    private List<IUIAutomationElement> _sessionCells = [];
    private List<IUIAutomationElement> _messageCells = [];
    private long _lastFlashTs;

    private uint _win32Tid;
    private Native.WndProc? _wndProc;
    private Native.WinEventProc? _winEventProc;
    private IntPtr _minimizeHook;
    private uint _shellMsg;
    private ProbeSink? _uiaSink;

    public ReadProbe(Uia uia, Redactor r, Sink sink, int pid, IntPtr mainHwnd)
    {
        _uia = uia;
        _r = r;
        _sink = sink;
        _pid = pid;
        _mainHwnd = mainHwnd;
    }

    public void Run(TimeSpan duration, CancellationToken ct)
    {
        var win32 = new Thread(Win32Loop) { IsBackground = true, Name = "shell-hook" };
        var ready = new ManualResetEventSlim();
        win32.Start(ready);
        ready.Wait();

        _uiaSink = new ProbeSink(this);
        var a = _uia.Automation;
        a.AddAutomationEventHandler(UiaIds.EvWindowOpened, a.GetRootElement(), TreeScope.TreeScope_Children, _uia.EventCache, _uiaSink);
        SubscribeMain();

        Enqueue(("start", Stopwatch.GetTimestamp()));
        var worker = new Thread(() =>
        {
            foreach (var (reason, ts) in _triggers.GetConsumingEnumerable())
            {
                try { Handle(reason, ts); }
                catch (Exception ex) { Log($"! {reason}: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}"); }
                _sink.Flush();
            }
        }) { IsBackground = true, Name = "reader" };
        worker.Start();

        try { Task.Delay(duration, ct).Wait(ct); } catch (OperationCanceledException) { } catch (AggregateException) { }

        try { a.RemoveAllEventHandlers(); } catch (COMException) { }
        Native.PostThreadMessageW(_win32Tid, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _triggers.CompleteAdding();
        worker.Join(5000);
        _sink.Flush();
    }

    private void SubscribeMain()
    {
        try
        {
            var root = _uia.Automation.ElementFromHandleBuildCache(_mainHwnd, _uia.EventCache);
            _uia.Automation.AddPropertyChangedEventHandler(root, TreeScope.TreeScope_Subtree, _uia.EventCache, _uiaSink,
                [UiaIds.RangeValueValue]);
            Log($"# subscribed RangeValue changes under main window 0x{_mainHwnd:X}");
        }
        catch (COMException ex) { Log($"# subscribe failed 0x{ex.HResult:X8}"); }
    }

    internal void OnUia(string reason, IUIAutomationElement sender)
    {
        long ts = Stopwatch.GetTimestamp();
        ElementInfo info;
        try { info = _uia.Snapshot(sender, false); } catch { return; }
        if (info.ProcessId != _pid) return;
        if (reason == "WindowOpened" && info.ClassName is { } cls && WeChatUiProfile.MainWindowUiaClassNames.Contains(cls))
        {
            Enqueue(($"main-window-opened 0x{info.NativeWindowHandle:X}", ts));
            return;
        }
        if (reason == "RangeValue" && info.ControlType == "ScrollBar")
            Enqueue(($"uia-scrollbar({info.ClassName})", ts));
    }

    private void OnShell(int code, IntPtr hwnd)
    {
        long ts = Stopwatch.GetTimestamp();
        if (Native.GetPid(hwnd) != _pid) return;
        if (code == 0x8006) // HSHELL_FLASH: repeats every ~310 ms while flashing; one burst = one trigger
        {
            bool newBurst = (ts - Interlocked.Exchange(ref _lastFlashTs, ts)) > Stopwatch.Frequency * 2;
            if (newBurst) Enqueue(("shell-flash", ts));
        }
    }

    private void Handle(string reason, long ts)
    {
        double trigAt = Sec(ts);
        bool visible = Native.IsWindowVisible(_mainHwnd);
        bool iconic = Native.IsIconic(_mainHwnd);
        Log("");
        Log($"[{trigAt,8:0.000}s] TRIGGER {reason}  main 0x{_mainHwnd:X} visible={visible} minimized={iconic} alive={Native.IsWindow(_mainHwnd)}");

        if (reason.StartsWith("main-window-opened", StringComparison.Ordinal) || reason == "start" || _sessionList is null)
        {
            // The main window HWND can change when WeChat restores it from the tray; re-resolve it.
            var match = WeChatLocator.FindMainWindow(_uia, new HashSet<int> { _pid });
            if (match is not null && match.Window.Hwnd != _mainHwnd)
            {
                Log($"  main window changed 0x{_mainHwnd:X} -> 0x{match.Window.Hwnd:X}");
                _mainHwnd = match.Window.Hwnd;
                SubscribeMain();
            }
            if (Native.IsWindowVisible(_mainHwnd)) Capture();
            else Log("  main window hidden: cannot capture references now (navigation filters invisible widgets)");
        }

        if (reason.StartsWith("uia-scrollbar", StringComparison.Ordinal) || reason == "shell-flash" || reason == "start"
            || reason.StartsWith("win-", StringComparison.Ordinal))
        {
            ReadAll(ts, verbose: true);
            // Re-read a few times after the trigger: tells whether the data arrives after the trigger (a race)
            // or never (e.g. WeChat not updating cells while minimized / hidden).
            foreach (var delay in new[] { 250, 1000, 3000 })
                _ = Task.Delay(delay).ContinueWith(_ => Enqueue(($"reread+{delay}ms", ts)));
        }
        else if (reason.StartsWith("reread", StringComparison.Ordinal))
        {
            ReadAll(ts, verbose: false);
        }
    }

    private void Capture()
    {
        var sw = Stopwatch.StartNew();
        var a = _uia.Automation;
        var root = a.ElementFromHandleBuildCache(_mainHwnd, _uia.EventCache);
        _sessionList = root.FindFirstBuildCache(TreeScope.TreeScope_Descendants,
            a.CreatePropertyCondition(UiaIds.AutomationId, SessionListId), _uia.EventCache);
        _messageList = root.FindFirstBuildCache(TreeScope.TreeScope_Descendants,
            a.CreatePropertyCondition(UiaIds.AutomationId, MessageListId), _uia.EventCache);
        _sessionCells = Children(_sessionList);
        _messageCells = Children(_messageList);
        Log($"  captured references in {sw.ElapsedMilliseconds} ms: session_list={(_sessionList is null ? "MISSING" : "ok")} cells={_sessionCells.Count}; " +
            $"chat_message_list={(_messageList is null ? "MISSING" : "ok")} cells={_messageCells.Count}");
    }

    private List<IUIAutomationElement> Children(IUIAutomationElement? parent)
    {
        var list = new List<IUIAutomationElement>();
        if (parent is null) return list;
        var arr = parent.FindAllBuildCache(TreeScope.TreeScope_Children, _uia.Automation.CreateTrueCondition(), _uia.EventCache);
        for (int i = 0; i < (arr?.Length ?? 0); i++) list.Add(arr!.GetElement(i));
        return list;
    }

    private void ReadAll(long triggerTs, bool verbose)
    {
        if (_sessionList is null)
        {
            Log("  no references captured yet");
            return;
        }
        var sw = Stopwatch.StartNew();

        // a) held references: direct property reads (no navigation); only changes are printed
        int alive = 0, changed = 0;
        for (int i = 0; i < _sessionCells.Count; i++)
        {
            var (ok, text, rid) = ReadName(_sessionCells[i]);
            if (!ok) { Log($"  held session[{i}] GONE ({text})"); continue; }
            alive++;
            bool isChanged = _lastNames.TryGetValue("s" + rid, out var prev) && prev != text;
            if (isChanged) changed++;
            _lastNames["s" + rid] = text;
            if (isChanged || (verbose && i < 2)) Log($"  held session[{i}]{(isChanged ? " CHANGED" : "")}: \"{_r.Apply(text)}\"");
        }
        long heldMs = sw.ElapsedMilliseconds;

        for (int i = 0; i < _messageCells.Count; i++)
        {
            var (ok, text, rid) = ReadName(_messageCells[i]);
            if (!ok) { if (verbose) Log($"  held message[{i}] GONE ({text})"); continue; }
            bool isChanged = _lastNames.TryGetValue("m" + rid, out var prev) && prev != text;
            _lastNames["m" + rid] = text;
            if (isChanged) Log($"  held message[{i}] CHANGED: \"{_r.Apply(text)}\"");
        }
        if (!verbose)
        {
            Log($"  ({changed} session changes, visible={Native.IsWindowVisible(_mainHwnd)} minimized={Native.IsIconic(_mainHwnd)}, " +
                $"{Sec(Stopwatch.GetTimestamp()) - Sec(triggerTs):0.000}s after trigger)");
            return;
        }

        // b) navigation
        int navSessions = CountNav(_sessionList), navMessages = CountNav(_messageList);
        string newestNav = "";
        if (navSessions > 0 && _uia.Automation.RawViewWalker.GetFirstChildElement(_sessionList) is { } first)
            newestNav = _r.Apply(ReadName(first).Text) ?? "";

        // c) selection pattern
        string sel = Selection(_sessionList), msgSel = Selection(_messageList);

        Log($"  held refs: {alive}/{_sessionCells.Count} alive, {changed} changed, read in {heldMs} ms | " +
            $"navigation children: sessions={navSessions} messages={navMessages} | " +
            $"total {sw.ElapsedMilliseconds} ms, trigger->done {Sec(Stopwatch.GetTimestamp()) - Sec(triggerTs):0.000}s");
        if (navSessions > 0) Log($"  nav first session: \"{newestNav}\"");
        Log($"  selection: sessions={sel} messages={msgSel}");
    }

    private (bool Ok, string Text, string Rid) ReadName(IUIAutomationElement el)
    {
        try
        {
            var rid = string.Join(".", ((int[])el.GetRuntimeId()).Select(x => x.ToString("X")));
            return (true, el.CurrentName ?? "", rid);
        }
        catch (COMException ex) { return (false, $"0x{ex.HResult:X8}", ""); }
    }

    private int CountNav(IUIAutomationElement? parent)
    {
        if (parent is null) return -1;
        try
        {
            int n = 0;
            var walker = _uia.Automation.RawViewWalker;
            for (var c = walker.GetFirstChildElement(parent); c is not null && n < 500; c = walker.GetNextSiblingElement(c)) n++;
            return n;
        }
        catch (COMException) { return -2; }
    }

    private string Selection(IUIAutomationElement? list)
    {
        if (list is null) return "-";
        try
        {
            var pattern = (IUIAutomationSelectionPattern)list.GetCurrentPattern(10001);
            var arr = pattern.GetCurrentSelection();
            var names = new List<string>();
            for (int i = 0; i < (arr?.Length ?? 0); i++) names.Add("\"" + _r.Apply(arr!.GetElement(i).CurrentName) + "\"");
            return names.Count == 0 ? "(none)" : string.Join(", ", names);
        }
        catch (Exception ex) { return $"failed {ex.GetType().Name} 0x{ex.HResult:X8}"; }
    }

    private void Win32Loop(object? ready)
    {
        _win32Tid = Native.GetCurrentThreadId();
        _wndProc = (h, msg, w, l) =>
        {
            if (_shellMsg != 0 && msg == _shellMsg) { OnShell((int)w.ToInt64(), l); return IntPtr.Zero; }
            return Native.DefWindowProcW(h, msg, w, l);
        };
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = Native.GetModuleHandleW(null),
            lpszClassName = "WeChatInspector.ReadProbe",
        };
        Native.RegisterClassExW(ref wc);
        var hwnd = Native.CreateWindowExW(0, wc.lpszClassName, wc.lpszClassName, Native.WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _shellMsg = Native.RegisterWindowMessageW("SHELLHOOK");
        Log($"# shell hook: {(Native.RegisterShellHookWindow(hwnd) ? "ok" : "FAILED")}");

        // Minimize / restore of the WeChat main window (0x16 = MINIMIZESTART, 0x17 = MINIMIZEEND).
        _winEventProc = (_, ev, h, idObject, _, _, _) =>
        {
            if (idObject == 0 && h == _mainHwnd)
                Enqueue((ev == 0x17 ? "win-restored" : "win-minimized", Stopwatch.GetTimestamp()));
        };
        _minimizeHook = Native.SetWinEventHook(0x16, 0x17, IntPtr.Zero, _winEventProc, (uint)_pid, 0, Native.WINEVENT_OUTOFCONTEXT);
        Log($"# minimize/restore hook: {(_minimizeHook != IntPtr.Zero ? "ok" : "FAILED")}");
        ((ManualResetEventSlim)ready!).Set();
        while (Native.GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref m);
            Native.DispatchMessageW(ref m);
        }
        if (_minimizeHook != IntPtr.Zero) Native.UnhookWinEvent(_minimizeHook);
        Native.DeregisterShellHookWindow(hwnd);
        Native.DestroyWindow(hwnd);
    }

    /// <summary>Producers run on UIA, hook and timer threads; any of them may fire after shutdown began.</summary>
    private void Enqueue((string Reason, long Ts) trigger)
    {
        try { _triggers.TryAdd(trigger); }
        catch (InvalidOperationException) { } // adding already completed
    }

    private double Sec(long ts) => (ts - _t0) / (double)Stopwatch.Frequency;

    private void Log(string s) => _sink.Always(s);
}

[ComVisible(true)]
public sealed class ProbeSink : IUIAutomationEventHandler, IUIAutomationPropertyChangedEventHandler
{
    private readonly ReadProbe _probe;

    internal ProbeSink(ReadProbe probe) => _probe = probe;

    public void HandleAutomationEvent(IUIAutomationElement sender, int eventId) => _probe.OnUia("WindowOpened", sender);

    public void HandlePropertyChangedEvent(IUIAutomationElement sender, int propertyId, object newValue)
    {
        if (newValue is not null) _probe.OnUia("RangeValue", sender); // null values are Qt's "value invalidated" echoes
    }
}
