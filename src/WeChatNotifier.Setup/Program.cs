using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;
using WeChatNotifier.App;
using WeChatNotifier.Core.Configuration;

namespace WeChatNotifier.Setup;

/// <summary>
/// WeChatNotifierSetup.exe (PHASE 6): installs or updates WeChat Notifier for the current user — no admin rights —
/// in %LOCALAPPDATA%\Programs\WeChatNotifier, adds a Start menu entry and an uninstall entry (Settings → Apps →
/// Installed apps; it runs <c>WeChatNotifier.exe --uninstall</c>), then starts WeChat Notifier. Settings are kept
/// across updates. Needs nothing installed first and downloads nothing: it runs on the .NET Framework built into
/// Windows, the program files bring their own .NET runtime, and the Windows App Runtime travels inside this exe
/// (<see cref="RuntimeBundle"/>), installed for the current user when missing.
///   --quiet   no questions, no progress window, no final message (updates, scripts)
/// </summary>
internal static class Program
{
    private const string Title = "WeChat Notifier 安装程序";
    private const string RuntimeDownload = "https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads";

    private static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    [STAThread]
    private static int Main(string[] args)
    {
        bool quiet = args.Any(a => string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase));

        if (IsElevated())
        {
            // A per-user install from an elevated process would also start the notifier elevated — and Windows shows
            // no notifications from elevated processes.
            Show("请直接双击运行安装程序，不要「以管理员身份运行」。\n\nWeChat Notifier 只为当前用户安装，不需要管理员权限；而且 Windows 不显示来自管理员进程的通知。", Native.MB_ICONWARNING);
            return 3;
        }
        if (Environment.OSVersion.Version.Build < 17763)
        {
            Show("WeChat Notifier 需要 Windows 10 1809 或更新版本（推荐 Windows 11）。", Native.MB_ICONERROR);
            return 3;
        }
        var missingRuntime = RuntimeBundle.Missing();
        bool installRuntime = missingRuntime.Count > 0;
        // The framework package is the big one; the others take seconds.
        string runtimeTime = missingRuntime.Any(p => p.IsFramework) ? "大约需要一分钟。" : "";

        var target = Path.Combine(InstallLayout.InstallDirectory, InstallLayout.NotifierExecutable);
        bool update = File.Exists(target);
        string runtimeNote = installRuntime
            ? $"\n\n还会为你安装 Windows App Runtime：微软提供的免费组件，WeChat Notifier 用它显示 Windows 原生通知。{runtimeTime}"
            : "";
        if (!quiet && Ask(update
                ? $"将 WeChat Notifier 更新到 {Version}？\n\n设置会保留。正在运行的 WeChat Notifier 会先退出，更新后自动重新启动。{runtimeNote}"
                : $"安装 WeChat Notifier {Version}？\n\n安装位置：{InstallLayout.InstallDirectory}\n\n它会在后台运行，微信收到新消息时显示 Windows 通知，并随 Windows 启动（可在设置里关闭）。全部在本机处理，不联网。{runtimeNote}") != Native.IDYES)
            return 1;

        void Install()
        {
            if (installRuntime) RuntimeBundle.Install(missingRuntime);
            StopRunning();
            long bytes = Extract(InstallLayout.InstallDirectory);
            Native.CreateShortcut(InstallLayout.StartMenuShortcut, target, "微信新消息的 Windows 通知");
            WriteUninstallEntry(target, bytes);
            // A start-with-Windows entry from an earlier copy (e.g. a development build) now starts this one;
            // its on/off state stays as it is.
            StartupRegistration.Retarget(target);
            using var started = Process.Start(new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = InstallLayout.InstallDirectory });
        }

        var failure = quiet
            ? ProgressWindow.RunQuietly(Install)
            : ProgressWindow.Run(Title,
                installRuntime ? $"正在安装 Windows App Runtime 和 WeChat Notifier…\n{runtimeTime}".TrimEnd() : update ? "正在更新 WeChat Notifier…" : "正在安装 WeChat Notifier…",
                Install);
        if (failure is not null)
        {
            Show(failure is RuntimeBundle.InstallFailedException
                    ? $"{failure.Message}\n\n可以从微软的网站手动安装 Windows App Runtime（x64），然后再运行本安装程序：\n{RuntimeDownload}"
                    : failure is IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception or System.Runtime.InteropServices.COMException or SecurityException
                        ? $"安装没有完成：{failure.Message}\n\n如果 WeChat Notifier 或它的设置窗口还开着，请关闭后再试。"
                        : $"安装没有完成：{failure.GetType().Name}: {failure.Message}",
                Native.MB_ICONERROR);
            return 4;
        }

        if (!quiet)
        {
            Show(update
                ? "更新完成，WeChat Notifier 已重新启动。"
                : "安装完成，WeChat Notifier 已在后台运行。\n\n单击任务栏右下角「显示隐藏的图标」里的绿色图标可以打开设置；也可以从开始菜单打开。卸载：设置 → 应用 → 已安装的应用。",
                Native.MB_ICONINFORMATION);
        }
        return 0;
    }

    /// <summary>Run as administrator (with UAC, an administrator's normal token does not count).</summary>
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Asks a running notifier to exit (the same request as <c>WeChatNotifier.exe --exit</c>) and closes an open
    /// settings window, so that their files can be replaced.
    /// </summary>
    private static void StopRunning()
    {
        if (Mutex.TryOpenExisting(ControlProtocol.HostMutex, out var mutex))
        {
            using (mutex)
            {
                if (EventWaitHandle.TryOpenExisting(ControlProtocol.ExitEvent, out var exit))
                {
                    using (exit) exit.Set();
                }
                try
                {
                    if (mutex.WaitOne(TimeSpan.FromSeconds(10))) mutex.ReleaseMutex();
                }
                catch (AbandonedMutexException)
                {
                    mutex.ReleaseMutex();
                }
            }
        }
        foreach (var settings in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstallLayout.SettingsExecutable)))
        {
            using (settings)
            {
                try
                {
                    settings.CloseMainWindow();
                    if (!settings.WaitForExit(3000)) settings.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            }
        }
    }

    /// <summary>Replaces the program folder with the files in this installer. Returns their size in bytes.</summary>
    private static long Extract(string folder)
    {
        using var payload = typeof(Program).Assembly.GetManifestResourceStream("payload.zip")
                            ?? throw new InvalidDataException("安装程序里没有程序文件（构建时没有打包）。");
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);

        // Old files go first (an older version may have had files this one no longer has); a process that is
        // still exiting can hold them for a moment.
        for (int attempt = 0; Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
        Directory.CreateDirectory(folder);
        zip.ExtractToDirectory(folder);
        return zip.Entries.Sum(e => e.Length);
    }

    private static void WriteUninstallEntry(string executable, long bytes)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InstallLayout.UninstallKey, writable: true);
        key.SetValue("DisplayName", InstallLayout.DisplayName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", InstallLayout.DisplayName);
        key.SetValue("DisplayIcon", $"\"{executable}\",0");
        key.SetValue("InstallLocation", InstallLayout.InstallDirectory);
        key.SetValue("UninstallString", $"\"{executable}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{executable}\" --uninstall --quiet");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static int Ask(string text) => Native.MessageBoxW(IntPtr.Zero, text, Title, Native.MB_YESNO | Native.MB_ICONQUESTION | Native.MB_SETFOREGROUND);

    private static void Show(string text, uint icon) => Native.MessageBoxW(IntPtr.Zero, text, Title, Native.MB_OK | icon | Native.MB_SETFOREGROUND);
}
