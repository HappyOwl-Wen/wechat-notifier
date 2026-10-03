namespace WeChatNotifier.Core.Imaging;

/// <summary>What <see cref="ImageAnalysis.FindPicture(Bitmap32, int, out (byte B, byte G, byte R), out PictureSearch)"/> saw.</summary>
public enum PictureSearch
{
    Found,

    /// <summary>The area is smaller than the smallest picture worth a preview.</summary>
    AreaTooSmall,

    /// <summary>No dense block that differs from the chat background (the bubble is not drawn yet, or no picture there).</summary>
    NoBlock,

    /// <summary>A block, but not picture-shaped (too narrow, too small).</summary>
    NotPictureShaped,

    /// <summary>A flat block: blank, still being drawn.</summary>
    Uniform,

    /// <summary>WeChat's grey stand-in while the thumbnail downloads.</summary>
    LoadingPlaceholder,
}

/// <summary>Small, allocation-light analysis of captured bubbles. Pure logic.</summary>
public static class ImageAnalysis
{
    /// <summary>Sum of |ΔB|+|ΔG|+|ΔR| above which a pixel is not background.</summary>
    internal const int BackgroundTolerance = 30;

    /// <summary>
    /// Finds the picture inside a captured message area: the largest dense block of pixels that differ from the
    /// chat background (sampled along the right edge, which a received message never reaches). Sparse rows — a
    /// sender nickname above the picture, rounded-corner gaps — are left out. Null when nothing picture-like is
    /// there: still loading (nearly uniform), too small, or no clear block.
    /// </summary>
    public static PixelRect? FindPicture(Bitmap32 area, int minSide) => FindPicture(area, minSide, out _, out _);

    /// <inheritdoc cref="FindPicture(Bitmap32, int)"/>
    /// <param name="background">The chat background (B, G, R) the picture was told apart from.</param>
    public static PixelRect? FindPicture(Bitmap32 area, int minSide, out (byte B, byte G, byte R) background) =>
        FindPicture(area, minSide, out background, out _);

    /// <inheritdoc cref="FindPicture(Bitmap32, int, out (byte B, byte G, byte R))"/>
    /// <param name="result">Why no picture was found, for the log (no content).</param>
    public static PixelRect? FindPicture(Bitmap32 area, int minSide, out (byte B, byte G, byte R) background, out PictureSearch result)
    {
        background = default;
        result = PictureSearch.AreaTooSmall;
        if (area.Width < minSide || area.Height < minSide) return null;
        var (bb, bg, br) = Background(area);
        background = ((byte)bb, (byte)bg, (byte)br);
        var p = area.Pixels;

        var rowCounts = new int[area.Height];
        for (int y = 0; y < area.Height; y++)
        {
            int count = 0, i = y * area.Width * 4;
            for (int x = 0; x < area.Width; x++, i += 4)
                if (Math.Abs(p[i] - bb) + Math.Abs(p[i + 1] - bg) + Math.Abs(p[i + 2] - br) > BackgroundTolerance) count++;
            rowCounts[y] = count;
        }
        result = PictureSearch.NoBlock;
        int maxRow = rowCounts.Max();
        if (maxRow < minSide) return null;
        result = PictureSearch.NotPictureShaped;
        var (y0, y1) = LongestRun(rowCounts, maxRow / 2);
        if (y1 - y0 < minSide) return null;

        var columnCounts = new int[area.Width];
        for (int y = y0; y < y1; y++)
        {
            int i = y * area.Width * 4;
            for (int x = 0; x < area.Width; x++, i += 4)
                if (Math.Abs(p[i] - bb) + Math.Abs(p[i + 1] - bg) + Math.Abs(p[i + 2] - br) > BackgroundTolerance) columnCounts[x]++;
        }
        var (x0, x1) = LongestRun(columnCounts, (y1 - y0) / 2);
        if (x1 - x0 < minSide) return null;

        var picture = new PixelRect(x0, y0, x1 - x0, y1 - y0);
        double aspect = (double)picture.Width / picture.Height;
        if (aspect is < 0.2 or > 5) return null;
        result = IsNearlyUniform(area, picture) ? PictureSearch.Uniform
            : IsLoadingPlaceholder(area, picture) ? PictureSearch.LoadingPlaceholder
            : PictureSearch.Found;
        return result == PictureSearch.Found ? picture : null;
    }

