using System.Diagnostics;
using System.Globalization;
using System.Text;
using WeChatNotifier.Core;
using WeChatNotifier.Core.Configuration;
using WeChatNotifier.Core.Notifications;
using WeChatNotifier.Diagnostics;
using WeChatNotifier.Diagnostics.Privacy;
using WeChatNotifier.Listener;
using WeChatNotifier.Listener.Profile;
using WeChatNotifier.Notifications;

namespace WeChatNotifier.ConsoleHost;

internal static class Program
{
    private const string Usage = """
        WeChatNotifier.ConsoleHost - prints every new WeChat message and shows it as a Windows notification
        (PHASE 3 prototype; local only, no network)

          --no-toast               Console output only, no Windows notifications.
          --test-toast             Show one test notification at start.
          --no-preview             Notifications show the conversation but not the message text (nor pictures).
          --no-avatar              No avatar on notifications.   --no-image-preview   "[图片]" instead of the picture.
          --no-image-cache         Never write copied avatars/pictures to disk (generated initials only).
          --silent                 Notifications without sound.          --long   Keep banners ~25 s.
          --no-open-chat           A click only brings WeChat to the front (does not open the conversation).
          --unregister             Remove the notifier's registration, notifications and images, then exit.

          --redact                 Mask chat text in the console (shape only).  --reveal <regex> keeps matches.
          --verbose                Echo the diagnostic log (no chat text) to the console.
          --debug-log              Write burst/read details to the log file (no chat text) without echoing them.
          --notify-muted           Also report conversations muted in WeChat.
          --notify-while-focused   Also report while a WeChat window is in the foreground.
          --self-test              After attaching, run one read burst as if WeChat had flashed, then exit.
          --seconds <n>            Exit after n seconds.   --stop-event <name>  exit when the named event is set.
          --no-log                 Do not write %LOCALAPPDATA%\WeChatNotifier\logs\listener.log
        """;

    private static readonly object ConsoleGate = new();

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var opts = Args.Parse(args);
        if (opts.Has("help") || opts.Has("?"))
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (opts.Has("unregister"))
        {
            Console.WriteLine($"[toast] {WindowsNotifier.RemoveRegistration()}");
            Console.WriteLine($"[avatars] {(WeChatListener.DeleteSavedAvatars(SettingsStore.AvatarFolder) ? "copied avatars removed" : "could not be removed (in use)")}");
            return 0;
        }

        // One notifier per user session: a second one would show every message twice.
        using var instance = new Mutex(initiallyOwned: true, @"Local\WeChatNotifier.Host", out bool firstInstance);
        if (!firstInstance)
        {
            Write(ConsoleColor.Yellow, "Another WeChat Notifier is already running in this session; exiting.");
            return 2;
        }

        var redactor = new TextRedactor(opts.Has("redact"), opts.Get("reveal"),
            WeChatUiProfile.AutomationIdPrefixesWithIdentity, WeChatUiProfile.MainWindowTitles);
        var logDir = opts.Has("no-log") ? null : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeChatNotifier", "logs");
        // --debug-log writes burst/read details to the log file without echoing them; --verbose also echoes.
        using var log = new DiagnosticLog(logDir, opts.Has("verbose") || opts.Has("debug-log") ? LogLevel.Debug : LogLevel.Info,
            opts.Has("verbose") ? line => Write(ConsoleColor.DarkGray, "  · " + line) : null);
        try { Console.Title = "WeChat Notifier — PHASE 3"; } catch (IOException) { }

        var notificationOptions = new NotificationOptions
        {
            ShowMessagePreview = !opts.Has("no-preview"),
            PlaySound = !opts.Has("silent"),
            LongDuration = opts.Has("long"),
            ShowAvatars = !opts.Has("no-avatar"),
            ShowImagePreview = !opts.Has("no-image-preview"),
            StoreImageCache = !opts.Has("no-image-cache"),
        };
        bool toasts = !opts.Has("no-toast");
        var options = new ListenerOptions
        {
            NotifyMutedConversations = opts.Has("notify-muted"),
            NotifyWhileWeChatFocused = opts.Has("notify-while-focused"),
            OpenConversationOnClick = !opts.Has("no-open-chat"),
            // Copy pixels only when a notification will actually show them.
            CaptureAvatars = toasts && notificationOptions is { ShowAvatars: true, StoreImageCache: true },
            AvatarFolder = SettingsStore.AvatarFolder,
            CaptureImagePreviews = toasts && notificationOptions is { ShowImagePreview: true, StoreImageCache: true, ShowMessagePreview: true },
        };

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        if (opts.GetInt("seconds") is { } seconds) stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var stopEvent = opts.Get("stop-event") is { Length: > 0 } name ? new EventWaitHandle(false, EventResetMode.ManualReset, name) : null;
        var stopWait = stopEvent is null ? null : ThreadPool.RegisterWaitForSingleObject(stopEvent, (_, _) => stop.Cancel(), null, Timeout.Infinite, true);

