using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Anaphora.CaptureProbe;

/// <summary>
/// Minimal PNG writer. The probe only needs to dump a BGRA buffer to disk, and
/// hand-rolling ~60 lines beats dragging System.Drawing.Common into the tree.
/// </summary>
internal static class Png
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void WriteBgra(string path, byte[] bgra, int width, int height, int stride)
    {
        using var file = File.Create(path);
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4, 4), (uint)height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // colour type: truecolour, no alpha
        WriteChunk(file, "IHDR", header);

        // One filter byte (0 = None) per scanline, then RGB triples.
        byte[] raw = new byte[(width * 3 + 1) * height];
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            raw[o++] = 0;
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int p = row + (x * 4);
                raw[o++] = bgra[p + 2];
                raw[o++] = bgra[p + 1];
                raw[o++] = bgra[p];
            }
        }

        using var deflated = new MemoryStream();
        using (var zlib = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        WriteChunk(file, "IDAT", deflated.GetBuffer().AsSpan(0, (int)deflated.Length));
        WriteChunk(file, "IEND", []);
    }

    /// <summary>Box-averaging downscale by an integer factor, BGRA in and out.</summary>
    public static byte[] Downscale(byte[] bgra, int width, int height, int stride, int factor, out int outWidth, out int outHeight)
    {
        outWidth = width / factor;
        outHeight = height / factor;
        byte[] dst = new byte[outWidth * outHeight * 4];

        for (int y = 0; y < outHeight; y++)
        {
            for (int x = 0; x < outWidth; x++)
            {
                int b = 0, g = 0, r = 0;
                for (int sy = 0; sy < factor; sy++)
                {
                    int row = ((y * factor) + sy) * stride;
                    for (int sx = 0; sx < factor; sx++)
                    {
                        int p = row + (((x * factor) + sx) * 4);
                        b += bgra[p];
                        g += bgra[p + 1];
                        r += bgra[p + 2];
                    }
                }

                int n = factor * factor;
                int d = ((y * outWidth) + x) * 4;
                dst[d] = (byte)(b / n);
                dst[d + 1] = (byte)(g / n);
                dst[d + 2] = (byte)(r / n);
                dst[d + 3] = 255;
            }
        }

        return dst;
    }

    public static byte[] Crop(byte[] bgra, int stride, int x0, int y0, int width, int height)
    {
        byte[] dst = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            Buffer.BlockCopy(bgra, ((y0 + y) * stride) + (x0 * 4), dst, y * width * 4, width * 4);
        }

        return dst;
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = Crc(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in type)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        foreach (byte b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
