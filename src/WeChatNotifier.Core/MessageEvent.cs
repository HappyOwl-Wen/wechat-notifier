namespace WeChatNotifier.Core;

/// <summary>Where the content of a <see cref="MessageEvent"/> came from; decides how much of it exists.</summary>
public enum ContentSource
{
    /// <summary>Nothing could be read (e.g. WeChat was never shown since it started): a generic "new message".</summary>
    None = 0,

    /// <summary>Conversation and unread count only: WeChat hides previews while its window is in the tray.</summary>
    SessionMetadata,

    /// <summary>The conversation-list preview. WeChat elides long previews to the column width ("…").</summary>
    SessionPreview,

    /// <summary>Full text of the newest message, read from an open conversation.</summary>
    MessageList,
}

/// <summary>One detected incoming message (or a burst of them for one conversation). Lives in RAM only.</summary>
public sealed record MessageEvent
{
    /// <summary>Conversation (contact or group) display name; null when WeChat gave no way to tell.</summary>
    public required string? Conversation { get; init; }

    /// <summary>
    /// For generic events (<see cref="Conversation"/> is null): the conversation that most likely received the
    /// message, e.g. the one open when WeChat was closed to the tray. A guess, never presented as fact.
    /// </summary>
    public string? ConversationHint { get; init; }

    public required MessageKind Kind { get; init; }

    /// <summary>
    /// Message text as WeChat shows it: the text itself, or its placeholder such as "[图片]" for non-text
    /// messages. Group messages keep WeChat's own "sender: text" form. Null when not readable.
    /// </summary>
    public string? Text { get; init; }

    public required ContentSource Source { get; init; }

    /// <summary>WeChat's unread counter for the conversation after this message; 0 when unknown.</summary>
    public int UnreadCount { get; init; }

    /// <summary>How many new messages this event stands for (unread delta); at least 1.</summary>
    public int NewMessages { get; init; } = 1;

    /// <summary>The preview carried WeChat's "[有人@我]" marker.</summary>
    public bool Mentioned { get; init; }

    /// <summary>
    /// Sender parsed from a group preview ("Tom: text"); a label only — <see cref="Text"/> keeps WeChat's
    /// "sender: text", which is what notifications show for group messages (spec §6).
    /// </summary>
    public string? Sender { get; init; }

    /// <summary>
    /// A group chat, as far as WeChat's previews tell: this preview names a sender, or the conversation was seen
    /// with two different senders this run. UIA exposes no explicit group flag.
    /// </summary>
    public bool IsGroup { get; init; }

    /// <summary>The conversation's avatar, captured from WeChat's visible window (now or earlier this run).</summary>
    public Imaging.Bitmap32? Avatar { get; init; }

    /// <summary>Preview of an image message, captured from its bubble while the conversation was on screen.</summary>
    public Imaging.Bitmap32? Image { get; init; }

    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>First trigger (taskbar flash) to detection.</summary>
    public TimeSpan? TriggerToDetect { get; init; }

    /// <summary>Stable key used for de-duplication.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Diagnostic reason, e.g. "unread-increase", "open-chat-preview", "unexplained-flash".</summary>
    public string? Reason { get; init; }

    public bool ContentAvailable => Source >= ContentSource.SessionPreview && Text is not null;
}
