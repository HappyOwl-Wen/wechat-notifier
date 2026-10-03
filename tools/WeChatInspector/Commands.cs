using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WeChatInspector;

internal static class Commands
{
    // ------------------------------------------------------------------ processes

    public static int Processes(Options o)
    {
        var sw = Stopwatch.StartNew();
        var all = WeChatLocator.FindProcesses(deep: o.Has("deep"));
        long scanMs = sw.ElapsedMilliseconds;
        if (all.Count == 0)
        {
            Console.WriteLine("No WeChat process found (WeChat not running?).");
            return 2;
        }

        var uia = new Uia();
        var candidates = WeChatLocator.UiCandidates(all);
        var main = WeChatLocator.FindMainWindow(uia, candidates.Select(p => p.Pid).ToHashSet());

        Console.WriteLine($"{"PID",7}  {"Name",-14} {"Role",-26} {"Version",-12} {"Match",-28} Path");
        foreach (var p in all)
        {
            string role = p.IsHelper ? "helper"
                : main is not null && main.Window.Pid == p.Pid ? $"UI host (main 0x{main.Window.Hwnd:X})"
                : candidates.Any(c => c.Pid == p.Pid) ? "same product (no main wnd)"
                : "other";
            Console.WriteLine($"{p.Pid,7}  {p.ProcessName,-14} {role,-26} {p.FileVersion,-12} {p.MatchReason,-28} {p.ImagePath}");
        }
        Console.WriteLine();
        Console.WriteLine(main is null
            ? "Main window: NOT FOUND"
            : $"Main window: 0x{main.Window.Hwnd:X} pid={main.Window.Pid} win32Class='{main.Window.ClassName}' uiaClass='{main.UiaClassName}' " +
              $"title='{o.CreateRedactor().Title(main.Window.Title)}' visible={main.Window.Visible} iconic={main.Window.Iconic} (rule: {main.Reason})");
        Console.WriteLine($"Process scan took {scanMs} ms ({(o.Has("deep") ? "deep: version resource of every process" : "name hints + version resource")}).");
        Console.WriteLine($"SPI_GETSCREENREADER = {Native.IsScreenReaderFlagSet()}");
        return 0;
    }

    // ------------------------------------------------------------------ windows

    public static int Windows(Options o)
    {
        var uia = new Uia();
        var redactor = o.CreateRedactor();
        var all = WeChatLocator.FindProcesses(deep: false);
        var pids = (o.Has("helpers") ? all : WeChatLocator.UiCandidates(all)).Select(p => p.Pid).ToHashSet();
        if (pids.Count == 0) { Console.WriteLine("No WeChat process found."); return 2; }

        IntPtr fg = Native.GetForegroundWindow();
        foreach (var w in WeChatLocator.EnumTopWindows(pids))
        {
            Console.WriteLine($"0x{w.Hwnd.ToInt64():X8} pid={w.Pid,-6} vis={(w.Visible ? 1 : 0)} min={(w.Iconic ? 1 : 0)} fg={(w.Hwnd == fg ? 1 : 0)} " +
                              $"class='{w.ClassName}' title='{redactor.Title(w.Title)}' rect={w.Rect} style=0x{w.Style:X8} ex=0x{w.ExStyle:X8}" +
                              (w.Owner != IntPtr.Zero ? $" owner=0x{w.Owner:X}" : ""));
            if (!WeChatUiProfile.IsQtWindowClass(w.ClassName) && !o.Has("all")) continue;

            try
            {
                var el = uia.Automation.ElementFromHandleBuildCache(w.Hwnd, uia.FullCache);
                var info = uia.Snapshot(el, full: true);
                int rawChildren = CountChildren(uia.Automation.RawViewWalker, el, 500);
                int controlChildren = CountChildren(uia.Automation.ControlViewWalker, el, 500);
                Console.WriteLine($"      UIA: {TreeDumper.Describe(info, redactor)}");
                Console.WriteLine($"      UIA children: raw={rawChildren} control={controlChildren}; MSAA: {MsaaSummary(w.Hwnd)}");
            }
            catch (COMException ex)
            {
                Console.WriteLine($"      UIA: failed 0x{ex.HResult:X8}");
            }
        }
        Console.WriteLine($"SPI_GETSCREENREADER = {Native.IsScreenReaderFlagSet()}");
        return 0;
    }

