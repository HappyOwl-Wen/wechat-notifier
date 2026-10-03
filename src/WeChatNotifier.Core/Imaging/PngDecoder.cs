using System.Buffers.Binary;
using System.IO.Compression;

namespace WeChatNotifier.Core.Imaging;

/// <summary>
/// Reads back the PNGs that <see cref="PngEncoder"/> writes (8-bit RGBA, not interlaced; every filter type), e.g. the
/// avatars kept across restarts. Anything else — or a damaged file — gives null.
/// </summary>
public static class PngDecoder
{
    /// <summary>Larger images are refused (the files read here are small avatars).</summary>
    internal const int MaxSide = 4096;

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static Bitmap32? Decode(ReadOnlySpan<byte> png)
    {
        if (!png.StartsWith(Signature)) return null;
        int width = 0, height = 0;
        using var compressed = new MemoryStream();
        int pos = Signature.Length;
        while (true)
        {
            if (pos + 12 > png.Length) return null;
            int length = BinaryPrimitives.ReadInt32BigEndian(png[pos..]);
            if (length < 0 || pos + 12 + length > png.Length) return null;
            var type = png.Slice(pos + 4, 4);
            var data = png.Slice(pos + 8, length);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png[(pos + 8 + length)..]);
            if ((PngEncoder.Crc32(PngEncoder.Crc32(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu) != crc) return null;
            pos += 12 + length;

            if (type.SequenceEqual("IHDR"u8))
            {
                if (length != 13) return null;
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                // 8-bit RGBA, deflate, standard filtering, no interlace — what PngEncoder writes.
                if (data[8] != 8 || data[9] != 6 || data[10] != 0 || data[11] != 0 || data[12] != 0) return null;
                if (width is <= 0 or > MaxSide || height is <= 0 or > MaxSide) return null;
            }
            else if (type.SequenceEqual("IDAT"u8)) compressed.Write(data);
            else if (type.SequenceEqual("IEND"u8)) break;
        }
        if (width == 0) return null;

        int stride = width * 4;
        var raw = new byte[height * (stride + 1)];
        try
        {
            compressed.Position = 0;
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            zlib.ReadExactly(raw);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return null;
        }

        var bitmap = new Bitmap32(width, height);
        var previous = new byte[stride];
        var line = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, line, 0, stride);
            if (!Unfilter(filter, line, previous)) return null;
            for (int x = 0; x < stride; x += 4)
            {
                int o = y * stride + x;
                bitmap.Pixels[o] = line[x + 2];     // B
                bitmap.Pixels[o + 1] = line[x + 1]; // G
                bitmap.Pixels[o + 2] = line[x];     // R
                bitmap.Pixels[o + 3] = line[x + 3]; // A
            }
            (previous, line) = (line, previous);
        }
        return bitmap;
    }

    /// <summary>Undoes one row's filter in place (4 bytes per pixel); false for an unknown filter type.</summary>
    private static bool Unfilter(int filter, byte[] line, byte[] previous)
    {
        if (filter is < 0 or > 4) return false;
        for (int i = 0; i < line.Length; i++)
        {
            int left = i >= 4 ? line[i - 4] : 0, up = previous[i], upLeft = i >= 4 ? previous[i - 4] : 0;
            int predictor = filter switch
            {
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upLeft),
                _ => 0,
            };
            line[i] = (byte)(line[i] + predictor);
        }
        return true;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
