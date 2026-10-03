using System.Runtime.InteropServices;

namespace WeChatNotifier.Core.Notifications;

/// <summary>
/// Pixel sizes of notification images on the screen where Windows shows notifications (the primary display). Images
/// are made at exactly the size they are drawn at: Windows scales other sizes with a simple filter, which leaves
/// jagged edges — 2026-10-02 at 125 %: a 160 px avatar drawn at 60 px and a 64 px header icon drawn at 20 px both
/// had stair-stepped circles, next to perfectly smooth text.
/// </summary>
public static class ToastScale
{
    /// <summary>The avatar (app logo override) is drawn 48 effective px wide.</summary>
    public const int AvatarEffectivePixels = 48;

    /// <summary>The app icon in the notification header is drawn 16 effective px wide.</summary>
    public const int HeaderIconEffectivePixels = 16;

    /// <summary>Effective DPI of the primary display now (96 = 100 %, 120 = 125 %); 96 when it cannot be read.</summary>
    public static uint PrimaryDpi()
    {
        try
        {
            var monitor = MonitorFromPoint(default, MonitorDefaultToPrimary);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpi, out _) == 0 && dpi >= 96) return dpi;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return 96;
    }

    /// <summary>Physical pixels for a length in effective pixels at <paramref name="dpi"/>.</summary>
    public static int Pixels(int effectivePixels, uint dpi) => (int)Math.Round(effectivePixels * dpi / 96.0);

    /// <summary>
    /// Sizes of the header icons embedded in the notifier (scripts\make-icons.ps1,
    /// src\WeChatNotifier.Notifications\Assets\HeaderIcon-&lt;n&gt;.png): 16 effective px at 100–300 % scaling.
    /// </summary>
    public static readonly int[] HeaderIconSizes = [16, 20, 24, 28, 32, 36, 40, 48];

    /// <summary>The header icon size for <paramref name="dpi"/>: the embedded one closest to the drawn size (the larger on a tie).</summary>
    public static int HeaderIconSize(uint dpi)
    {
        int pixels = Pixels(HeaderIconEffectivePixels, dpi);
        return HeaderIconSizes.OrderBy(s => Math.Abs(s - pixels)).ThenByDescending(s => s).First();
    }

    private const uint MonitorDefaultToPrimary = 1;
    private const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
