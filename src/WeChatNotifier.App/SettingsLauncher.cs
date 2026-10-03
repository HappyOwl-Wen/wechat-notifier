using System.ComponentModel;
using System.Diagnostics;
using WeChatNotifier.Core.Configuration;
using WeChatNotifier.Diagnostics;

namespace WeChatNotifier.App;

/// <summary>
/// Opens the settings window (WeChatNotifier.Settings.exe, WinUI 3, a separate process that exists only while the
/// window is open), or brings an open one to the front. Call it from a process that may take the foreground —
/// right after a click on the tray icon or menu, or in a process the user just started — so that the window
/// comes up in front instead of only flashing in the taskbar.
/// </summary>
internal static class SettingsLauncher
{
    private const string ExecutableName = "WeChatNotifier.Settings.exe";

    /// <returns>False when the settings program is missing or could not start.</returns>
    public static bool Open(DiagnosticLog log)
    {
        var open = Native.FindWindowW(null, ControlProtocol.SettingsWindowTitle);
        if (open != IntPtr.Zero)
        {
            if (Native.IsIconic(open)) Native.ShowWindow(open, Native.SW_RESTORE);
            Native.SetForegroundWindow(open);
            return true;
        }
        if (Executable() is not { } exe)
        {
            log.Warn("app", $"{ExecutableName} not found");
            return false;
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            log.Info("app", "settings window started");
            return true;
        }
        catch (Win32Exception ex)
        {
            log.Warn("app", $"{ExecutableName} could not start 0x{ex.HResult:X8}");
            return false;
        }
    }

    /// <summary>Next to this executable (installed); in a development checkout, the settings project's build output.</summary>
    private static string? Executable()
    {
        var here = Path.Combine(AppContext.BaseDirectory, ExecutableName);
        if (File.Exists(here)) return here;
        // src\WeChatNotifier.App\bin\<configuration>\<framework>\  →  src\WeChatNotifier.Settings\bin\<configuration>\…
        var framework = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = framework.Parent;
        var src = configuration?.Parent?.Parent?.Parent;
        if (configuration is null || src is null) return null;
        var dev = Path.Combine(src.FullName, "WeChatNotifier.Settings", "bin", configuration.Name);
        return Directory.Exists(dev) ? Directory.EnumerateFiles(dev, ExecutableName, SearchOption.AllDirectories).FirstOrDefault() : null;
    }
}
