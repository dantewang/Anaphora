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
            return new PresenceReading(false, 0, 0, 0);
        }

        if (roi.Signature is ColourGate signature)
        {
            double coverage = Coverage(frame, rect, signature);
            return new PresenceReading(coverage >= roi.MinimumCoverage, coverage, 0, 0);
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
            0,
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

        double centreX = rect.X + (rect.Width / 2.0) - 0.5;
        double centreY = rect.Y + (rect.Height / 2.0) - 0.5;
        double radius = Math.Min(rect.Width, rect.Height) / 2.0;

        // One spoke every five degrees, starting half a step past 12 o'clock so
        // the arc's leading edge, which sits exactly on the vertical, is never
        // sampled edge-on.
        Span<bool> arc = stackalloc bool[DiscSpokes];
        int ringLit = 0;

        for (int spoke = 0; spoke < DiscSpokes; spoke++)
        {
            double theta = (spoke + 0.5) * (2 * Math.PI / DiscSpokes);
            double sin = Math.Sin(theta);
            double cos = Math.Cos(theta);

            arc[spoke] = SpokeLit(frame, rect, centreX, centreY, sin, cos, roi.ArcInner * radius, roi.ArcOuter * radius, roi.ArcLuma, roi.ArcSaturation);
            if (SpokeLit(frame, rect, centreX, centreY, sin, cos, roi.RingInner * radius, roi.RingOuter * radius, roi.RingLuma, roi.RingSaturation))
            {
                ringLit++;
            }
        }

        double coverage = (double)ringLit / DiscSpokes;
        bool ready = coverage >= roi.ReadyCoverage;

        // The charge is the unbroken run clockwise from the top. Background that
        // happens to be vivid somewhere else round the disc is not part of it;
        // a single unlit spoke is tolerated for anti-aliasing and small overlaps.
        int reached = 0;
        int gap = 0;
        for (int spoke = 0; spoke < DiscSpokes; spoke++)
        {
            if (arc[spoke])
            {
                reached = spoke + 1;
                gap = 0;
            }
            else if (++gap >= 2)
            {
                break;
            }
        }

        return new DiscStateReading(ready, ready ? 1 : (double)reached / DiscSpokes, coverage);
    }

    private const int DiscSpokes = 72;

    /// <summary>True when at least half the pixels along a spoke, between two radii, are vivid enough.</summary>
    private static bool SpokeLit(
        in FrameView frame,
        PixelRect rect,
        double centreX,
        double centreY,
        double sin,
        double cos,
        double from,
        double to,
        double luma,
        double saturation)
    {
        int hits = 0;
        int samples = 0;

        for (double r = from; r <= to; r += 1)
        {
            int x = (int)Math.Round(centreX + (r * sin));
            int y = (int)Math.Round(centreY - (r * cos));
            if (x < rect.X || y < rect.Y || x >= rect.Right || y >= rect.Bottom)
            {
                continue;
            }

            Rgb pixel = frame[x, y];
            samples++;
            if (pixel.Luma >= luma && pixel.Saturation >= saturation)
            {
                hits++;
            }
        }

        return samples > 0 && hits * 2 >= samples;
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

        PortraitReference? best = null;
        int bestDistance = int.MaxValue;

        foreach (PortraitReference candidate in references)
        {
            int distance = PerceptualHash.Distance(hash, candidate.Hash);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best is not null && bestDistance <= roi.MaxHashDistance
            ? new PortraitReading(best.Id, bestDistance, hash) { Slot = best.Slot }
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
