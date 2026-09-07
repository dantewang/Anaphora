using Anaphora.Core;

namespace Anaphora.Analysis;

/// <summary>
/// One method per ROI kind. All pure, all allocation-free: these run on every
/// captured frame, and the pipeline targets 15-30 Hz with the game still needing
/// the GPU.
/// </summary>
public static class RoiReader
{
    /// <summary>
    /// A bar's fill is allowed this many consecutive unfilled lines before the
    /// walk gives up. One or two is anti-aliasing at a gradient boundary or a
    /// stray effect pixel; three in a row is the end of the fill.
    /// </summary>
    private const int FillBreakRun = 3;

    /// <summary>
    /// Is the HUD on screen? Two-sided by design -- see <see cref="PresenceRoi"/>
    /// for why a signature colour is the wrong test here.
    /// </summary>
    public static PresenceReading Read(PresenceRoi roi, in FrameView frame)
    {
        ArgumentNullException.ThrowIfNull(roi);

        PixelRect rect = frame.RectFor(roi.Bounds);
        if (rect.IsEmpty)
        {
            return new PresenceReading(false, 0, 0);
        }

        int dark = 0;
        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            for (int x = rect.X; x < rect.Right; x++)
            {
                if (frame[x, y].Luma <= roi.DarkLuma)
                {
                    dark++;
                }
            }
        }

        double total = (double)rect.Width * rect.Height;
        double darkFraction = dark / total;
        double litFraction = 1 - darkFraction;

