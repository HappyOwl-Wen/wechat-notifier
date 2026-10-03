namespace WeChatNotifier.Listener;

/// <summary>Listener behaviour; a record, so a settings change can tell whether the listener must restart.</summary>
public sealed record ListenerOptions
{
    /// <summary>
    /// Report messages while a WeChat window is the foreground window (spec §15, default off). WeChat does not
    /// flash its taskbar button while it is in the foreground, so this only affects flashes that still arrive.
    /// </summary>
    public bool NotifyWhileWeChatFocused { get; init; }

    /// <summary>Report conversations muted in WeChat (消息免打扰). Default off, like WeChat itself.</summary>
    public bool NotifyMutedConversations { get; init; }

    /// <summary>Read the full text from the message list when the conversation is open in a shown window.</summary>
    public bool ReadFullTextWhenOpen { get; init; } = true;

    /// <summary>
    /// After a click on a notification, also open its conversation through UI Automation (spec §17, option 3;
    /// verified, falls back to just showing WeChat). Off: only bring WeChat to the front.
    /// </summary>
    public bool OpenConversationOnClick { get; init; } = true;

    /// <summary>
    /// Copy conversation avatars from WeChat's window while it is on screen (spec §6; Level 3). Off: notifications
    /// use generated initials, and kept copies are deleted.
    /// </summary>
    public bool CaptureAvatars { get; init; } = true;

    /// <summary>
    /// Where copied avatars outlive a restart (PNG files named by the name-free conversation key, deleted after 30
    /// days without a new copy), so that notifications show real avatars right after a restart even while WeChat is
    /// minimized or in the tray. Null: RAM only. Read when the listener starts.
    /// </summary>
    public string? AvatarFolder { get; init; }

    /// <summary>
    /// Copy a received picture from its bubble when the conversation is open on screen (spec §7, Level 3).
    /// Off: picture messages show "[图片]".
    /// </summary>
    public bool CaptureImagePreviews { get; init; } = true;
}

public enum ListenerState
{
    Starting,
    WaitingForWeChat,
    Attached,
    Stopped,
}

public enum WeChatWindowState
{
    NotFound,
    Visible,
    Minimized,
    Hidden,
}

/// <summary>Component state for the diagnostics page (no chat data).</summary>
public sealed record ListenerStatus(
    ListenerState State,
    int? WeChatPid,
    string? WeChatVersion,
    WeChatWindowState WindowState,
    bool ReferencesHeld,
    int HeldSessionCells,
    DateTime? ReferencesCapturedUtc,
    bool ShellHookActive,
    int TrackedConversations,
    int ChatWindows);
