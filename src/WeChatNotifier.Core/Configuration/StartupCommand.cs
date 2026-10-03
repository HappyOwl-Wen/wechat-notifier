namespace WeChatNotifier.Core.Configuration;

/// <summary>
/// The command line the notifier writes to the per-user Run key, and the "StartupApproved" mark Windows keeps
/// next to it when the user switches the entry off in Task Manager → Startup apps or Settings → Apps → Startup.
/// Pure logic; the registry access lives in the app. Also compiled into the installer, which targets the .NET
/// Framework 4.7.2 built into Windows: plain Substring/Split only.
/// </summary>
public static class StartupCommand
{
    /// <summary>Tells the app it was started by Windows at sign-in (no "running in the background" notification).</summary>
    public const string AutostartArgument = "--autostart";

    public static string For(string executable) => $"\"{executable}\" {AutostartArgument}";

    /// <summary>The executable of a Run command line: a quoted path, or an unquoted path up to ".exe".</summary>
    public static string? ExecutableOf(string? command)
    {
        var c = command?.Trim();
        if (c is not { Length: > 0 }) return null;
        if (c[0] == '"')
        {
            int end = c.IndexOf('"', 1);
            return end > 1 ? c.Substring(1, end - 1) : null;
        }
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c.Substring(0, exe + 4) : c.Split([' '], 2)[0];
    }

    /// <summary>
    /// Whether the user switched the entry off in Windows. Windows stores a 12-byte value per entry whose first
    /// byte is even while the entry is enabled (02, 06) and odd once it was disabled (03, 07), followed by the time
    /// of the change. No value means enabled.
    /// </summary>
    public static bool IsDisabledByUser(byte[]? approved) => approved is { Length: > 0 } && (approved[0] & 1) != 0;
}
