using System.Diagnostics;
using WeChatNotifier.Listener.Reading;

namespace WeChatInspector;

/// <summary>
/// Runs the listener's tray-icon lookup (<see cref="ShellTray"/>) against the real taskbar without clicking WeChat:
/// finds WeChat's icon on the taskbar, or opens the "Show hidden icons" flyout, looks for it there and closes the
/// flyout again. Checks that a notification click on a WeChat hidden in the tray can work after a Windows update.
/// </summary>
internal static class TrayProbe
{
    public static int Run()
    {
        var tray = new ShellTray(new UiaSession());
        var sw = Stopwatch.StartNew();

        var icon = tray.FindWeChatIcon(inOverflow: false, out bool ambiguous);
        Console.WriteLine($"taskbar: WeChat icon {Describe(icon is not null, ambiguous)} ({sw.ElapsedMilliseconds} ms)");
        if (icon is not null || ambiguous) return 0;

        var chevron = tray.FindOverflowButton();
        Console.WriteLine($"\"Show hidden icons\" button: {(chevron is null ? "not found" : "found")} ({sw.ElapsedMilliseconds} ms)");
        if (chevron is null) return 1;
        if (!ShellTray.Invoke(chevron))
        {
            Console.WriteLine("the button cannot be invoked");
            return 1;
        }

        long deadline = sw.ElapsedMilliseconds + 1500;
        while (!tray.OverflowOpen && sw.ElapsedMilliseconds < deadline) Thread.Sleep(50);
        Console.WriteLine($"flyout open: {tray.OverflowOpen} ({sw.ElapsedMilliseconds} ms)");

        var hidden = tray.FindWeChatIcon(inOverflow: true, out ambiguous);
        Console.WriteLine($"hidden icons: WeChat icon {Describe(hidden is not null, ambiguous)}, not clicked ({sw.ElapsedMilliseconds} ms)");

        if (tray.OverflowOpen && tray.FindOverflowButton() is { } button) ShellTray.Invoke(button);
        Thread.Sleep(400);
        Console.WriteLine($"flyout open after closing: {tray.OverflowOpen}");
        return hidden is not null ? 0 : 1;
    }

    private static string Describe(bool found, bool ambiguous) => found ? "found" : ambiguous ? "ambiguous (several WeChat icons)" : "not found";
}
