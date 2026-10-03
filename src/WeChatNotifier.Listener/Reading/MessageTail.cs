using WeChatNotifier.Core;
using WeChatNotifier.Core.Imaging;
using WeChatNotifier.Listener.Profile;

namespace WeChatNotifier.Listener.Reading;

/// <summary>What the bottom of a conversation's message list shows.</summary>
internal enum TailState
{
    /// <summary>No message list on screen (no conversation open, another tab shown, or the window changed).</summary>
    NoList,

    /// <summary>The list has no message cell in its visible part.</summary>
    NoCells,

    /// <summary>The lowest visible cell continues below the list: scrolled up, or still scrolling to a new message.</summary>
    NotAtEnd,

    /// <summary>The lowest cell is a time separator or has no name yet: a new message is still being laid out.</summary>
    NotSettled,

    /// <summary>The list shows its end; <see cref="MessageTail.Newest"/> is the last message.</summary>
    Ready,
}

/// <summary>One child of a message list as read through UIA.</summary>
internal readonly record struct TailCell(string? ClassName, string? Name, PixelRect Rect);

/// <summary>
/// The newest message of an open conversation, known only while the list shows its end. The list
/// (mmui::RecyclerListView) recycles cells, which keep stale content outside its visible part; and when it is scrolled
/// up, the lowest fully visible cell is an older message — for an image message that would be an older picture.
/// </summary>
/// <param name="Newest">The last message when <see cref="TailState.Ready"/>, else null.</param>
/// <param name="UnknownClass">Class of the lowest cell when the profile does not know it (for the log).</param>
/// <param name="Visible">Cells in the visible part of the list.</param>
/// <param name="Refreshed">The references were captured again for this read.</param>
internal sealed record MessageTail(TailState State, MessageCell? Newest = null, string? UnknownClass = null, int Visible = 0, bool Refreshed = false)
{
    /// <summary>Pixels a cell may stick out of the list (rounding) and still count as inside.</summary>
    internal const int EdgeTolerance = 4;

    public static MessageTail NoList { get; } = new(TailState.NoList);

    public static MessageTail Of(IEnumerable<TailCell> cells, PixelRect? list)
    {
        if (list is not { IsEmpty: false } l) return NoList;
        TailCell? lowest = null;
        int visible = 0;
        foreach (var cell in cells)
        {
            if (cell.Rect.IsEmpty) continue;
            if (cell.Rect.Bottom <= l.Y + EdgeTolerance || cell.Rect.Y >= l.Bottom - EdgeTolerance) continue; // outside the visible part
            visible++;
            if (lowest is null || cell.Rect.Bottom > lowest.Value.Rect.Bottom) lowest = cell;
        }
        if (lowest is not { } last) return new MessageTail(TailState.NoCells);
        if (last.Rect.Bottom > l.Bottom + EdgeTolerance) return new MessageTail(TailState.NotAtEnd, Visible: visible);

        var type = last.ClassName is not null && WeChatUiProfile.MessageCellTypes.TryGetValue(last.ClassName, out var t) ? t : MessageCellType.Other;
        string? unknown = type == MessageCellType.Other ? last.ClassName ?? "(none)" : null;
        if (type == MessageCellType.TimeSeparator || string.IsNullOrEmpty(last.Name))
            return new MessageTail(TailState.NotSettled, UnknownClass: unknown, Visible: visible);
        var kind = type switch
        {
            MessageCellType.Text => MessageKind.Text,
            MessageCellType.Media => PreviewText.KindOfCellLabel(last.Name),
            _ => MessageKind.Unknown,
        };
        return new MessageTail(TailState.Ready, new MessageCell(kind, last.Name, last.Rect, l), unknown, visible);
    }

    /// <summary>For the log: list structure only, never message content.</summary>
    public string Describe()
    {
        string what = State switch
        {
            TailState.NoList => "no message list on screen",
            TailState.NoCells => "the message list shows no message",
            TailState.NotAtEnd => "the message list is not at its end (scrolled up, or still scrolling)",
            TailState.NotSettled => "the newest message is not laid out yet",
            _ => $"the newest message is {Newest!.Kind}",
        };
        if (UnknownClass is not null) what += $" (cell class {UnknownClass})";
        return Refreshed ? what + ", read again" : what;
    }
}
