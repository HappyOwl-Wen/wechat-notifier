namespace WeChatNotifier.Core.Imaging;

/// <summary>
/// Turns a square avatar (copied from WeChat, or generated) into the notification's round avatar: resized with a
/// smooth filter to exactly the pixels the notification draws it at (<see cref="Notifications.ToastScale"/>: 48
/// effective px, 60 px at 125 %), and cut to a circle with an anti-aliased edge (transparent outside). Windows then
/// draws it 1:1; any other size it scales with a simple filter — enlarged, its own circle crop is jagged (PHASE 3),
/// shrunk from 160 px, the smooth edge turns into steps too (0.6.6). Pure logic.
/// </summary>
public static class AvatarImage
{
    /// <summary>Side generated avatars are drawn at before being scaled to the display size (scales down smoothly).</summary>
    public const int SourceSize = 160;

    public static Bitmap32 Round(Bitmap32 square, int size)
    {
        var result = square.Width == size && square.Height == size ? Copy(square) : Resample(square, size, size);
        ApplyCircleMask(result);
        return result;
    }

    /// <summary>
    /// Separable Catmull-Rom resampling (sharp but without ringing halos at this scale). When shrinking, the kernel
    /// is widened by the scale factor so that no detail aliases. Alpha is not resampled: the input is opaque.
    /// </summary>
    public static Bitmap32 Resample(Bitmap32 source, int width, int height)
    {
        var columns = Taps(source.Width, width);
        var rows = Taps(source.Height, height);
        var p = source.Pixels;

        // Horizontal pass: source rows → width columns (B, G, R as floats).
        var temp = new float[source.Height * width * 3];
        for (int y = 0; y < source.Height; y++)
        {
            int rowStart = y * source.Width;
            for (int x = 0; x < width; x++)
            {
                var (index, weight) = columns[x];
                float b = 0, g = 0, r = 0;
                for (int k = 0; k < index.Length; k++)
                {
                    int i = (rowStart + index[k]) * 4;
                    float w = weight[k];
                    b += p[i] * w;
                    g += p[i + 1] * w;
                    r += p[i + 2] * w;
                }
                int t = (y * width + x) * 3;
                temp[t] = b;
                temp[t + 1] = g;
                temp[t + 2] = r;
            }
        }

        // Vertical pass.
        var result = new Bitmap32(width, height);
        var o = result.Pixels;
        for (int y = 0; y < height; y++)
        {
            var (index, weight) = rows[y];
            for (int x = 0; x < width; x++)
            {
                float b = 0, g = 0, r = 0;
                for (int k = 0; k < index.Length; k++)
                {
                    int t = (index[k] * width + x) * 3;
                    float w = weight[k];
                    b += temp[t] * w;
                    g += temp[t + 1] * w;
                    r += temp[t + 2] * w;
                }
                int i = (y * width + x) * 4;
                o[i] = ToByte(b);
                o[i + 1] = ToByte(g);
                o[i + 2] = ToByte(r);
                o[i + 3] = 255;
            }
        }
        return result;
    }

    /// <summary>
    /// Alpha = how much of each pixel lies inside the inscribed circle (8×8 samples near the edge: 64 shades, which
    /// matters at the ~60 px the avatar is drawn at).
    /// </summary>
    internal static void ApplyCircleMask(Bitmap32 bitmap)
    {
        const int n = 8;
        double cx = bitmap.Width / 2.0, cy = bitmap.Height / 2.0, radius = Math.Min(cx, cy);
        var p = bitmap.Pixels;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy, distance = Math.Sqrt(dx * dx + dy * dy);
                int alpha;
                if (distance <= radius - 0.75) alpha = 255;
                else if (distance >= radius + 0.75) alpha = 0;
                else
                {
                    int inside = 0;
                    for (int sy = 0; sy < n; sy++)
                    {
                        for (int sx = 0; sx < n; sx++)
                        {
                            double ex = x + (sx + 0.5) / n - cx, ey = y + (sy + 0.5) / n - cy;
                            if (ex * ex + ey * ey <= radius * radius) inside++;
                        }
                    }
                    alpha = (inside * 255 + n * n / 2) / (n * n);
                }
                int i = (y * bitmap.Width + x) * 4;
                p[i + 3] = (byte)(p[i + 3] * alpha / 255);
            }
        }
    }

    private static Bitmap32 Copy(Bitmap32 source) => new(source.Width, source.Height, (byte[])source.Pixels.Clone());

    private static (int[] Index, float[] Weight)[] Taps(int sourceLength, int targetLength)
    {
        double scale = (double)targetLength / sourceLength;
        double widen = Math.Max(1.0, 1.0 / scale); // shrinking: wider kernel
        double support = 2.0 * widen;
        var taps = new (int[], float[])[targetLength];
        for (int i = 0; i < targetLength; i++)
        {
            double center = (i + 0.5) / scale - 0.5; // source coordinate of the target pixel's centre
            int first = (int)Math.Floor(center - support) + 1, last = (int)Math.Floor(center + support);
            var index = new int[last - first + 1];
            var weight = new float[index.Length];
            double sum = 0;
            for (int j = first; j <= last; j++)
            {
                double w = CatmullRom((j - center) / widen);
                index[j - first] = Math.Clamp(j, 0, sourceLength - 1); // edges: repeat the outermost pixel
                weight[j - first] = (float)w;
                sum += w;
            }
            for (int k = 0; k < weight.Length; k++) weight[k] = (float)(weight[k] / sum);
            taps[i] = (index, weight);
        }
        return taps;
    }

    private static double CatmullRom(double x)
    {
        x = Math.Abs(x);
        if (x < 1) return (1.5 * x - 2.5) * x * x + 1;
        if (x < 2) return ((-0.5 * x + 2.5) * x - 4) * x + 2;
        return 0;
    }

    private static byte ToByte(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);
}
