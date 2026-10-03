using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;

namespace WeChatInspector;

internal sealed class RecorderSettings
{
    public required int Pid { get; init; }
    public required IntPtr MainHwnd { get; init; }
    public bool Uia { get; init; } = true;
    public bool WinEvents { get; init; } = true;
    public bool ShellHook { get; init; } = true;
    public bool Foreground { get; init; } = true;
    public bool IncludeLocation { get; init; }
    public bool IncludeBounds { get; init; }
    public bool IncludeTextSelection { get; init; }
    public bool Ancestry { get; init; }
    public bool ResolveWinEvents { get; init; }
    public bool FollowWindows { get; init; } = true;
}

/// <summary>One observed event. Raw text stays in memory only; it is redacted when written.</summary>
internal sealed class Rec
{
    public long Ts;
    public string Src = "";
    public string Kind = "";
    public string? Detail;
    public string? RawText;             // text payload (property value / notification string) to be redacted on output
    public ElementInfo? El;
    public IUIAutomationElement? Element;
    public IntPtr Hwnd;
    public string? HwndClass;
    public int IdObject;
    public int IdChild;
    public uint EventId;
    public int DelayMs = -1;            // WinEvent: receive time - dwmsEventTime
    public int OtherPid;
}

/// <summary>
/// Records everything WeChat emits through the three event channels Windows offers an outside process:
///  1. UI Automation events (subtree of the WeChat window + global focus + new top-level windows)
///  2. WinEvents (SetWinEventHook, out-of-context, filtered to the WeChat process)
///  3. Shell hook messages (RegisterShellHookWindow: taskbar flash, window create/destroy/activate)
/// All timestamps come from one Stopwatch so the channels can be compared. No polling is involved.
/// </summary>
internal sealed class EventRecorder
{
    private readonly Uia _uia;
    private readonly Redactor _r;
    private readonly Sink _sink;
    private readonly RecorderSettings _s;
    private readonly BlockingCollection<Rec> _queue = new(new ConcurrentQueue<Rec>(), 200_000);
    private readonly long _t0 = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _senderCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _ancestryCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _followed = new(StringComparer.Ordinal);
    private readonly List<string> _registrations = new();
    private readonly ManualResetEventSlim _win32Ready = new();
    private long _dropped, _filteredFocus, _filteredWindows, _filteredLocation, _filteredShell;
    private uint _win32Tid;
    private IntPtr _pidHook, _fgHook, _shellWnd;
    private uint _shellMsg;
    private Native.WinEventProc? _winEventProc;
    private Native.WndProc? _wndProc;
    private UiaEventSink? _handler;

    public EventRecorder(Uia uia, Redactor r, Sink sink, RecorderSettings s)
    {
        _uia = uia;
        _r = r;
        _sink = sink;
        _s = s;
    }

