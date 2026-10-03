namespace WeChatNotifier.Core.Configuration;

/// <summary>
/// How the settings window talks to the running notifier: WM_COPYDATA to the notifier's hidden window — local
/// window messages within the user's session, no pipes or sockets. The command travels in
/// COPYDATASTRUCT.dwData; a status request names the window to answer in wParam, and the notifier answers with
/// <see cref="StatusReply"/> carrying a <see cref="StatusSnapshot"/> as UTF-8 JSON.
/// </summary>
public static class ControlProtocol
{
    /// <summary>Window class of the notifier's hidden window (one per session).</summary>
    public const string WindowClass = "WeChatNotifier.Tray";

    /// <summary>Title of the settings window, also used to bring an open one to the front.</summary>
    public const string SettingsWindowTitle = "WeChat Notifier 设置";

    /// <summary>One settings window per session.</summary>
    public const string SettingsMutex = @"Local\WeChatNotifier.Settings";

    /// <summary>One notifier per session (two would show every message twice); held by the notifier and the console host.</summary>
    public const string HostMutex = @"Local\WeChatNotifier.Host";

    /// <summary>Set by a second start of the notifier when the settings window cannot be opened.</summary>
    public const string ShowEvent = @"Local\WeChatNotifier.Show";

    /// <summary>Set by <c>--exit</c>, the installer and the uninstaller: the running notifier exits.</summary>
    public const string ExitEvent = @"Local\WeChatNotifier.Exit";

    private const long Base = 0x574E_0000; // "WN"

    /// <summary>settings.json was written: load and apply it.</summary>
    public const long ReloadSettings = Base + 1;

    public const long TestNotification = Base + 2;

    public const long ClearImageCache = Base + 3;

    public const long RestartListener = Base + 4;

    /// <summary>Answer with <see cref="StatusReply"/> to the window in wParam.</summary>
    public const long StatusRequest = Base + 5;

    public const long StatusReply = Base + 0x105;
}
