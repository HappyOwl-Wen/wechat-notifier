using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using WeChatNotifier.Listener.Interop;
using WeChatNotifier.Listener.Profile;

namespace WeChatNotifier.Listener.Reading;

/// <summary>
/// Finds and clicks WeChat's notification-area icon through the Windows shell's UI Automation tree — the same
/// action as a user's (or a screen reader's) click, after which WeChat shows its own main window. Touches only
/// Explorer's taskbar, never WeChat. Owned by the listener thread.
/// </summary>
internal sealed class ShellTray
{
    private readonly UiaSession _uia;

    public ShellTray(UiaSession uia) => _uia = uia;

    /// <summary>The hidden-icons flyout is open.</summary>
    public bool OverflowOpen => OverflowWindow() != IntPtr.Zero;

    /// <param name="inOverflow">Look in the open hidden-icons flyout instead of on the taskbar.</param>
    /// <param name="ambiguous">More than one icon looks like WeChat's (several WeChat instances): none is returned.</param>
    public IUIAutomationElement? FindWeChatIcon(bool inOverflow, out bool ambiguous)
    {
        ambiguous = false;
        var window = inOverflow ? OverflowWindow() : Native.FindWindowW(ShellTrayProfile.TaskbarWindowClass, null);
        if (window == IntPtr.Zero) return null;

        var condition = _uia.Automation.CreatePropertyCondition(UiaSession.AutomationIdProperty, ShellTrayProfile.NotifyIconAutomationId);
        var icons = _uia.Automation.ElementFromHandle(window).FindAllBuildCache(TreeScope.TreeScope_Descendants, condition, _uia.Element);
        IUIAutomationElement? found = null;
        foreach (var icon in UiaSession.Elements(icons))
        {
            if (!WeChatUiProfile.IsTrayIconName(_uia.CachedString(icon, UiaSession.NameProperty))) continue;
            if (found is not null)
            {
                ambiguous = true;
                return null;
            }
            found = icon;
        }
        return found;
    }

    /// <summary>The "Show hidden icons" button on the taskbar (toggles the flyout).</summary>
    public IUIAutomationElement? FindOverflowButton()
    {
        var taskbar = Native.FindWindowW(ShellTrayProfile.TaskbarWindowClass, null);
        if (taskbar == IntPtr.Zero) return null;

        var a = _uia.Automation;
        var condition = a.CreateAndCondition(
            a.CreatePropertyCondition(UiaSession.ClassNameProperty, ShellTrayProfile.OverflowButtonClass),
            a.CreatePropertyCondition(UiaSession.AutomationIdProperty, ShellTrayProfile.OverflowButtonAutomationId));
        var buttons = a.ElementFromHandle(taskbar).FindAllBuildCache(TreeScope.TreeScope_Descendants, condition, _uia.Element);
        IUIAutomationElement? leftmost = null;
        double left = double.MaxValue;
        foreach (var button in UiaSession.Elements(buttons))
        {
            if (ShellTrayProfile.OverflowButtonNames.Contains(_uia.CachedString(button, UiaSession.NameProperty)?.Trim())) return button;
            if (_uia.CachedRect(button) is { } r && r[0] < left)
            {
                left = r[0];
                leftmost = button;
            }
        }
        return leftmost;
    }

    public static bool Invoke(IUIAutomationElement element)
    {
        try
        {
            if (element.GetCurrentPattern(UiaSession.InvokePatternId) is not IUIAutomationInvokePattern invoke) return false;
            invoke.Invoke();
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static IntPtr OverflowWindow()
    {
        var hwnd = Native.FindWindowW(ShellTrayProfile.OverflowWindowClass, null);
        return hwnd != IntPtr.Zero && Native.IsWindowVisible(hwnd) ? hwnd : IntPtr.Zero;
    }
}
