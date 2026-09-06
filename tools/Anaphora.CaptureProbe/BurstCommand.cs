using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Windows.Graphics.Capture;

namespace Anaphora.CaptureProbe;

/// <summary>
/// Time-lapse capture: grab one frame every N milliseconds for a fixed duration
/// and write them all to disk. Used to collect real gameplay stills to mark ROIs
/// against -- a menu screenshot shows none of the bars, cooldowns or portraits
/// that the analysis layer actually has to read.
/// </summary>
internal static class BurstCommand
{
    /// <summary>
    /// Bounded so a slow encoder can never turn into unbounded memory growth. A
    /// 4K BGRA frame is 33 MB; the queue is deliberately shallow, and frames that
    /// arrive with it full are dropped rather than queued.
    /// </summary>
    private const int QueueDepth = 4;

    public static int Run(
        IntPtr hwnd,
        string outputDirectory,
        int durationSeconds,
        int intervalMs,
        int leadInSeconds,
        int fullEvery)
    {
        Directory.CreateDirectory(outputDirectory);

        using WgcSession capture = WgcSession.Create(hwnd, suppressBorder: true, Console.WriteLine);
        int planned = durationSeconds * 1000 / intervalMs;
        Console.WriteLine($"item           : \"{capture.Item.DisplayName}\" {capture.Item.Size.Width}x{capture.Item.Size.Height}");
        Console.WriteLine($"plan           : {durationSeconds}s at {intervalMs}ms intervals (~{planned} frames)");

        // A 4K PNG is ~15 MB, so keeping every frame at native resolution costs
        // gigabytes. Downscaled frames are enough to see what changes over a fight;
        // native resolution is only needed to judge whether a ROI is readable.
        Console.WriteLine($"resolution     : all frames downscaled, every {fullEvery}{Ordinal(fullEvery)} also at native ({planned / fullEvery} full frames, ~{planned / fullEvery * 15}MB)");
        Console.WriteLine($"output         : {outputDirectory}");

        if (leadInSeconds > 0)
        {
            Console.WriteLine($"lead-in        : {leadInSeconds}s before the first save");
        }

        Console.WriteLine();

        var queue = new BlockingCollection<Shot>(QueueDepth);
        var manifest = new ConcurrentBag<string>();
        int written = 0;
        int dropped = 0;

        var writer = new Thread(() =>
        {
            foreach (Shot shot in queue.GetConsumingEnumerable())
            {
                string name = $"frame-{shot.Index:D3}";

                int factor = Math.Max(1, shot.Frame.Width / 1280);
                byte[] small = Png.Downscale(
                    shot.Frame.Pixels, shot.Frame.Width, shot.Frame.Height, shot.Frame.Stride, factor,
                    out int sw, out int sh);
                Png.WriteBgra(Path.Combine(outputDirectory, $"{name}-small.png"), small, sw, sh, sw * 4);

                bool full = shot.Index % fullEvery == 0;
                if (full)
                {
                    Png.WriteBgra(
                        Path.Combine(outputDirectory, $"{name}.png"),
                        shot.Frame.Pixels,
                        shot.Frame.Width,
                        shot.Frame.Height,
                        shot.Frame.Stride);
                }

                double luma = MeanLuma(shot.Frame);
                manifest.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{shot.Index},{shot.ElapsedMs},{luma:F2},{name}-small.png,{(full ? $"{name}.png" : string.Empty)}"));

                int n = Interlocked.Increment(ref written);
                Console.Write($"\rsaved          : {n} frames (dropped {Volatile.Read(ref dropped)})   ");
            }
        })
        {
            IsBackground = true,
            Name = "burst-writer",
        };
        writer.Start();

        var clock = Stopwatch.StartNew();
        long leadInMs = leadInSeconds * 1000L;
        long nextDue = leadInMs;
        int arrived = 0;
        int index = 0;
        int capturing = 0;
        bool stopping = false;

        capture.Start(pool =>
        {
            using Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
            if (frame is null || Volatile.Read(ref stopping))
            {
                return;
            }

            Interlocked.Increment(ref arrived);

            long elapsed = clock.ElapsedMilliseconds;
            if (elapsed < Volatile.Read(ref nextDue))
            {
                return;
            }

            // One capture at a time; anything arriving mid-readback is dropped on
            // the floor, never queued. Same discipline the real capture loop needs.
            if (Interlocked.Exchange(ref capturing, 1) == 1)
            {
                return;
            }

            try
            {
                if (elapsed < Volatile.Read(ref nextDue))
                {
                    return;
                }

                Volatile.Write(ref nextDue, elapsed + intervalMs);
                Frame shot = capture.ReadBack(frame);
                if (!queue.TryAdd(new Shot(Interlocked.Increment(ref index) - 1, elapsed - leadInMs, shot)))
                {
                    Interlocked.Increment(ref dropped);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"\n[warn] readback failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref capturing, 0);
            }
        });

        Thread.Sleep(TimeSpan.FromSeconds(leadInSeconds + durationSeconds));
        Volatile.Write(ref stopping, true);
        Thread.Sleep(200);

        queue.CompleteAdding();
        writer.Join();
        Console.WriteLine();
        Console.WriteLine();

        int total = Volatile.Read(ref written);
        Console.WriteLine($"frames seen    : {Volatile.Read(ref arrived)} ({Volatile.Read(ref arrived) / clock.Elapsed.TotalSeconds:F1}/s from the game)");
        Console.WriteLine($"frames saved   : {total}");
        Console.WriteLine($"frames dropped : {Volatile.Read(ref dropped)} (encoder could not keep up)");

        if (total == 0)
        {
            Console.Error.WriteLine("[FAIL] nothing was saved.");
            return 6;
        }

        string manifestPath = Path.Combine(outputDirectory, "manifest.csv");
        var lines = new List<string> { "index,elapsed_ms,mean_luma,small,full" };
        lines.AddRange(manifest.OrderBy(line => int.Parse(line.Split(',')[0], CultureInfo.InvariantCulture)));
        File.WriteAllLines(manifestPath, lines, new UTF8Encoding(false));
        Console.WriteLine($"manifest       : {manifestPath}");

        Console.WriteLine();
        Console.WriteLine("[OK] burst complete.");
        return 0;
    }

    private static double MeanLuma(Frame frame)
    {
        long sum = 0;
        long count = 0;
        for (int y = 0; y < frame.Height; y += 8)
        {
            int row = y * frame.Stride;
            for (int x = 0; x < frame.Width; x += 8)
            {
                int p = row + (x * 4);
                sum += ((frame.Pixels[p + 2] * 54) + (frame.Pixels[p + 1] * 183) + (frame.Pixels[p] * 19)) >> 8;
                count++;
            }
        }

        return (double)sum / count;
    }

    private static string Ordinal(int n) => (n % 100) switch
    {
        11 or 12 or 13 => "th",
        _ => (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" },
    };

    private sealed record Shot(int Index, long ElapsedMs, Frame Frame);
}