    public void Run(TimeSpan? duration, CancellationToken ct)
    {
        var self = Process.GetCurrentProcess();
        var selfCpu0 = self.TotalProcessorTime;
        var wxCpu0 = Native.GetProcessCpu(_s.Pid);
        var wall = Stopwatch.StartNew();

        var writer = new Thread(WriterLoop) { IsBackground = true, Name = "writer" };
        writer.Start();

        Thread? win32 = null;
        if (_s.WinEvents || _s.ShellHook || _s.Foreground)
        {
            win32 = new Thread(Win32Loop) { IsBackground = true, Name = "win32-hooks" };
            win32.Start();
            _win32Ready.Wait();
        }

        if (_s.Uia) SubscribeUia();

        foreach (var line in _registrations) _sink.Always(line);
        _sink.Always($"# recording{(duration is { } d ? $" for {d.TotalSeconds:0}s" : " until Ctrl+C")} ...");

        try
        {
            if (duration is { } dd) Task.Delay(dd, ct).Wait(ct);
            else ct.WaitHandle.WaitOne();
        }
        catch (OperationCanceledException) { }
        catch (AggregateException) { }

        // Teardown: never from a UIA callback thread.
        if (_s.Uia)
        {
            try { _uia.Automation.RemoveAllEventHandlers(); } catch (COMException) { }
        }
        if (win32 is not null)
        {
            Native.PostThreadMessageW(_win32Tid, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            win32.Join(3000);
        }
        _queue.CompleteAdding();
        writer.Join(10000);

        self.Refresh();
        var selfCpu = self.TotalProcessorTime - selfCpu0;
        var wxCpu1 = Native.GetProcessCpu(_s.Pid);
        PrintSummary(wall.Elapsed, selfCpu, wxCpu0 is { } a && wxCpu1 is { } b ? b - a : null, self);
    }

    // ------------------------------------------------------------------ UIA

    private void SubscribeUia()
    {
        var a = _uia.Automation;
        _handler = new UiaEventSink(this);
        IUIAutomationElement root;
        try { root = a.ElementFromHandleBuildCache(_s.MainHwnd, _uia.EventCache); }
        catch (COMException ex)
        {
            _registrations.Add($"# UIA: ElementFromHandle(0x{_s.MainHwnd:X}) failed 0x{ex.HResult:X8}");
            return;
        }

        lock (_followed) _followed.Add(_uia.Snapshot(root, false).RuntimeIdText);
        SubscribeSubtree(root, "main");

        TryRegister("global:FocusChanged", () => a.AddFocusChangedEventHandler(_uia.EventCache, _handler));
        TryRegister("desktop:Window_WindowOpened(children)", () =>
            a.AddAutomationEventHandler(UiaIds.EvWindowOpened, a.GetRootElement(), TreeScope.TreeScope_Children, _uia.EventCache, _handler));
    }

    private void SubscribeSubtree(IUIAutomationElement root, string label)
    {
        var a = _uia.Automation;
        var cache = _uia.EventCache;
        var scope = TreeScope.TreeScope_Subtree;

        // WindowOpened is registered once on the desktop (children scope); registering it per subtree duplicates it.
        var generic = UiaIds.GenericEvents.Where(e => e != UiaIds.EvWindowOpened).ToList();
        if (_s.IncludeTextSelection) generic.Add(UiaIds.EvTextSelectionChanged);
        int ok = 0, failed = 0;
        foreach (var ev in generic)
        {
            try { a.AddAutomationEventHandler(ev, root, scope, cache, _handler); ok++; }
            catch (COMException) { failed++; }
        }
        _registrations.Add($"# UIA {label}: {ok} generic automation events registered ({failed} failed): " +
                           string.Join(",", generic.Select(UiaIds.EventName)));

        TryRegister($"{label}:StructureChanged", () => a.AddStructureChangedEventHandler(root, scope, cache, _handler));
        int[] props = _s.IncludeBounds ? [.. UiaIds.WatchedProperties, UiaIds.BoundingRectangle] : UiaIds.WatchedProperties;
        TryRegister($"{label}:PropertyChanged({string.Join(",", props.Select(UiaIds.PropertyName))})",
            () => a.AddPropertyChangedEventHandler(root, scope, cache, _handler, props));
        TryRegister($"{label}:Notification", () => a.AddNotificationEventHandler(root, scope, cache, _handler));
    }

    private void TryRegister(string what, Action register)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            register();
            _registrations.Add($"# UIA {what}: registered ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            _registrations.Add($"# UIA {what}: FAILED {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
    }

    internal void OnUia(string kind, string? detail, IUIAutomationElement sender, string? rawText = null)
    {
        long ts = Stopwatch.GetTimestamp();
        ElementInfo? info = null;
        try { info = _uia.Snapshot(sender, full: false); } catch (Exception) { }

        if (kind == "FocusChanged" && info?.ProcessId != _s.Pid) { Interlocked.Increment(ref _filteredFocus); return; }
        if (kind == "Window_WindowOpened" && info?.ProcessId != _s.Pid) { Interlocked.Increment(ref _filteredWindows); return; }

        Post(new Rec
        {
            Ts = ts, Src = "UIA", Kind = kind, Detail = detail, RawText = rawText, El = info,
            Element = _s.Ancestry || (_s.FollowWindows && kind == "Window_WindowOpened") ? sender : null,
        });
    }

    // ------------------------------------------------------------------ WinEvents + shell hook

    private void Win32Loop()
    {
        _win32Tid = Native.GetCurrentThreadId();
        _winEventProc = OnWinEvent;
        const uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
        if (_s.WinEvents)
        {
            _pidHook = Native.SetWinEventHook(Native.EVENT_MIN, Native.EVENT_MAX, IntPtr.Zero, _winEventProc, (uint)_s.Pid, 0, flags);
            _registrations.Add($"# WinEvent hook (all events, pid {_s.Pid}): {(_pidHook != IntPtr.Zero ? "ok" : "FAILED")}");
        }
        if (_s.Foreground)
        {
            _fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, flags);
            _registrations.Add($"# WinEvent hook (global SYSTEM_FOREGROUND): {(_fgHook != IntPtr.Zero ? "ok" : "FAILED")}");
        }
        if (_s.ShellHook) CreateShellHookWindow();
        _win32Ready.Set();

        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }

