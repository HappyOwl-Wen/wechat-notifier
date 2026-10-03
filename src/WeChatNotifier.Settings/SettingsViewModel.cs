using System.ComponentModel;
using WeChatNotifier.App;
using WeChatNotifier.Core.Configuration;

namespace WeChatNotifier.Settings;

/// <summary>
/// The settings shown in the window (spec §18). Every change is written to settings.json and the running notifier
/// applies it at once (<see cref="ControlProtocol.ReloadSettings"/>); changes made elsewhere — the tray menu —
/// arrive through the file (<see cref="Reload"/>).
/// </summary>
internal sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly NotifierChannel _channel;
    private AppSettings _s;

    public SettingsViewModel(NotifierChannel channel)
    {
        _channel = channel;
        _s = Current();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- General ----

    public bool StartWithWindows { get => _s.StartWithWindows; set => Change(_s with { StartWithWindows = value }); }

    public bool ShowTrayIcon { get => _s.ShowTrayIcon; set => Change(_s with { ShowTrayIcon = value }); }

    public bool NotifyWhileWeChatFocused { get => _s.NotifyWhileWeChatFocused; set => Change(_s with { NotifyWhileWeChatFocused = value }); }

    // ---- Notifications ----

    public bool MessagePreview { get => _s.MessagePreview; set => Change(_s with { MessagePreview = value }); }

    public bool ImagePreview { get => _s.ImagePreview; set => Change(_s with { ImagePreview = value }); }

    /// <summary>A picture preview needs the message preview and the temporary image cache.</summary>
    public bool ImagePreviewAvailable => _s.MessagePreview && _s.StoreImageCache;

    public bool Avatars { get => _s.Avatars; set => Change(_s with { Avatars = value }); }

    public bool GroupMessages { get => _s.GroupMessages; set => Change(_s with { GroupMessages = value }); }

    public bool Sound { get => _s.Sound; set => Change(_s with { Sound = value }); }

    /// <summary>0: Windows' default (about 5 s); 1: long (about 25 s).</summary>
    public int DurationIndex { get => _s.LongDuration ? 1 : 0; set => Change(_s with { LongDuration = value == 1 }); }

    public bool NotifyMutedConversations { get => _s.NotifyMutedConversations; set => Change(_s with { NotifyMutedConversations = value }); }

    public bool OpenConversationOnClick { get => _s.OpenConversationOnClick; set => Change(_s with { OpenConversationOnClick = value }); }

    public bool ShowAsWeChat { get => _s.ShowAsWeChat; set => Change(_s with { ShowAsWeChat = value }); }

    // ---- Privacy ----

    public bool StoreImageCache { get => _s.StoreImageCache; set => Change(_s with { StoreImageCache = value }); }

    public bool DebugLogging { get => _s.DebugLogging; set => Change(_s with { DebugLogging = value }); }

    /// <summary>settings.json changed on disk: show what it says now.</summary>
    public void Reload()
    {
        var current = Current();
        if (current == _s) return;
        _s = current;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private static AppSettings Current()
    {
        var s = SettingsStore.Load();
        // Windows decides whether the notifier starts with it: Task Manager can switch the entry off.
        return s with { StartWithWindows = StartupRegistration.State() == StartupState.On };
    }

    private void Change(AppSettings next)
    {
        if (next == _s) return;
        bool startupChanged = next.StartWithWindows != _s.StartWithWindows;
        _s = next;
        SettingsStore.Save(next);
        // The notifier applies the file (and the start-with-Windows entry). When it does not run, the entry is
        // written from here, for the notifier's executable.
        if (!_channel.Send(ControlProtocol.ReloadSettings) && startupChanged && AppLocator.NotifierExecutable() is { } exe)
            StartupRegistration.Apply(next.StartWithWindows, exe);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