    private static int CountChildren(IUIAutomationTreeWalker walker, IUIAutomationElement el, int max)
    {
        int n = 0;
        try
        {
            var c = walker.GetFirstChildElement(el);
            while (c is not null && n < max)
            {
                n++;
                c = walker.GetNextSiblingElement(c);
            }
        }
        catch (COMException) { return -1; }
        return n;
    }

    private static string MsaaSummary(IntPtr hwnd)
    {
        var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        int hr = Native.AccessibleObjectFromWindow(hwnd, 0xFFFFFFFC /* OBJID_CLIENT */, ref iid, out var obj);
        if (hr != 0 || obj is not IAccessible acc) return $"AccessibleObjectFromWindow hr=0x{hr:X8}";
        try { return $"client accChildCount={acc.accChildCount} role={acc.get_accRole(0)}"; }
        catch (Exception ex) { return $"IAccessible failed {ex.GetType().Name}"; }
    }

    // ------------------------------------------------------------------ dump

    public static int Dump(Options o)
    {
        var uia = new Uia();
        var (hwnd, _, describe) = ResolveTarget(uia, o);
        if (hwnd == IntPtr.Zero) { Console.WriteLine(describe); return 2; }

        using var sink = new Sink(o.OutBase("dump"), console: !o.Has("quiet"));
        var redactor = o.CreateRedactor();
        sink.Always($"# WeChatInspector dump  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  target {describe}");
        sink.Always($"# window visible={Native.IsWindowVisible(hwnd)} iconic={Native.IsIconic(hwnd)} foreground={Native.GetForegroundWindow() == hwnd} " +
                    $"screenReaderFlag={Native.IsScreenReaderFlagSet()} redaction={(redactor.Enabled ? "on" : "OFF")}");

        var root = uia.Automation.ElementFromHandleBuildCache(hwnd, uia.FullCache);
        var dumper =new TreeDumper(uia, redactor, sink, o.GetInt("depth", 80), o.GetInt("max", 20000), o.Has("control"));
        if (o.Has("bulk")) dumper.DumpBulk(root);
        else dumper.DumpWalk(root);

        if (sink.TextPath is not null) sink.Always($"# written: {sink.TextPath} and {sink.JsonPath}");
        return 0;
    }

    // ------------------------------------------------------------------ events

