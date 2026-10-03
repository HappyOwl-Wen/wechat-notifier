using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using Microsoft.Win32;
using WeChatNotifier.Core.Configuration;
using WeChatNotifier.Listener;
using WeChatNotifier.Notifications;

namespace WeChatNotifier.App;

/// <summary>
/// <c>WeChatNotifier.exe --uninstall</c>, the uninstall command the installer registers (Settings → Apps →
/// Installed apps). Removes everything the notifier and the installer set up; the program folder only when this is
/// the installed copy, never a development build. Settings and logs only when the user agrees.
/// </summary>
internal static class Uninstaller
{
    private const uint MB_YESNO = 0x4, MB_ICONQUESTION = 0x20;
    private const int IDYES = 6;

    public static int Run(bool quiet)
    {
        if (!quiet && Ask("要卸载 WeChat Notifier 吗？\n\n会删除程序、开机启动项、开始菜单项和它发出的所有通知。") != IDYES) return 1;

        Program.RequestExit();
        CloseSettingsWindow();
        WindowsNotifier.RemoveRegistration();              // registration, notifications, notification images
        WeChatListener.DeleteSavedAvatars(SettingsStore.AvatarFolder); // copied avatars are temporary: always removed
        StartupRegistration.Apply(false, Environment.ProcessPath!);
        Native.WerRemoveExcludedApplication(Path.GetFileName(Environment.ProcessPath!), false);
        TryDeleteFile(InstallLayout.StartMenuShortcut);
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(InstallLayout.UninstallKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }

        if (!quiet && Ask("同时删除设置和日志吗？\n\n" + SettingsStore.Folder) == IDYES)
        {
            try
            {
                if (Directory.Exists(SettingsStore.Folder)) Directory.Delete(SettingsStore.Folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        if (InstallLayout.IsInstalledCopy(Environment.ProcessPath!)) RemoveFolderAfterExit(InstallLayout.InstallDirectory);
        if (!quiet) Native.MessageBoxW(IntPtr.Zero, "WeChat Notifier 已卸载。", "WeChat Notifier", Native.MB_OK | Native.MB_ICONINFORMATION);
        return 0;
    }

    private static int Ask(string text) =>
        Native.MessageBoxW(IntPtr.Zero, text, "卸载 WeChat Notifier", MB_YESNO | MB_ICONQUESTION | Native.MB_SETFOREGROUND);

    /// <summary>The settings window runs from the program folder: close it (it saves on every change, nothing is lost).</summary>
    private static void CloseSettingsWindow()
    {
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

    /// <summary>This process runs from the folder, so a short-lived command deletes it once this process has exited.</summary>
    private static void RemoveFolderAfterExit(string folder)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{folder}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            });
        }
        catch (Win32Exception) { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
