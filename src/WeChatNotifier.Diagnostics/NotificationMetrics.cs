namespace WeChatNotifier.Diagnostics;

/// <summary>Counters and latencies of the Windows notification side. Numbers only.</summary>
public sealed class NotificationMetrics
{
    private readonly LatencyStat _showCall = new();
    private readonly LatencyStat _endToEnd = new();
    private long _shown;
    private long _failed;
    private long _removed;
    private long _clicks;
    private long _lastShownUtcTicks;

    /// <param name="showCall">Building the notification plus the AppNotificationManager.Show call.</param>
    /// <param name="endToEnd">Trigger (taskbar flash) → notification handed to Windows, when known.</param>
    public void Shown(TimeSpan showCall, TimeSpan? endToEnd)
    {
        Interlocked.Increment(ref _shown);
        Interlocked.Exchange(ref _lastShownUtcTicks, DateTime.UtcNow.Ticks);
        _showCall.Add(showCall);
        if (endToEnd is { } e) _endToEnd.Add(e);
    }

    public void Failed() => Interlocked.Increment(ref _failed);

    public void Removed() => Interlocked.Increment(ref _removed);

    public void Clicked() => Interlocked.Increment(ref _clicks);

    public NotificationMetricsSnapshot Snapshot()
    {
        long last = Interlocked.Read(ref _lastShownUtcTicks);
        return new NotificationMetricsSnapshot(
            Shown: Interlocked.Read(ref _shown),
            Failed: Interlocked.Read(ref _failed),
            Removed: Interlocked.Read(ref _removed),
            Clicks: Interlocked.Read(ref _clicks),
            ShowCall: _showCall.Snapshot(),
            EndToEnd: _endToEnd.Snapshot(),
            LastShownUtc: last == 0 ? null : new DateTime(last, DateTimeKind.Utc));
    }

    private sealed class LatencyStat
    {
        private readonly object _gate = new();
        private long _count;
        private double _totalMs;
        private double _lastMs;
        private double _maxMs;

        public void Add(TimeSpan value)
        {
            double ms = value.TotalMilliseconds;
            lock (_gate)
            {
                _count++;
                _totalMs += ms;
                _lastMs = ms;
                if (ms > _maxMs) _maxMs = ms;
            }
        }

        public LatencySnapshot? Snapshot()
        {
            lock (_gate) return _count == 0 ? null : new LatencySnapshot(_count, _lastMs, _totalMs / _count, _maxMs);
        }
    }
}

public sealed record LatencySnapshot(long Samples, double LastMs, double AverageMs, double MaxMs);

public sealed record NotificationMetricsSnapshot(
    long Shown,
    long Failed,
    long Removed,
    long Clicks,
    LatencySnapshot? ShowCall,
    LatencySnapshot? EndToEnd,
    DateTime? LastShownUtc);
