namespace WeChatNotifier.Core.Imaging;

/// <summary>Rectangle in pixels (screen or bitmap coordinates).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PixelRect Intersect(PixelRect other)
    {
        int x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        int r = Math.Min(Right, other.Right), b = Math.Min(Bottom, other.Bottom);
        return r > x && b > y ? new PixelRect(x, y, r - x, b - y) : default;
    }

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>From a UIA BoundingRectangle (left, top, width, height as doubles).</summary>
    public static PixelRect? FromUia(double[]? r) =>
        r is { Length: 4 } && r[2] > 0 && r[3] > 0
            ? new PixelRect((int)Math.Round(r[0]), (int)Math.Round(r[1]), (int)Math.Round(r[2]), (int)Math.Round(r[3]))
            : null;
}

/// <summary>
/// 32-bit BGRA pixels, top-down, no padding. Captured avatars and image previews live in RAM in this form and are
/// only written to disk as a notification's image (see WeChatNotifier.Notifications).
/// </summary>
public sealed class Bitmap32
{
    public Bitmap32(int width, int height, byte[]? pixels = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length != width * height * 4) throw new ArgumentException("pixel buffer size does not match", nameof(pixels));
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>B, G, R, A per pixel.</summary>
    public byte[] Pixels { get; }

    public PixelRect Bounds => new(0, 0, Width, Height);

    public Bitmap32 Crop(PixelRect rect)
    {
        var r = rect.Intersect(Bounds);
        if (r.IsEmpty) throw new ArgumentException("crop rectangle outside the bitmap", nameof(rect));
        var result = new Bitmap32(r.Width, r.Height);
        for (int y = 0; y < r.Height; y++)
            Buffer.BlockCopy(Pixels, ((r.Y + y) * Width + r.X) * 4, result.Pixels, y * r.Width * 4, r.Width * 4);
        return result;
    }

    /// <summary>Screen captures carry no alpha; make every pixel opaque.</summary>
    public Bitmap32 Opaque()
    {
        for (int i = 3; i < Pixels.Length; i += 4) Pixels[i] = 255;
        return this;
    }
}
