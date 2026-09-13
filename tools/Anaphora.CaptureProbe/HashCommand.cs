using System.Globalization;
using Anaphora.Analysis;
using Anaphora.Core;

namespace Anaphora.CaptureProbe;

/// <summary>
/// Perceptual hashes of the same rectangle across frames, and the distance
/// between every pair. Registering a portrait means knowing how far apart the
/// same operator hashes across frames and how far apart different operators
/// hash; this prints both.
///
///   hash x y w h label=file.png [label=file.png ...]
/// </summary>
internal static class HashCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("usage: hash <x> <y> <w> <h> <label=file.png> [label=file.png ...]");
            return 64;
        }

        var rect = new PixelRect(
            int.Parse(args[0], CultureInfo.InvariantCulture),
            int.Parse(args[1], CultureInfo.InvariantCulture),
            int.Parse(args[2], CultureInfo.InvariantCulture),
            int.Parse(args[3], CultureInfo.InvariantCulture));

        var hashes = new List<(string Label, ulong Hash)>();
        foreach (string spec in args[4..])
        {
            int split = spec.IndexOf('=', StringComparison.Ordinal);
            string label = split > 0 ? spec[..split] : Path.GetFileNameWithoutExtension(spec);
            string file = split > 0 ? spec[(split + 1)..] : spec;

            byte[] pixels = Png.ReadBgra(Path.GetFullPath(file), out int width, out int height);
            ulong hash = PerceptualHash.Compute(new FrameView(pixels, width, height), rect);
            hashes.Add((label, hash));
            Console.WriteLine($"{label,-12} 0x{hash:X16}");
        }

        Console.WriteLine();
        Console.Write(new string(' ', 12));
        foreach ((string label, _) in hashes)
        {
            Console.Write($" {label,8}");
        }

        Console.WriteLine();
        foreach ((string rowLabel, ulong rowHash) in hashes)
        {
            Console.Write($"{rowLabel,-12}");
            foreach ((_, ulong columnHash) in hashes)
            {
                Console.Write($" {PerceptualHash.Distance(rowHash, columnHash),8}");
            }

            Console.WriteLine();
        }

        return 0;
    }
}
