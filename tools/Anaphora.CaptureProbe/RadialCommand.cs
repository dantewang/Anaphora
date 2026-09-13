using System.Globalization;

namespace Anaphora.CaptureProbe;

/// <summary>
/// Prints colour against distance from a centre, averaged over a thin wedge.
/// Circular HUD elements -- an ultimate slot's charge arc inside its track, the
/// gold ring that means "ready" -- are defined by radius, and a rectangle scan
/// cannot separate one ring from the next.
///
///   radial file.png cx cy radius [angle-degrees, clockwise from 12 o'clock] [half-width-degrees]
/// </summary>
internal static class RadialCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: radial <file.png> <cx> <cy> <radius> [angle] [half-width]");
            return 64;
        }

        byte[] pixels = Png.ReadBgra(Path.GetFullPath(args[0]), out int width, out int height);
        int cx = int.Parse(args[1], CultureInfo.InvariantCulture);
        int cy = int.Parse(args[2], CultureInfo.InvariantCulture);
        int radius = int.Parse(args[3], CultureInfo.InvariantCulture);
        double angle = args.Length > 4 ? double.Parse(args[4], CultureInfo.InvariantCulture) : 90;
        double halfWidth = args.Length > 5 ? double.Parse(args[5], CultureInfo.InvariantCulture) : 4;

        Console.WriteLine($"centre ({cx},{cy}), angle {angle}° ±{halfWidth}° clockwise from 12 o'clock");
        Console.WriteLine("  r      rgb      luma   sat");

        for (int r = 0; r <= radius; r++)
        {
            double sumR = 0, sumG = 0, sumB = 0;
            int count = 0;

            for (double a = angle - halfWidth; a <= angle + halfWidth; a += 0.5)
            {
                double radians = a * Math.PI / 180;
                int x = (int)Math.Round(cx + (r * Math.Sin(radians)));
                int y = (int)Math.Round(cy - (r * Math.Cos(radians)));
                if (x < 0 || y < 0 || x >= width || y >= height)
                {
                    continue;
                }

                int p = ((y * width) + x) * 4;
                sumB += pixels[p];
                sumG += pixels[p + 1];
                sumR += pixels[p + 2];
                count++;
            }

            if (count == 0)
            {
                continue;
            }

            byte red = (byte)(sumR / count), green = (byte)(sumG / count), blue = (byte)(sumB / count);
            double luma = (red * 0.2126) + (green * 0.7152) + (blue * 0.0722);
            int max = Math.Max(red, Math.Max(green, blue)), min = Math.Min(red, Math.Min(green, blue));
            double saturation = max == 0 ? 0 : (double)(max - min) / max;

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {r,2}  #{red:X2}{green:X2}{blue:X2}  {luma,5:F0}  {saturation:F2}  {new string('#', (int)(luma / 16))}"));
        }

        return 0;
    }
}
