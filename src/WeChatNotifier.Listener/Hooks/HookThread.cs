using System.Runtime.InteropServices;
using WeChatNotifier.Diagnostics;
using WeChatNotifier.Listener.Interop;
using WeChatNotifier.Listener.Profile;

namespace WeChatNotifier.Listener.Hooks;

/// <summary>
/// Dedicated thread with a message loop that owns every OS hook. It does no UIA work: each callback only posts
/// a small item to the listener, so hooks never block. Idle cost is one blocked GetMessage call.
///  - hidden top-level window registered with RegisterShellHookWindow (HSHELL_FLASH, HSHELL_WINDOWCREATED)
///    that also receives WM_POWERBROADCAST and the "TaskbarCreated" broadcast (Explorer restarts)
///  - global out-of-context WinEvent hook for EVENT_SYSTEM_FOREGROUND
///  - WinEvent hooks filtered to the attached WeChat process: window create/destroy/show/hide, minimize/restore,
///    move-size end. The chatty LOCATIONCHANGE range is deliberately never hooked.
/// </summary>
internal sealed class HookThread : IDisposable
{
    private const uint WM_APP_ATTACH = Native.WM_APP + 1;
    private const string WindowClass = "WeChatNotifier.Hooks";

    private readonly Action<ListenerItem> _post;
    private readonly Func<long> _clock;
    private readonly DiagnosticLog _log;
    private readonly ManualResetEventSlim _ready = new();
    private readonly List<IntPtr> _processHooks = [];
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd;
    private uint _shellMsg;
    private uint _taskbarCreatedMsg;
    private IntPtr _foregroundHook;
    private Native.WndProc? _wndProc;
    private Native.WinEventProc? _winEventProc;

    public HookThread(Action<ListenerItem> post, Func<long> clock, DiagnosticLog log)
    {
        _post = post;
        _clock = clock;
        _log = log;
    }

    public bool ShellHookActive { get; private set; }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "WeChatNotifier.Hooks" };
        _thread.Start();
        _ready.Wait();
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create the hook window.");
    }

    /// <summary>(Re)installs the per-process WinEvent hooks on the hook thread; 0 removes them.</summary>
    public void AttachProcess(int pid) => Native.PostMessageW(_hwnd, WM_APP_ATTACH, new IntPtr(pid), IntPtr.Zero);

    public void Dispose()
    {
        if (_thread is null) return;
        Native.PostThreadMessageW(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(3000);
        _thread = null;
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        _wndProc = WndProc;
        _winEventProc = OnWinEvent;
        var hInstance = Native.GetModuleHandleW(null);
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = WindowClass,
        };
        Native.RegisterClassExW(ref wc);
        // A hidden top-level window: shell hook messages, power and TaskbarCreated broadcasts need one.
        _hwnd = Native.CreateWindowExW(0, WindowClass, WindowClass, Native.WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd != IntPtr.Zero)
        {
            _shellMsg = Native.RegisterWindowMessageW("SHELLHOOK");
            _taskbarCreatedMsg = Native.RegisterWindowMessageW("TaskbarCreated");
            ShellHookActive = Native.RegisterShellHookWindow(_hwnd);
            _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            _log.Info("hooks", $"shell hook {(ShellHookActive ? "registered" : "FAILED")}, foreground hook {(_foregroundHook != IntPtr.Zero ? "ok" : "FAILED")}");
        }
        _ready.Set();
        if (_hwnd == IntPtr.Zero) return;

        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }

        RemoveProcessHooks();
        if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
        Native.DeregisterShellHookWindow(_hwnd);
        Native.DestroyWindow(_hwnd);
        Native.UnregisterClassW(WindowClass, hInstance);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_shellMsg != 0 && msg == _shellMsg)
        {
            switch ((int)wParam.ToInt64())
            {
                case Native.HSHELL_FLASH: _post(new FlashItem(lParam, _clock())); break;
                case Native.HSHELL_WINDOWCREATED: _post(new ShellWindowCreatedItem(lParam, _clock())); break;
            }
            return IntPtr.Zero;
        }
        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            // Explorer restarted: register again so shell notifications keep flowing.
            Native.DeregisterShellHookWindow(_hwnd);
            ShellHookActive = Native.RegisterShellHookWindow(_hwnd);
            _post(new ShellRestartedItem(_clock()));
            return IntPtr.Zero;
        }
        if (msg == WM_APP_ATTACH)
        {
            InstallProcessHooks((int)wParam.ToInt64());
            return IntPtr.Zero;
        }
        if (msg == Native.WM_POWERBROADCAST)
        {
            int ev = (int)wParam.ToInt64();
            if (ev is Native.PBT_APMRESUMEAUTOMATIC or Native.PBT_APMRESUMESUSPEND) _post(new ResumeItem(_clock()));
            return new IntPtr(1);
        }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void InstallProcessHooks(int pid)
    {
        RemoveProcessHooks();
        if (pid == 0) return;
        const uint flags = Native.WINEVENT_OUTOFCONTEXT;
        foreach (var (min, max) in new[]
                 {
                     (Native.EVENT_OBJECT_CREATE, Native.EVENT_OBJECT_HIDE),
                     (Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND),
                     (Native.EVENT_SYSTEM_MOVESIZEEND, Native.EVENT_SYSTEM_MOVESIZEEND),
                 })
        {
            var hook = Native.SetWinEventHook(min, max, IntPtr.Zero, _winEventProc!, (uint)pid, 0, flags);
            if (hook != IntPtr.Zero) _processHooks.Add(hook);
        }
        _log.Info("hooks", $"process hooks for pid {pid}: {_processHooks.Count}/3");
    }

    private void RemoveProcessHooks()
    {
        foreach (var hook in _processHooks) Native.UnhookWinEvent(hook);
        _processHooks.Clear();
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hook == _foregroundHook)
        {
            _post(new WindowSignalItem(WindowSignal.Foreground, hwnd, _clock()));
            return;
        }
        if (idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF || hwnd == IntPtr.Zero) return;

        WindowSignal? signal = ev switch
        {
            Native.EVENT_OBJECT_CREATE => WindowSignal.Created,
            Native.EVENT_OBJECT_DESTROY => WindowSignal.Destroyed,
            Native.EVENT_OBJECT_SHOW => WindowSignal.Shown,
            Native.EVENT_OBJECT_HIDE => WindowSignal.Hidden,
            Native.EVENT_SYSTEM_MINIMIZESTART => WindowSignal.Minimized,
            Native.EVENT_SYSTEM_MINIMIZEEND => WindowSignal.Restored,
            Native.EVENT_SYSTEM_MOVESIZEEND => WindowSignal.MoveSizeEnded,
            _ => null,
        };
        if (signal is null) return;

        // Only WeChat's own top-level windows matter; menus, tooltips and popups are also top-level Qt windows,
        // so filter by class here (cheap, no cross-process call). Destroyed windows may no longer report a class.
        if (signal is not WindowSignal.Destroyed)
        {
            if (!Native.IsTopLevel(hwnd)) return;
            if (!WeChatUiProfile.IsCandidateTopLevelClass(Native.GetClassName(hwnd))) return;
        }
        _post(new WindowSignalItem(signal.Value, hwnd, _clock()));
    }
}