        return new PresenceReading(
            darkFraction >= roi.MinimumDark && litFraction >= roi.MinimumLit,
            darkFraction,
            litFraction);
    }

    public static SegmentedBarReading Read(SegmentedBarRoi roi, in FrameView frame)
    {
        ArgumentNullException.ThrowIfNull(roi);
        return new SegmentedBarReading(ReadSegments(roi, frame, []), roi.SegmentCount);
    }

    /// <summary>
    /// Counts complete segments, optionally reporting each one's coverage. The
    /// coverages are what calibration and the marking UI need: a segment sitting
    /// near the threshold is the thing worth seeing before it starts flickering.
    /// </summary>
    public static int ReadSegments(SegmentedBarRoi roi, in FrameView frame, Span<double> coverage)
    {
        ArgumentNullException.ThrowIfNull(roi);

        int filled = 0;
        for (int i = 0; i < roi.SegmentCount; i++)
        {
            NormalizedRect cell = roi.Bounds.Segment(i, roi.SegmentCount, roi.Axis, roi.SegmentGap);

            // Only the far end: a segment is banked when its last stretch is lit,
            // not when most of it is.
            NormalizedRect tail = roi.TailFraction >= 1
                ? cell
                : cell.Slice(1 - roi.TailFraction, 1, roi.Axis);

            double covered = Coverage(frame, frame.RectFor(tail), roi.Filled);

            if (i < coverage.Length)
            {
                coverage[i] = covered;
            }

            if (covered >= roi.FilledCoverage)
            {
                filled++;
            }
        }

        return filled;
    }

    public static FillBarReading Read(FillBarRoi roi, in FrameView frame)
    {
        ArgumentNullException.ThrowIfNull(roi);

        PixelRect rect = frame.RectFor(roi.Bounds);
        bool horizontal = roi.Direction is FillDirection.LeftToRight or FillDirection.RightToLeft;
        bool forward = roi.Direction is FillDirection.LeftToRight or FillDirection.TopToBottom;

        int steps = horizontal ? rect.Width : rect.Height;
        int lines = horizontal ? rect.Height : rect.Width;
        if (steps == 0 || lines == 0)
        {
            return new FillBarReading(0, false);
        }

        // Walk from the end the bar fills from and stop where the fill stops,
        // rather than counting matches anywhere: a bar with a bright icon
        // floating over its empty half should still read as half full.
        int reached = 0;
        int broken = 0;

        for (int k = 0; k < steps; k++)
        {
            int step = forward ? k : steps - 1 - k;
            int hits = 0;

            for (int j = 0; j < lines; j++)
            {
                int x = horizontal ? rect.X + step : rect.X + j;
                int y = horizontal ? rect.Y + j : rect.Y + step;
                if (roi.Fill.Accepts(frame[x, y]))
                {
                    hits++;
                }
            }

            if (hits * 2 >= lines)
            {
                reached = k + 1;
                broken = 0;
            }
            else if (++broken >= FillBreakRun)
            {
                break;
            }
        }

        double ratio = (double)reached / steps;
        return new FillBarReading(ratio, ratio >= roi.ReadyRatio);
    }

    public static DiscStateReading Read(DiscStateRoi roi, in FrameView frame)
    {
        ArgumentNullException.ThrowIfNull(roi);

        PixelRect rect = frame.RectFor(roi.Bounds);
        if (rect.IsEmpty)
        {
            return new DiscStateReading(false, 0, 0);
        }

        // Sample a disc, not the bounding box: the corners of the box reach the
        // ring, where the charge arc lives, and that arc is exactly what must not
        // influence the verdict.
        double centreX = rect.X + (rect.Width / 2.0) - 0.5;
        double centreY = rect.Y + (rect.Height / 2.0) - 0.5;
        double radius = Math.Min(rect.Width, rect.Height) / 2.0 * roi.InnerRadius;
        double radiusSquared = radius * radius;

        double luma = 0;
        double saturation = 0;
        int count = 0;

        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            double dy = y - centreY;
            for (int x = rect.X; x < rect.Right; x++)
            {
                double dx = x - centreX;
                if ((dx * dx) + (dy * dy) > radiusSquared)
                {
                    continue;
                }

                Rgb pixel = frame[x, y];
                luma += pixel.Luma;
                saturation += pixel.Saturation;
                count++;
            }
        }

        if (count == 0)
        {
            return new DiscStateReading(false, 0, 0);
        }

        double meanLuma = luma / count;
        double meanSaturation = saturation / count;

        // The saturation test is opt-in because HSV saturation is meaningless at
        // low value: an empty slot measured #001018, which is almost black and
        // still scores 1.0. Endfield's profile leaves it at zero and relies on
        // luma alone, which separates 182 from 51.
        bool ready = meanLuma >= roi.ReadyLuma &&
            (roi.ReadySaturation <= 0 || meanSaturation >= roi.ReadySaturation);

        return new DiscStateReading(ready, meanLuma, meanSaturation);
    }

    public static PortraitReading Read(
        PortraitSlotRoi roi,
        in FrameView frame,
        IReadOnlyList<PortraitReference> references)
    {
        ArgumentNullException.ThrowIfNull(roi);
        ArgumentNullException.ThrowIfNull(references);

        // Inset to stay inside the circular mask; the corners of the slot are
        // whatever the game world happens to be showing behind it.
        PixelRect rect = frame.RectFor(roi.Bounds.Inset(1 - roi.InnerRadius));
        ulong hash = PerceptualHash.Compute(frame, rect);

        string? bestId = null;
        int bestDistance = int.MaxValue;

        foreach (PortraitReference candidate in references)
        {
            int distance = PerceptualHash.Distance(hash, candidate.Hash);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestId = candidate.Id;
            }
        }

        return bestDistance <= roi.MaxHashDistance
            ? new PortraitReading(bestId, bestDistance, hash)
            : new PortraitReading(null, bestDistance == int.MaxValue ? 64 : bestDistance, hash);
    }

    private static double Coverage(in FrameView frame, PixelRect rect, ColourGate gate)
    {
        if (rect.IsEmpty)
        {
            return 0;
        }

        int hits = 0;
        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            for (int x = rect.X; x < rect.Right; x++)
            {
                if (gate.Accepts(frame[x, y]))
                {
                    hits++;
                }
            }
        }

        return hits / ((double)rect.Width * rect.Height);
    }
}
