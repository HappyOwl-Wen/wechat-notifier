namespace WeChatNotifier.Diagnostics;

/// <summary>Counters and latencies of the listener. Numbers only; safe to show on a diagnostics page.</summary>
public sealed class ListenerMetrics
{
    private long _flashes;
    private long _foreignFlashes;
    private long _bursts;
    private long _reads;
    private long _readFailures;
    private long _captures;
    private long _captureFailures;
    private long _events;
    private long _eventsWithoutContent;
    private long _unexplainedFlashes;
    private long _duplicatesDropped;
    private long _suppressedMuted;
    private long _suppressedFocused;
    private long _attaches;
    private long _detaches;
    private long _openRequests;
    private long _wechatLaunches;
    private long _trayIconClicks;
    private long _avatarsCaptured;
    private long _picturesCaptured;
    private long _pictureMisses;
    private long _picturesAfterWait;
    private long _picturesNotLoaded;
    private long _conversationsOpened;
    private long _conversationOpenFailures;
    private long _lastLatencyMicros = -1;
    private long _latencyMicrosTotal;
    private long _latencySamples;
    private long _maxLatencyMicros;
    private long _lastTriggerUtcTicks;
    private long _lastEventUtcTicks;

    public void Flash() { Interlocked.Increment(ref _flashes); Interlocked.Exchange(ref _lastTriggerUtcTicks, DateTime.UtcNow.Ticks); }
    public void ForeignFlash() => Interlocked.Increment(ref _foreignFlashes);
    public void Burst() => Interlocked.Increment(ref _bursts);
    public void Read(bool ok)
    {
        if (ok) Interlocked.Increment(ref _reads);
        else Interlocked.Increment(ref _readFailures);
    }

    public void Capture(bool ok)
    {
        if (ok) Interlocked.Increment(ref _captures);
        else Interlocked.Increment(ref _captureFailures);
    }
    public void UnexplainedFlash() => Interlocked.Increment(ref _unexplainedFlashes);
    public void DuplicateDropped() => Interlocked.Increment(ref _duplicatesDropped);
    public void SuppressedMuted() => Interlocked.Increment(ref _suppressedMuted);
    public void SuppressedFocused() => Interlocked.Increment(ref _suppressedFocused);
    public void Attached() => Interlocked.Increment(ref _attaches);
    public void Detached() => Interlocked.Increment(ref _detaches);
    public void OpenRequested() => Interlocked.Increment(ref _openRequests);
    public void WeChatLaunched() => Interlocked.Increment(ref _wechatLaunches);
    public void TrayIconClicked() => Interlocked.Increment(ref _trayIconClicks);
    public void AvatarsCaptured(int count) => Interlocked.Add(ref _avatarsCaptured, count);

    public void PictureCaptured(bool found)
    {
        if (found) Interlocked.Increment(ref _picturesCaptured);
        else Interlocked.Increment(ref _pictureMisses);
    }

    /// <summary>An image message was held for its thumbnail: it loaded in time, or not.</summary>
    public void PictureWaited(bool found)
    {
        if (found) Interlocked.Increment(ref _picturesAfterWait);
        else Interlocked.Increment(ref _picturesNotLoaded);
    }

    public void ConversationOpened(bool ok)
    {
        if (ok) Interlocked.Increment(ref _conversationsOpened);
        else Interlocked.Increment(ref _conversationOpenFailures);
    }

    public void Event(bool contentAvailable, TimeSpan? triggerToDetect)
    {
        Interlocked.Increment(ref _events);
        if (!contentAvailable) Interlocked.Increment(ref _eventsWithoutContent);
        Interlocked.Exchange(ref _lastEventUtcTicks, DateTime.UtcNow.Ticks);
        if (triggerToDetect is not { } t) return;
        long us = (long)(t.TotalMilliseconds * 1000);
        Interlocked.Exchange(ref _lastLatencyMicros, us);
        Interlocked.Add(ref _latencyMicrosTotal, us);
        Interlocked.Increment(ref _latencySamples);
        long max;
        while (us > (max = Interlocked.Read(ref _maxLatencyMicros)) && Interlocked.CompareExchange(ref _maxLatencyMicros, us, max) != max) { }
    }

