using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using WeChatNotifier.Core;
using WeChatNotifier.Core.Imaging;
using WeChatNotifier.Core.Notifications;
using WeChatNotifier.Diagnostics;

namespace WeChatNotifier.Notifications;

public enum NotifierState
{
    NotStarted,

    /// <summary>Registered with Windows; notifications are shown.</summary>
    Ready,

    /// <summary>The Windows App Runtime this build needs is not installed.</summary>
    RuntimeMissing,

    /// <summary>Windows refuses notifications for this process (elevated, or started with another app's identity).</summary>
    Unsupported,

    Failed,
}

/// <summary>
/// A click on one of our notifications. <paramref name="ConversationKey"/> (see <see cref="Core.ConversationKey"/>)
/// is null for notifications that belong to no conversation (generic, test).
/// </summary>
public sealed record NotificationActivation(string? ConversationKey, bool LaunchedProcess);

/// <summary>
/// Shows WeChat messages as native Windows notifications through the Windows App SDK
/// (<see cref="AppNotificationManager"/>; spec §5). Windows draws them, so light/dark mode, accent colour,
/// accessibility, Do Not Disturb / Focus and the per-app settings in Settings → System → Notifications all apply
/// without any code here (spec §13, §16).
///
/// Show/remove calls run on one dedicated thread so the listener never waits for the notification platform.
/// The notification text lives in RAM only until it is handed to Windows. No conversation name is kept here:
/// tags and click arguments are <see cref="ConversationKey"/> hashes, resolved back by the listener.
/// </summary>
public sealed class WindowsNotifier : IDisposable
{
    private const string ActionArgument = "action";
    private const string KeyArgument = "key";
    private const int MaxShownKeys = 512;

    private volatile NotificationOptions _options;
    private readonly DiagnosticLog _log;
    private readonly BlockingCollection<Action> _work = new(new ConcurrentQueue<Action>(), 256);
    private readonly HashSet<string> _shownKeys = new(StringComparer.Ordinal); // conversations with a notification out
    private readonly Queue<string> _shownOrder = new();

