using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using WeChatNotifier.Core.Configuration;
using WeChatNotifier.Diagnostics;
using WeChatNotifier.Listener;
using WeChatNotifier.Notifications;

namespace WeChatNotifier.App;

/// <summary>
/// The notification-area icon and its menu (spec §11), on a hidden top-level window that also receives what the
/// background app reacts to: session lock/unlock, sign-out and shutdown, Explorer restarts (TaskbarCreated),
/// display changes, and the settings window's requests (WM_COPYDATA, <see cref="ControlProtocol"/>). Plain Win32
/// on the main thread: no UI framework in a process that is idle all day — the WinUI settings window is a separate
/// process that exists only while it is open.
/// </summary>
internal sealed class TrayApp : IDisposable
{
    private const uint WM_TRAY = Native.WM_APP + 1;
    private const uint WM_SHOW_REQUEST = Native.WM_APP + 2;
    private const uint WM_HOST_CHANGED = Native.WM_APP + 3;
    private const uint WM_EXIT_REQUEST = Native.WM_APP + 4;
    private const uint IconId = 1;
    private const int MaxTipLength = 127;

    private enum Command
    {
        None = 0,
        Settings = 100,
        Resume,
        Pause30Minutes,
        Pause1Hour,
        PauseUntilResumed,
        TestNotification,
        RestartListener,
        Diagnostics,
        Exit,
    }

    private static readonly Native.WndProc WndProcDelegate = StaticWndProc; // must outlive the window class
    private static TrayApp? _current;

    private readonly NotifierHost _host;
    private readonly DiagnosticLog _log;
    private readonly uint _taskbarCreatedMsg;
    private readonly IntPtr _hwnd;
    private readonly bool _sessionNotifications;
    private IntPtr _icon;
    private bool _iconOwned;
    private bool _iconAdded;
    private bool _forceIcon; // shown for this run although the setting hides it (fallback when settings cannot open)
    private int _changePosted;
    private int _dialogOpen;

    public TrayApp(NotifierHost host, DiagnosticLog log)
    {
        _host = host;
        _log = log;
        _current = this;
        _taskbarCreatedMsg = Native.RegisterWindowMessageW("TaskbarCreated");

        var instance = Native.GetModuleHandleW(null);
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
            hInstance = instance,
            lpszClassName = ControlProtocol.WindowClass,
        };
        if (Native.RegisterClassExW(ref wc) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        // A real (never shown) top-level window, not a message-only one: only top-level windows receive broadcasts
        // such as TaskbarCreated and WM_ENDSESSION, and the settings window finds it by its class.
        _hwnd = Native.CreateWindowExW(0, ControlProtocol.WindowClass, "WeChat Notifier", Native.WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError());

        _sessionNotifications = Native.WTSRegisterSessionNotification(_hwnd, Native.NOTIFY_FOR_THIS_SESSION);
        LoadIcon();
        if (host.Settings.ShowTrayIcon) AddIcon();
        host.Changed += OnHostChanged;
    }

    private bool IconWanted => _host.Settings.ShowTrayIcon || _forceIcon;

