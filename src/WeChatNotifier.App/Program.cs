using System.Reflection;
using WeChatNotifier.Core.Configuration;
using WeChatNotifier.Diagnostics;
using WeChatNotifier.Listener;
using WeChatNotifier.Notifications;

namespace WeChatNotifier.App;

/// <summary>
/// WeChat Notifier, the background app (PHASE 4): no console, no main window — a notification-area icon, started
/// with Windows, idle until WeChat flashes. Command line, for the installer and for support:
///   --autostart    started by Windows at sign-in (the Run key): no "running in the background" notification
///   --exit         ask the running notifier to exit, and wait until it has
///   --unregister   --exit, then remove autostart, the notification registration with its notifications and
///                  images, and the error-reporting exclusion. Settings and logs stay.
///   --uninstall    what Settings → Apps → Installed apps runs: --unregister, plus the Start menu entry, the
///                  uninstall entry and the program folder; settings and logs if the user agrees (--quiet: no questions)
/// </summary>
internal static class Program
{
    private const string MutexName = ControlProtocol.HostMutex;
    private const string ShowEventName = ControlProtocol.ShowEvent;
    private const string ExitEventName = ControlProtocol.ExitEvent;
    private const string ActivationArgumentPrefix = "----AppNotificationActivated:";

    private static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    [MTAThread]
    private static int Main(string[] args)
    {
        if (Has(args, "--exit"))
            return Report(RequestExit(), "WeChat Notifier is not running (any more).", "WeChat Notifier did not exit in time (a console host has to be closed in its own window).");
        if (Has(args, "--unregister")) return Unregister();
        if (Has(args, "--uninstall")) return Uninstaller.Run(quiet: Has(args, "--quiet"));

        bool autostart = Has(args, StartupCommand.AutostartArgument);
        bool byNotification = args.Any(a => a.StartsWith(ActivationArgumentPrefix, StringComparison.Ordinal));
        using var instance = new Mutex(initiallyOwned: true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Started again by the user: open the settings window from here (a process the user just started may
            // take the foreground); if that is impossible, the running notifier answers with its icon and a notification.
            if (!autostart && !byNotification && !SettingsLauncher.Open(DiagnosticLog.Disabled) && !Signal(ShowEventName))
                Native.MessageBoxW(IntPtr.Zero, "另一个 WeChat Notifier（控制台版本）正在运行。", "WeChat Notifier", Native.MB_OK | Native.MB_ICONINFORMATION);
            return 0;
        }

        try
        {
            // Right after the mutex, so that a second start always finds them.
            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
            return Run(autostart, showEvent, exitEvent);
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    private static int Run(bool autostart, EventWaitHandle showEvent, EventWaitHandle exitEvent)
    {
        bool firstRun = !File.Exists(SettingsStore.FilePath);
        var settings = SettingsStore.Load();
        using var log = new DiagnosticLog(Path.Combine(SettingsStore.Folder, "logs"), settings.DebugLogging ? LogLevel.Debug : LogLevel.Info);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Error("app", $"unhandled {e.ExceptionObject.GetType().Name}; exiting");
        log.Info("app", $"start v{Version} autostart={autostart} firstRun={firstRun} pid={Environment.ProcessId}");

        ExcludeFromErrorReporting(log);
        var synced = SyncStartup(settings, firstRun, log);
        if (firstRun || synced != settings) SettingsStore.Save(synced);
        settings = synced;

        using var host = new NotifierHost(settings, log, Version);
        using var tray = new TrayApp(host, log);
        host.Start();

        var showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => tray.PostShowRequest(), null, Timeout.Infinite, executeOnlyOnce: false);
        var exitWait = ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => tray.PostExitRequest(), null, Timeout.Infinite, executeOnlyOnce: true);

        // Started by hand: say that it runs (there is no window). Not at sign-in, not when a click started it.
        if (!autostart && host.Notifier.LaunchActivation is null)
        {
            tray.Announce(firstRun && settings.StartWithWindows
                ? "WeChat Notifier 已在后台运行，并会随 Windows 自动启动。收到微信消息时会在这里提醒你。单击通知区域中的图标打开设置，右键单击可以暂停或退出。"
                : "WeChat Notifier 已在后台运行。单击通知区域中的图标打开设置，右键单击可以暂停或退出。");
        }

        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }

        showWait.Unregister(null);
        exitWait.Unregister(null);
        log.Info("app", "exit");
        return 0;
    }

