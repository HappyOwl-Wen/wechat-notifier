namespace WeChatNotifier.Listener.Hooks;

internal enum WindowSignal
{
    Created,
    Shown,
    Hidden,
    Destroyed,
    Minimized,
    Restored,
    MoveSizeEnded,
    Foreground,
}

/// <summary>Work items posted to the listener thread. <c>AtMs</c> is the listener's monotonic clock.</summary>
internal abstract record ListenerItem(long AtMs);

/// <summary>HSHELL_FLASH from any process (the listener filters by process, which also enables discovery).</summary>
internal sealed record FlashItem(IntPtr Hwnd, long AtMs) : ListenerItem(AtMs);

/// <summary>HSHELL_WINDOWCREATED from any process.</summary>
internal sealed record ShellWindowCreatedItem(IntPtr Hwnd, long AtMs) : ListenerItem(AtMs);

/// <summary>WinEvent for a top-level window of the attached WeChat process, or a global foreground change.</summary>
internal sealed record WindowSignalItem(WindowSignal Signal, IntPtr Hwnd, long AtMs) : ListenerItem(AtMs);

internal sealed record ProcessExitedItem(int Pid, long AtMs) : ListenerItem(AtMs);

internal sealed record ResumeItem(long AtMs) : ListenerItem(AtMs);

internal sealed record ShellRestartedItem(long AtMs) : ListenerItem(AtMs);

/// <summary>Diagnostics: behave as if WeChat's main window flashed, without touching WeChat.</summary>
internal sealed record SyntheticFlashItem(long AtMs) : ListenerItem(AtMs);

/// <summary>Look at WeChat again (e.g. after the session was unlocked): re-check the process, refresh references.</summary>
internal sealed record RefreshItem(long AtMs) : ListenerItem(AtMs);

/// <summary>New listener options (settings changed), applied on the listener thread.</summary>
internal sealed record OptionsItem(ListenerOptions Options, long AtMs) : ListenerItem(AtMs);

/// <summary>The user cleared the image cache: forget the copied avatars, in RAM and on disk.</summary>
internal sealed record ClearAvatarsItem(long AtMs) : ListenerItem(AtMs);

/// <summary>The user clicked a notification: bring WeChat forward and open the conversation with this key, if any.</summary>
internal sealed record OpenRequestItem(string? ConversationKey, long AtMs) : ListenerItem(AtMs);

internal sealed record StopItem(long AtMs) : ListenerItem(AtMs);
