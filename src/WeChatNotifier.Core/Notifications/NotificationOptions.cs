namespace WeChatNotifier.Core.Notifications;

/// <summary>User-facing notification settings (spec §18 "Notifications"); the settings UI arrives in PHASE 5.</summary>
public sealed class NotificationOptions
{
    /// <summary>Show the message text. Off: only the conversation name and "新消息" are shown.</summary>
    public bool ShowMessagePreview { get; init; } = true;

    /// <summary>Play the Windows instant-message sound (Windows' own per-app sound setting still applies).</summary>
    public bool PlaySound { get; init; } = true;

    /// <summary>Keep the banner on screen for ~25 s instead of Windows' default ~5 s.</summary>
    public bool LongDuration { get; init; }

    /// <summary>
    /// How long a notification stays in Notification Center. Windows keeps the text in its notification database
    /// until then (or until the next reboot, or until the conversation is read), never longer.
    /// </summary>
    public TimeSpan Expiration { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Remove a conversation's notification once WeChat shows it as read (unread counter back to 0).</summary>
    public bool RemoveWhenRead { get; init; } = true;

    /// <summary>
    /// Round avatar on message notifications (spec §6): the conversation's avatar copied from WeChat's window, or
    /// generated initials when none could be copied (or <see cref="StoreImageCache"/> is off).
    /// </summary>
    public bool ShowAvatars { get; init; } = true;

    /// <summary>Show a received picture inside the notification when it could be copied (spec §7).</summary>
    public bool ShowImagePreview { get; init; } = true;

    /// <summary>
    /// Allow copied avatars and pictures to be written to the temporary image cache
    /// (%LOCALAPPDATA%\WeChatNotifier\cache): Windows shows notification images only from files. Files are deleted
    /// with their notification, at the latest after <see cref="Expiration"/> or a restart. Off: no chat images on
    /// disk at all (generated initials only).
    /// </summary>
    public bool StoreImageCache { get; init; } = true;

    /// <summary>
    /// The notifications' header shows "微信" and WeChat's own icon (read from the installed Weixin.exe) instead of
    /// "WeChat Notifier", like the notifications of a messenger app (spec §6: close to WhatsApp's). Windows reads the
    /// name and icon once per sign-in, so a change shows after the next one.
    /// </summary>
    public bool AppearAsWeChat { get; init; } = true;
}
