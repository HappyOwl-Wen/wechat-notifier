namespace WeChatNotifier.Listener.Profile;

/// <summary>
/// The Windows 11 taskbar structure used to click WeChat's notification-area icon through UI Automation, the
/// way screen readers do. Like <see cref="WeChatUiProfile"/>, the one place to update if Windows moves things.
/// Verified on Windows 11 build 26300 (XAML taskbar).
/// </summary>
public static class ShellTrayProfile
{
    /// <summary>Primary taskbar: notification icons shown on the taskbar and the "Show hidden icons" button.</summary>
    public const string TaskbarWindowClass = "Shell_TrayWnd";

    /// <summary>Flyout with the hidden notification icons; a visible top-level window only while it is open.</summary>
    public const string OverflowWindowClass = "TopLevelWindowForOverflowXamlIsland";

    /// <summary>AutomationId of every application's notification icon button, on the taskbar and in the flyout.</summary>
    public const string NotifyIconAutomationId = "NotifyItemIcon";

    /// <summary>
    /// The "Show hidden icons" button: class + AutomationId, shared with a few system icons. It is recognised by its
    /// localized name, or else as the leftmost candidate (it comes first in the notification area).
    /// </summary>
    public const string OverflowButtonClass = "SystemTray.NormalButton";

    public const string OverflowButtonAutomationId = "SystemTrayIcon";

    public static readonly string[] OverflowButtonNames = ["显示隐藏的图标", "顯示隱藏的圖示", "Show hidden icons"];
}
