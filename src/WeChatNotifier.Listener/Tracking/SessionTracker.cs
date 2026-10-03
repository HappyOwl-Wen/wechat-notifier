using WeChatNotifier.Listener.Profile;

namespace WeChatNotifier.Listener.Tracking;

/// <param name="NewMessages">Unread delta (at least 1).</param>
/// <param name="Muted">Muted flag remembered from the last read that could see it (hidden reads cannot).</param>
/// <param name="ReadEpoch">
/// How often this conversation was read (unread counter back to 0) since it was first seen. WeChat's counter starts
/// again from 0 after a read, so "unread 2, [图片], 23:12" can describe two different messages of the same minute;
/// the epoch tells them apart in the de-duplication fingerprint.
/// </param>
/// <param name="PreviousPreview">For the open conversation (counter does not grow): the preview before this change, so
/// that "A, B, A" within one minute gives three fingerprints. Only ever hashed.</param>
internal sealed record SessionChange(SessionCell Cell, int NewMessages, string Reason, bool Muted, int ReadEpoch = 0, string? PreviousPreview = null);

/// <summary>
/// Remembers the last known state of every conversation seen in the session list and turns re-reads into
/// "this conversation got new messages". Pure logic, no UIA — unit-tested with the name formats observed in
/// Phase 0. Rows moving up or down (re-sorting) never produce changes: state is keyed by conversation, not row.
/// </summary>
internal sealed class SessionTracker
{
    internal const int MaxConversations = 500;

    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private long _tick;

    public bool HasBaseline { get; private set; }

    public int Count => _states.Count;

    /// <param name="cells">Rows from one read.</param>
    /// <param name="contentVisible">
    /// The names carry previews (fresh read). Hidden-window reads lack preview/time/muted lines, so those
    /// fields keep their previous values instead of being overwritten with "nothing".
    /// </param>
    /// <param name="openConversation">Conversation open in the main window; its unread counter may not grow.</param>
    /// <param name="detect">False only (re)builds the baseline, e.g. when the window is shown.</param>
    /// <param name="read">
    /// Receives conversations whose unread counter went back to 0 (read in WeChat, here or on the phone), so
    /// their notifications can be withdrawn. Reported whether or not <paramref name="detect"/> is set.
    /// </param>
    public List<SessionChange> Apply(IReadOnlyList<SessionCell> cells, bool contentVisible, string? openConversation, bool detect,
        List<string>? read = null)
    {
        _tick++;
        var changes = new List<SessionChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in cells)
        {
            if (cell.Title.Length == 0 || !seen.Add(cell.Title)) continue;

            if (!_states.TryGetValue(cell.Title, out var st))
            {
                st = new State();
                _states[cell.Title] = st;
                Update(st, cell, contentVisible);
                // A conversation scrolled into the visible rows because it just received messages.
                if (detect && HasBaseline && cell.Unread > 0)
                    changes.Add(new SessionChange(cell, cell.Unread, "appeared-with-unread", st.Muted));
                continue;
            }

            if (st.Unread > 0 && cell.Unread == 0)
            {
                read?.Add(cell.Title);
                st.ReadEpoch++;
            }

            SessionChange? change = null;
            if (detect)
            {
                if (cell.Unread > st.Unread)
                {
                    change = new SessionChange(cell, cell.Unread - st.Unread, "unread-increase", st.Muted, st.ReadEpoch);
                }
                else if (cell.Unread > 0 && cell.Unread < st.Unread && !(contentVisible && PreviewText.Analyze(cell.Preview).IsSystemNotice))
                {
                    // The counter went back to 0 unseen — read on the phone, or in WeChat right before it went to the
                    // tray — and grew again: these are new messages (a recalled unread message shows as a notice).
                    st.ReadEpoch++;
                    change = new SessionChange(cell, cell.Unread, "unread-after-read", st.Muted, st.ReadEpoch);
                }
                else if (contentVisible
                         && cell.Unread == st.Unread
                         && cell.Title == openConversation
                         && st.Preview is not null && cell.Preview is not null
                         && (cell.Preview != st.Preview || cell.Time != st.Time))
                {
                    // The open conversation's counter does not grow; its preview does.
                    var info = PreviewText.Analyze(cell.Preview);
                    if (!info.IsDraft && !info.IsSystemNotice)
                        change = new SessionChange(cell, 1, "open-chat-preview", st.Muted || cell.Muted, st.ReadEpoch, st.Preview);
                }
            }

            Update(st, cell, contentVisible);
            if (change is not null) changes.Add(change with { Muted = st.Muted });
        }

        if (cells.Count > 0) HasBaseline = true;
        Trim();
        return changes;
    }

    public void Reset()
    {
        _states.Clear();
        HasBaseline = false;
    }

    private void Update(State st, SessionCell cell, bool contentVisible)
    {
        st.Unread = cell.Unread;
        st.LastSeen = _tick;
        if (!contentVisible) return;
        st.Preview = cell.Preview;
        st.Time = cell.Time;
        st.Muted = cell.Muted;
    }

    private void Trim()
    {
        if (_states.Count <= MaxConversations) return;
        foreach (var key in _states.OrderBy(kv => kv.Value.LastSeen).Take(_states.Count - MaxConversations).Select(kv => kv.Key).ToList())
            _states.Remove(key);
    }

    private sealed class State
    {
        public int Unread;
        public string? Preview;
        public string? Time;
        public bool Muted;
        public long LastSeen;
        public int ReadEpoch;
    }
}
