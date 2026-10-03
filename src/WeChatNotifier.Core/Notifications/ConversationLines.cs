namespace WeChatNotifier.Core.Notifications;

/// <summary>
/// The latest messages of each conversation, shown as the lines of its notification (2026-10-02, the user: one
/// message per line, top to bottom, at most three, first in first out): oldest on top, the newest at the bottom, at
/// most <see cref="MaxLines"/> — when a fourth arrives, the oldest goes. Messages whose content could not be read
/// (WeChat in the tray, or a burst read at once) stand together as one "[N 条新消息]" line, so a gap is never hidden.
/// Lines older than WeChat's unread counter were read meanwhile (e.g. on the phone) and are dropped; a conversation
/// read in WeChat starts over (<see cref="Clear(string)"/>). In RAM only, never written anywhere. Thread-safe.
/// </summary>
public sealed class ConversationLines
{
    /// <summary>Lines in one notification, the newest included.</summary>
    public const int MaxLines = 3;

    /// <summary>Conversations remembered at once; the one inactive longest goes first.</summary>
    internal const int MaxConversations = 200;

    /// <summary>A message's text, or — <see cref="Text"/> null — <see cref="Count"/> messages whose content is unknown.</summary>
    public readonly record struct Line(string? Text, int Count)
    {
        public bool Known => Text is not null;
    }

    private sealed class Entry
    {
        public readonly List<Line> Lines = new(MaxLines + 1); // oldest first
        public long LastUsed;
    }

    private readonly Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);
    private long _clock;

    internal int Count
    {
        get { lock (_byKey) return _byKey.Count; }
    }

    /// <summary>
    /// Records the newest event of a conversation and returns the lines its notification shows, oldest first; the
    /// last one stands for this event.
    /// </summary>
    /// <param name="key">The conversation's <see cref="ConversationKey"/>.</param>
    /// <param name="text">The newest message, or null when its content is unknown.</param>
    /// <param name="newMessages">Messages this event stands for: of a burst read at once only the newest is known.</param>
    /// <param name="unreadCount">WeChat's unread counter after this event; 0 when unknown.</param>
    public IReadOnlyList<Line> Add(string key, string? text, int newMessages, int unreadCount)
    {
        newMessages = Math.Max(1, newMessages);
        lock (_byKey)
        {
            var lines = Get(key).Lines;
            // What came before this event and is no longer counted as unread was read meanwhile.
            if (unreadCount > 0) KeepLast(lines, unreadCount - newMessages);
            if (text is null)
            {
                AppendUnknown(lines, newMessages);
            }
            else
            {
                if (newMessages > 1) AppendUnknown(lines, newMessages - 1);
                lines.Add(new Line(text, 1));
            }
            if (lines.Count > MaxLines) lines.RemoveRange(0, lines.Count - MaxLines);
            return lines.ToArray();
        }
    }

    /// <summary>The conversation was read: its next notification starts with an empty list.</summary>
    public void Clear(string key)
    {
        lock (_byKey) _byKey.Remove(key);
    }

    public void Clear()
    {
        lock (_byKey) _byKey.Clear();
    }

    private Entry Get(string key)
    {
        if (!_byKey.TryGetValue(key, out var entry))
        {
            if (_byKey.Count >= MaxConversations) _byKey.Remove(_byKey.MinBy(kv => kv.Value.LastUsed).Key);
            _byKey[key] = entry = new Entry();
        }
        entry.LastUsed = ++_clock;
        return entry;
    }

    /// <summary>Unknown messages next to each other are one line.</summary>
    private static void AppendUnknown(List<Line> lines, int count)
    {
        if (lines.Count > 0 && lines[^1] is { Known: false } last) lines[^1] = last with { Count = last.Count + count };
        else lines.Add(new Line(null, count));
    }

    /// <summary>Drops the oldest lines until the rest stand for at most <paramref name="messages"/> messages.</summary>
    private static void KeepLast(List<Line> lines, int messages)
    {
        int excess = lines.Sum(l => l.Count) - Math.Max(0, messages);
        while (excess > 0 && lines.Count > 0)
        {
            var oldest = lines[0];
            if (!oldest.Known && oldest.Count > excess)
            {
                lines[0] = oldest with { Count = oldest.Count - excess };
                return;
            }
            lines.RemoveAt(0);
            excess -= oldest.Count;
        }
    }
}
