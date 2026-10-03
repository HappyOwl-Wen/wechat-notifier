namespace WeChatNotifier.Core.Configuration;

/// <summary>
/// Where the installer puts WeChat Notifier — for the current user only, no admin rights — and what it registers.
/// Shared by the installer and by <c>WeChatNotifier.exe --uninstall</c>.
/// </summary>
public static class InstallLayout
{
    public const string DisplayName = "WeChat Notifier";

    public const string NotifierExecutable = "WeChatNotifier.exe";

    public const string SettingsExecutable = "WeChatNotifier.Settings.exe";

    /// <summary>Per-user uninstall entry: listed in Settings → Apps → Installed apps.</summary>
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WeChatNotifier";

    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WeChatNotifier");

    public static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), DisplayName + ".lnk");

    /// <summary>Whether <paramref name="executable"/> lives in the install directory (and the folder may be removed on uninstall).</summary>
    public static bool IsInstalledCopy(string executable) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(executable))?.TrimEnd(Path.DirectorySeparatorChar),
            InstallDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
