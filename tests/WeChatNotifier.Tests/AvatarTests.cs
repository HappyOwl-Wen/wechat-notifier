using WeChatNotifier.Core.Imaging;
using WeChatNotifier.Core.Notifications;

namespace WeChatNotifier.Tests;

public class AvatarTests
{
    private static readonly (byte B, byte G, byte R) ListBackground = (247, 247, 247);

    private static Bitmap32 Filled(int w, int h, (byte B, byte G, byte R) c)
    {
        var bmp = new Bitmap32(w, h);
        for (int i = 0; i < bmp.Pixels.Length; i += 4)
        {
            bmp.Pixels[i] = c.B;
            bmp.Pixels[i + 1] = c.G;
            bmp.Pixels[i + 2] = c.R;
            bmp.Pixels[i + 3] = 255;
        }
        return bmp;
    }

    private static void Set(Bitmap32 bmp, int x, int y, (byte B, byte G, byte R) c)
    {
        if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) return;
        int i = (y * bmp.Width + x) * 4;
        bmp.Pixels[i] = c.B;
        bmp.Pixels[i + 1] = c.G;
        bmp.Pixels[i + 2] = c.R;
    }

    /// <summary>A photo-like avatar with rounded corners, the way WeChat draws it.</summary>
    private static void DrawAvatar(Bitmap32 bmp, PixelRect r, int cornerRadius)
    {
        for (int y = r.Y; y < r.Bottom; y++)
        {
            for (int x = r.X; x < r.Right; x++)
            {
                int dx = Math.Max(0, Math.Max(r.X + cornerRadius - x, x - (r.Right - 1 - cornerRadius)));
                int dy = Math.Max(0, Math.Max(r.Y + cornerRadius - y, y - (r.Bottom - 1 - cornerRadius)));
                if (dx * dx + dy * dy > cornerRadius * cornerRadius) continue;
                Set(bmp, x, y, ((byte)(40 + x * 3 % 150), (byte)(90 + y * 5 % 120), (byte)(30 + (x + y) % 90)));
            }
        }
    }

    private static void DrawDisc(Bitmap32 bmp, int cx, int cy, int radius, (byte B, byte G, byte R) c)
    {
        for (int y = cy - radius; y <= cy + radius; y++)
            for (int x = cx - radius; x <= cx + radius; x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius) Set(bmp, x, y, c);
    }

    [Fact]
    public void Avatar_is_found_exactly_in_the_cell_area()
    {
        // 125 %: cell 81 px high, avatar 45 px at (18, 18), rounded corners.
        var area = Filled(81, 81, ListBackground);
        DrawAvatar(area, new PixelRect(18, 18, 45, 45), cornerRadius: 5);
        Assert.Equal(new PixelRect(18, 18, 45, 45), ImageAnalysis.FindAvatar(area, 32, 65));
    }

    [Fact]
    public void Unread_badge_and_title_text_do_not_widen_the_avatar()
    {
        var area = Filled(81, 81, ListBackground);
        DrawAvatar(area, new PixelRect(18, 18, 45, 45), cornerRadius: 5);
        DrawDisc(area, 60, 20, 10, (81, 81, 250));                     // red unread badge over the top-right corner
        for (int x = 76; x < 81; x++) for (int y = 20; y < 36; y += 2) Set(area, x, y, (30, 30, 30)); // start of the title
        Assert.Equal(new PixelRect(18, 18, 45, 45), ImageAnalysis.FindAvatar(area, 32, 65));
    }

    [Fact]
    public void Selected_cell_background_is_handled()
    {
        var area = Filled(81, 81, (222, 222, 222));
        DrawAvatar(area, new PixelRect(16, 17, 46, 46), cornerRadius: 4);
        Assert.Equal(new PixelRect(16, 17, 46, 46), ImageAnalysis.FindAvatar(area, 32, 65));
    }

    [Fact]
    public void No_avatar_when_the_block_is_not_a_plausible_square()
    {
        Assert.Null(ImageAnalysis.FindAvatar(Filled(81, 81, ListBackground), 32, 65));   // empty row
        var wide = Filled(81, 81, ListBackground);
        DrawAvatar(wide, new PixelRect(5, 25, 70, 30), cornerRadius: 0);
        Assert.Null(ImageAnalysis.FindAvatar(wide, 32, 65));                             // not square
        var small = Filled(81, 81, ListBackground);
        DrawAvatar(small, new PixelRect(30, 30, 20, 20), cornerRadius: 0);
        Assert.Null(ImageAnalysis.FindAvatar(small, 32, 65));                            // too small
    }

    [Fact]
    public void A_copy_that_missed_the_avatar_is_no_avatar()
    {
        // 2026-10-02, just after waking up: the copy showed the cell below the avatar, with the avatar's lower edge
        // across its top, and was kept for 30 days.
        var area = Filled(81, 81, (238, 238, 238));
        DrawAvatar(area, new PixelRect(18, -20, 45, 45), cornerRadius: 5);   // the avatar sits 38 px higher than copied
        var missed = area.Crop(new PixelRect(19, 19, 43, 43));
        Assert.False(ImageAnalysis.IsNearlyUniform(missed, missed.Bounds, maxStdDev: 3)); // the old check let it through
        Assert.False(ImageAnalysis.LooksLikeAvatar(missed, area));
        Assert.False(ImageAnalysis.LooksLikeAvatar(missed));                  // a kept copy: its lower corners show the list
    }

    [Fact]
    public void Real_avatars_pass_also_light_ones_with_a_mark_in_the_middle()
    {
        var area = Filled(81, 81, ListBackground);
        DrawAvatar(area, new PixelRect(18, 18, 45, 45), cornerRadius: 5);
        var photo = area.Crop(new PixelRect(19, 19, 43, 43));
        Assert.True(ImageAnalysis.LooksLikeAvatar(photo, area));
        Assert.True(ImageAnalysis.LooksLikeAvatar(photo));

        var logo = Filled(81, 81, ListBackground);
        for (int y = 18; y < 63; y++)
            for (int x = 18; x < 63; x++) Set(logo, x, y, (255, 255, 255)); // white, within the list's tolerance
        DrawDisc(logo, 40, 40, 8, (40, 40, 40));
        var mark = logo.Crop(new PixelRect(19, 19, 43, 43));
        Assert.True(ImageAnalysis.LooksLikeAvatar(mark, logo));
        Assert.True(ImageAnalysis.LooksLikeAvatar(mark));
    }

    [Theory]
    [InlineData(60)]   // 48 effective px at 125 %, the size the notification draws it at
    [InlineData(160)]
    public void Round_avatar_has_a_smooth_transparent_edge(int size)
    {
        var square = Filled(45, 45, (200, 100, 50));
        var round = AvatarImage.Round(square, size);

        Assert.Equal((size, size), (round.Width, round.Height));
        byte Alpha(int x, int y) => round.Pixels[(y * round.Width + x) * 4 + 3];
        int c = size / 2;
        Assert.Equal(255, Alpha(c, c));                                // centre opaque
        Assert.Equal(0, Alpha(0, 0));                                  // corners transparent
        Assert.Equal(0, Alpha(size - 1, size - 1));
        Assert.Equal(255, Alpha(c, 3));                                // just inside the top of the circle

        // Along the edge there are partially covered pixels: anti-aliased, not a hard step.
        int partial = 0;
        var shades = new HashSet<byte>();
        for (int y = 0; y < round.Height; y++)
            for (int x = 0; x < round.Width; x++)
                if (Alpha(x, y) is > 0 and < 255 and var a) { partial++; shades.Add(a); }
        Assert.InRange(partial, size * 2, size * 6);
        Assert.True(shades.Count >= 12, $"only {shades.Count} edge shades");  // 8×8 samples: fine gradation

        // A flat colour stays exactly that colour when resized.
        int i = (c * round.Width + c) * 4;
        Assert.Equal(new byte[] { 200, 100, 50 }, round.Pixels[i..(i + 3)]);
    }

    [Theory]
    [InlineData(96, 48, 16)]
    [InlineData(120, 60, 20)]   // 125 %: the screen of 2026-10-02
    [InlineData(144, 72, 24)]
    [InlineData(168, 84, 28)]
    [InlineData(192, 96, 32)]
    [InlineData(216, 108, 36)]
    [InlineData(240, 120, 40)]
    [InlineData(288, 144, 48)]
    public void Notification_images_are_made_at_the_drawn_size(uint dpi, int avatar, int headerIcon)
    {
        Assert.Equal(avatar, ToastScale.Pixels(ToastScale.AvatarEffectivePixels, dpi));
        Assert.Equal(headerIcon, ToastScale.HeaderIconSize(dpi));
        Assert.Equal(headerIcon, ToastScale.Pixels(ToastScale.HeaderIconEffectivePixels, dpi)); // an exact match
    }

    [Theory]
    [InlineData(108u, 20)]      // 112.5 %: 18 px drawn, 16 and 20 are equally close — the larger
    [InlineData(132u, 24)]      // 137.5 %: 22 px
    [InlineData(336u, 48)]      // 350 %: beyond the largest
    public void Header_icon_between_two_sizes_uses_the_closest(uint dpi, int headerIcon) =>
        Assert.Equal(headerIcon, ToastScale.HeaderIconSize(dpi));

    [Fact]
    public void Every_header_icon_is_embedded_at_its_size_with_a_smooth_circle()
    {
        foreach (int size in ToastScale.HeaderIconSizes)
        {
            var file = Path.Combine(RepoRoot(), "src", "WeChatNotifier.Notifications", "Assets", $"HeaderIcon-{size}.png");
            var icon = PngDecoder.Decode(File.ReadAllBytes(file));
            Assert.NotNull(icon);
            Assert.Equal((size, size), (icon.Width, icon.Height));
            byte Alpha(int x, int y) => icon.Pixels[(y * size + x) * 4 + 3];
            Assert.Equal(0, Alpha(0, 0));                                    // outside the circle
            Assert.Equal(255, Alpha(size / 2, size / 2));
            int partial = 0;
            for (int i = 3; i < icon.Pixels.Length; i += 4) if (icon.Pixels[i] is > 0 and < 255) partial++;
            Assert.True(partial >= size, $"{size} px: only {partial} anti-aliased edge pixels");
        }
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    [Fact]
    public void Resampling_keeps_flat_colours_and_shrinks_without_aliasing()
    {
        var checker = new Bitmap32(64, 64);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                byte v = (byte)(((x + y) & 1) == 0 ? 0 : 255);
                int i = (y * 64 + x) * 4;
                checker.Pixels[i] = checker.Pixels[i + 1] = checker.Pixels[i + 2] = v;
                checker.Pixels[i + 3] = 255;
            }
        // A one-pixel checkerboard shrunk 4x must average to mid grey, not to a moiré of black and white.
        var small = AvatarImage.Resample(checker, 16, 16);
        for (int i = 0; i < small.Pixels.Length; i += 4) Assert.InRange(small.Pixels[i], (byte)110, (byte)145);

        var flat = AvatarImage.Resample(Filled(10, 10, (12, 34, 56)), 37, 37);
        for (int i = 0; i < flat.Pixels.Length; i += 4) Assert.Equal(new byte[] { 12, 34, 56, 255 }, flat.Pixels[i..(i + 4)]);
    }
}
