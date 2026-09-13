using System.Globalization;
using System.Text;
using Anaphora.Analysis;
using Anaphora.Core;

namespace Anaphora.CaptureProbe;

/// <summary>
/// Feeds a saved burst through the readers and the rotation tracker, frame by
/// frame at the times they were captured, and prints what the app would have
/// shown. The offline twin of running the app during a fight: slower to sample,
/// but every frame can be looked at afterwards to settle who was right.
/// </summary>
internal static class ReplayCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: replay <burst-dir> [profile.json] [rotation.json]");
            return 64;
        }

        string directory = Path.GetFullPath(args[0]);
        GameProfile profile = HudCommand.LoadProfile(args.Length > 1 ? args[1] : null);
        Rotation rotation = LoadRotation(args.Length > 2 ? args[2] : null);

        List<(int Index, long Milliseconds, string File)> frames = ReadManifest(directory);
        if (frames.Count == 0)
        {
            Console.Error.WriteLine($"[FAIL] no full-resolution frames listed in {directory}\\manifest.csv");
            return 65;
        }

        var reader = new HudReader(profile);
        var tracker = new RotationTracker(rotation);
        Console.WriteLine($"replaying      : {frames.Count} frames, profile {profile.Id}, rotation {rotation.Id}");
        Console.WriteLine();

        foreach ((int index, long milliseconds, string file) in frames)
        {
            byte[] pixels = Png.ReadBgra(Path.Combine(directory, file), out int width, out int height);
            HudSnapshot snapshot = reader.Read(new FrameView(pixels, width, height));
            RotationState state = tracker.Observe(snapshot, TimeSpan.FromMilliseconds(milliseconds));

            var line = new StringBuilder();
            line.Append(CultureInfo.InvariantCulture, $"{index:D3} {milliseconds / 1000.0,6:F1}s  ");
            line.Append(HudCommand.Describe(snapshot).PadRight(62));
            if (snapshot.HudPresent)
            {
                // The evidence behind each ultimate verdict, for calibrating ReadyCoverage.
                line.Append(CultureInfo.InvariantCulture, $"  ring [{string.Join(' ', snapshot.Ultimates.Select(u => $"{u.RingCoverage * 100,3:F0}"))}]");
            }
            line.Append(CultureInfo.InvariantCulture, $"  | now {state.Current.Step.Action,-6}");
            line.Append(CultureInfo.InvariantCulture, $" r{state.Cursor.Round} {state.Cursor.Index + 1}/{state.StepsInRound}");

            if (state.Current.Blocked)
            {
                line.Append(" BLOCKED");
            }

            if (state.Completed.Count > 0)
            {
                line.Append(CultureInfo.InvariantCulture, $"  done {string.Join(" ", state.Completed)}");
            }

            if (state.Unexpected.Count > 0)
            {
                line.Append(CultureInfo.InvariantCulture, $"  ?? {string.Join(", ", state.Unexpected)}");
            }

            Console.WriteLine(line.ToString());
        }

        return 0;
    }

    private static Rotation LoadRotation(string? path)
    {
        string[] candidates = path is not null
            ? [path]
            :
            [
                Path.Combine(AppContext.BaseDirectory, "rotations", "endfield-example.json"),
                Path.Combine(Environment.CurrentDirectory, "rotations", "endfield-example.json"),
            ];

        string found = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"no rotation at {string.Join(" or ", candidates)}");
        return RotationStore.Load(found);
    }

    private static List<(int, long, string)> ReadManifest(string directory)
    {
        var frames = new List<(int, long, string)>();
        foreach (string line in File.ReadLines(Path.Combine(directory, "manifest.csv")).Skip(1))
        {
            string[] cells = line.Split(',');
            if (cells.Length >= 5 && cells[4].Length > 0)
            {
                frames.Add((
                    int.Parse(cells[0], CultureInfo.InvariantCulture),
                    long.Parse(cells[1], CultureInfo.InvariantCulture),
                    cells[4]));
            }
        }

        return frames;
    }
}
