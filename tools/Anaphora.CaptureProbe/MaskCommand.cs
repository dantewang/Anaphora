using System.Globalization;

namespace Anaphora.CaptureProbe;

/// <summary>
/// Blacks out everything outside the given rectangles, keeping the frame at its
/// original size.
///
/// This exists to make committable test fixtures. Readers address pixels through
/// normalised coordinates, so a cropped frame would need every bound rewritten;
/// keeping the full 3840x2160 canvas means the shipped profile applies verbatim.
/// A frame that is 98% pure black costs a couple of hundred kilobytes, against
/// 15 MB for the original, and captures/ is not in the repository.
/// </summary>
internal static class MaskCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: mask <in.png> <out.png> <x,y,w,h> [<x,y,w,h> ...]");
            return 64;
        }

        string source = Path.GetFullPath(args[0]);
        string destination = Path.GetFullPath(args[1]);
        byte[] pixels = Png.ReadBgra(source, out int width, out int height);

        var keep = new List<(int X, int Y, int W, int H)>();
        foreach (string spec in args[2..])
        {
            string[] parts = spec.Split(',');
            if (parts.Length != 4)
            {
                Console.Error.WriteLine($"[FAIL] '{spec}' is not x,y,w,h");
                return 64;
            }

            int x = int.Parse(parts[0], CultureInfo.InvariantCulture);
            int y = int.Parse(parts[1], CultureInfo.InvariantCulture);
            int w = int.Parse(parts[2], CultureInfo.InvariantCulture);
            int h = int.Parse(parts[3], CultureInfo.InvariantCulture);
            keep.Add((
                Math.Clamp(x, 0, width - 1),
                Math.Clamp(y, 0, height - 1),
                Math.Clamp(w, 1, width - Math.Clamp(x, 0, width - 1)),
                Math.Clamp(h, 1, height - Math.Clamp(y, 0, height - 1))));
        }

        byte[] masked = new byte[pixels.Length];
        for (int i = 3; i < masked.Length; i += 4)
        {
            masked[i] = 255;
        }

        long kept = 0;
        foreach ((int x, int y, int w, int h) in keep)
        {
            for (int row = y; row < y + h; row++)
            {
                Buffer.BlockCopy(pixels, (row * width * 4) + (x * 4), masked, (row * width * 4) + (x * 4), w * 4);
            }

            kept += (long)w * h;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Png.WriteBgra(destination, masked, width, height, width * 4);

        var info = new FileInfo(destination);
        Console.WriteLine(
            $"{Path.GetFileName(source)} -> {Path.GetFileName(destination)}  " +
            $"kept {kept * 100.0 / ((long)width * height):F1}% of pixels, {info.Length / 1024.0:F0} KB");
        return 0;
    }
}
