namespace WeChatNotifier.Listener.Tracking;

/// <param name="ChangeSeen">At least one read in the burst found a change.</param>
internal sealed record BurstEnd(bool ChangeSeen, int Flashes, long StartMs, int Reads);

/// <summary>
/// Decides when to read after taskbar flashes. Phase 0 measured that WeChat flashes up to ~250 ms *before* it
/// updates the session list, and flashes 4 times per message (every ~310 ms while shown, ~625 ms while hidden).
///
/// A burst starts with the first flash and schedules reads at +60/100/150/210/300/600/1000 ms (dense where the list
/// usually changes: 2026-10-01, 87 messages, most within 100-300 ms; each read costs a few ms). Every further flash in
/// the burst adds one read 250 ms later (a second message can arrive while the button is still blinking), and
/// the burst ends with a final read 1.5 s after the last flash. Once a change was found and a following read
/// found nothing new, the remaining initial reads are dropped. No timers exist outside a burst.
/// Pure logic with an injected clock (milliseconds); owned by the listener thread.
/// </summary>
internal sealed class ReadScheduler
{
    internal static readonly int[] InitialOffsetsMs = [60, 100, 150, 210, 300, 600, 1000];
    internal const int RepeatFlashReadMs = 250;
    internal const int FinalReadMs = 1500;
    internal const int MergeWindowMs = 80;

    private readonly SortedSet<long> _due = [];
    private readonly HashSet<long> _cancellable = [];
    private long _finalReadMs;
    private int _flashes;
    private int _reads;
    private bool _changeSeen;
    private bool _quietAfterChange;

    public bool BurstActive { get; private set; }

    public long BurstStartMs { get; private set; }

    public long? NextDueMs => _due.Count > 0 ? _due.Min : null;

    public void OnFlash(long nowMs)
    {
        if (!BurstActive)
        {
            BurstActive = true;
            BurstStartMs = nowMs;
            _flashes = 0;
            _reads = 0;
            _changeSeen = false;
            _quietAfterChange = false;
            foreach (var offset in InitialOffsetsMs)
            {
                _due.Add(nowMs + offset);
                _cancellable.Add(nowMs + offset);
            }
        }
        else
        {
            AddUnlessNear(nowMs + RepeatFlashReadMs);
        }

        _flashes++;
        if (_finalReadMs != 0) _due.Remove(_finalReadMs);
        _finalReadMs = nowMs + FinalReadMs;
        _due.Add(_finalReadMs);
    }

    /// <summary>Consumes every read due at <paramref name="nowMs"/>; true when a read should run now.</summary>
    public bool TakeDue(long nowMs)
    {
        bool any = false;
        while (_due.Count > 0 && _due.Min <= nowMs)
        {
            var t = _due.Min;
            _due.Remove(t);
            _cancellable.Remove(t);
            if (t == _finalReadMs) _finalReadMs = 0;
            any = true;
        }
        return any;
    }

    /// <summary>Reports a finished read; returns the burst summary when it was the burst's last read.</summary>
    public BurstEnd? OnReadCompleted(bool changed)
    {
        if (!BurstActive) return null;
        _reads++;
        if (changed)
        {
            _changeSeen = true;
            _quietAfterChange = false;
        }
        else if (_changeSeen && !_quietAfterChange)
        {
            _quietAfterChange = true;
            foreach (var t in _cancellable) _due.Remove(t);
            _cancellable.Clear();
        }

        if (_due.Count > 0) return null;
        BurstActive = false;
        _finalReadMs = 0;
        return new BurstEnd(_changeSeen, _flashes, BurstStartMs, _reads);
    }

    public void Reset()
    {
        _due.Clear();
        _cancellable.Clear();
        _finalReadMs = 0;
        BurstActive = false;
    }

    private void AddUnlessNear(long t)
    {
        if (_due.GetViewBetween(t - MergeWindowMs, t + MergeWindowMs).Count == 0) _due.Add(t);
    }
}
