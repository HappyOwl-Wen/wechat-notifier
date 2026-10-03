using System.Diagnostics;
using WeChatNotifier.Core;
using WeChatNotifier.Core.Configuration;
using WeChatNotifier.Core.Notifications;
using WeChatNotifier.Diagnostics;
using WeChatNotifier.Listener;
using WeChatNotifier.Listener.Discovery;
using WeChatNotifier.Notifications;

namespace WeChatNotifier.App;

/// <summary>
/// Owns the listener and the Windows notifier of the background app: turns detected messages into notifications,
/// applies settings, pauses, and restarts the listener after a failure with exponential backoff (spec §21: never
/// a tight retry loop). Thread-safe: commands come from the tray thread, events from the listener and COM threads.
/// </summary>
internal sealed class NotifierHost : IDisposable
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();        // _listener, _settings, the timers, _disposed
    private readonly object _replaceGate = new(); // one listener replacement at a time
    private readonly DiagnosticLog _log;
    private readonly string _version;
    private readonly WindowsNotifier _notifier;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly ConversationLines _recent = new(); // the latest messages per conversation, RAM only
    private volatile WeChatListener? _listener;
    private volatile AppSettings _settings;
    private volatile NotificationOptions _notificationOptions;
    private Timer? _restartTimer;
    private Timer? _resumeTimer;
    private TimeSpan _retryDelay = FirstRetry;
    private DateTime _listenerStartedUtc;
    private int _listenerRestarts;
    private long _pausedUntilTicks; // UTC ticks; 0 = not paused, long.MaxValue = until resumed
    private long _heldBack;         // messages not shown during the current pause
    private int _iconPid;           // WeChat process whose icon was passed to the notifier
    private bool _disposed;

    public NotifierHost(AppSettings settings, DiagnosticLog log, string version)
    {
        _settings = settings;
        _log = log;
        _version = version;
        _notificationOptions = settings.ToNotificationOptions();
        _notifier = new WindowsNotifier(_notificationOptions, log);
    }

    /// <summary>Something the tray shows changed (listener state, pause, settings). Raised on any thread.</summary>
    public event Action? Changed;

    public AppSettings Settings => _settings;

    public WindowsNotifier Notifier => _notifier;

    public ListenerStatus? Status => _listener?.Status;

    /// <summary>Null while notifications are shown; <see cref="DateTime.MaxValue"/> when paused until resumed.</summary>
    public DateTime? PausedUntilUtc
    {
        get
        {
            long until = Interlocked.Read(ref _pausedUntilTicks);
            if (until == 0) return null;
            if (until == long.MaxValue) return DateTime.MaxValue;
            return until > DateTime.UtcNow.Ticks ? new DateTime(until, DateTimeKind.Utc) : null;
        }
    }

    public void Start()
    {
        _notifier.Activated += a =>
        {
            if (_listener is { } listener) listener.OpenWeChat(a.ConversationKey);
            else _log.Warn("app", "notification clicked while the listener restarts; ignored");
        };
        _notifier.UseWeChatExecutable(WeChatProcessLocator.FindCandidates().FirstOrDefault()?.ImagePath); // the header's icon
        _notifier.Start();
        StartListener("start");
        // Started by a click on a notification while no notifier ran: handle that click now.
        if (_notifier.LaunchActivation is { } launch) _listener?.OpenWeChat(launch.ConversationKey);
    }

    /// <summary>
    /// Holds notifications back for <paramref name="duration"/> (null: until <see cref="Resume"/>). Detection keeps
    /// running, so nothing is reported late or twice afterwards; the number held back is shown on resume.
    /// </summary>
    public void Pause(TimeSpan? duration)
    {
        long until = duration is { } d ? (DateTime.UtcNow + d).Ticks : long.MaxValue;
        lock (_gate)
        {
            if (_disposed) return;
            Interlocked.Exchange(ref _pausedUntilTicks, until);
            _resumeTimer?.Dispose();
            // Resumes only the pause that set it (a newer pause replaces this one).
            _resumeTimer = duration is { } due
                ? new Timer(_ => { if (Interlocked.Read(ref _pausedUntilTicks) == until) Resume(automatic: true); }, null, due, Timeout.InfiniteTimeSpan)
                : null;
        }
        _log.Info("app", duration is { } m ? $"notifications paused for {m.TotalMinutes:0} min" : "notifications paused until resumed");
        Changed?.Invoke();
    }

    public void Resume() => Resume(automatic: false);

    public bool ShowAppMessage(string body) => _notifier.Show(NotificationComposer.AppMessage(body, DateTimeOffset.Now));

    public bool ShowTestNotification() => _notifier.Show(NotificationComposer.Test(DateTimeOffset.Now, _notificationOptions));

    /// <summary>Deletes the notification images and the kept avatar copies.</summary>
    public void ClearImageCache()
    {
        _notifier.ClearImageCache();
        if (_listener is { } listener) listener.ClearAvatars();
        else WeChatListener.DeleteSavedAvatars(SettingsStore.AvatarFolder);
    }

    /// <summary>Applies new settings at once, without restarting the listener (that would lose WeChat's list while it is in the tray).</summary>
    public void ApplySettings(AppSettings settings)
    {
        AppSettings before;
        WeChatListener? listener;
        lock (_gate)
        {
            before = _settings;
            _settings = settings;
            listener = _listener;
        }
        _notificationOptions = settings.ToNotificationOptions();
        _notifier.UpdateOptions(_notificationOptions);
        _log.MinLevel = settings.DebugLogging ? LogLevel.Debug : LogLevel.Info;
        // Copies are no longer wanted on disk: remove the ones there.
        if (before.StoreImageCache && !settings.StoreImageCache) _notifier.ClearImageCache();
        // Message text is no longer wanted in notifications: forget the lines kept for them.
        if (!_notificationOptions.ShowMessagePreview) _recent.Clear();
        var options = ListenerOptionsFrom(settings);
        if (options != ListenerOptionsFrom(before)) listener?.UpdateOptions(options);
        _log.Info("app", "settings applied");
        Changed?.Invoke();
    }

    /// <summary>
    /// Replaces the listener (menu "restart listener"). Runs in the background: stopping the old one can take a
    /// moment when WeChat is busy. If WeChat sits in the tray, conversation names return once it is opened.
    /// </summary>
    public void RestartListener(string why) => ThreadPool.QueueUserWorkItem(_ => ReplaceListener(why));

    /// <summary>The session was unlocked or reconnected: let the listener look at WeChat again.</summary>
    public void OnSessionUnlocked() => _listener?.Refresh();

    /// <summary>The diagnostics page (spec §24). Component states and numbers only — never message text.</summary>
    public StatusSnapshot Snapshot()
    {
        var listener = _listener;
        var status = listener?.Status;
        var m = listener?.Metrics.Snapshot();
        var t = _notifier.Metrics.Snapshot();
        using var self = Process.GetCurrentProcess();
        return new StatusSnapshot
        {
            Version = _version,
            NotificationsReady = _notifier.State == NotifierState.Ready,
            NotificationsDetail = _notifier.StateDetail,
            WindowsSetting = _notifier.QueryWindowsSetting(),
            PausedUntilUtc = PausedUntilUtc,
            ListenerState = status?.State.ToString() ?? "Restarting",
            WeChatPid = status?.WeChatPid,
            WeChatVersion = status?.WeChatVersion,
            WindowState = (status?.WindowState ?? WeChatWindowState.NotFound).ToString(),
            UiAutomationConnected = status?.ReferencesHeld == true,
            SessionCells = status?.HeldSessionCells ?? 0,
            ShellHookActive = status?.ShellHookActive == true,
            LastTriggerUtc = m?.LastTriggerUtc,
            LastEventUtc = m?.LastEventUtc,
            LastLatencyMs = t.EndToEnd?.LastMs,
            AverageLatencyMs = t.EndToEnd?.AverageMs,
            MaxLatencyMs = t.EndToEnd?.MaxMs,
            DuplicatesDropped = m?.DuplicatesDropped ?? 0,
            Shown = t.Shown,
            Failed = t.Failed,
            Withdrawn = t.Removed,
            Clicks = t.Clicks,
            HeldBack = Interlocked.Read(ref _heldBack),
            PicturesAfterWait = m?.PicturesAfterWait ?? 0,
            PicturesNotLoaded = m?.PicturesNotLoaded ?? 0,
            WorkingSetMb = self.WorkingSet64 / 1048576.0,
            PeakWorkingSetMb = self.PeakWorkingSet64 / 1048576.0,
            CpuSeconds = self.TotalProcessorTime.TotalSeconds,
            UptimeSeconds = _uptime.Elapsed.TotalSeconds,
            ListenerRestarts = _listenerRestarts,
            LogFile = _log.FilePath,
        };
    }

    public void Dispose()
    {
        WeChatListener? listener;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _restartTimer?.Dispose();
            _resumeTimer?.Dispose();
            listener = _listener;
            _listener = null;
        }
        listener?.Dispose();
        _notifier.Dispose();
    }

    private void StartListener(string why)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var listener = new WeChatListener(ListenerOptionsFrom(_settings), _log);
            listener.MessageDetected += ev => OnMessage(listener, ev);
            listener.ConversationRead += conversation =>
            {
                if (!ReferenceEquals(listener, _listener)) return;
                _recent.Clear(NotificationComposer.ConversationTag(conversation)); // its next notification starts over
                _notifier.RemoveConversation(conversation);
            };
            listener.StatusChanged += s => OnListenerStatus(listener, s);
            _listener = listener;
            _listenerStartedUtc = DateTime.UtcNow;
            listener.Start();
        }
        _log.Info("app", $"listener started ({why})");
        Changed?.Invoke();
    }

    private void ReplaceListener(string why)
    {
        lock (_replaceGate)
        {
            WeChatListener? old;
            lock (_gate)
            {
                if (_disposed) return;
                old = _listener;
                _listener = null; // from now on its last events and its Stopped status are ignored
                _restartTimer?.Dispose();
                _restartTimer = null;
            }
            old?.Dispose(); // joins its thread; normally milliseconds
            StartListener(why);
        }
    }

    private void OnMessage(WeChatListener source, MessageEvent ev)
    {
        if (!ReferenceEquals(source, _listener)) return; // being replaced
        if (ev.IsGroup && !_settings.GroupMessages)
        {
            _log.Debug("app", "group message not shown (group messages off)");
            return;
        }
        var options = _notificationOptions;
        // Composed while paused too: what arrived meanwhile appears above the first message after the pause.
        var content = ev.Reason == EventReasons.SelfTest
            ? NotificationComposer.Test(ev.DetectedAt, options)
            : NotificationComposer.Compose(ev, options, _recent);
        if (PausedUntilUtc is not null)
        {
            Interlocked.Add(ref _heldBack, Math.Max(1, ev.NewMessages));
            return;
        }
        _notifier.Show(content);
    }

    private void OnListenerStatus(WeChatListener source, ListenerStatus status)
    {
        if (!ReferenceEquals(source, _listener)) return; // being replaced
        if (status.WeChatPid is { } pid && pid != _iconPid)
        {
            // WeChat found (started after the notifier, or restarted): its icon for the notifications' header.
            _iconPid = pid;
            _notifier.UseWeChatExecutable(WeChatProcessLocator.Identify(pid)?.ImagePath);
        }
        if (status.State == ListenerState.Stopped)
        {
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(source, _listener)) return;
                // The listener thread died: try again later, waiting longer each time it dies again soon.
                if (DateTime.UtcNow - _listenerStartedUtc > StableAfter) _retryDelay = FirstRetry;
                var delay = _retryDelay;
                _retryDelay = TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaxRetry.Ticks));
                _listenerRestarts++;
                _log.Warn("app", $"listener stopped unexpectedly; restarting in {delay.TotalSeconds:0} s");
                _restartTimer?.Dispose();
                _restartTimer = new Timer(_ => ReplaceListener("recovery"), null, delay, Timeout.InfiniteTimeSpan);
            }
        }
        Changed?.Invoke();
    }

    private void Resume(bool automatic)
    {
        lock (_gate)
        {
            _resumeTimer?.Dispose();
            _resumeTimer = null;
        }
        if (Interlocked.Exchange(ref _pausedUntilTicks, 0) == 0) return;
        long held = Interlocked.Exchange(ref _heldBack, 0);
        _log.Info("app", $"notifications resumed ({(automatic ? "pause over" : "by the user")}); {held} held back");
        if (held > 0) ShowAppMessage($"暂停期间收到 {held} 条微信消息，没有弹出通知。");
        Changed?.Invoke();
    }

    private static ListenerOptions ListenerOptionsFrom(AppSettings s) => new()
    {
        NotifyWhileWeChatFocused = s.NotifyWhileWeChatFocused,
        NotifyMutedConversations = s.NotifyMutedConversations,
        OpenConversationOnClick = s.OpenConversationOnClick,
        // Copy pixels only when a notification will show them.
        CaptureAvatars = s.Avatars && s.StoreImageCache,
        AvatarFolder = SettingsStore.AvatarFolder,
        CaptureImagePreviews = s.ImagePreview && s.StoreImageCache && s.MessagePreview,
    };
}
