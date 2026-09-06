using System.Diagnostics;
using Windows.Graphics.Capture;

namespace Anaphora.CaptureProbe;

/// <summary>
/// Watch the window for a few seconds, then read one frame back and say whether
/// it contains anything. Answers "does WGC work against this game at all".
/// </summary>
internal static class ProbeCommand
{
    private const int ObserveSeconds = 3;

    public static int Run(IntPtr hwnd, string outputDirectory)
    {
        Frame? captured;

        using (WgcSession capture = WgcSession.Create(hwnd, suppressBorder: true, Console.WriteLine))
        {
            Console.WriteLine($"item           : \"{capture.Item.DisplayName}\" {capture.Item.Size.Width}x{capture.Item.Size.Height}");

            int frameCount = 0;
            int wantCapture = 0;
            Frame? readBack = null;
            using var captureDone = new ManualResetEventSlim(false);

            capture.Start(pool =>
            {
                using Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }

                Interlocked.Increment(ref frameCount);

                // Drop every frame until the observation window is over, then read
                // exactly one back. Same "never queue, never block the callback"
                // discipline the real capture loop will need.
                if (Interlocked.CompareExchange(ref wantCapture, 2, 1) != 1)
                {
                    return;
                }

                try
                {
                    readBack = capture.ReadBack(frame);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[FAIL] readback threw: {ex}");
                }
                finally
                {
                    captureDone.Set();
                }
            });

            var stopwatch = Stopwatch.StartNew();
            Thread.Sleep(TimeSpan.FromSeconds(ObserveSeconds));
            double observed = stopwatch.Elapsed.TotalSeconds;
            int observedFrames = Volatile.Read(ref frameCount);

            Console.WriteLine($"frames         : {observedFrames} in {observed:F2}s ({observedFrames / observed:F1}/s)");

            if (observedFrames == 0)
            {
                Console.Error.WriteLine("[FAIL] the frame pool never fired. Nothing is being presented, or capture is blocked.");
                return 3;
            }

            Volatile.Write(ref wantCapture, 1);
            if (!captureDone.Wait(TimeSpan.FromSeconds(5)))
            {
                Console.Error.WriteLine("[FAIL] frames arrive but none could be read back.");
                return 4;
            }

            captured = readBack;
        }

        if (captured is null)
        {
            Console.Error.WriteLine("[FAIL] readback produced nothing.");
            return 4;
        }

        return Report(captured, outputDirectory);
    }

    private static int Report(Frame frame, string outputDirectory)
    {
        Console.WriteLine($"texture        : {frame.Width}x{frame.Height}, row pitch {frame.Stride}");
        Console.WriteLine();

        Statistics stats = Analyse(frame);
        Console.WriteLine($"luma mean      : {stats.MeanLuma:F2} / 255");
        Console.WriteLine($"luma range     : {stats.MinLuma} .. {stats.MaxLuma}");
        Console.WriteLine($"non-black      : {stats.NonBlackFraction * 100:F2}% of pixels");
        Console.WriteLine($"distinct cols  : {stats.DistinctColours} (sampled, capped at 4096)");
        Console.WriteLine();

        Directory.CreateDirectory(outputDirectory);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        string fullPath = Path.Combine(outputDirectory, $"{stamp}-full.png");
        Png.WriteBgra(fullPath, frame.Pixels, frame.Width, frame.Height, frame.Stride);
        Console.WriteLine($"wrote          : {fullPath}");

        int factor = Math.Max(1, frame.Width / 1280);
        byte[] small = Png.Downscale(frame.Pixels, frame.Width, frame.Height, frame.Stride, factor, out int sw, out int sh);
        string previewPath = Path.Combine(outputDirectory, $"{stamp}-preview.png");
        Png.WriteBgra(previewPath, small, sw, sh, sw * 4);
        Console.WriteLine($"wrote          : {previewPath} ({sw}x{sh})");

        // The top-left corner is where readable text is expected, so keep a
        // native-resolution crop that survives the downscale.
        int cropWidth = Math.Min(1400, frame.Width);
        int cropHeight = Math.Min(500, frame.Height);
        byte[] crop = Png.Crop(frame.Pixels, frame.Stride, 0, 0, cropWidth, cropHeight);
        string cropPath = Path.Combine(outputDirectory, $"{stamp}-topleft.png");
        Png.WriteBgra(cropPath, crop, cropWidth, cropHeight, cropWidth * 4);
        Console.WriteLine($"wrote          : {cropPath} ({cropWidth}x{cropHeight})");

        Console.WriteLine();
        if (stats.NonBlackFraction < 0.001)
        {
            Console.Error.WriteLine("[FAIL] the frame is black. Capture is being blocked or the surface is protected.");
            return 5;
        }

        Console.WriteLine("[OK] WGC returned real pixels from the game window.");
        return 0;
    }

    private static Statistics Analyse(Frame frame)
    {
        long lumaSum = 0;
        long total = 0;
        long nonBlack = 0;
        int min = 255;
        int max = 0;
        var colours = new HashSet<int>();

        // Every 4th pixel on every 4th row: plenty for a sanity check, 16x cheaper.
        for (int y = 0; y < frame.Height; y += 4)
        {
            int row = y * frame.Stride;
            for (int x = 0; x < frame.Width; x += 4)
            {
                int p = row + (x * 4);
                int b = frame.Pixels[p];
                int g = frame.Pixels[p + 1];
                int r = frame.Pixels[p + 2];
                int luma = ((r * 54) + (g * 183) + (b * 19)) >> 8;

                lumaSum += luma;
                total++;
                if (luma > 8)
                {
                    nonBlack++;
                }

                min = Math.Min(min, luma);
                max = Math.Max(max, luma);
                if (colours.Count < 4096)
                {
                    colours.Add((r << 16) | (g << 8) | b);
                }
            }
        }

        return new Statistics((double)lumaSum / total, min, max, (double)nonBlack / total, colours.Count);
    }

    private sealed record Statistics(
        double MeanLuma,
        int MinLuma,
        int MaxLuma,
        double NonBlackFraction,
        int DistinctColours);
}