        if (_pidHook != IntPtr.Zero) Native.UnhookWinEvent(_pidHook);
        if (_fgHook != IntPtr.Zero) Native.UnhookWinEvent(_fgHook);
        if (_shellWnd != IntPtr.Zero)
        {
            Native.DeregisterShellHookWindow(_shellWnd);
            Native.DestroyWindow(_shellWnd);
        }
    }

    private void CreateShellHookWindow()
    {
        _wndProc = ShellWndProc;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = Native.GetModuleHandleW(null),
            lpszClassName = "WeChatInspector.ShellHook",
        };
        Native.RegisterClassExW(ref wc);
        _shellWnd = Native.CreateWindowExW(0, wc.lpszClassName, "WeChatInspector.ShellHook", Native.WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _shellMsg = Native.RegisterWindowMessageW("SHELLHOOK");
        bool ok = _shellWnd != IntPtr.Zero && Native.RegisterShellHookWindow(_shellWnd);
        _registrations.Add($"# Shell hook window: {(ok ? "ok" : "FAILED")}");
    }

    private IntPtr ShellWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_shellMsg != 0 && msg == _shellMsg)
        {
            long ts = Stopwatch.GetTimestamp();
            int code = (int)wParam.ToInt64();
            string? name = WinEventNames.ShellCode(code);
            if (name is null) return IntPtr.Zero;
            if (Native.GetPid(lParam) != _s.Pid) { Interlocked.Increment(ref _filteredShell); return IntPtr.Zero; }
            Post(new Rec { Ts = ts, Src = "SHELL", Kind = name, Hwnd = lParam, HwndClass = Native.GetClassName(lParam), Detail = Native.GetWindowText(lParam) });
            return IntPtr.Zero;
        }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        long ts = Stopwatch.GetTimestamp();
        int delay = unchecked(Environment.TickCount - (int)time);
        if (hook == _fgHook)
        {
            int pid = Native.GetPid(hwnd);
            if (pid == _s.Pid) return; // already delivered by the per-process hook
            Post(new Rec { Ts = ts, Src = "SYS", Kind = "FOREGROUND->other", OtherPid = pid, DelayMs = delay });
            return;
        }
        if (ev == Native.EVENT_OBJECT_LOCATIONCHANGE && !_s.IncludeLocation) { Interlocked.Increment(ref _filteredLocation); return; }

        Post(new Rec
        {
            Ts = ts, Src = "WIN", Kind = WinEventNames.Name(ev), EventId = ev, Hwnd = hwnd,
            HwndClass = hwnd != IntPtr.Zero ? Native.GetClassName(hwnd) : null,
            IdObject = idObject, IdChild = idChild, DelayMs = delay,
        });
    }

    private void Post(Rec r)
    {
        if (!_queue.TryAdd(r)) Interlocked.Increment(ref _dropped);
    }

    // ------------------------------------------------------------------ output

    private void WriterLoop()
    {
        foreach (var r in _queue.GetConsumingEnumerable())
        {
            try { Write(r); }
            catch (Exception ex) { _sink.Line($"! writer error: {ex.GetType().Name}: {ex.Message}"); }
            if (_queue.Count == 0) _sink.Flush(); // burst finished: make the log readable while recording
        }
        _sink.Flush();
    }

    private void Write(Rec r)
    {
        double t = (r.Ts - _t0) / (double)Stopwatch.Frequency;
        string key = r.Src + " " + r.Kind;
        _counts[key] = _counts.TryGetValue(key, out var c) ? c + 1 : 1;

        string? resolved = null;
        if (r.Src == "WIN" && _s.ResolveWinEvents && ShouldResolve(r.EventId)) resolved = ResolveWinEvent(r);

        string? ancestry = null;
        if (r.Src == "UIA" && r.Element is not null && _s.Ancestry) ancestry = Ancestry(r.Element, r.El);

        if (r.Src == "UIA" && r.El is not null)
        {
            string sk = $"{r.Kind} <- {r.El.ControlType}({r.El.ClassName})";
            _senderCounts[sk] = _senderCounts.TryGetValue(sk, out var sc) ? sc + 1 : 1;
        }

        bool follow;
        lock (_followed)
            follow = r.Kind == "Window_WindowOpened" && _s.FollowWindows && r.Element is not null && r.El is not null
                     && _followed.Add(r.El.RuntimeIdText);
        if (follow)
        {
            SubscribeSubtree(r.Element!, $"window {r.El!.ClassName}");
            _sink.Line($"# following new WeChat window {r.El.ClassName} rid={r.El.RuntimeIdText}: {_registrations[^1]}");
        }

        var sb = new StringBuilder();
        sb.Append($"[{t,9:0.000}s] {r.Src,-5} {r.Kind}");
        if (r.Detail is not null) sb.Append("  ").Append(r.Src == "SHELL" ? "\"" + _r.Apply(r.Detail) + "\"" : r.Detail);
        if (r.RawText is not null) sb.Append(" \"").Append(_r.Apply(r.RawText)).Append('"');
        if (r.Src is "WIN" or "SHELL")
            sb.Append($"  hwnd=0x{r.Hwnd:X}({r.HwndClass})");
        if (r.Src == "WIN")
            sb.Append($" obj={WinEventNames.ObjectId(r.IdObject)} child={r.IdChild} delay={r.DelayMs}ms");
        if (r.Src == "SYS")
            sb.Append($"  pid={r.OtherPid} ({SafeProcessName(r.OtherPid)}) delay={r.DelayMs}ms");
        if (r.El is not null) sb.Append("  | ").Append(TreeDumper.Describe(r.El, _r));
        if (resolved is not null) sb.Append("  => ").Append(resolved);
        if (ancestry is not null) sb.Append("  @ ").Append(ancestry);
        _sink.Line(sb.ToString());

        _sink.Record(new
        {
            t = Math.Round(t, 4),
            src = r.Src,
            kind = r.Kind,
            detail = r.Src == "SHELL" ? _r.Apply(r.Detail) : r.Detail,
            text = _r.Apply(r.RawText),
            hwnd = r.Hwnd != IntPtr.Zero ? $"0x{r.Hwnd:X}" : null,
            hwndClass = r.HwndClass,
            idObject = r.Src == "WIN" ? r.IdObject : (int?)null,
            idChild = r.Src == "WIN" ? r.IdChild : (int?)null,
            delayMs = r.DelayMs >= 0 ? r.DelayMs : (int?)null,
            el = r.El is null ? null : new
            {
                r.El.ControlType,
                name = _r.Apply(r.El.Name),
                automationId = _r.Ids(r.El.AutomationId),
                r.El.ClassName,
                rect = r.El.Rect,
                rid = r.El.RuntimeIdText,
                r.El.IsOffscreen,
                itemStatus = _r.Apply(r.El.ItemStatus),
                helpText = _r.Apply(r.El.HelpText),
            },
            resolved,
            ancestry,
        });
    }

    private static bool ShouldResolve(uint ev) => ev is 0x8000 or 0x8002 or 0x8004 or 0x8006 or 0x8007 or 0x800A or 0x800C or 0x800E or 0x8019 or 0x0002;

    private string ResolveWinEvent(Rec r)
    {
        try
        {
            int hr = Native.AccessibleObjectFromEvent(r.Hwnd, r.IdObject, r.IdChild, out var acc, out var child);
            if (hr != 0 || acc is null) return $"AccessibleObjectFromEvent hr=0x{hr:X8}";
            int childId = child is int ci ? ci : 0;
            var el = _uia.Automation.ElementFromIAccessibleBuildCache(acc, childId, _uia.EventCache);
            return TreeDumper.Describe(_uia.Snapshot(el, full: false), _r);
        }
        catch (Exception ex)
        {
            return $"resolve failed {ex.GetType().Name} 0x{ex.HResult:X8}";
        }
    }

    private string Ancestry(IUIAutomationElement el, ElementInfo? info)
    {
        string cacheKey = info?.RuntimeIdText ?? "";
        if (cacheKey.Length > 0 && _ancestryCache.TryGetValue(cacheKey, out var cached)) return cached;

        var walker = _uia.Automation.RawViewWalker;
        var parts = new List<string>();
        IUIAutomationElement? cur = el;
        for (int i = 0; i < 25 && cur is not null; i++)
        {
            IUIAutomationElement? parent;
            try { parent = walker.GetParentElementBuildCache(cur, _uia.EventCache); }
            catch (COMException) { parts.Add("!"); break; }
            if (parent is null) break;
            var pi = _uia.Snapshot(parent, full: false);
            if (pi.ClassName == "#32769") break; // desktop root
            parts.Add(pi.ControlType + (pi.ClassName is { } cn ? $"({cn})" : "") + (pi.AutomationId is { } aid ? "#" + _r.Ids(aid) : ""));
            cur = parent;
        }
        parts.Reverse();
        var s = parts.Count == 0 ? "(no parent)" : string.Join(" > ", parts);
        if (cacheKey.Length > 0) _ancestryCache[cacheKey] = s;
        return s;
    }

    private static string SafeProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return "?"; }
    }

    private void PrintSummary(TimeSpan wall, TimeSpan selfCpu, TimeSpan? wxCpu, Process self)
    {
        int cores = Environment.ProcessorCount;
        _sink.Always("");
        _sink.Always($"# ===== summary: {wall.TotalSeconds:0.0}s =====");
        foreach (var kv in _counts.OrderByDescending(k => k.Value))
            _sink.Always($"#   {kv.Value,7}  {kv.Key}");
        if (_senderCounts.Count > 0)
        {
            _sink.Always("# UIA events by sender (ControlType(ClassName)):");
            foreach (var kv in _senderCounts.OrderByDescending(k => k.Value).Take(40))
                _sink.Always($"#   {kv.Value,7}  {kv.Key}");
        }
        _sink.Always($"# filtered: focus(other pid)={_filteredFocus} windowOpened(other pid)={_filteredWindows} " +
                     $"locationChange={_filteredLocation} shell(other pid)={_filteredShell} dropped={_dropped}");
        _sink.Always($"# inspector CPU: {selfCpu.TotalMilliseconds:0} ms = {100.0 * selfCpu.TotalMilliseconds / wall.TotalMilliseconds / cores:0.000}% of machine " +
                     $"({cores} logical cores), peak working set {self.PeakWorkingSet64 / 1048576.0:0.0} MB");
        if (wxCpu is { } w)
            _sink.Always($"# WeChat CPU during recording: {w.TotalMilliseconds:0} ms = {100.0 * w.TotalMilliseconds / wall.TotalMilliseconds / cores:0.000}% of machine");
        _sink.Flush();
    }
}