    // Image files, used on the notification thread only.
    private readonly ImageStore _images;
    private readonly Dictionary<string, string> _pictureByTag = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Bitmap32 Source, int Size, string Path)> _copiedAvatarByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Size, string Path)> _generatedAvatarByKey = new(StringComparer.Ordinal);

    /// <summary>Tags shown by this run and not withdrawn since (the user may have dismissed some); for the timing log.</summary>
    private readonly HashSet<string> _displayedTags = new(StringComparer.Ordinal);

    private Thread? _thread;
    private bool _registered;
    private bool _handlerAttached;
    private volatile string? _weChatExe;
    private (string Name, string? Icon) _identity; // the header's name and icon as registered

    public WindowsNotifier(NotificationOptions? options = null, DiagnosticLog? log = null)
    {
        _options = options ?? new NotificationOptions();
        _log = log ?? DiagnosticLog.Disabled;
        _images = new ImageStore(_log);
    }

    public NotificationMetrics Metrics { get; } = new();

    public NotifierState State { get; private set; } = NotifierState.NotStarted;

    /// <summary>Human-readable reason for the state (no chat data), for the console / diagnostics page.</summary>
    public string? StateDetail { get; private set; }

    /// <summary>Windows' per-app notification setting at start-up (e.g. turned off by the user in Settings).</summary>
    public string? WindowsSetting { get; private set; }

    /// <summary>Set when this process was started by a click on a notification (no notifier was running).</summary>
    public NotificationActivation? LaunchActivation { get; private set; }

    /// <summary>Raised on a COM thread when the user clicks one of our notifications.</summary>
    public event Action<NotificationActivation>? Activated;

    /// <summary>
    /// Connects to the Windows App Runtime, registers the unpackaged app (HKCU only) and starts the worker.
    /// Never throws: on failure <see cref="State"/> says why and every Show becomes a no-op.
    /// </summary>
    public NotifierState Start()
    {
        if (State != NotifierState.NotStarted) return State;
        try
        {
            if (AppIdentity.HasForeignPackageIdentity())
                return Fail(NotifierState.Unsupported, "process runs with another app's package identity; start the notifier from Explorer or a terminal");

            int hr = WindowsAppRuntime.Initialize();
            if (hr != 0)
                return Fail(NotifierState.RuntimeMissing, $"Windows App Runtime {WindowsAppRuntime.RequiredVersion} (or newer 2.x) not found (0x{hr:X8})");

            if (!AppNotificationManager.IsSupported())
                return Fail(NotifierState.Unsupported, "Windows does not show notifications from elevated (administrator) processes");

            AppIdentity.Apply();
            var manager = AppNotificationManager.Default;
            if (!AppIdentity.ActivationServerIsCurrent(out _))
            {
                // Registered by a build in another folder: re-create it so clicks start this executable.
                _log.Info("toast", "registration pointed to another executable; re-registering");
                manager.UnregisterAll();
            }

            // The handler must be attached before Register, otherwise clicks start a new process.
            manager.NotificationInvoked += OnInvoked;
            _handlerAttached = true;
            RegisterWithIcon(manager);
            _registered = true;

            WindowsSetting = manager.Setting.ToString();
            if (manager.Setting != AppNotificationSetting.Enabled)
                _log.Warn("toast", $"Windows notification setting for this app: {WindowsSetting}");

            // Started by a click while no notifier was running: Windows hands that click over here.
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind == ExtendedActivationKind.AppNotification && activation.Data is AppNotificationActivatedEventArgs args)
            {
                Metrics.Clicked();
                LaunchActivation = new NotificationActivation(KeyOf(args), LaunchedProcess: true);
                _log.Info("toast", "process started by a notification click");
            }

            _thread = new Thread(Work) { IsBackground = true, Name = "WeChatNotifier.Notifications" };
            _thread.Start();
            Enqueue(() => _images.CleanUp(_options.Expiration));
            Enqueue(WarmUp);
            State = NotifierState.Ready;
            StateDetail = $"registered as {AppIdentity.AppUserModelId}; Windows setting {WindowsSetting}";
            _log.Info("toast", $"ready: aumid={AppIdentity.AppUserModelId} runtime>={WindowsAppRuntime.RequiredVersion} setting={WindowsSetting} header={Describe(_identity)}");
            return State;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or TypeLoadException or FileNotFoundException)
        {
            return Fail(NotifierState.Failed, $"{ex.GetType().Name} 0x{ex.HResult:X8}");
        }
    }

    /// <summary>Settings changed (expiration, withdrawal, the header's name); notification content itself comes composed.</summary>
    public void UpdateOptions(NotificationOptions options)
    {
        bool identityChanged = options.AppearAsWeChat != _options.AppearAsWeChat;
        _options = options;
        if (identityChanged && State == NotifierState.Ready) Enqueue(UpdateIdentity);
    }

    /// <summary>
    /// WeChat's executable, whose icon the header shows when the notifications appear as "微信". Set before
    /// <see cref="Start"/> and whenever WeChat is found.
    /// </summary>
    public void UseWeChatExecutable(string? path)
    {
        if (path is null || string.Equals(path, _weChatExe, StringComparison.OrdinalIgnoreCase)) return;
        _weChatExe = path;
        if (State == NotifierState.Ready && _options.AppearAsWeChat) Enqueue(UpdateIdentity);
    }

    /// <summary>
    /// Brings the registered name and icon in line with the options (Windows shows them from the next sign-in, see
    /// <see cref="AppIdentity.WriteRegistration"/>). Notification thread.
    /// </summary>
    private void UpdateIdentity()
    {
        var identity = AppIdentity.Identity(_options.AppearAsWeChat, _weChatExe);
        if (identity == _identity) return;
        if (!AppIdentity.WriteRegistration(identity.Name, identity.Icon))
        {
            _log.Warn("toast", "could not update the notifications' name and icon");
            return;
        }
        _identity = identity;
        _log.Info("toast", $"notifications' header set to {Describe(identity)}; Windows shows it after the next sign-in");
    }

    private static string Describe((string Name, string? Icon) identity) =>
        (identity.Name == AppIdentity.WeChatDisplayName ? "WeChat" : "WeChat Notifier") +
        (identity.Icon is null ? " (no icon)" : Path.GetFileName(identity.Icon).StartsWith("Weixin", StringComparison.Ordinal) ? " with WeChat's icon" : " with the notifier's icon");

    /// <summary>
    /// Windows' per-app notification setting now — the user can switch it in Settings at any time. Null when
    /// notifications are unavailable altogether (see <see cref="State"/>).
    /// </summary>
    public string? QueryWindowsSetting()
    {
        if (State != NotifierState.Ready) return null;
        try
        {
            return WindowsSetting = AppNotificationManager.Default.Setting.ToString();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return WindowsSetting;
        }
    }

    /// <summary>
    /// Deletes every notification image now (the user cleared the cache or switched it off). Notifications still in
    /// Notification Center lose their pictures; new ones write new files only when the settings allow it.
    /// </summary>
    public void ClearImageCache()
    {
        if (State != NotifierState.Ready || !Enqueue(ClearImagesNow)) ImageStore.DeleteAll();
    }

    /// <summary>Queues a notification. Returns false when notifications are unavailable or the queue is full.</summary>
    public bool Show(NotificationContent content)
    {
        if (State != NotifierState.Ready) return false;
        if (content.Kind == NotificationKind.Message) RememberShown(content.Tag);
        return Enqueue(() => ShowNow(content));
    }

    /// <summary>Withdraws the conversation's notification (if this run showed one) from Notification Center.</summary>
    public void RemoveConversation(string conversation)
    {
        if (State != NotifierState.Ready || !_options.RemoveWhenRead) return;
        var key = ConversationKey.Of(conversation);
        lock (_shownKeys)
        {
            if (!_shownKeys.Remove(key)) return;
        }
        Enqueue(() => RemoveNow(key));
    }

    public void Dispose()
    {
        if (_thread is not null)
        {
            _work.CompleteAdding();
            _thread.Join(3000);
            _thread = null;
        }
        try
        {
            if (_handlerAttached) AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            // Hands clicks back to Windows: from now on a click starts a new notifier process.
            if (_registered) AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _log.Warn("toast", $"unregister failed 0x{ex.HResult:X8}");
        }
        _registered = _handlerAttached = false;
        WindowsAppRuntime.Shutdown();
    }

    /// <summary>
    /// Removes every trace of the notifier from the notification platform: the HKCU registration and all of its
    /// notifications in Notification Center. For uninstall / cleanup.
    /// </summary>
    public static string RemoveRegistration()
    {
        if (AppIdentity.HasForeignPackageIdentity()) return "skipped: process has another app's package identity";
        int hr = WindowsAppRuntime.Initialize();
        if (hr != 0) return $"Windows App Runtime not available (0x{hr:X8})";
        try
        {
            AppIdentity.Apply();
            var manager = AppNotificationManager.Default;
            manager.RemoveAllAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            manager.UnregisterAll();
            ImageStore.DeleteAll();
            return "registration, notifications and notification images removed";
        }
        catch (Exception ex) when (ex is COMException or AggregateException or InvalidOperationException)
        {
            return $"failed: {ex.GetType().Name} 0x{ex.HResult:X8}";
        }
        finally
        {
            WindowsAppRuntime.Shutdown();
        }
    }

    // ------------------------------------------------------------------ worker

    private void Work()
    {
        foreach (var action in _work.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Metrics.Failed();
                _log.Error("toast", $"{ex.GetType().Name} 0x{ex.HResult:X8}");
            }
        }
    }

    private bool Enqueue(Action action)
    {
        try
        {
            if (_work.TryAdd(action)) return true;
            _log.Warn("toast", "queue full, notification dropped");
            return false;
        }
        catch (InvalidOperationException)
        {
            return false; // shutting down
        }
    }

    /// <summary>
    /// Builds (never shows) one notification at start-up, so the first real message does not pay for JIT and
    /// for activating the builder's runtime classes.
    /// </summary>
    private void WarmUp()
    {
        var sw = Stopwatch.StartNew();
        Build(NotificationComposer.Test(DateTimeOffset.Now, _options), null, null);
        _log.Debug("toast", $"warm-up build {sw.Elapsed.TotalMilliseconds:0.0}ms");
    }

    private static AppNotification Build(NotificationContent c, string? avatarFile, string? pictureFile)
    {
        var builder = new AppNotificationBuilder().AddArgument(ActionArgument, "open");
        if (c.Kind == NotificationKind.Message) builder.AddArgument(KeyArgument, c.Tag);
        // Already round, with a smooth transparent edge (AvatarImage): Windows' own circle crop is jagged on small images.
        if (avatarFile is not null) builder.SetAppLogoOverride(FileUri(avatarFile), AppNotificationImageCrop.Default);
        builder.AddText(c.Title);
        // Earlier messages one line each, the newest in its own element below with the remaining lines: Windows shows
        // at most four body lines and drops the rest unmarked, so an earlier message that wrapped would push the
        // newest one out, and a long newest one would end mid-sentence (ToastText cuts it with "…").
        double textScale = TextScale();
        if (c.EarlierLines.Count > 0)
            builder.AddText(string.Join('\n', c.EarlierLines.Select(line => ToastText.FitOneLine(line, textScale))));
        builder.AddText(ToastText.Fit(c.Body, ToastText.BodyLines - c.EarlierLines.Count, textScale))
            .SetTag(c.Tag)
            .SetGroup(c.Group)
            .SetTimeStamp(c.Timestamp);
        if (pictureFile is not null) builder.SetInlineImage(FileUri(pictureFile));
        if (c.Attribution is not null) builder.SetAttributionText(c.Attribution);
        if (c.Silent) builder.MuteAudio();
        else builder.SetAudioEvent(AppNotificationSoundEvent.IM);
        if (c.LongDuration) builder.SetDuration(AppNotificationDuration.Long);

        var notification = builder.BuildNotification();
        notification.ExpiresOnReboot = true;
        notification.Expiration = c.Expires;
        return notification;
    }

    /// <summary>Windows' text size (Settings → Accessibility → Text size): 1.0 at 100 %, up to 2.25.</summary>
    private static double TextScale()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            return key?.GetValue("TextScaleFactor") is int percent and >= 100 and <= 225 ? percent / 100.0 : 1.0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return 1.0;
        }
    }

    private void ShowNow(NotificationContent c)
    {
        // Timed in parts: files (avatar / picture written for Windows), building the payload, and Windows' Show()
        // itself, which does most of the work and varies most (2026-10-01: 55–360 ms for build+show).
        var sw = Stopwatch.StartNew();
        string? avatarFile = AvatarFile(c);
        string? pictureFile = c.Image is { } image ? _images.Save(image) : null;
        var filesMs = sw.Elapsed.TotalMilliseconds;
        var notification = Build(c, avatarFile, pictureFile);
        var buildMs = sw.Elapsed.TotalMilliseconds - filesMs;
        bool replaces = !_displayedTags.Add(c.Tag); // this conversation's notification of this run is still there
        try
        {
            AppNotificationManager.Default.Show(notification);
        }
        catch
        {
            ImageStore.Delete(pictureFile);
            throw;
        }
        sw.Stop();
        var showMs = sw.Elapsed.TotalMilliseconds - filesMs - buildMs;

        // The new notification replaced this conversation's previous one, and with it the previous picture.
        if (_pictureByTag.Remove(c.Tag, out var replaced)) ImageStore.Delete(replaced);
        if (pictureFile is not null) _pictureByTag[c.Tag] = pictureFile;

        TimeSpan? endToEnd = c is { TriggerToDetect: { } t, DetectedAt: { } detected } ? t + (DateTimeOffset.Now - detected) : null;
        Metrics.Shown(sw.Elapsed, endToEnd);
        _log.Info("toast", $"shown kind={c.Kind} id={notification.Id} avatar={(avatarFile is null ? "none" : c.Avatar is null ? "initials" : "copied")} " +
                           $"picture={(pictureFile is null ? "no" : "yes")} lines={1 + c.EarlierLines.Count} replaces={(replaces ? "yes" : "no")} " +
                           $"build+show={sw.Elapsed.TotalMilliseconds:0.0}ms (files {filesMs:0.0}, build {buildMs:0.0}, show {showMs:0.0})" +
                           (endToEnd is { } e ? $" flash-to-toast={e.TotalMilliseconds:0}ms" : ""));
    }

    /// <summary>
    /// The avatar file for a notification, made round (<see cref="AvatarImage"/>) at exactly the pixels the
    /// notification draws it at on the primary display: the copied avatar (written once per captured image and size),
    /// otherwise generated initials (once per conversation, size and run).
    /// </summary>
    private string? AvatarFile(NotificationContent c)
    {
        if (c.AvatarText is null) return null;
        int size = ToastScale.Pixels(ToastScale.AvatarEffectivePixels, ToastScale.PrimaryDpi());
        if (c.Avatar is { } copied)
        {
            if (_copiedAvatarByKey.TryGetValue(c.Tag, out var known) && ReferenceEquals(known.Source, copied) && known.Size == size) return known.Path;
            if (_images.Save(AvatarImage.Round(copied, size)) is { } path)
            {
                if (_copiedAvatarByKey.TryGetValue(c.Tag, out var outdated)) ImageStore.Delete(outdated.Path);
                _copiedAvatarByKey[c.Tag] = (copied, size, path);
                return path;
            }
        }
        if (_generatedAvatarByKey.TryGetValue(c.Tag, out var generated) && generated.Size == size) return generated.Path;
        if (InitialAvatar.Render(c.AvatarText, c.Tag) is not { } initials || _images.Save(AvatarImage.Round(initials, size)) is not { } file) return null;
        _generatedAvatarByKey[c.Tag] = (size, file);
        return file;
    }

    private void RemoveNow(string key)
    {
        var sw = Stopwatch.StartNew();
        AppNotificationManager.Default.RemoveByTagAndGroupAsync(key, NotificationComposer.ChatGroup).AsTask().Wait(TimeSpan.FromSeconds(2));
        _displayedTags.Remove(key);
        if (_pictureByTag.Remove(key, out var picture)) ImageStore.Delete(picture);
        Metrics.Removed();
        _log.Debug("toast", $"withdrew a conversation's notification (read in WeChat), {sw.Elapsed.TotalMilliseconds:0} ms");
    }

    private void ClearImagesNow()
    {
        _pictureByTag.Clear();
        _copiedAvatarByKey.Clear();
        _generatedAvatarByKey.Clear();
        ImageStore.DeleteAll();
        _log.Info("images", "notification image cache cleared");
    }

    /// <summary>Proper file URI: the payload's image source must be a URI, not a bare path.</summary>
    private static Uri FileUri(string path) => new(new Uri(path).AbsoluteUri);

    // ------------------------------------------------------------------ clicks

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            Metrics.Clicked();
            var activation = new NotificationActivation(KeyOf(args), LaunchedProcess: false);
            _log.Info("toast", $"clicked ({(activation.ConversationKey is null ? "no conversation" : "with conversation key")})");
            Activated?.Invoke(activation);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("toast", $"click handler failed: {ex.GetType().Name}");
        }
    }

    private static string? KeyOf(AppNotificationActivatedEventArgs args) =>
        args.Arguments.TryGetValue(KeyArgument, out var key) && !string.IsNullOrEmpty(key) ? key : null;

    private void RememberShown(string key)
    {
        lock (_shownKeys)
        {
            if (!_shownKeys.Add(key)) return;
            _shownOrder.Enqueue(key);
            while (_shownOrder.Count > MaxShownKeys) _shownKeys.Remove(_shownOrder.Dequeue());
        }
    }

    private void RegisterWithIcon(AppNotificationManager manager)
    {
        var identity = AppIdentity.Identity(_options.AppearAsWeChat, _weChatExe);
        if (identity.Icon is { } icon)
        {
            try
            {
                manager.Register(identity.Name, new Uri(icon));
                _identity = identity;
                return;
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                // Name and icon are cosmetic: fall back to what Windows derives from the executable.
                _log.Warn("toast", $"register with name/icon failed 0x{ex.HResult:X8}; using defaults");
            }
        }
        manager.Register();
    }

    private NotifierState Fail(NotifierState state, string detail)
    {
        State = state;
        StateDetail = detail;
        _log.Warn("toast", $"notifications unavailable ({state}): {detail}");
        return state;
    }
}
