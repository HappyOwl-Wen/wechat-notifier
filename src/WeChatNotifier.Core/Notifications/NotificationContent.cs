namespace WeChatNotifier.Core.Notifications;

public enum NotificationKind
{
    /// <summary>A message (or several) in a known conversation.</summary>
    Message,

    /// <summary>WeChat signalled a message but nothing readable said where: "微信 · 新消息".</summary>
    Generic,

    /// <summary>Diagnostics: proves that notifications reach the screen.</summary>
    Test,
}

/// <summary>
/// The text of one Windows notification, independent of the Windows App SDK (rendered by
/// WeChatNotifier.Notifications). Built by <see cref="NotificationComposer"/>; lives in RAM only.
/// </summary>
public sealed record NotificationContent
{
    public required NotificationKind Kind { get; init; }

    /// <summary>
    /// Replacement key: a newer notification with the same tag replaces the older one (one per conversation).
    /// For <see cref="NotificationKind.Message"/> it is the <see cref="ConversationKey"/>, which a click passes
    /// back so the listener can open that conversation; no name is kept anywhere for that.
    /// </summary>
    public required string Tag { get; init; }

    public required string Group { get; init; }

    public required string Title { get; init; }

    /// <summary>The newest message (the bottom line).</summary>
    public required string Body { get; init; }

    /// <summary>
    /// The conversation's earlier messages above <see cref="Body"/>, oldest first, one line each
    /// (<see cref="ConversationLines"/>); empty for a single message.
    /// </summary>
    public IReadOnlyList<string> EarlierLines { get; init; } = [];

    /// <summary>Small grey line under the body, e.g. "3 条未读".</summary>
    public string? Attribution { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Windows drops the notification from Notification Center at this time (and at the next reboot).</summary>
    public required DateTimeOffset Expires { get; init; }

    public bool Silent { get; init; }

    public bool LongDuration { get; init; }

    /// <summary>Copied avatar to show (circle-cropped by Windows); null: generate initials from <see cref="AvatarText"/>.</summary>
    public Imaging.Bitmap32? Avatar { get; init; }

    /// <summary>Name whose initial is drawn when there is no copied avatar; null: no avatar at all (app icon only).</summary>
    public string? AvatarText { get; init; }

    /// <summary>Picture preview shown inside the notification.</summary>
    public Imaging.Bitmap32? Image { get; init; }

    /// <summary>Listener-side latency (trigger → detection), for the end-to-end latency metric.</summary>
    public TimeSpan? TriggerToDetect { get; init; }

    public DateTimeOffset? DetectedAt { get; init; }
}