    public static int Events(Options o)
    {
        var uia = new Uia();
        var (hwnd, pid, describe) = ResolveTarget(uia, o);
        if (hwnd == IntPtr.Zero) { Console.WriteLine(describe); return 2; }

        var redactor = o.CreateRedactor();
        using var sink = new Sink(o.OutBase("events"), console: !o.Has("quiet"));
        var root = uia.Automation.ElementFromHandleBuildCache(hwnd, uia.EventCache);
        sink.Always($"# WeChatInspector events  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  target {describe}");
        sink.Always($"# window visible={Native.IsWindowVisible(hwnd)} iconic={Native.IsIconic(hwnd)} foreground={Native.GetForegroundWindow() == hwnd} " +
                    $"rawChildren={CountChildren(uia.Automation.RawViewWalker, root, 500)} screenReaderFlag={Native.IsScreenReaderFlagSet()} " +
                    $"redaction={(redactor.Enabled ? "on" : "OFF")}");

        var settings = new RecorderSettings
        {
            Pid = pid,
            MainHwnd = hwnd,
            Uia = !o.Has("no-uia"),
            WinEvents = !o.Has("no-winevent"),
            ShellHook = !o.Has("no-shell"),
            Foreground = !o.Has("no-foreground"),
            IncludeLocation = o.Has("location"),
            IncludeBounds = o.Has("bounds"),
            IncludeTextSelection = o.Has("textsel"),
            Ancestry = o.Has("ancestry"),
            ResolveWinEvents = o.Has("resolve"),
            FollowWindows = !o.Has("no-follow"),
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // --stop-event lets another process end the recording cleanly (summary + flush), e.g. from PowerShell:
        // [Threading.EventWaitHandle]::OpenExisting('Local\wxi-stop').Set()
        EventWaitHandle? stopEvent = null;
        RegisteredWaitHandle? stopWait = null;
        if (o.Get("stop-event") is { Length: > 0 } stopName)
        {
            stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
            stopWait = ThreadPool.RegisterWaitForSingleObject(stopEvent, (_, _) => cts.Cancel(), null, Timeout.Infinite, executeOnlyOnce: true);
            sink.Always($"# stop with: [Threading.EventWaitHandle]::OpenExisting('{stopName}').Set()");
        }

        int seconds = o.GetInt("seconds", 0);
        new EventRecorder(uia, redactor, sink, settings).Run(seconds > 0 ? TimeSpan.FromSeconds(seconds) : null, cts.Token);
        stopWait?.Unregister(null);
        stopEvent?.Dispose();
        if (sink.TextPath is not null) sink.Always($"# written: {sink.TextPath} and {sink.JsonPath}");
        return 0;
    }

    // ------------------------------------------------------------------ locate

    /// <summary>
    /// Finds the semantic elements the listener needs, using only the rules in <see cref="WeChatUiProfile"/>,
    /// and parses them. Run it after a WeChat update: if this still works, the adapter still works.
    /// </summary>
    public static int Locate(Options o)
    {
        var uia = new Uia();
        var (hwnd, _, describe) = ResolveTarget(uia, o);
        if (hwnd == IntPtr.Zero) { Console.WriteLine(describe); return 2; }
        var r = o.CreateRedactor();
        Console.WriteLine($"# target {describe}");
        bool visible = Native.IsWindowVisible(hwnd), iconic = Native.IsIconic(hwnd);
        Console.WriteLine($"main window: visible={visible} minimized={iconic} foreground={Native.GetForegroundWindow() == hwnd}");
        if (!visible)
        {
            Console.WriteLine("main window is hidden (tray): Qt's UIA bridge skips invisible children during navigation,");
            Console.WriteLine("so nothing below the window can be located right now. Open WeChat once and run again.");
            return 3;
        }

        var sw = Stopwatch.StartNew();
        var cr = uia.EventCache.Clone();
        cr.TreeScope = TreeScope.TreeScope_Subtree;
        var root = uia.Automation.ElementFromHandleBuildCache(hwnd, cr);
        long fetchMs = sw.ElapsedMilliseconds;

        var sessionList = FindCached(uia, root, i => i.AutomationId == WeChatUiProfile.SessionListAutomationId);
        var chatTitle = FindCached(uia, root, i => i.AutomationId?.EndsWith(WeChatUiProfile.CurrentChatNameIdSuffix, StringComparison.Ordinal) == true);
        var messageList = FindCached(uia, root, i => i.AutomationId == WeChatUiProfile.MessageListAutomationId);
        Console.WriteLine($"one BuildUpdatedCache(Subtree) call: {fetchMs} ms; search: {sw.ElapsedMilliseconds - fetchMs} ms");
        Console.WriteLine();

        Console.WriteLine($"session list ({WeChatUiProfile.SessionListAutomationId}): {(sessionList is null ? "NOT FOUND" : uia.Snapshot(sessionList, false).ClassName)}");
        if (sessionList is not null)
        {
            foreach (var cell in CachedChildren(sessionList))
            {
                var info = uia.Snapshot(cell, false);
                if (info.ClassName != WeChatUiProfile.SessionCellClass) { Console.WriteLine($"  (other child: {info.ControlType} {info.ClassName})"); continue; }
                var s = WeChatUiProfile.ParseSessionCell(info.Name ?? "");
                var preview = WeChatNotifier.Listener.Profile.PreviewText.Analyze(s.Preview);
                Console.WriteLine($"  - \"{r.Apply(s.Title)}\"{(s.Pinned ? " pinned" : "")}{(s.Muted ? " muted" : "")} unread={s.Unread} time={s.Time ?? "-"} " +
                                  $"kind={preview.Kind}{(preview.Mentioned ? " @me" : "")} " +
                                  $"preview={(s.HasContent ? "\"" + r.Apply(preview.Text) + "\"" : "(not available)")}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"current chat title: {(chatTitle is null ? "NOT FOUND" : "\"" + r.Apply(uia.Snapshot(chatTitle, false).Name) + "\"")}");
        Console.WriteLine($"message list ({WeChatUiProfile.MessageListAutomationId}): {(messageList is null ? "NOT FOUND" : uia.Snapshot(messageList, false).ClassName)}");
        if (messageList is not null)
        {
            var listRect = uia.Snapshot(messageList, false).Rect;
            var tailCells = new List<WeChatNotifier.Listener.Reading.TailCell>();
            foreach (var item in CachedChildren(messageList))
            {
                var info = uia.Snapshot(item, false);
                string kind = info.ClassName is { } c && WeChatUiProfile.MessageCellTypes.TryGetValue(c, out var k) ? k.ToString() : "Other";
                bool inView = listRect is { Length: 4 } lr && info.Rect is { Length: 4 } ir
                              && ir[1] + ir[3] > lr[1] && ir[1] < lr[1] + lr[3];
                bool fully = listRect is { Length: 4 } lr2 && info.Rect is { Length: 4 } ir2
                             && ir2[1] >= lr2[1] && ir2[1] + ir2[3] <= lr2[1] + lr2[3];
                Console.WriteLine($"  - {kind,-14} {(fully ? "in-view " : inView ? "partial " : "STALE   ")} \"{r.Apply(info.Name)}\"  ({info.ClassName})");
                if (WeChatNotifier.Core.Imaging.PixelRect.FromUia(info.Rect) is { } rect) tailCells.Add(new(info.ClassName, info.Name, rect));
            }
            // The listener's verdict: is the newest message at the end of the list (where a picture can be copied)?
            Console.WriteLine($"  end of list: {WeChatNotifier.Listener.Reading.MessageTail.Of(tailCells, WeChatNotifier.Core.Imaging.PixelRect.FromUia(listRect)).Describe()}");
        }
        return 0;
    }

