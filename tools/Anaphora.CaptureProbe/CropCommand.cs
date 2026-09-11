using System.Globalization;

namespace Anaphora.CaptureProbe;

/// <summary>
/// Cuts a rectangle out of an already-captured PNG. Used to inspect ROI
/// candidates at native resolution: a 3840x2160 frame is unreadable as a whole,
/// but a 400x120 slice of the HUD shows exactly what the analysis layer has to
/// work with.
/// </summary>
internal static class CropCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("usage: crop <file.png> <x> <y> <w> <h> [out.png] [scale]");
            Console.Error.WriteLine("       coordinates are pixels, or fractions of the frame if written with a decimal point");
            return 64;
        }

        string source = Path.GetFullPath(args[0]);
        byte[] pixels = Png.ReadBgra(source, out int width, out int height);

        // Normalised coordinates are the project's storage convention, so accept
        // them here too: "0.5" means half the frame, "960" means 960 pixels.
        bool normalised = args.Take(5).Skip(1).Any(a => a.Contains('.', StringComparison.Ordinal));
        int x = Coord(args[1], width, normalised);
        int y = Coord(args[2], height, normalised);
        int w = Coord(args[3], width, normalised);
        int h = Coord(args[4], height, normalised);

        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        w = Math.Clamp(w, 1, width - x);
        h = Math.Clamp(h, 1, height - y);

        int scale = args.Length > 6 && int.TryParse(args[6], out int parsed) ? Math.Clamp(parsed, 1, 8) : 1;
        string destination = args.Length > 5 && args[5].Length > 0
            ? Path.GetFullPath(args[5])
            : Path.Combine(
                Path.GetDirectoryName(source)!,
                $"{Path.GetFileNameWithoutExtension(source)}-crop-{x}-{y}-{w}x{h}.png");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        byte[] crop = Png.Crop(pixels, width * 4, x, y, w, h);
        if (scale > 1)
        {
            crop = Magnify(crop, w, h, scale);
            w *= scale;
            h *= scale;
        }

        Png.WriteBgra(destination, crop, w, h, w * 4);

        Console.WriteLine($"source         : {source} ({width}x{height})");
        Console.WriteLine($"rect           : x={x} y={y} w={w / scale} h={h / scale}");
        Console.WriteLine($"normalised     : x={(double)x / width:F4} y={(double)y / height:F4} w={(double)(w / scale) / width:F4} h={(double)(h / scale) / height:F4}");
        Console.WriteLine($"wrote          : {destination} ({w}x{h}{(scale > 1 ? $", {scale}x nearest" : string.Empty)})");
        return 0;
    }

    internal static int Coord(string value, int extent, bool normalised)
    {
        double parsed = double.Parse(value, CultureInfo.InvariantCulture);
        return normalised && parsed <= 1.0 ? (int)Math.Round(parsed * extent) : (int)Math.Round(parsed);
    }

    /// <summary>Nearest-neighbour zoom: keeps pixel edges hard so thresholds stay judgeable.</summary>
    private static byte[] Magnify(byte[] bgra, int width, int height, int scale)
    {
        int outWidth = width * scale;
        byte[] dst = new byte[outWidth * height * scale * 4];

        for (int y = 0; y < height * scale; y++)
        {
            int srcRow = (y / scale) * width * 4;
            int dstRow = y * outWidth * 4;
            for (int x = 0; x < outWidth; x++)
            {
                Buffer.BlockCopy(bgra, srcRow + ((x / scale) * 4), dst, dstRow + (x * 4), 4);
            }
        }

        return dst;
    }
}