    /// <summary>Another start of the app asked this one to show itself (any thread).</summary>
    public void PostShowRequest() => Native.PostMessageW(_hwnd, WM_SHOW_REQUEST, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Another process (installer, <c>--exit</c>) asked the app to exit (any thread).</summary>
    public void PostExitRequest() => Native.PostMessageW(_hwnd, WM_EXIT_REQUEST, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// A message from the app itself: a quiet notification, or — when Windows does not show our notifications —
    /// a message box that also says why.
    /// </summary>
    public void Announce(string text)
    {
        var setting = _host.Notifier.QueryWindowsSetting();
        if (setting == "Enabled" && _host.ShowAppMessage(text)) return;
        ShowMessage(text + "\n\n" + NotificationProblem(setting), warning: true);
    }

    public void Dispose()
    {
        _host.Changed -= OnHostChanged;
        if (Native.IsWindow(_hwnd)) Native.DestroyWindow(_hwnd); // also removes the icon
        if (_iconOwned) Native.DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        _current = null;
    }

    // ------------------------------------------------------------------ window procedure

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            return _current is { } app ? app.WndProc(hwnd, msg, wParam, lParam) : Native.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Must not unwind into user32. Type only: messages never carry chat data here anyway.
            _current?._log.Error("tray", $"message 0x{msg:X4}: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return IntPtr.Zero;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TRAY:
                // NOTIFYICON_VERSION_4: the event in LOWORD(lParam), the anchor point in wParam.
                switch ((uint)(lParam.ToInt64() & 0xFFFF))
                {
                    case Native.WM_CONTEXTMENU:
                        ShowMenu((short)(wParam.ToInt64() & 0xFFFF), (short)((wParam.ToInt64() >> 16) & 0xFFFF));
                        break;
                    case Native.NIN_SELECT:
                    case Native.NIN_KEYSELECT:
                        OpenSettings();
                        break;
                }
                return IntPtr.Zero;

            case Native.WM_COPYDATA:
                return OnCopyData(wParam, lParam);

            case WM_HOST_CHANGED:
                Interlocked.Exchange(ref _changePosted, 0);
                UpdateIcon();
                return IntPtr.Zero;

            case WM_SHOW_REQUEST:
                OnStartedAgain();
                return IntPtr.Zero;

            case WM_EXIT_REQUEST:
                _log.Info("app", "exit requested by another process");
                Native.DestroyWindow(hwnd);
                return IntPtr.Zero;

            case Native.WM_WTSSESSION_CHANGE:
                OnSessionChange((int)wParam.ToInt64());
                return IntPtr.Zero;

            case Native.WM_QUERYENDSESSION:
                return 1;

            case Native.WM_ENDSESSION:
                // Sign-out, shutdown, or an installer's Restart Manager: clean up now, the process may end any time
                // after this message returns.
                if (wParam != IntPtr.Zero)
                {
                    _log.Info("app", "session ending: shutting down");
                    _host.Dispose();
                    Native.DestroyWindow(hwnd);
                }
                return IntPtr.Zero;

            case Native.WM_DISPLAYCHANGE:
            case Native.WM_DPICHANGED:
                ReloadIcon(); // the taskbar's icon size may have changed
                break;

            case Native.WM_DESTROY:
                RemoveIcon();
                if (_sessionNotifications) Native.WTSUnRegisterSessionNotification(hwnd);
                Native.PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                if (msg == _taskbarCreatedMsg && msg != 0)
                {
                    // Explorer (re)started: the new taskbar has no icons yet.
                    _iconAdded = false;
                    ReloadIcon();
                    if (IconWanted) AddIcon();
                    return IntPtr.Zero;
                }
                break;
        }
        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void OnHostChanged()
    {
        // Coalesced: one repaint of the tooltip however many changes arrive meanwhile.
        if (Interlocked.Exchange(ref _changePosted, 1) == 0) Native.PostMessageW(_hwnd, WM_HOST_CHANGED, IntPtr.Zero, IntPtr.Zero);
    }

    private void OnSessionChange(int change)
    {
        switch (change)
        {
            case Native.WTS_SESSION_LOCK:
                _log.Info("app", "session locked");
                break;
            case Native.WTS_SESSION_UNLOCK:
            case Native.WTS_CONSOLE_CONNECT:
            case Native.WTS_REMOTE_CONNECT:
                _log.Info("app", change == Native.WTS_SESSION_UNLOCK ? "session unlocked" : "session reconnected");
                _host.OnSessionUnlocked();
                break;
        }
    }

    /// <summary>The user started the app again but the settings window could not open there: show the icon at least.</summary>
    private void OnStartedAgain()
    {
        _log.Info("app", "started again by the user");
        if (!IconWanted) _forceIcon = true;
        if (IconWanted) AddIcon();
        Announce(_host.Settings.ShowTrayIcon
            ? "WeChat Notifier 已经在运行。右键单击通知区域中的图标可以暂停通知、打开设置或退出。"
            : "WeChat Notifier 已经在运行。托盘图标已临时显示。");
    }

    // ------------------------------------------------------------------ settings window

    private void OpenSettings()
    {
        // The click gave this process the right to take the foreground; taking it passes it on to the new window.
        Native.SetForegroundWindow(_hwnd);
        if (!SettingsLauncher.Open(_log)) ShowMessage("设置窗口无法打开：找不到 WeChatNotifier.Settings.exe。", warning: true);
    }

    /// <summary>A request from the settings window (<see cref="ControlProtocol"/>); runs while it waits.</summary>
    private IntPtr OnCopyData(IntPtr sender, IntPtr lParam)
    {
        var data = Marshal.PtrToStructure<Native.COPYDATASTRUCT>(lParam);
        switch (data.dwData.ToInt64())
        {
            case ControlProtocol.ReloadSettings:
                ReloadSettings();
                return 1;
            case ControlProtocol.TestNotification:
                return _host.Notifier.QueryWindowsSetting() == "Enabled" && _host.ShowTestNotification() ? 1 : 0;
            case ControlProtocol.ClearImageCache:
                _host.ClearImageCache();
                return 1;
            case ControlProtocol.RestartListener:
                _host.RestartListener("settings window");
                return 1;
            case ControlProtocol.StatusRequest:
                return Reply(sender, ControlProtocol.StatusReply, _host.Snapshot().ToJson()) ? 1 : 0;
            default:
                return 0;
        }
    }

    private bool Reply(IntPtr target, long kind, string text)
    {
        if (target == IntPtr.Zero || !Native.IsWindow(target)) return false;
        var bytes = Encoding.UTF8.GetBytes(text);
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var data = new Native.COPYDATASTRUCT { dwData = (IntPtr)kind, cbData = bytes.Length, lpData = buffer };
            return Native.SendMessageTimeoutW(target, Native.WM_COPYDATA, _hwnd, ref data, Native.SMTO_ABORTIFHUNG, 2000, out _) != IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The settings window wrote settings.json. Applied at once; "start with Windows" is written to the registry
    /// here, so that one process owns it (and Windows' own on/off switch stays respected).
    /// </summary>
    private void ReloadSettings()
    {
        var loaded = SettingsStore.Load();
        var before = _host.Settings;
        bool startupOn = StartupRegistration.State() == StartupState.On;
        if (loaded.StartWithWindows != startupOn && !StartupRegistration.Apply(loaded.StartWithWindows, Environment.ProcessPath!))
        {
            _log.Warn("app", "start with Windows could not be changed (registry refused)");
            loaded = loaded with { StartWithWindows = startupOn };
            SettingsStore.Save(loaded); // the settings window follows the file and shows the real state
        }
        _host.ApplySettings(loaded);
        if (loaded.ShowTrayIcon != before.ShowTrayIcon)
        {
            _forceIcon = false;
            if (loaded.ShowTrayIcon) AddIcon();
            else RemoveIcon();
        }
    }

    // ------------------------------------------------------------------ menu

    private void ShowMenu(int x, int y)
    {
        IntPtr menu = Native.CreatePopupMenu();
        int chosen;
        try
        {
            Item(menu, Command.None, "WeChat Notifier：" + StatusText(), grayed: true);
            Separator(menu);
            Item(menu, Command.Settings, "设置…");
            if (_host.PausedUntilUtc is { } until)
            {
                Item(menu, Command.Resume, until == DateTime.MaxValue ? "恢复通知" : $"恢复通知（已暂停到 {until.ToLocalTime():HH:mm}）");
            }
            else
            {
                var pause = Native.CreatePopupMenu();
                Item(pause, Command.Pause30Minutes, "30 分钟");
                Item(pause, Command.Pause1Hour, "1 小时");
                Item(pause, Command.PauseUntilResumed, "直到手动恢复");
                Popup(menu, pause, "暂停通知");
            }
            Item(menu, Command.TestNotification, "发送测试通知");
            Separator(menu);
            Item(menu, Command.RestartListener, "重新启动监听");
            Item(menu, Command.Diagnostics, "诊断信息…");
            Separator(menu);
            Item(menu, Command.Exit, "退出");

            // The menu closes on a click elsewhere only when its window is in the foreground; the trailing
            // WM_NULL makes a second click on the icon work right away (documented TrackPopupMenu quirks).
            Native.SetForegroundWindow(_hwnd);
            uint align = Native.GetSystemMetrics(Native.SM_MENUDROPALIGNMENT) != 0 ? Native.TPM_RIGHTALIGN : Native.TPM_LEFTALIGN;
            chosen = Native.TrackPopupMenuEx(menu, align | Native.TPM_BOTTOMALIGN | Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_NONOTIFY, x, y, _hwnd, IntPtr.Zero);
            Native.PostMessageW(_hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            Native.DestroyMenu(menu); // and its submenus
        }
        Execute((Command)chosen);
    }

    private void Execute(Command command)
    {
        switch (command)
        {
            case Command.Settings:
                OpenSettings();
                break;
            case Command.Resume:
                _host.Resume();
                break;
            case Command.Pause30Minutes:
                _host.Pause(TimeSpan.FromMinutes(30));
                break;
            case Command.Pause1Hour:
                _host.Pause(TimeSpan.FromHours(1));
                break;
            case Command.PauseUntilResumed:
                _host.Pause(null);
                break;
            case Command.TestNotification:
                if (_host.Notifier.QueryWindowsSetting() != "Enabled" || !_host.ShowTestNotification())
                    ShowMessage("测试通知没有发出。\n\n" + NotificationProblem(_host.Notifier.WindowsSetting), warning: true);
                break;
            case Command.RestartListener:
                _host.RestartListener("menu");
                break;
            case Command.Diagnostics:
                ShowMessage(_host.Snapshot().ToText());
                break;
            case Command.Exit:
                _log.Info("app", "exit from the tray menu");
                Native.DestroyWindow(_hwnd);
                break;
        }
    }

    private static void Item(IntPtr menu, Command id, string text, bool check = false, bool grayed = false) =>
        Native.AppendMenuW(menu, Native.MF_STRING | (check ? Native.MF_CHECKED : 0) | (grayed ? Native.MF_GRAYED : 0), (IntPtr)(int)id, text);

    private static void Popup(IntPtr menu, IntPtr submenu, string text) => Native.AppendMenuW(menu, Native.MF_POPUP, submenu, text);

    private static void Separator(IntPtr menu) => Native.AppendMenuW(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);

    /// <summary>
    /// A message box on its own thread, so the tray keeps working while it is open; one at a time. Ctrl+C in it
    /// copies the text (useful for the diagnostics).
    /// </summary>
    private void ShowMessage(string text, bool warning = false)
    {
        if (Interlocked.Exchange(ref _dialogOpen, 1) == 1) return;
        var thread = new Thread(() =>
        {
            try
            {
                uint icon = warning ? Native.MB_ICONWARNING : Native.MB_ICONINFORMATION;
                Native.MessageBoxW(IntPtr.Zero, text, "WeChat Notifier", Native.MB_OK | icon | Native.MB_SETFOREGROUND | Native.MB_TOPMOST);
            }
            finally
            {
                Interlocked.Exchange(ref _dialogOpen, 0);
            }
        })
        { IsBackground = true, Name = "WeChatNotifier.Dialog" };
        thread.Start();
    }

    // ------------------------------------------------------------------ texts

    private string StatusText()
    {
        if (_host.Notifier.State != NotifierState.Ready) return "Windows 通知不可用";
        if (_host.PausedUntilUtc is { } until) return until == DateTime.MaxValue ? "通知已暂停" : $"通知已暂停到 {until.ToLocalTime():HH:mm}";
        return _host.Status switch
        {
            { State: ListenerState.Attached, ReferencesHeld: true } => "正在监听微信",
            { State: ListenerState.Attached } => "正在监听微信（打开一次微信窗口后可显示会话名）",
            { State: ListenerState.WaitingForWeChat } => "等待微信启动",
            { State: ListenerState.Stopped } => "监听已停止，稍后自动重试",
            _ => "正在启动",
        };
    }

    private string NotificationProblem(string? setting) => _host.Notifier.State switch
    {
        NotifierState.RuntimeMissing => "无法显示 Windows 通知：需要安装 Windows App Runtime 2.x（Windows App SDK 运行时）。",
        NotifierState.Unsupported => "无法显示 Windows 通知：Windows 不允许这个进程发通知（例如以管理员身份运行时）。请以普通方式启动。",
        NotifierState.Ready => setting switch
        {
            "DisabledForApplication" => "WeChat Notifier 的通知已在 Windows 设置 → 系统 → 通知 中关闭，请在那里打开。",
            "DisabledForUser" => "Windows 的通知已全部关闭（设置 → 系统 → 通知）。",
            "DisabledByGroupPolicy" => "组策略禁止了通知。",
            _ => "Windows 当前不显示本应用的通知。",
        },
        _ => "通知功能初始化失败，详情见日志文件夹。",
    };

    // ------------------------------------------------------------------ notification-area icon

    private Native.NOTIFYICONDATAW NewData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private string Tip()
    {
        var tip = "WeChat Notifier\n" + StatusText();
        return tip.Length <= MaxTipLength ? tip : tip[..MaxTipLength];
    }

    private void AddIcon()
    {
        if (_iconAdded)
        {
            UpdateIcon();
            return;
        }
        var data = NewData(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        data.uCallbackMessage = WM_TRAY;
        data.hIcon = _icon;
        data.szTip = Tip();
        // NIM_ADD fails while Explorer is still starting (sign-in; TaskbarCreated follows), and can report a
        // time-out although the icon was added — NIM_MODIFY tells which.
        if (!Native.Shell_NotifyIconW(Native.NIM_ADD, ref data) && !Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref data))
        {
            _log.Info("tray", "notification area not ready; the icon follows when the taskbar appears");
            return;
        }
        data.uTimeoutOrVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIconW(Native.NIM_SETVERSION, ref data);
        _iconAdded = true;
    }

    private void UpdateIcon()
    {
        if (!_iconAdded) return;
        var data = NewData(Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        data.hIcon = _icon;
        data.szTip = Tip();
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref data);
    }

    private void RemoveIcon()
    {
        if (!_iconAdded) return;
        var data = NewData(0);
        Native.Shell_NotifyIconW(Native.NIM_DELETE, ref data);
        _iconAdded = false;
    }

    private void ReloadIcon()
    {
        var (old, owned) = (_icon, _iconOwned);
        LoadIcon();
        UpdateIcon(); // the shell keeps its own copy, so the old handle can go afterwards
        if (owned && old != _icon) Native.DestroyIcon(old);
    }

    /// <summary>The app icon at the taskbar's small-icon size for the current DPI (Windows' default icon as fallback).</summary>
    private void LoadIcon()
    {
        uint dpi = Native.GetDpiForWindow(_hwnd) is var d and > 0 ? d : Native.GetDpiForSystem();
        int cx = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, dpi), cy = Native.GetSystemMetricsForDpi(Native.SM_CYSMICON, dpi);
        var module = Native.GetModuleHandleW(null);
        IntPtr icon;
        try
        {
            if (Native.LoadIconWithScaleDown(module, Native.AppIconId, cx, cy, out icon) != 0) icon = IntPtr.Zero;
        }
        catch (EntryPointNotFoundException)
        {
            icon = Native.LoadImageW(module, Native.AppIconId, Native.IMAGE_ICON, cx, cy, 0);
        }
        if (icon != IntPtr.Zero)
        {
            (_icon, _iconOwned) = (icon, true);
            return;
        }
        _log.Warn("tray", "app icon resource not found; using the Windows default icon");
        (_icon, _iconOwned) = (Native.LoadIconW(IntPtr.Zero, Native.IDI_APPLICATION), false); // shared: never destroyed
    }
}