    private static IUIAutomationElement? FindCached(Uia uia, IUIAutomationElement el, Func<ElementInfo, bool> match)
    {
        if (match(uia.Snapshot(el, false))) return el;
        foreach (var child in CachedChildren(el))
            if (FindCached(uia, child, match) is { } found) return found;
        return null;
    }

    private static IEnumerable<IUIAutomationElement> CachedChildren(IUIAutomationElement el)
    {
        IUIAutomationElementArray? arr = null;
        try { arr = el.GetCachedChildren(); } catch (COMException) { }
        for (int i = 0; i < (arr?.Length ?? 0); i++) yield return arr!.GetElement(i);
    }

    // ------------------------------------------------------------------ readprobe

    public static int ReadProbe(Options o)
    {
        var uia = new Uia();
        var (hwnd, pid, describe) = ResolveTarget(uia, o);
        if (hwnd == IntPtr.Zero) { Console.WriteLine(describe); return 2; }

        using var sink = new Sink(o.OutBase("readprobe"), console: true);
        sink.Always($"# WeChatInspector readprobe  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  target {describe}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var stop = o.Get("stop-event") is { Length: > 0 } name ? new EventWaitHandle(false, EventResetMode.ManualReset, name) : null;
        var reg = stop is null ? null : ThreadPool.RegisterWaitForSingleObject(stop, (_, _) => cts.Cancel(), null, Timeout.Infinite, true);

        new ReadProbe(uia, o.CreateRedactor(), sink, pid, hwnd).Run(TimeSpan.FromSeconds(o.GetInt("seconds", 600)), cts.Token);
        reg?.Unregister(null);
        if (sink.TextPath is not null) sink.Always($"# written: {sink.TextPath}");
        return 0;
    }

    // ------------------------------------------------------------------ hover

    /// <summary>Manual exploration aid: describes the element under the mouse cursor. Diagnostic only (it samples).</summary>
    public static int Hover(Options o)
    {
        var uia = new Uia();
        var redactor = o.CreateRedactor();
        int seconds = o.GetInt("seconds", 0);
        var until = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        string last = "";
        Console.WriteLine("# move the mouse over WeChat elements; Ctrl+C to stop");
        while (!cts.IsCancellationRequested && DateTime.UtcNow < until)
        {
            Native.GetCursorPos(out var pt);
            try
            {
                var el = uia.Automation.ElementFromPointBuildCache(new tagPOINT { x = pt.X, y = pt.Y }, uia.FullCache);
                var info = uia.Snapshot(el, full: true);
                if (info.RuntimeIdText != last)
                {
                    last = info.RuntimeIdText;
                    Console.WriteLine($"({pt.X},{pt.Y}) {TreeDumper.Describe(info, redactor)}");
                    Console.WriteLine($"      @ {AncestryOf(uia, redactor, el)}");
                }
            }
            catch (COMException) { }
            cts.Token.WaitHandle.WaitOne(400);
        }
        return 0;
    }

    private static string AncestryOf(Uia uia, Redactor redactor, IUIAutomationElement el)
    {
        var parts = new List<string>();
        var walker = uia.Automation.RawViewWalker;
        var cur = el;
        for (int i = 0; i < 25; i++)
        {
            IUIAutomationElement? p;
            try { p = walker.GetParentElementBuildCache(cur, uia.EventCache); } catch (COMException) { break; }
            if (p is null) break;
            var pi = uia.Snapshot(p, false);
            if (pi.ClassName == "#32769") break;
            parts.Add(pi.ControlType + (pi.ClassName is { } c ? $"({c})" : "") + (pi.AutomationId is { } a ? "#" + redactor.Ids(a) : ""));
            cur = p;
        }
        parts.Reverse();
        return string.Join(" > ", parts);
    }

    // ------------------------------------------------------------------ helpers

    internal static (IntPtr Hwnd, int Pid, string Describe) ResolveTarget(Uia uia, Options o)
    {
        if (o.GetHwnd("hwnd") is { } explicitHwnd)
        {
            if (!Native.IsWindow(explicitHwnd)) return (IntPtr.Zero, 0, $"0x{explicitHwnd:X} is not a window");
            return (explicitHwnd, Native.GetPid(explicitHwnd),
                $"hwnd 0x{explicitHwnd:X} class='{Native.GetClassName(explicitHwnd)}' uiaClass='{uia.TryGetRootClassName(explicitHwnd)}'");
        }

        var all = WeChatLocator.FindProcesses(deep: false);
        var candidates = WeChatLocator.UiCandidates(all);
        if (candidates.Count == 0) return (IntPtr.Zero, 0, "No WeChat UI process found (is WeChat running?).");
        var main = WeChatLocator.FindMainWindow(uia, candidates.Select(p => p.Pid).ToHashSet());
        if (main is null) return (IntPtr.Zero, 0, "WeChat is running but its main window was not found (still logging in?).");
        var proc = candidates.First(p => p.Pid == main.Window.Pid);
        return (main.Window.Hwnd, main.Window.Pid,
            $"{proc.ProcessName} {proc.FileVersion} pid={proc.Pid} main=0x{main.Window.Hwnd:X} uiaClass='{main.UiaClassName}' ({main.Reason})");
    }
}
