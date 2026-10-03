using System.Runtime.InteropServices;
using WeChatNotifier.Core.Imaging;
using WeChatNotifier.Listener.Interop;

namespace WeChatNotifier.Listener.Reading;

/// <summary>
/// Level 3 of the spec (§4, §7): copies given regions of a WeChat window — an avatar, an image bubble — when UIA
/// cannot provide the pixels. One PrintWindow(PW_RENDERFULLCONTENT) per call: DWM hands over its copy of the
/// window, so it works while other windows cover WeChat, but never for minimized, hidden or cloaked windows
/// (other virtual desktop). Only the requested regions are kept; nothing is captured periodically.
/// Coordinates are physical screen pixels (the listener thread is per-monitor DPI aware, like UIA's rectangles).
/// </summary>
internal static class WindowCapture
{
    public static bool CanCapture(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && Native.IsWindowVisible(hwnd) && !Native.IsIconic(hwnd) && !IsCloaked(hwnd);

    /// <returns>One bitmap per region (null where the region lies outside the window); null when nothing can be captured.</returns>
    public static Bitmap32?[]? CaptureRegions(IntPtr hwnd, IReadOnlyList<PixelRect> screenRegions)
    {
        if (screenRegions.Count == 0 || !CanCapture(hwnd) || !Native.GetWindowRect(hwnd, out var wr)) return null;
        int width = wr.Right - wr.Left, height = wr.Bottom - wr.Top;
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return null;

        IntPtr screen = Native.GetDC(IntPtr.Zero), memory = IntPtr.Zero, dib = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            memory = Native.CreateCompatibleDC(screen);
            var header = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
            dib = Native.CreateDIBSection(memory, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            previous = Native.SelectObject(memory, dib);
            if (!Native.PrintWindow(hwnd, memory, Native.PW_RENDERFULLCONTENT)) return null;

            var window = new PixelRect(0, 0, width, height);
            var result = new Bitmap32?[screenRegions.Count];
            for (int n = 0; n < screenRegions.Count; n++)
            {
                var r = screenRegions[n].Offset(-wr.Left, -wr.Top).Intersect(window);
                if (r.IsEmpty) continue;
                var crop = new Bitmap32(r.Width, r.Height);
                for (int y = 0; y < r.Height; y++)
                    Marshal.Copy(bits + ((r.Y + y) * width + r.X) * 4, crop.Pixels, y * r.Width * 4, r.Width * 4);
                result[n] = crop.Opaque();
            }
            return result;
        }
        finally
        {
            if (previous != IntPtr.Zero) Native.SelectObject(memory, previous);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            if (memory != IntPtr.Zero) Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
}
