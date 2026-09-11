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
        WriteBgra(file, bgra, width, height, stride);
    }

    public static void WriteBgra(Stream file, byte[] bgra, int width, int height, int stride)
    {
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

    /// <summary>
    /// Reads back a truecolour PNG as BGRA. Only what this writer emits plus the
    /// standard filter set -- enough to re-open captured frames and cut ROI
    /// candidates out of them without a decoding dependency.
    /// </summary>
    public static byte[] ReadBgra(string path, out int width, out int height)
    {
        byte[] file = File.ReadAllBytes(path);
        if (file.Length < 8 || file[0] != 137 || file[1] != 'P' || file[2] != 'N' || file[3] != 'G')
        {
            throw new InvalidDataException($"{path} is not a PNG.");
        }

        int offset = 8;
        width = 0;
        height = 0;
        int channels = 0;
        using var idat = new MemoryStream();

        while (offset + 8 <= file.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(offset, 4));
            string type = Encoding.ASCII.GetString(file, offset + 4, 4);
            int dataStart = offset + 8;

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(dataStart, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(dataStart + 4, 4));
                    int depth = file[dataStart + 8];
                    int colourType = file[dataStart + 9];
                    if (depth != 8 || (colourType != 2 && colourType != 6))
                    {
                        throw new NotSupportedException(
                            $"{path}: only 8-bit truecolour PNG is supported (depth {depth}, colour type {colourType}).");
                    }

                    if (file[dataStart + 12] != 0)
                    {
                        throw new NotSupportedException($"{path}: interlaced PNG is not supported.");
                    }

                    channels = colourType == 6 ? 4 : 3;
                    break;

                case "IDAT":
                    idat.Write(file, dataStart, length);
                    break;
            }

            if (type == "IEND")
            {
                break;
            }

            offset = dataStart + length + 4;
        }

        idat.Position = 0;
        byte[] raw = new byte[(width * channels + 1) * height];
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
        {
            zlib.ReadExactly(raw);
        }

        return Unfilter(raw, width, height, channels);
    }

    private static byte[] Unfilter(byte[] raw, int width, int height, int channels)
    {
        int lineBytes = width * channels;
        byte[] bgra = new byte[width * height * 4];
        byte[] previous = new byte[lineBytes];
        byte[] current = new byte[lineBytes];

        for (int y = 0; y < height; y++)
        {
            int src = y * (lineBytes + 1);
            byte filter = raw[src];
            Buffer.BlockCopy(raw, src + 1, current, 0, lineBytes);

            for (int i = 0; i < lineBytes; i++)
            {
                int a = i >= channels ? current[i - channels] : 0;
                int b = previous[i];
                int c = i >= channels ? previous[i - channels] : 0;

                current[i] = filter switch
                {
                    0 => current[i],
                    1 => (byte)(current[i] + a),
                    2 => (byte)(current[i] + b),
                    3 => (byte)(current[i] + ((a + b) >> 1)),
                    4 => (byte)(current[i] + Paeth(a, b, c)),
                    _ => throw new InvalidDataException($"unknown PNG filter {filter} on row {y}."),
                };
            }

            int dst = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int s = x * channels;
                bgra[dst + (x * 4)] = current[s + 2];
                bgra[dst + (x * 4) + 1] = current[s + 1];
                bgra[dst + (x * 4) + 2] = current[s];
                bgra[dst + (x * 4) + 3] = channels == 4 ? current[s + 3] : (byte)255;
            }

            (previous, current) = (current, previous);
        }

        return bgra;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
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
