using System.Globalization;
using System.Text;

namespace Anaphora.CaptureProbe;

/// <summary>
/// Measures a rectangle instead of guessing at it: mean colour, the colours that
/// actually dominate, and -- with "scan" -- a per-column classification that
/// shows where a bar's fill starts and stops.
///
/// Every threshold in a profile has to come from somewhere. Reading them off a
/// screenshot by eye produces numbers that look plausible and fail on the first
/// frame with an effect in it.
/// </summary>
internal static class SampleCommand
{
    /// <summary>Below this luma a pixel is treated as background rather than content.</summary>
    private const double DarkLuma = 40;

    /// <summary>At or above this luma a pixel is lit: a border, a fill, a glow.</summary>
    private const double LitLuma = 150;

    /// <summary>Below this saturation a bright pixel is neutral: white, grey, silver.</summary>
    private const double NeutralSaturation = 0.20;

    public static int Run(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("usage: sample <file.png> <x> <y> <w> <h> [scan]");
            Console.Error.WriteLine("       scan adds a per-column (or per-row) classification strip");
            return 64;
        }

        string source = Path.GetFullPath(args[0]);
        byte[] pixels = Png.ReadBgra(source, out int width, out int height);

        int x = Math.Clamp(int.Parse(args[1], CultureInfo.InvariantCulture), 0, width - 1);
        int y = Math.Clamp(int.Parse(args[2], CultureInfo.InvariantCulture), 0, height - 1);
        int w = Math.Clamp(int.Parse(args[3], CultureInfo.InvariantCulture), 1, width - x);
        int h = Math.Clamp(int.Parse(args[4], CultureInfo.InvariantCulture), 1, height - y);
        bool scan = args.Length > 5 && args[5].Equals("scan", StringComparison.OrdinalIgnoreCase);

        Console.WriteLine($"source         : {Path.GetFileName(source)} ({width}x{height})");
        Console.WriteLine($"rect           : x={x} y={y} w={w} h={h}");
        Console.WriteLine($"normalised     : x={(double)x / width:F6} y={(double)y / height:F6} w={(double)w / width:F6} h={(double)h / height:F6}");
        Console.WriteLine();

        ReportMean(pixels, width, x, y, w, h);
        ReportLevels(pixels, width, x, y, w, h);
        Console.WriteLine();
        ReportHistogram(pixels, width, x, y, w, h);

        if (scan)
        {
            Console.WriteLine();
            ReportScan(pixels, width, x, y, w, h);
        }

