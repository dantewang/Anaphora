using System.Globalization;
using System.Text;
using Anaphora.Core;

namespace Anaphora.Analysis.Tests;

/// <summary>
/// Not an assertion: writes what the readers actually see across every fixture
/// so thresholds can be judged against the frames rather than argued about.
/// Set ANAPHORA_DUMP to a path to collect it.
/// </summary>
public class DumpReadings
{
    [Fact]
    public void WriteReadingsWhenAsked()
    {
        string? path = Environment.GetEnvironmentVariable("ANAPHORA_DUMP");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var report = new StringBuilder();
        var skillPoints = Fixture.Profile.RoisOf<SegmentedBarRoi>().First();

        foreach (string frame in new[] { "008", "020", "032", "036", "040", "024", "048", "084" })
        {
            byte[] pixels = Fixture.Load(frame, out int width, out int height);
            var view = new FrameView(pixels, width, height);
            HudSnapshot snapshot = new HudReader(Fixture.Profile).Read(view);

            report.AppendLine(CultureInfo.InvariantCulture, $"=== frame-{frame} ===");
            report.AppendLine(CultureInfo.InvariantCulture, $"  presence  {snapshot.Presence}");

            if (!snapshot.HudPresent)
            {
                report.AppendLine();
                continue;
            }

            double[] coverage = new double[skillPoints.SegmentCount];
            int filled = RoiReader.ReadSegments(skillPoints, view, coverage);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  skill     {filled}  coverage [{string.Join(", ", coverage.Select(c => c.ToString("F3", CultureInfo.InvariantCulture)))}]");
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  chains    {string.Join("  ", snapshot.Chains.Select(c => c.ToString()))}");
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  ultimates {string.Join("  ", snapshot.Ultimates.Select(u => u.ToString()))}");
            report.AppendLine();
        }

        File.WriteAllText(path, report.ToString());
    }
}
