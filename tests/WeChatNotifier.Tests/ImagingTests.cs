using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using WeChatNotifier.Core.Imaging;

namespace WeChatNotifier.Tests;

public class ImagingTests
{
    private static Bitmap32 Filled(int w, int h, byte b, byte g, byte r)
    {
        var bmp = new Bitmap32(w, h);
        for (int i = 0; i < bmp.Pixels.Length; i += 4)
        {
            bmp.Pixels[i] = b;
            bmp.Pixels[i + 1] = g;
            bmp.Pixels[i + 2] = r;
            bmp.Pixels[i + 3] = 255;
        }
        return bmp;
    }

    private static void Paint(Bitmap32 bmp, PixelRect rect, Func<int, int, (byte B, byte G, byte R)> color)
    {
        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            for (int x = rect.X; x < rect.Right; x++)
            {
                var (b, g, r) = color(x, y);
                int i = (y * bmp.Width + x) * 4;
                bmp.Pixels[i] = b;
                bmp.Pixels[i + 1] = g;
                bmp.Pixels[i + 2] = r;
            }
        }
    }

    /// <summary>A "photo": varied colours, nothing like the background.</summary>
    private static (byte, byte, byte) Photo(int x, int y) => ((byte)(x * 7 % 200), (byte)(y * 5 % 200), (byte)((x + y) * 3 % 200));

    [Fact]
    public void Png_round_trips_pixels()
    {
        var bmp = new Bitmap32(7, 5);
        new Random(42).NextBytes(bmp.Pixels);

        var png = PngEncoder.Encode(bmp);

        Assert.Equal(bmp.Pixels, DecodeRgbaPng(png, out int w, out int h).Pixels);
        Assert.Equal((7, 5), (w, h));
    }

    [Fact]
    public void Crop()
    {
        var bmp = Filled(40, 20, 10, 20, 30);
        Paint(bmp, new PixelRect(20, 0, 20, 20), (_, _) => (200, 200, 200));

        var crop = bmp.Crop(new PixelRect(18, 5, 4, 2));
        Assert.Equal((4, 2), (crop.Width, crop.Height));
        Assert.Equal(10, crop.Pixels[0]);              // left half: original colour
        Assert.Equal(200, crop.Pixels[(2 * 4)]);       // right half: painted
    }

    [Fact]
    public void Rect_helpers()
    {
        var a = new PixelRect(0, 0, 10, 10);
        Assert.Equal(new PixelRect(5, 5, 5, 5), a.Intersect(new PixelRect(5, 5, 20, 20)));
        Assert.True(a.Intersect(new PixelRect(20, 20, 5, 5)).IsEmpty);
        Assert.Equal(new PixelRect(1, 2, 3, 4), PixelRect.FromUia([1.2, 2, 3, 4]));
        Assert.Null(PixelRect.FromUia([1, 2, 0, 4]));
    }

    [Fact]
    public void Finds_the_picture_and_leaves_out_the_nickname_line()
    {
        var area = Filled(400, 200, 237, 237, 237);                          // chat background
        Paint(area, new PixelRect(12, 6, 60, 10), (x, _) => x % 3 == 0 ? ((byte)120, (byte)120, (byte)120) : ((byte)237, (byte)237, (byte)237)); // sparse "text"
        var picture = new PixelRect(10, 24, 160, 120);
        Paint(area, picture, Photo);

        Assert.Equal(picture, ImageAnalysis.FindPicture(area, minSide: 32));
    }

    [Fact]
    public void Loading_placeholder_and_tiny_blocks_are_not_pictures()
    {
        var loading = Filled(400, 200, 237, 237, 237);
        Paint(loading, new PixelRect(10, 20, 160, 120), (_, _) => (200, 200, 200)); // flat grey box
        Assert.Null(ImageAnalysis.FindPicture(loading, minSide: 32));

        var tiny = Filled(400, 200, 237, 237, 237);
        Paint(tiny, new PixelRect(10, 20, 20, 20), Photo);
        Assert.Null(ImageAnalysis.FindPicture(tiny, minSide: 32));

        Assert.Null(ImageAnalysis.FindPicture(Filled(400, 200, 237, 237, 237), minSide: 32));
    }

    [Fact]
    public void Uniformity()
    {
        var bmp = Filled(50, 50, 100, 100, 100);
        Assert.True(ImageAnalysis.IsNearlyUniform(bmp, bmp.Bounds));
        Paint(bmp, bmp.Bounds, Photo);
        Assert.False(ImageAnalysis.IsNearlyUniform(bmp, bmp.Bounds));
    }

    /// <summary>Independent minimal PNG reader: chunk CRCs, zlib inflate, filters None/Sub; RGBA → BGRA.</summary>
    private static Bitmap32 DecodeRgbaPng(byte[] png, out int width, out int height)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        int pos = 8;
        width = height = 0;
        using var idat = new MemoryStream();
        while (pos < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, length);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + length));
            Assert.Equal(crc, PngEncoder.Crc32(PngEncoder.Crc32(0xFFFFFFFF, png.AsSpan(pos + 4, 4)), data) ^ 0xFFFFFFFF);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                Assert.Equal(8, data[8]);
                Assert.Equal(6, data[9]);
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }
            pos += 12 + length;
        }

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var bytes = raw.ToArray();
        int stride = width * 4;
        var result = new Bitmap32(width, height);
        for (int y = 0; y < height; y++)
        {
            int filter = bytes[y * (stride + 1)];
            Assert.InRange(filter, 0, 1);
            var line = bytes.AsSpan(y * (stride + 1) + 1, stride).ToArray();
            if (filter == 1) for (int i = 4; i < stride; i++) line[i] = (byte)(line[i] + line[i - 4]);
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                result.Pixels[o] = line[x * 4 + 2];
                result.Pixels[o + 1] = line[x * 4 + 1];
                result.Pixels[o + 2] = line[x * 4];
                result.Pixels[o + 3] = line[x * 4 + 3];
            }
        }
        return result;
    }
}
