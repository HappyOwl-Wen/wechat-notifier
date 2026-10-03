using WeChatNotifier.Core.Imaging;

namespace WeChatNotifier.Tests;

public class PictureTests
{
    private static readonly (byte B, byte G, byte R) ChatBackground = (237, 237, 237);
    private static readonly (byte B, byte G, byte R) PlaceholderGrey = (205, 205, 205);

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

    private static void Paint(Bitmap32 bmp, PixelRect r, Func<int, int, (byte B, byte G, byte R)> color)
    {
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
            {
                var (b, g, rr) = color(x, y);
                int i = (y * bmp.Width + x) * 4;
                bmp.Pixels[i] = b;
                bmp.Pixels[i + 1] = g;
                bmp.Pixels[i + 2] = rr;
            }
    }

    private static (byte, byte, byte) Photo(int x, int y) => ((byte)(30 + x * 7 % 180), (byte)(60 + y * 5 % 160), (byte)(20 + (x + y) * 3 % 200));

    private static byte Alpha(Bitmap32 bmp, int x, int y) => bmp.Pixels[(y * bmp.Width + x) * 4 + 3];

    [Fact]
    public void Grey_loading_box_with_a_small_icon_is_not_a_picture()
    {
        var area = Filled(400, 220, ChatBackground);
        var box = new PixelRect(100, 20, 180, 180);
        Paint(area, box, (_, _) => PlaceholderGrey);
        Paint(area, new PixelRect(175, 95, 30, 30), (_, _) => (150, 150, 150)); // the image icon in the middle
        Assert.True(ImageAnalysis.IsLoadingPlaceholder(area, box));
        Assert.Null(ImageAnalysis.FindPicture(area, minSide: 40));
    }

    [Fact]
    public void Photos_and_white_documents_are_pictures()
    {
        var area = Filled(400, 220, ChatBackground);
        var box = new PixelRect(100, 20, 180, 180);
        Paint(area, box, Photo);
        Assert.False(ImageAnalysis.IsLoadingPlaceholder(area, box));
        Assert.Equal(box, ImageAnalysis.FindPicture(area, minSide: 40));

        // A screenshot of a document: mostly white with lines of text.
        var doc = Filled(400, 220, ChatBackground);
        Paint(doc, box, (x, y) => y % 12 < 2 && x % 40 < 30 ? ((byte)40, (byte)40, (byte)40) : ((byte)255, (byte)255, (byte)255));
        Assert.False(ImageAnalysis.IsLoadingPlaceholder(doc, box));
    }

    [Fact]
    public void Picture_reports_the_background_it_was_found_on()
    {
        var area = Filled(400, 220, ChatBackground);
        Paint(area, new PixelRect(100, 20, 180, 180), Photo);
        ImageAnalysis.FindPicture(area, 40, out var background);
        Assert.Equal(ChatBackground, background);
    }

    [Fact]
    public void Picture_search_says_why_there_is_no_picture()
    {
        PictureSearch Search(Bitmap32 area)
        {
            ImageAnalysis.FindPicture(area, 40, out _, out var result);
            return result;
        }

        Assert.Equal(PictureSearch.AreaTooSmall, Search(Filled(400, 30, ChatBackground)));
        Assert.Equal(PictureSearch.NoBlock, Search(Filled(400, 220, ChatBackground)));     // bubble not drawn yet

        var placeholder = Filled(400, 220, ChatBackground);
        Paint(placeholder, new PixelRect(100, 20, 180, 180), (_, _) => PlaceholderGrey);
        Paint(placeholder, new PixelRect(175, 95, 30, 30), (_, _) => (150, 150, 150));
        Assert.Equal(PictureSearch.LoadingPlaceholder, Search(placeholder));

        var blank = Filled(400, 220, ChatBackground);
        Paint(blank, new PixelRect(100, 20, 180, 180), (_, _) => (40, 90, 200));
        Assert.Equal(PictureSearch.Uniform, Search(blank));

        var line = Filled(400, 220, ChatBackground);
        Paint(line, new PixelRect(20, 100, 360, 20), Photo);                              // a wide strip, no picture
        Assert.Equal(PictureSearch.NotPictureShaped, Search(line));

        var photo = Filled(400, 220, ChatBackground);
        Paint(photo, new PixelRect(100, 20, 180, 180), Photo);
        Assert.Equal(PictureSearch.Found, Search(photo));
    }

    [Fact]
    public void Small_thumbnail_is_enlarged_to_the_notification_width_with_transparent_rounded_corners()
    {
        // A 150 x 100 thumbnail with WeChat's rounded corners (chat background in the corner pixels).
        var thumb = Filled(150, 100, ChatBackground);
        Paint(thumb, thumb.Bounds, Photo);
        for (int y = 0; y < 6; y++)
            for (int x = 0; x < 6; x++)
                if ((6 - x) * (6 - x) + (6 - y) * (6 - y) > 36)
                    Paint(thumb, new PixelRect(x, y, 1, 1), (_, _) => ChatBackground);

        var shown = PictureImage.ForNotification(thumb, ChatBackground, dpi: 120);
        Assert.Equal(455, shown.Width);                       // 364 effective px at 125 %
        Assert.Equal(303, shown.Height);                      // aspect kept
        Assert.Equal(0, Alpha(shown, 0, 0));                  // corner: transparent, no grey triangle
        Assert.Equal(255, Alpha(shown, shown.Width / 2, shown.Height / 2));
        Assert.Equal(255, Alpha(shown, shown.Width / 2, 0));  // the straight edges stay
    }

    [Fact]
    public void Large_pictures_are_shrunk_to_the_drawn_width_and_tall_ones_are_capped()
    {
        // Windows would shrink a larger picture itself, with a simple filter: jagged edges.
        var large = PictureImage.ForNotification(Filled(600, 400, (10, 20, 30)), ChatBackground, 120);
        Assert.Equal((455, 303), (large.Width, large.Height));
        var exact = PictureImage.ForNotification(Filled(455, 300, (10, 20, 30)), ChatBackground, 120);
        Assert.Equal((455, 300), (exact.Width, exact.Height));     // already the drawn width: copied as is

        var tall = Filled(100, 300, (10, 20, 30));
        var shown = PictureImage.ForNotification(tall, ChatBackground, 96);
        Assert.Equal(546, shown.Height);                      // 1.5 x 364
        Assert.Equal(182, shown.Width);
    }

    [Fact]
    public void A_light_photo_corner_inside_the_arc_is_kept()
    {
        var bmp = Filled(200, 200, (10, 20, 30));
        Paint(bmp, new PixelRect(10, 10, 20, 20), (_, _) => ChatBackground); // light area of the photo near a corner
        PictureImage.ClearCorners(bmp, ChatBackground, radius: 16);
        Assert.Equal(255, Alpha(bmp, 15, 15));                // inside the arc: untouched
        Assert.Equal(255, Alpha(bmp, 25, 25));                // outside the corner square: untouched
    }
}
