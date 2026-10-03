using System.Runtime.InteropServices;
using WeChatNotifier.Core.Imaging;
using WeChatNotifier.Core.Notifications;

namespace WeChatNotifier.Notifications;

/// <summary>
/// Fallback avatar when no real one could be copied from WeChat: the conversation's initial on a colour derived
/// from its key (stable per conversation), drawn with GDI at the final avatar size. Square; made round by
/// <see cref="AvatarImage.Round"/>. Carries no chat data.
/// </summary>
internal static class InitialAvatar
{
    public const int Size = AvatarImage.SourceSize;

    /// <summary>Muted colours (0xRRGGBB) that keep white text readable in light and dark notifications.</summary>
    private static readonly int[] Palette = [0x4A7BD0, 0x2E9E6B, 0xD9822B, 0x8E5BD0, 0xD0504F, 0x2A8FBF, 0xC2577A, 0x6B7A8F];

    public static Bitmap32? Render(string name, string key)
    {
        int rgb = Palette[StableIndex(key)];
        string initial = NotificationComposer.Initial(name);

        IntPtr screen = GetDC(IntPtr.Zero), dc = IntPtr.Zero, dib = IntPtr.Zero, font = IntPtr.Zero, brush = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero, oldFont = IntPtr.Zero;
        try
        {
            dc = CreateCompatibleDC(screen);
            var header = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = Size, biHeight = -Size, biPlanes = 1, biBitCount = 32 };
            dib = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            oldBitmap = SelectObject(dc, dib);

            var rect = new RECT { Right = Size, Bottom = Size };
            brush = CreateSolidBrush(ToColorRef(rgb));
            FillRect(dc, ref rect, brush);

            font = CreateFontW(-(int)(Size * 0.46), 0, 0, 0, 600, 0, 0, 0, 1 /*DEFAULT_CHARSET*/, 0, 0, 4 /*ANTIALIASED_QUALITY*/, 0, "Microsoft YaHei UI");
            oldFont = SelectObject(dc, font);
            SetBkMode(dc, 1 /*TRANSPARENT*/);
            SetTextColor(dc, 0x00FFFFFF);
            DrawTextW(dc, initial, -1, ref rect, 0x1 /*DT_CENTER*/ | 0x4 /*DT_VCENTER*/ | 0x20 /*DT_SINGLELINE*/ | 0x800 /*DT_NOPREFIX*/);
            GdiFlush();

            var avatar = new Bitmap32(Size, Size);
            Marshal.Copy(bits, avatar.Pixels, 0, avatar.Pixels.Length);
            return avatar.Opaque();
        }
        finally
        {
            if (oldFont != IntPtr.Zero) SelectObject(dc, oldFont);
            if (oldBitmap != IntPtr.Zero) SelectObject(dc, oldBitmap);
            if (font != IntPtr.Zero) DeleteObject(font);
            if (brush != IntPtr.Zero) DeleteObject(brush);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static uint ToColorRef(int rgb) => (uint)(((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF));

    /// <summary>FNV-1a: string.GetHashCode is randomised per process, the colour must stay the same.</summary>
    private static int StableIndex(string key)
    {
        uint hash = 2166136261;
        foreach (char ch in key) hash = (hash ^ ch) * 16777619;
        return (int)(hash % (uint)Palette.Length);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