    /// <summary>
    /// First run: register autostart as the settings say (default on). Later runs: Windows is the authority — the
    /// user may have switched the entry off in Task Manager → Startup apps — and a moved executable is followed.
    /// </summary>
    private static AppSettings SyncStartup(AppSettings settings, bool firstRun, DiagnosticLog log)
    {
        if (firstRun)
        {
            bool ok = StartupRegistration.Apply(settings.StartWithWindows, Environment.ProcessPath!);
            log.Info("app", $"first run: start with Windows {(settings.StartWithWindows ? "on" : "off")}{(ok ? "" : " (registry refused)")}");
            return ok ? settings : settings with { StartWithWindows = false };
        }
        if (StartupRegistration.RepairIfMoved(Environment.ProcessPath!)) log.Info("app", "autostart entry pointed to a missing executable; now points here");
        bool on = StartupRegistration.State() == StartupState.On;
        return settings.StartWithWindows == on ? settings : settings with { StartWithWindows = on };
    }

    /// <summary>
    /// Crash reports can contain process memory — chat text included — so none may leave the PC (spec §14):
    /// Windows Error Reporting skips this executable. Per user, no admin rights; undone by --unregister.
    /// </summary>
    private static void ExcludeFromErrorReporting(DiagnosticLog log)
    {
        int hr = Native.WerAddExcludedApplication(Path.GetFileName(Environment.ProcessPath!), false);
        if (hr != 0) log.Warn("app", $"Windows Error Reporting exclusion failed 0x{hr:X8}");
    }

    private static int Unregister()
    {
        bool exited = RequestExit();
        var lines = new List<string>
        {
            $"running notifier: {(exited ? "not running / exited" : "did not exit in time")}",
            $"notifications: {WindowsNotifier.RemoveRegistration()}",
            $"copied avatars: {(WeChatListener.DeleteSavedAvatars(SettingsStore.AvatarFolder) ? "removed" : "could not be removed (in use)")}",
            $"autostart: {(StartupRegistration.Apply(false, Environment.ProcessPath!) ? "removed" : "could not be removed")}",
        };
        int hr = Native.WerRemoveExcludedApplication(Path.GetFileName(Environment.ProcessPath!), false);
        lines.Add($"error reporting exclusion: {(hr == 0 ? "removed" : $"not present (0x{hr:X8})")}");
        lines.Add($"settings and logs kept: {SettingsStore.Folder}");
        return Report(exited, string.Join(Environment.NewLine, lines), string.Join(Environment.NewLine, lines));
    }

    /// <summary>Asks a running notifier to exit and waits until it has. True also when none was running.</summary>
    internal static bool RequestExit()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return true;
        using (mutex)
        {
            if (!Signal(ExitEventName)) return false; // the console host takes no requests
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) return false;
            }
            catch (AbandonedMutexException)
            {
                // It ended without releasing the mutex: just as good, and now it is ours.
            }
            mutex.ReleaseMutex();
            return true;
        }
    }

    private static bool Signal(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var ev)) return false;
        using (ev) return ev.Set();
    }

    /// <summary>Command-line results go to the console the command was typed in, if any (this is a GUI executable).</summary>
    private static int Report(bool ok, string success, string failure)
    {
        if (Native.AttachConsole(Native.ATTACH_PARENT_PROCESS))
        {
            Console.WriteLine();
            Console.WriteLine(ok ? success : failure);
        }
        return ok ? 0 : 1;
    }

    private static bool Has(string[] args, string option) => args.Any(a => string.Equals(a, option, StringComparison.OrdinalIgnoreCase));
}