        var self = Process.GetCurrentProcess();
        var cpu0 = self.TotalProcessorTime;
        var wall = Stopwatch.StartNew();

        Write(ConsoleColor.Cyan, $"WeChat Notifier — PHASE 3 console host. Redaction {(redactor.Enabled ? "ON" : "off")}. Ctrl+C to exit.");
        if (log.FilePath is not null) Write(ConsoleColor.DarkGray, $"diagnostic log (no chat text): {log.FilePath}");

        using var notifier = opts.Has("no-toast") ? null : new WindowsNotifier(notificationOptions, log);
        using var listener = new WeChatListener(options, log);
        bool selfTest = opts.Has("self-test");
        int selfTestStarted = 0;
        listener.StatusChanged += s =>
        {
            Write(ConsoleColor.DarkCyan, $"[status] {Describe(s)}");
            if (selfTest && s.State == ListenerState.Attached && Interlocked.Exchange(ref selfTestStarted, 1) == 0)
            {
                Write(ConsoleColor.Yellow, $"[self-test] injecting a synthetic flash (nothing is sent to WeChat); window={s.WindowState}, references={s.ReferencesHeld}");
                listener.InjectSyntheticFlash();
                stop.CancelAfter(TimeSpan.FromSeconds(4));
            }
        };
        var recent = new ConversationLines();
        listener.MessageDetected += ev =>
        {
            Print(ev, redactor);
            // A self-test event is not a real message: shown as a clearly labelled test notification.
            notifier?.Show(ev.Reason == EventReasons.SelfTest
                ? NotificationComposer.Test(ev.DetectedAt, notificationOptions)
                : NotificationComposer.Compose(ev, notificationOptions, recent));
        };
        listener.ConversationRead += conversation => recent.Clear(NotificationComposer.ConversationTag(conversation));

        if (notifier is not null)
        {
            notifier.Activated += a =>
            {
                Write(ConsoleColor.Magenta, $"[click] opening WeChat{(a.ConversationKey is null ? "" : " and the notification's conversation")}");
                listener.OpenWeChat(a.ConversationKey);
            };
            listener.ConversationRead += notifier.RemoveConversation;
            notifier.UseWeChatExecutable(WeChatNotifier.Listener.Discovery.WeChatProcessLocator.FindCandidates().FirstOrDefault()?.ImagePath);
            var state = notifier.Start();
            Write(state == NotifierState.Ready ? ConsoleColor.DarkCyan : ConsoleColor.Yellow, $"[toast] {Describe(notifier)}");
        }
        else
        {
            Write(ConsoleColor.DarkGray, "[toast] off (--no-toast): console output only");
        }

        listener.Start();
        if (notifier?.LaunchActivation is { } launch)
        {
            Write(ConsoleColor.Magenta, "[click] started by a notification click: opening WeChat");
            listener.OpenWeChat(launch.ConversationKey);
        }
        if (opts.Has("test-toast") && notifier is { State: NotifierState.Ready })
        {
            notifier.Show(NotificationComposer.Test(DateTimeOffset.Now, notificationOptions));
            Write(ConsoleColor.DarkCyan, "[toast] test notification sent");
        }

        stop.Token.WaitHandle.WaitOne();
        if (selfTest && selfTestStarted == 0) Write(ConsoleColor.Yellow, "[self-test] not run: WeChat is not running");
        listener.Dispose();
        notifier?.Dispose();
        stopWait?.Unregister(null);

        self.Refresh();
        PrintSummary(listener.Metrics.Snapshot(), notifier?.Metrics.Snapshot(), self.TotalProcessorTime - cpu0, wall.Elapsed, self.PeakWorkingSet64, self.WorkingSet64);
        return 0;
    }

    private static void Print(MessageEvent ev, TextRedactor r)
    {
        string time = ev.DetectedAt.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string latency = ev.TriggerToDetect is { } t ? $"{t.TotalMilliseconds:0} ms" : "-";
        if (ev.Conversation is null)
        {
            string hint = ev.ConversationHint is { } h ? $"，可能来自「{r.Apply(h)}」" : "";
            Write(ConsoleColor.Yellow,
                $"[{time}] conversation=?  sender=?  type=Unknown  content=<微信 · 新消息，内容不可读{hint}>  ({ev.Reason}, {latency})");
            return;
        }

        string conversation = r.Apply(ev.Conversation) ?? "";
        // Groups: the sender comes from WeChat's "sender: text" preview (a label; private chats have none).
        string sender = r.Apply(ev.Sender ?? ev.Conversation) ?? "";
        string content = ev.Text is { } text ? "\"" + r.Apply(text) + "\"" : "<内容不可读：微信在托盘中>";
        string count = ev.NewMessages > 1 ? $" ×{ev.NewMessages}" : "";
        string mention = ev.Mentioned ? " @me" : "";
        string images = (ev.Avatar is null ? "" : " +avatar") + (ev.Image is { } img ? $" +picture {img.Width}x{img.Height}" : "");
        Write(ev.ContentAvailable ? ConsoleColor.Green : ConsoleColor.Yellow,
            $"[{time}] conversation=\"{conversation}\"  sender=\"{sender}\"  type={ev.Kind}{count}{mention}  content={content}{images}" +
            $"  (unread={ev.UnreadCount}, {ev.Source}, {ev.Reason}, {latency})");
    }