    public MetricsSnapshot Snapshot()
    {
        long samples = Interlocked.Read(ref _latencySamples);
        long last = Interlocked.Read(ref _lastLatencyMicros);
        long lastTrigger = Interlocked.Read(ref _lastTriggerUtcTicks);
        long lastEvent = Interlocked.Read(ref _lastEventUtcTicks);
        return new MetricsSnapshot(
            Flashes: Interlocked.Read(ref _flashes),
            ForeignFlashes: Interlocked.Read(ref _foreignFlashes),
            Bursts: Interlocked.Read(ref _bursts),
            Reads: Interlocked.Read(ref _reads),
            ReadFailures: Interlocked.Read(ref _readFailures),
            Captures: Interlocked.Read(ref _captures),
            CaptureFailures: Interlocked.Read(ref _captureFailures),
            Events: Interlocked.Read(ref _events),
            EventsWithoutContent: Interlocked.Read(ref _eventsWithoutContent),
            UnexplainedFlashes: Interlocked.Read(ref _unexplainedFlashes),
            DuplicatesDropped: Interlocked.Read(ref _duplicatesDropped),
            SuppressedMuted: Interlocked.Read(ref _suppressedMuted),
            SuppressedFocused: Interlocked.Read(ref _suppressedFocused),
            Attaches: Interlocked.Read(ref _attaches),
            Detaches: Interlocked.Read(ref _detaches),
            OpenRequests: Interlocked.Read(ref _openRequests),
            WeChatLaunches: Interlocked.Read(ref _wechatLaunches),
            TrayIconClicks: Interlocked.Read(ref _trayIconClicks),
            AvatarsCaptured: Interlocked.Read(ref _avatarsCaptured),
            PicturesCaptured: Interlocked.Read(ref _picturesCaptured),
            PictureMisses: Interlocked.Read(ref _pictureMisses),
            PicturesAfterWait: Interlocked.Read(ref _picturesAfterWait),
            PicturesNotLoaded: Interlocked.Read(ref _picturesNotLoaded),
            ConversationsOpened: Interlocked.Read(ref _conversationsOpened),
            ConversationOpenFailures: Interlocked.Read(ref _conversationOpenFailures),
            LastLatencyMs: last < 0 ? null : last / 1000.0,
            AverageLatencyMs: samples == 0 ? null : Interlocked.Read(ref _latencyMicrosTotal) / 1000.0 / samples,
            MaxLatencyMs: samples == 0 ? null : Interlocked.Read(ref _maxLatencyMicros) / 1000.0,
            LastTriggerUtc: lastTrigger == 0 ? null : new DateTime(lastTrigger, DateTimeKind.Utc),
            LastEventUtc: lastEvent == 0 ? null : new DateTime(lastEvent, DateTimeKind.Utc));
    }
}

public sealed record MetricsSnapshot(
    long Flashes,
    long ForeignFlashes,
    long Bursts,
    long Reads,
    long ReadFailures,
    long Captures,
    long CaptureFailures,
    long Events,
    long EventsWithoutContent,
    long UnexplainedFlashes,
    long DuplicatesDropped,
    long SuppressedMuted,
    long SuppressedFocused,
    long Attaches,
    long Detaches,
    long OpenRequests,
    long WeChatLaunches,
    long TrayIconClicks,
    long AvatarsCaptured,
    long PicturesCaptured,
    long PictureMisses,
    long PicturesAfterWait,
    long PicturesNotLoaded,
    long ConversationsOpened,
    long ConversationOpenFailures,
    double? LastLatencyMs,
    double? AverageLatencyMs,
    double? MaxLatencyMs,
    DateTime? LastTriggerUtc,
    DateTime? LastEventUtc);