/// <summary>COM-visible UIA event sink; forwards everything to the recorder and returns immediately.</summary>
[ComVisible(true)]
public sealed class UiaEventSink :
    IUIAutomationEventHandler,
    IUIAutomationPropertyChangedEventHandler,
    IUIAutomationStructureChangedEventHandler,
    IUIAutomationNotificationEventHandler,
    IUIAutomationFocusChangedEventHandler
{
    private readonly EventRecorder _rec;

    internal UiaEventSink(EventRecorder rec) => _rec = rec;

    public void HandleAutomationEvent(IUIAutomationElement sender, int eventId) =>
        _rec.OnUia(UiaIds.EventName(eventId), null, sender);

    public void HandlePropertyChangedEvent(IUIAutomationElement sender, int propertyId, object newValue)
    {
        if (newValue is string s)
            _rec.OnUia("PropertyChanged", UiaIds.PropertyName(propertyId) + "=", sender, s);
        else
            _rec.OnUia("PropertyChanged", UiaIds.PropertyName(propertyId) + "=" + Uia.FormatValue(newValue, NoText), sender);
    }

    public void HandleStructureChangedEvent(IUIAutomationElement sender, StructureChangeType changeType, int[] runtimeId) =>
        _rec.OnUia("StructureChanged",
            (UiaIds.StructureChangeNames.TryGetValue((int)changeType, out var n) ? n : changeType.ToString()) +
            (runtimeId is { Length: > 0 } ? " child=[" + string.Join(",", runtimeId.Select(x => x.ToString("X"))) + "]" : ""),
            sender);

    public void HandleNotificationEvent(IUIAutomationElement sender, NotificationKind kind, NotificationProcessing processing, string displayString, string activityId) =>
        _rec.OnUia("Notification", $"{kind}/{processing} activity='{activityId}'", sender, displayString);

    public void HandleFocusChangedEvent(IUIAutomationElement sender) =>
        _rec.OnUia("FocusChanged", null, sender);

    private static readonly Redactor NoText = new(enabled: true, revealPattern: null);
}
