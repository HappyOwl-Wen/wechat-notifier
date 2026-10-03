using System.Buffers.Binary;
using System.IO.Compression;

namespace WeChatNotifier.Core.Imaging;

/// <summary>
/// Minimal PNG writer (8-bit RGBA, "Sub" filter, zlib) for notification images. Windows shows notification
/// images only from files, and this avoids GDI+/WIC dependencies in the libraries.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(Bitmap32 bitmap)
    {
        using var png = new MemoryStream();
        png.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, bitmap.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], bitmap.Height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // colour type RGBA
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlace
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, Compress(bitmap));
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static byte[] Compress(Bitmap32 bitmap)
    {
        int stride = bitmap.Width * 4;
        var line = new byte[stride + 1];
        var p = bitmap.Pixels;
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = y * stride;
                line[0] = 1; // Sub: each byte minus the same byte of the pixel to its left
                for (int x = 0; x < stride; x += 4)
                {
                    int i = row + x;
                    byte r = p[i + 2], g = p[i + 1], b = p[i], a = p[i + 3];
                    if (x > 0)
                    {
                        r -= p[i - 2];
                        g -= p[i - 3];
                        b -= p[i - 4];
                        a -= p[i - 1];
                    }
                    line[x + 1] = r;
                    line[x + 2] = g;
                    line[x + 3] = b;
                    line[x + 4] = a;
                }
                zlib.Write(line);
            }
        }
        return output.ToArray();
    }

    private static void WriteChunk(Stream png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        png.Write(type);
        png.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(Crc32(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu);
        png.Write(number);
    }

    internal static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
