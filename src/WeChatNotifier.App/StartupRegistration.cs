using System.Security;
using Microsoft.Win32;
using WeChatNotifier.Core.Configuration;

namespace WeChatNotifier.App;

internal enum StartupState
{
    Off,
    On,

    /// <summary>Registered, but switched off by the user in Task Manager → Startup apps (or Settings → Apps → Startup).</summary>
    DisabledInWindows,
}

/// <summary>
/// Start with Windows through the standard per-user Run key (spec §12): no admin rights, listed and switchable in
/// Task Manager → Startup apps. Nothing about WeChat is touched; the notifier waits for WeChat itself. Windows'
/// own on/off switch for the entry is respected: the notifier never turns itself back on. Shared with the settings
/// window, which passes the notifier's executable.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "WeChatNotifier";

    public static StartupState State()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string) return StartupState.Off;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return StartupCommand.IsDisabledByUser(approved?.GetValue(ValueName) as byte[]) ? StartupState.DisabledInWindows : StartupState.On;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return StartupState.Off;
        }
    }

    /// <summary>
    /// The user's choice (menu, settings, first run): writes or removes the Run value. Switching on also clears
    /// Windows' "disabled" mark — this is the user turning it back on. Returns false when the registry refused.
    /// </summary>
    public static bool Apply(bool enabled, string executable)
    {
        try
        {
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
            {
                if (enabled) run.SetValue(ValueName, StartupCommand.For(executable), RegistryValueKind.String);
                else run.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Points an existing entry at <paramref name="executable"/> (the installer: the installed copy replaces a
    /// development build), keeping Windows' on/off state. No entry: nothing happens.
    /// </summary>
    public static bool Retarget(string executable)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(ValueName) is not string) return false;
            run.SetValue(ValueName, StartupCommand.For(executable), RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The registered executable no longer exists (moved, reinstalled elsewhere): point the entry at this one,
    /// keeping its on/off state. An entry for another copy that still exists is left alone, so two copies never
    /// fight over it.
    /// </summary>
    public static bool RepairIfMoved(string executable)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(ValueName) is not string command) return false;
            if (StartupCommand.ExecutableOf(command) is { } exe && File.Exists(exe)) return false;
            run.SetValue(ValueName, StartupCommand.For(executable), RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
