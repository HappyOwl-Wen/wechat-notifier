using System.Runtime.InteropServices;
using WeChatNotifier.Core.Imaging;

namespace WeChatNotifier.Notifications;

/// <summary>
/// WeChat's own icon, for the notification header when the notifications appear as "微信"
/// (<see cref="Core.Notifications.NotificationOptions.AppearAsWeChat"/>): read from the installed Weixin.exe at the
/// pixel size the header draws it, so nothing of Tencent's ships with the notifier. Reads the file's icon resource
/// through the shell, as Explorer does; WeChat itself is not touched.
/// </summary>
internal static class WeChatAppIcon
{
    /// <summary>The executable's icon at <paramref name="size"/> × <paramref name="size"/> px with its transparency; null when there is none.</summary>
    public static Bitmap32? Extract(string exe, int size)
    {
        if (size is < 8 or > 256 || !File.Exists(exe)) return null;
        IntPtr icon = IntPtr.Zero;
        try
        {
            if (SHDefExtractIconW(exe, 0, 0, out icon, IntPtr.Zero, (uint)size) != 0 || icon == IntPtr.Zero) return null;
            if (!GetIconInfo(icon, out var info)) return null;
            try
            {
                if (info.hbmColor == IntPtr.Zero || Bits(info.hbmColor, size) is not { } color) return null; // monochrome icon
                bool alpha = false;
                for (int i = 3; i < color.Length && !alpha; i += 4) alpha = color[i] != 0;
                if (!alpha)
                {
                    // An icon without an alpha channel: its mask (white = transparent) says what shows.
                    if (Bits(info.hbmMask, size) is not { } mask) return null;
                    for (int i = 0; i < color.Length; i += 4) color[i + 3] = mask[i] == 0 ? (byte)255 : (byte)0;
                }
                return new Bitmap32(size, size, color);
            }
            finally
            {
                if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
                if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (icon != IntPtr.Zero) DestroyIcon(icon);
        }
    }

    /// <summary>A bitmap's pixels as 32-bit BGRA, top row first.</summary>
    private static byte[]? Bits(IntPtr bitmap, int size)
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        IntPtr header = Marshal.AllocHGlobal(BitmapInfoBytes);
        try
        {
            for (int i = 0; i < BitmapInfoBytes; i += 4) Marshal.WriteInt32(header, i, 0);
            Marshal.WriteInt32(header, 0, 40);     // biSize
            Marshal.WriteInt32(header, 4, size);   // biWidth
            Marshal.WriteInt32(header, 8, -size);  // biHeight: top-down
            Marshal.WriteInt16(header, 12, 1);     // biPlanes
            Marshal.WriteInt16(header, 14, 32);    // biBitCount; biCompression BI_RGB
            var bits = new byte[size * size * 4];
            return GetDIBits(dc, bitmap, 0, (uint)size, bits, header, 0) == size ? bits : null;
        }
        finally
        {
            Marshal.FreeHGlobal(header);
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    /// <summary>BITMAPINFOHEADER and room for the largest colour table GetDIBits may write after it.</summary>
    private const int BitmapInfoBytes = 40 + 256 * 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIconW(string iconFile, int index, uint flags, out IntPtr large, IntPtr small, uint size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, IntPtr info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);
}