    /// <summary>
    /// WeChat's stand-in while a thumbnail downloads: a neutral grey box, possibly with a small icon in it. True when
    /// one grey (R≈G≈B, neither near black nor near white) covers at least 85 % of the block — real photos and
    /// screenshots do not. A white document stays a picture: white is excluded.
    /// </summary>
    public static bool IsLoadingPlaceholder(Bitmap32 bitmap, PixelRect rect)
    {
        var r = rect.Intersect(bitmap.Bounds);
        if (r.IsEmpty) return false;
        var p = bitmap.Pixels;
        var counts = new Dictionary<int, int>();
        int n = 0, step = Math.Max(1, Math.Min(r.Width, r.Height) / 64);
        for (int y = r.Y; y < r.Bottom; y += step)
        {
            for (int x = r.X; x < r.Right; x += step, n++)
            {
                int i = (y * bitmap.Width + x) * 4;
                int key = (p[i] >> 3) << 10 | (p[i + 1] >> 3) << 5 | (p[i + 2] >> 3); // 5 bits per channel
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }
        var (top, count) = counts.MaxBy(kv => kv.Value) is var kv ? (kv.Key, kv.Value) : (0, 0);
        int b = (top >> 10) << 3, g = ((top >> 5) & 31) << 3, rr = (top & 31) << 3;
        bool neutralGrey = Math.Abs(b - g) <= 8 && Math.Abs(g - rr) <= 8 && g is >= 96 and <= 232;
        // Neighbouring 5-bit buckets of the same grey count too (anti-aliasing, slight gradients).
        int near = counts.Where(c => Math.Abs((c.Key >> 10) - (top >> 10)) <= 1 && Math.Abs(((c.Key >> 5) & 31) - ((top >> 5) & 31)) <= 1
                                     && Math.Abs((c.Key & 31) - (top & 31)) <= 1).Sum(c => c.Value);
        return neutralGrey && near >= n * 0.85;
    }

    /// <summary>
    /// Finds the avatar in a captured session-cell area: the square block of pixels that differ from the cell's
    /// background (sampled along the area's border, so hover and selection colours are handled). Rows and columns
    /// that are less than half covered do not count — that leaves out the unread badge, which overlaps the avatar's
    /// top-right corner and sticks out, and the start of the title text. Null when the block is not a plausible
    /// avatar (not square, or outside <paramref name="minSide"/>..<paramref name="maxSide"/>); the caller then
    /// falls back to the expected geometry.
    /// </summary>
    public static PixelRect? FindAvatar(Bitmap32 area, int minSide, int maxSide)
    {
        if (area.Width < minSide || area.Height < minSide) return null;
        var (bb, bg, br) = BorderBackground(area);
        var p = area.Pixels;
        var rowCounts = new int[area.Height];
        var columnCounts = new int[area.Width];
        for (int y = 0; y < area.Height; y++)
        {
            int i = y * area.Width * 4;
            for (int x = 0; x < area.Width; x++, i += 4)
            {
                if (Math.Abs(p[i] - bb) + Math.Abs(p[i + 1] - bg) + Math.Abs(p[i + 2] - br) <= BackgroundTolerance) continue;
                rowCounts[y]++;
                columnCounts[x]++;
            }
        }
        int maxRow = rowCounts.Max(), maxColumn = columnCounts.Max();
        if (maxRow < minSide || maxColumn < minSide) return null;
        var (x0, x1) = LongestRun(columnCounts, maxColumn / 2);
        var (y0, y1) = LongestRun(rowCounts, maxRow / 2);
        int w = x1 - x0, h = y1 - y0;
        if (w < minSide || h < minSide || w > maxSide || h > maxSide) return null;
        if (Math.Abs(w - h) > Math.Max(2, Math.Max(w, h) / 12)) return null;
        int side = Math.Min(w, h);
        return new PixelRect(x0 + (w - side) / 2, y0 + (h - side) / 2, side, side);
    }

    /// <summary>
    /// False for a copied avatar whose middle is the list's background: the empty cell around an avatar, not an
    /// avatar. Seen 2026-10-02 right after the PC woke from sleep: copies taken while the window's picture and UIA's
    /// positions disagreed showed the cell below each avatar with a sliver of it at the top — and were kept for 30 days.
    /// The background is the cell's own (sampled along the captured area's border), or, for a copy kept on disk, any of
    /// its corners: WeChat rounds the avatar's corners, so they show the list. An avatar that is itself almost only
    /// that colour in its middle half is refused too; generated initials are shown instead.
    /// </summary>
    public static bool LooksLikeAvatar(Bitmap32 avatar, Bitmap32? cellArea = null)
    {
        if (avatar.Width < 4 || avatar.Height < 4) return false;
        var backgrounds = new List<(int B, int G, int R)>(4);
        if (cellArea is not null)
        {
            backgrounds.Add(BorderBackground(cellArea));
        }
        else
        {
            foreach (var (x, y) in new[] { (0, 0), (avatar.Width - 1, 0), (0, avatar.Height - 1), (avatar.Width - 1, avatar.Height - 1) })
            {
                int i = (y * avatar.Width + x) * 4;
                backgrounds.Add((avatar.Pixels[i], avatar.Pixels[i + 1], avatar.Pixels[i + 2]));
            }
        }
        var p = avatar.Pixels;
        int x0 = avatar.Width / 4, x1 = avatar.Width - x0, y0 = avatar.Height / 4, y1 = avatar.Height - y0;
        int total = (x1 - x0) * (y1 - y0);
        foreach (var (bb, bg, br) in backgrounds)
        {
            int same = 0;
            for (int y = y0; y < y1; y++)
            {
                int i = (y * avatar.Width + x0) * 4;
                for (int x = x0; x < x1; x++, i += 4)
                    if (Math.Abs(p[i] - bb) + Math.Abs(p[i + 1] - bg) + Math.Abs(p[i + 2] - br) <= BackgroundTolerance) same++;
            }
            if (same >= total * 0.9) return false;
        }
        return true;
    }

    /// <summary>True for flat areas such as a grey "loading" placeholder (luminance standard deviation &lt; 6).</summary>
    public static bool IsNearlyUniform(Bitmap32 bitmap, PixelRect rect, double maxStdDev = 6)
    {
        var r = rect.Intersect(bitmap.Bounds);
        if (r.IsEmpty) return true;
        double sum = 0, sumSq = 0;
        long n = 0;
        var p = bitmap.Pixels;
        int step = Math.Max(1, Math.Min(r.Width, r.Height) / 64); // sample on a grid; enough for a decision
        for (int y = r.Y; y < r.Bottom; y += step)
        {
            for (int x = r.X; x < r.Right; x += step)
            {
                int i = (y * bitmap.Width + x) * 4;
                double l = 0.114 * p[i] + 0.587 * p[i + 1] + 0.299 * p[i + 2];
                sum += l;
                sumSq += l * l;
                n++;
            }
        }
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0, sumSq / n - mean * mean)) < maxStdDev;
    }

    /// <summary>Median colour of the two rightmost columns.</summary>
    private static (int B, int G, int R) Background(Bitmap32 area)
    {
        var bs = new List<byte>(area.Height * 2);
        var gs = new List<byte>(area.Height * 2);
        var rs = new List<byte>(area.Height * 2);
        for (int x = Math.Max(0, area.Width - 2); x < area.Width; x++)
        {
            for (int y = 0; y < area.Height; y++)
            {
                int i = (y * area.Width + x) * 4;
                bs.Add(area.Pixels[i]);
                gs.Add(area.Pixels[i + 1]);
                rs.Add(area.Pixels[i + 2]);
            }
        }
        bs.Sort();
        gs.Sort();
        rs.Sort();
        return (bs[bs.Count / 2], gs[gs.Count / 2], rs[rs.Count / 2]);
    }

    /// <summary>Median colour of the one-pixel border ring.</summary>
    private static (int B, int G, int R) BorderBackground(Bitmap32 area)
    {
        int capacity = 2 * (area.Width + area.Height);
        var bs = new List<byte>(capacity);
        var gs = new List<byte>(capacity);
        var rs = new List<byte>(capacity);
        void Add(int x, int y)
        {
            int i = (y * area.Width + x) * 4;
            bs.Add(area.Pixels[i]);
            gs.Add(area.Pixels[i + 1]);
            rs.Add(area.Pixels[i + 2]);
        }
        for (int x = 0; x < area.Width; x++)
        {
            Add(x, 0);
            Add(x, area.Height - 1);
        }
        for (int y = 1; y < area.Height - 1; y++)
        {
            Add(0, y);
            Add(area.Width - 1, y);
        }
        bs.Sort();
        gs.Sort();
        rs.Sort();
        return (bs[bs.Count / 2], gs[gs.Count / 2], rs[rs.Count / 2]);
    }

    /// <summary>Longest run of consecutive entries with a count of at least <paramref name="threshold"/> (≥ 1).</summary>
    private static (int Start, int End) LongestRun(int[] counts, int threshold)
    {
        threshold = Math.Max(1, threshold);
        int bestStart = 0, bestEnd = 0, start = -1;
        for (int i = 0; i <= counts.Length; i++)
        {
            bool dense = i < counts.Length && counts[i] >= threshold;
            if (dense && start < 0) start = i;
            if (!dense && start >= 0)
            {
                if (i - start > bestEnd - bestStart) (bestStart, bestEnd) = (start, i);
                start = -1;
            }
        }
        return (bestStart, bestEnd);
    }
}