        return 0;
    }

    private static void ReportMean(byte[] pixels, int stride, int x, int y, int w, int h)
    {
        long sumB = 0;
        long sumG = 0;
        long sumR = 0;

        for (int row = y; row < y + h; row++)
        {
            int start = (row * stride * 4) + (x * 4);
            for (int i = 0; i < w; i++)
            {
                sumB += pixels[start + (i * 4)];
                sumG += pixels[start + (i * 4) + 1];
                sumR += pixels[start + (i * 4) + 2];
            }
        }

        long count = (long)w * h;
        var mean = new Colour((byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count));

        Console.WriteLine($"mean           : {mean.Hex}  luma {mean.Luma:F1}  saturation {mean.Saturation:F2}");
    }

    /// <summary>
    /// The share of pixels that are unlit, mid, and lit. A HUD widget holds both
    /// extremes at once -- bright borders against a dark track -- while a
    /// full-screen cut-in or a menu backdrop is locally uniform and lands almost
    /// entirely in one band. That contrast is what tells them apart.
    /// </summary>
    private static void ReportLevels(byte[] pixels, int stride, int x, int y, int w, int h)
    {
        int unlit = 0;
        int mid = 0;
        int lit = 0;

        for (int row = y; row < y + h; row++)
        {
            int start = (row * stride * 4) + (x * 4);
            for (int i = 0; i < w; i++)
            {
                double luma =
                    (pixels[start + (i * 4) + 2] * 0.2126) +
                    (pixels[start + (i * 4) + 1] * 0.7152) +
                    (pixels[start + (i * 4)] * 0.0722);

                if (luma <= DarkLuma)
                {
                    unlit++;
                }
                else if (luma >= LitLuma)
                {
                    lit++;
                }
                else
                {
                    mid++;
                }
            }
        }

        double total = (double)w * h;
        Console.WriteLine(
            $"levels         : unlit(<={DarkLuma:F0}) {unlit / total * 100,5:F1}%   " +
            $"mid {mid / total * 100,5:F1}%   " +
            $"lit(>={LitLuma:F0}) {lit / total * 100,5:F1}%");
    }

    private static void ReportHistogram(byte[] pixels, int stride, int x, int y, int w, int h)
    {
        // Quantised to 5 bits per channel: enough to keep a bar's fill separate
        // from its track, coarse enough that anti-aliasing does not shatter the
        // counts into hundreds of near-identical buckets.
        var buckets = new Dictionary<int, int>();

        for (int row = y; row < y + h; row++)
        {
            int start = (row * stride * 4) + (x * 4);
            for (int i = 0; i < w; i++)
            {
                int b = pixels[start + (i * 4)] >> 3;
                int g = pixels[start + (i * 4) + 1] >> 3;
                int r = pixels[start + (i * 4) + 2] >> 3;
                int key = (r << 10) | (g << 5) | b;
                buckets[key] = buckets.GetValueOrDefault(key) + 1;
            }
        }

        double total = (double)w * h;
        Console.WriteLine("dominant       :");
        foreach ((int key, int count) in buckets.OrderByDescending(p => p.Value).Take(6))
        {
            var colour = new Colour(
                (byte)(((key >> 10) & 0x1F) << 3),
                (byte)(((key >> 5) & 0x1F) << 3),
                (byte)((key & 0x1F) << 3));

            Console.WriteLine(
                $"  {colour.Hex}  {count / total * 100,5:F1}%  luma {colour.Luma,5:F1}  sat {colour.Saturation:F2}  {Classify(colour)}");
        }
    }

    private static void ReportScan(byte[] pixels, int stride, int x, int y, int w, int h)
    {
        bool horizontal = w >= h;
        int steps = horizontal ? w : h;
        var classes = new char[steps];

        for (int i = 0; i < steps; i++)
        {
            long sumB = 0;
            long sumG = 0;
            long sumR = 0;
            int samples = horizontal ? h : w;

            for (int j = 0; j < samples; j++)
            {
                int px = horizontal ? x + i : x + j;
                int py = horizontal ? y + j : y + i;
                int p = (py * stride * 4) + (px * 4);
                sumB += pixels[p];
                sumG += pixels[p + 1];
                sumR += pixels[p + 2];
            }

            classes[i] = Symbol(new Colour(
                (byte)(sumR / samples),
                (byte)(sumG / samples),
                (byte)(sumB / samples)));
        }

        Console.WriteLine($"scan           : {(horizontal ? "columns" : "rows")}, '.' dark  'W' bright neutral  'Y' saturated");

        for (int offset = 0; offset < steps; offset += 100)
        {
            int take = Math.Min(100, steps - offset);
            int origin = (horizontal ? x : y) + offset;
            Console.WriteLine($"  {origin,5} |{new string(classes, offset, take)}|");
        }

        Console.WriteLine("runs           :");
        var runs = new List<(char Symbol, int Start, int Length)>();
        int runStart = 0;
        for (int i = 1; i <= steps; i++)
        {
            if (i == steps || classes[i] != classes[runStart])
            {
                runs.Add((classes[runStart], runStart, i - runStart));
                runStart = i;
            }
        }

        int printed = 0;
        foreach ((char symbol, int start, int length) in runs)
        {
            // Single-pixel runs are anti-aliasing, not structure.
            if (length < 3)
            {
                continue;
            }

            int origin = (horizontal ? x : y) + start;
            Console.WriteLine($"  {symbol}  {origin,5} .. {origin + length - 1,5}  ({length} px)");

            if (++printed >= 30)
            {
                Console.WriteLine("  ... truncated");
                break;
            }
        }
    }

    private static char Symbol(Colour colour) =>
        colour.Luma < DarkLuma ? '.' : colour.Saturation < NeutralSaturation ? 'W' : 'Y';

    private static string Classify(Colour colour) => Symbol(colour) switch
    {
        '.' => "dark",
        'W' => "bright neutral",
        _ => "saturated",
    };

    private readonly record struct Colour(byte R, byte G, byte B)
    {
        public double Luma => (R * 0.2126) + (G * 0.7152) + (B * 0.0722);

        public double Saturation
        {
            get
            {
                int max = Math.Max(R, Math.Max(G, B));
                int min = Math.Min(R, Math.Min(G, B));
                return max == 0 ? 0 : (double)(max - min) / max;
            }
        }

        public string Hex => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");
    }
}
