using System.Diagnostics;
using System.Globalization;
using System.Text;
using Anaphora.Analysis;
using Anaphora.Capture;
using Anaphora.Core;

namespace Anaphora.CaptureProbe;

/// <summary>
/// Runs the production pipeline -- Anaphora.Capture's session and GPU atlas
/// feeding Anaphora.Analysis's readers -- against a live window for a while and
/// reports what it read.
///
/// Unlike the rest of the probe, which keeps its own hand-rolled capture path
/// on purpose, this command exists to exercise the real one. With verification
/// on, it also proves the GPU atlas copy matches a CPU copy of the same frame
/// byte for byte; that part needs no game, any window will do.
/// </summary>
internal static class HudCommand
{
    public static int Run(string[] args)
    {
        string processName = args.Length > 0 ? args[0] : "Endfield";
        int seconds = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 10;
        double rate = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 20;
        int verify = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 0;
        string? profilePath = args.Length > 4 ? args[4] : null;

        GameWindow? window = GameWindow.Find(processName);
        if (window is null)
        {
            Console.Error.WriteLine($"[FAIL] no process named '{processName}' with a top-level window.");
            return 1;
        }

        GameProfile profile = LoadProfile(profilePath);
        Console.WriteLine($"window         : {window.ProcessName} 0x{window.Handle:X} \"{window.Title}\"");
        Console.WriteLine($"profile        : {profile.Id}");
        Console.WriteLine($"plan           : {seconds}s at {rate} Hz{(verify > 0 ? $", verifying every {verify}th frame" : string.Empty)}");
        Console.WriteLine();

        HudRun result = Capture(window.Handle, profile, TimeSpan.FromSeconds(seconds), rate, verify, Console.Out);

        Console.WriteLine();
        Console.WriteLine(result.Summary());
        return result.Failure is null && result.Stats.VerificationFailures == 0 ? 0 : 9;
    }

    public static GameProfile LoadProfile(string? path)
    {
        string[] candidates = path is not null
            ? [path]
            :
            [
                Path.Combine(AppContext.BaseDirectory, "profiles", "endfield.json"),
                Path.Combine(Environment.CurrentDirectory, "profiles", "endfield.json"),
            ];

        string found = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"no profile at {string.Join(" or ", candidates)}");
        return ProfileStore.Load(found);
    }

    public static HudRun Capture(nint window, GameProfile profile, TimeSpan duration, double rate, int verify, TextWriter? log)
    {
        var timeline = new List<string>();
        var clock = Stopwatch.StartNew();
        string? previous = null;
        int snapshots = 0;
        int present = 0;
        Exception? failure = null;
        bool closed = false;

        using HudCapture capture = HudCapture.Create(
            window,
            profile,
            new CaptureOptions { Rate = rate, VerifyEvery = verify });

        capture.SnapshotReady += snapshot =>
        {
            snapshots++;
            if (snapshot.HudPresent)
            {
                present++;
            }

            // Only changes are worth a line; at 20 Hz an unchanged HUD would bury them.
            string line = Describe(snapshot);
            if (line != previous)
            {
                previous = line;
                string entry = string.Create(CultureInfo.InvariantCulture, $"{clock.Elapsed.TotalSeconds,6:F2}s  {line}");
                lock (timeline)
                {
                    timeline.Add(entry);
                }

                log?.WriteLine(entry);
            }
        };
        capture.Faulted += ex => failure = ex;
        capture.Closed += () => closed = true;

        capture.Start();
        Thread.Sleep(duration);

        CaptureStats stats = capture.Stats;
        double elapsed = clock.Elapsed.TotalSeconds;

        lock (timeline)
        {
            return new HudRun(stats, elapsed, snapshots, present, [.. timeline], failure, closed);
        }
    }

    public static string Describe(HudSnapshot snapshot)
    {
        if (!snapshot.HudPresent)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"no hud  (dark {snapshot.Presence.DarkFraction:P0}, lit {snapshot.Presence.LitFraction:P0})");
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"sp {snapshot.SkillPoints}  chain [");
        text.AppendJoin(' ', snapshot.Chains.Select(c => c.IsReady ? " RDY" : string.Create(CultureInfo.InvariantCulture, $"{c.Ratio * 100,3:F0}%")));
        text.Append("]  ult [");
        text.AppendJoin(' ', snapshot.Ultimates.Select(u => u.IsReady ? "RDY" : " - "));
        text.Append("]  prompt [");
        text.AppendJoin(' ', snapshot.Prompts.Select(p => p.PortraitId ?? "-"));
        text.Append(']');
        return text.ToString();
    }
}

internal sealed record HudRun(
    CaptureStats Stats,
    double Seconds,
    int Snapshots,
    int Present,
    IReadOnlyList<string> Timeline,
    Exception? Failure,
    bool Closed)
{
    public string Summary()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"frames arrived : {Stats.Arrived} ({Stats.Arrived / Seconds:F1}/s)");
        text.AppendLine(CultureInfo.InvariantCulture, $"processed      : {Stats.Processed} ({Stats.Processed / Seconds:F1}/s), last took {Stats.LastProcessMilliseconds:F2} ms");
        text.AppendLine(CultureInfo.InvariantCulture, $"dropped        : {Stats.Throttled} over rate, {Stats.Busy} while busy, {Stats.Skipped} skipped (resize or no client area)");
        text.AppendLine(CultureInfo.InvariantCulture, $"os throttle    : {(Stats.MinUpdateIntervalApplied ? "MinUpdateInterval applied" : "not available, software only")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"atlas          : {Stats.AtlasWidth}x{Stats.AtlasHeight}, client area {Stats.ClientArea}");
        text.AppendLine(CultureInfo.InvariantCulture, $"hud present    : {Present} of {Snapshots} snapshots");
        if (Stats.Verified > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"verification   : {Stats.Verified - Stats.VerificationFailures} of {Stats.Verified} GPU atlases matched the CPU copy");
        }

        if (Closed)
        {
            text.AppendLine("window         : closed during the run");
        }

        text.Append(Failure is null ? "[OK] pipeline ran." : $"[FAIL] {Failure.GetType().Name}: {Failure.Message}");
        return text.ToString();
    }
}
