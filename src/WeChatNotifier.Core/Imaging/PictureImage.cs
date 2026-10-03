namespace WeChatNotifier.Core.Imaging;

/// <summary>
/// Prepares a picture copied from a WeChat bubble for the notification: enlarged with a smooth filter to the width
/// the notification shows it at (WeChat's thumbnails are smaller, and Windows would stretch them blurrily), and
/// WeChat's rounded corners — chat background in the copy — made transparent so no grey corners show. Pure logic.
/// </summary>
public static class PictureImage
{
    /// <summary>Notification width in effective pixels; an inline picture fills it.</summary>
    public const int NotificationWidthEpx = 364;

    /// <summary>Taller pictures are not enlarged beyond this height relative to the width.</summary>
    private const double MaxHeightToWidth = 1.5;

    /// <param name="picture">The picture as found in the bubble (opaque).</param>
    /// <param name="background">The chat background around it, for the corners.</param>
    /// <param name="dpi">Display DPI: the target width is <see cref="NotificationWidthEpx"/> at this DPI.</param>
    public static Bitmap32 ForNotification(Bitmap32 picture, (byte B, byte G, byte R) background, uint dpi)
    {
        int targetWidth = (int)Math.Ceiling(NotificationWidthEpx * Math.Max(96u, dpi) / 96.0);
        double scale = (double)targetWidth / picture.Width;
        if (picture.Height * scale > targetWidth * MaxHeightToWidth) scale = targetWidth * MaxHeightToWidth / picture.Height;
        // Exactly the drawn size, up or down: Windows scales with a simple filter (shrinking leaves jagged edges).
        var result = Math.Abs(scale - 1) > 0.01
            ? AvatarImage.Resample(picture, Math.Max(1, (int)Math.Round(picture.Width * scale)), Math.Max(1, (int)Math.Round(picture.Height * scale)))
            : new Bitmap32(picture.Width, picture.Height, (byte[])picture.Pixels.Clone());
        ClearCorners(result, background, radius: Math.Max(2, (int)Math.Round(Math.Min(result.Width, result.Height) / 12.0)));
        return result;
    }

    /// <summary>
    /// In each corner, outside a quarter circle of <paramref name="radius"/> (where a rounded bubble shows the chat
    /// background), pixels close to the background become transparent and blended ones partly transparent. Pixels
    /// inside the arcs are never touched, so a light corner of the photo itself stays.
    /// </summary>
    internal static void ClearCorners(Bitmap32 bitmap, (byte B, byte G, byte R) background, int radius)
    {
        radius = Math.Min(radius, Math.Min(bitmap.Width, bitmap.Height) / 2);
        var p = bitmap.Pixels;
        int w = bitmap.Width, h = bitmap.Height;
        foreach (var (x0, y0, cx, cy) in new[] { (0, 0, radius, radius), (w - radius, 0, w - radius, radius), (0, h - radius, radius, h - radius), (w - radius, h - radius, w - radius, h - radius) })
        {
            for (int y = y0; y < y0 + radius; y++)
            {
                for (int x = x0; x < x0 + radius; x++)
                {
                    double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                    if (dx * dx + dy * dy < (radius - 1.5) * (radius - 1.5)) continue; // inside the arc: picture
                    int i = (y * w + x) * 4;
                    int distance = Math.Abs(p[i] - background.B) + Math.Abs(p[i + 1] - background.G) + Math.Abs(p[i + 2] - background.R);
                    // 0-24: background; 24-96: edge blended with it; above: picture.
                    int alpha = distance <= 24 ? 0 : distance >= 96 ? 255 : (distance - 24) * 255 / 72;
                    p[i + 3] = (byte)Math.Min(p[i + 3], alpha);
                }
            }
        }
    }
}