    private static string Describe(ListenerStatus s) => s.State switch
    {
        ListenerState.Attached =>
            $"attached to WeChat pid={s.WeChatPid} v{s.WeChatVersion}; window={s.WindowState}; " +
            $"references={(s.ReferencesHeld ? $"{s.HeldSessionCells} cells" : "none (open WeChat once)")}; " +
            $"shell hook={(s.ShellHookActive ? "on" : "OFF")}; tracked={s.TrackedConversations}; chat windows={s.ChatWindows}",
        ListenerState.WaitingForWeChat => "WeChat not running — waiting (no polling)",
        _ => s.State.ToString(),
    };

    private static string Describe(WindowsNotifier n) => n.State switch
    {
        NotifierState.Ready when n.WindowsSetting is "Enabled" =>
            "Windows notifications on — they appear as «WeChat Notifier» (Settings → System → Notifications)",
        NotifierState.Ready =>
            $"registered, but Windows reports the setting «{n.WindowsSetting}»: turn on «WeChat Notifier» in Settings → System → Notifications",
        _ => $"Windows notifications OFF ({n.State}): {n.StateDetail}. Messages are printed here only.",
    };

    private static void PrintSummary(MetricsSnapshot m, NotificationMetricsSnapshot? t, TimeSpan cpu, TimeSpan wall, long peakWs, long ws)
    {
        Write(ConsoleColor.Cyan, "");
        Write(ConsoleColor.Cyan, $"==== summary ({wall.TotalSeconds:0.0} s) ====");
        Write(ConsoleColor.Cyan, $"events={m.Events} (without content {m.EventsWithoutContent}), generic/unexplained={m.UnexplainedFlashes}, duplicates dropped={m.DuplicatesDropped}");
        Write(ConsoleColor.Cyan, $"flashes={m.Flashes} (other apps {m.ForeignFlashes}), bursts={m.Bursts}, reads={m.Reads} (failed {m.ReadFailures}), captures={m.Captures} (failed {m.CaptureFailures})");
        Write(ConsoleColor.Cyan, $"suppressed: muted={m.SuppressedMuted}, WeChat focused={m.SuppressedFocused}; attaches={m.Attaches}, detaches={m.Detaches}");
        if (m.AverageLatencyMs is { } avg)
            Write(ConsoleColor.Cyan, $"flash→detect latency: last {m.LastLatencyMs:0} ms, avg {avg:0} ms, max {m.MaxLatencyMs:0} ms");
        if (t is not null)
        {
            Write(ConsoleColor.Cyan, $"notifications: shown={t.Shown}, failed={t.Failed}, withdrawn after reading={t.Removed}, clicked={t.Clicks}");
            if (t.ShowCall is { } call)
                Write(ConsoleColor.Cyan, $"build+Show(): avg {call.AverageMs:0.0} ms, max {call.MaxMs:0.0} ms" +
                                         (t.EndToEnd is { } e ? $"; flash→notification: last {e.LastMs:0} ms, avg {e.AverageMs:0} ms, max {e.MaxMs:0} ms" : ""));
        }
        if (m.OpenRequests > 0)
            Write(ConsoleColor.Cyan, $"clicks handled={m.OpenRequests}: tray icon clicked={m.TrayIconClicks}, WeChat started (was not running)={m.WeChatLaunches}, " +
                                     $"conversation opened={m.ConversationsOpened}, not opened={m.ConversationOpenFailures}");
        if (m.AvatarsCaptured + m.PicturesCaptured + m.PictureMisses > 0)
            Write(ConsoleColor.Cyan, $"copied from WeChat's window: avatars={m.AvatarsCaptured}, pictures={m.PicturesCaptured} (not found {m.PictureMisses}); " +
                                     $"held for the thumbnail: loaded {m.PicturesAfterWait}, not in time {m.PicturesNotLoaded}");
        Write(ConsoleColor.Cyan, $"CPU {cpu.TotalMilliseconds:0} ms = {100 * cpu.TotalMilliseconds / Math.Max(1, wall.TotalMilliseconds):0.000}% of one core; " +
                                 $"working set {ws / 1048576.0:0.0} MB (peak {peakWs / 1048576.0:0.0} MB)");
    }

    private static void Write(ConsoleColor color, string line)
    {
        lock (ConsoleGate)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(line);
            Console.ForegroundColor = old;
        }
    }
}

internal sealed class Args
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Args Parse(string[] args)
    {
        var a = new Args();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) value = args[++i];
            a._values[key] = value;
        }
        return a;
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public int? GetInt(string key) => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
