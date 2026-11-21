using System.Buffers.Binary;
using System.IO.Compression;
using WeChatNotifier.Core.Imaging;

namespace WeChatNotifier.Tests;

public class PngDecoderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void All_filters_preserve_rows_and_alpha(int filter)
    {
        // Precomputed filtered scanlines for two 2-pixel RGBA rows. The second row needs the first row's values.
        byte[][] rows = filter switch
        {
            0 => [[10, 20, 30, 40, 50, 60, 70, 80], [20, 40, 60, 80, 10, 20, 30, 40]],
            1 => [[10, 20, 30, 40, 40, 40, 40, 40], [20, 40, 60, 80, 246, 236, 226, 216]],
            2 => [[10, 20, 30, 40, 50, 60, 70, 80], [10, 20, 30, 40, 216, 216, 216, 216]],
            3 => [[10, 20, 30, 40, 45, 50, 55, 60], [15, 30, 45, 60, 231, 226, 221, 216]],
            _ => [[10, 20, 30, 40, 40, 40, 40, 40], [10, 20, 30, 40, 216, 216, 216, 216]],
        };
        var png = MakePng([(byte)filter, ..rows[0], (byte)filter, ..rows[1]]);
        var decoded = PngDecoder.Decode(png);
        Assert.NotNull(decoded);
        Assert.Equal((2, 2), (decoded.Width, decoded.Height));
        Assert.Equal(new byte[] { 30, 20, 10, 40, 70, 60, 50, 80, 60, 40, 20, 80, 30, 20, 10, 40 }, decoded.Pixels);
    }

    [Theory]
    [InlineData(new byte[] { 0, 1, 2, 3 })] // partial first scanline
    [InlineData(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })] // missing second row
    [InlineData(new byte[] { 5, 1, 2, 3, 4, 5, 6, 7, 8 })] // unsupported filter
    public void Incomplete_or_invalid_scanlines_are_rejected(byte[] raw)
    {
        Assert.Null(PngDecoder.Decode(MakePng(raw)));
    }

    [Fact]
    public void Chunk_length_cannot_overflow_the_input_bounds()
    {
        var png = PngEncoder.Encode(new Bitmap32(1, 1));
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(8), int.MaxValue);
        Assert.Null(PngDecoder.Decode(png));
    }

    private static byte[] MakePng(byte[] raw)
    {
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, 2);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 2);
        header[8] = 8;
        header[9] = 6;
        Chunk(png, "IHDR"u8, header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionMode.Compress, leaveOpen: true)) zlib.Write(raw);
        // Separate IDAT chunks must still decode as a single stream.
        var data = compressed.ToArray();
        Chunk(png, "IDAT"u8, data.AsSpan(0, data.Length / 2));
        Chunk(png, "IDAT"u8, data.AsSpan(data.Length / 2));
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        png.Write(type);
        png.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, PngEncoder.Crc32(PngEncoder.Crc32(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF);
        png.Write(number);
    }
}
