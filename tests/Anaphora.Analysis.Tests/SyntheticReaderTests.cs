using Anaphora.Analysis;
using Anaphora.Core;

namespace Anaphora.Analysis.Tests;

/// <summary>
/// Painted frames, so each reader's contract is pinned exactly. The captured
/// frames in <see cref="EndfieldFixtureTests"/> say whether the thresholds are
/// right; these say whether the arithmetic is.
/// </summary>
public class SyntheticReaderTests
{
    private const int Width = 200;
    private const int Height = 40;

    private static readonly Rgb Yellow = new(0xF8, 0xF8, 0x00);
    private static readonly Rgb White = new(0xF8, 0xF8, 0xF8);
    private static readonly Rgb Track = new(0x10, 0x10, 0x10);

    private static byte[] Fill(Rgb colour)
    {
        byte[] pixels = new byte[Width * Height * 4];
        Paint(pixels, new PixelRect(0, 0, Width, Height), colour);
        return pixels;
    }

    private static void Paint(byte[] pixels, PixelRect rect, Rgb colour)
    {
        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            for (int x = rect.X; x < rect.Right; x++)
            {
                int p = (y * Width * 4) + (x * 4);
                pixels[p] = colour.B;
                pixels[p + 1] = colour.G;
                pixels[p + 2] = colour.R;
                pixels[p + 3] = 255;
            }
        }
    }

    [Fact]
    public void SegmentedBarCountsOnlyCompleteSegments()
    {
        byte[] pixels = Fill(Track);
        // Three cells across the full width: fill the first two, leave a third
        // of the last one lit the way a charging segment would be.
        Paint(pixels, new PixelRect(0, 0, 66, Height), Yellow);
        Paint(pixels, new PixelRect(67, 0, 66, Height), Yellow);
        Paint(pixels, new PixelRect(134, 0, 22, Height), White);

        var roi = new SegmentedBarRoi
        {
            Id = "sp",
            Bounds = new NormalizedRect(0, 0, 1, 1),
            SegmentCount = 3,
            SegmentGap = 0.1,
            Filled = new ColourGate(Yellow, 0.20),
            FilledCoverage = 0.5,
        };

        Assert.Equal(2, RoiReader.Read(roi, new FrameView(pixels, Width, Height)).Filled);
    }

    [Fact]
    public void SegmentedBarIgnoresTheChargingWhite()
    {
        // White is not yellow: a segment that is entirely mid-charge still counts
        // as zero, which is the whole point of only banking whole segments.
        byte[] pixels = Fill(White);

        var roi = new SegmentedBarRoi
        {
            Id = "sp",
            Bounds = new NormalizedRect(0, 0, 1, 1),
            SegmentCount = 3,
            Filled = new ColourGate(Yellow, 0.20),
            FilledCoverage = 0.5,
        };

        Assert.Equal(0, RoiReader.Read(roi, new FrameView(pixels, Width, Height)).Filled);
    }

    [Fact]
    public void SegmentCoveragesComeBackForCalibration()
    {
        byte[] pixels = Fill(Track);
        Paint(pixels, new PixelRect(0, 0, 66, Height), Yellow);

        var roi = new SegmentedBarRoi
        {
            Id = "sp",
            Bounds = new NormalizedRect(0, 0, 1, 1),
            SegmentCount = 3,
            SegmentGap = 0.1,
            Filled = new ColourGate(Yellow, 0.20),
            FilledCoverage = 0.5,
        };

        Span<double> coverage = stackalloc double[3];
        int filled = RoiReader.ReadSegments(roi, new FrameView(pixels, Width, Height), coverage);

        Assert.Equal(1, filled);
        Assert.Equal(1.0, coverage[0], 3);
        Assert.Equal(0.0, coverage[1], 3);
        Assert.Equal(0.0, coverage[2], 3);
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(50, 0.25)]
    [InlineData(100, 0.5)]
    [InlineData(200, 1.0)]
    public void FillBarMeasuresHowFarTheFillReaches(int filledWidth, double expected)
    {
        byte[] pixels = Fill(Track);
        if (filledWidth > 0)
        {
            Paint(pixels, new PixelRect(0, 0, filledWidth, Height), White);
        }

        var roi = new FillBarRoi
        {
            Id = "chain",
            Bounds = new NormalizedRect(0, 0, 1, 1),
            Direction = FillDirection.LeftToRight,
            Fill = new ColourGate(new Rgb(0xCC, 0xCC, 0xCC), 0.25),
        };

        FillBarReading reading = RoiReader.Read(roi, new FrameView(pixels, Width, Height));

        Assert.Equal(expected, reading.Ratio, 2);
        Assert.Equal(expected >= 0.97, reading.IsReady);
    }

    [Fact]
    public void FillBarAcceptsBothTheCoolingGreyAndTheReadyWhite()
    {
        // Endfield draws the cooldown progress grey and the finished bar white.
        // One gate has to take both or a charging bar reads as empty.
        var roi = new FillBarRoi
        {
            Id = "chain",
            Bounds = new NormalizedRect(0, 0, 1, 1),
            Fill = new ColourGate(new Rgb(0xCC, 0xCC, 0xCC), 0.25),
        };

        byte[] grey = Fill(new Rgb(0xA0, 0xA0, 0xA0));
        byte[] white = Fill(White);

        Assert.Equal(1.0, RoiReader.Read(roi, new FrameView(grey, Width, Height)).Ratio, 2);
        Assert.Equal(1.0, RoiReader.Read(roi, new FrameView(white, Width, Height)).Ratio, 2);
        Assert.Equal(0.0, RoiReader.Read(roi, new FrameView(Fill(Track), Width, Height)).Ratio, 2);
    }

    [Fact]
    public void FillBarStopsAtTheFillRatherThanCountingStrayBrightPixels()
    {
        // An icon or a damage number sitting over the empty half must not add to
        // the reading.
        byte[] pixels = Fill(Track);
        Paint(pixels, new PixelRect(0, 0, 60, Height), White);
        Paint(pixels, new PixelRect(150, 0, 40, Height), White);

        var roi = new FillBarRoi
        {
            Id = "chain",
            Bounds = new NormalizedRect(0, 0, 1, 1),
            Fill = new ColourGate(new Rgb(0xCC, 0xCC, 0xCC), 0.25),
        };

        Assert.Equal(0.30, RoiReader.Read(roi, new FrameView(pixels, Width, Height)).Ratio, 2);
    }

    [Fact]
    public void FillBarRespectsDirection()
    {
        byte[] pixels = Fill(Track);
        Paint(pixels, new PixelRect(140, 0, 60, Height), White);

        var bounds = new NormalizedRect(0, 0, 1, 1);
        var gate = new ColourGate(new Rgb(0xCC, 0xCC, 0xCC), 0.25);

        var leftToRight = new FillBarRoi { Id = "a", Bounds = bounds, Fill = gate, Direction = FillDirection.LeftToRight };
        var rightToLeft = new FillBarRoi { Id = "b", Bounds = bounds, Fill = gate, Direction = FillDirection.RightToLeft };

        Assert.Equal(0.0, RoiReader.Read(leftToRight, new FrameView(pixels, Width, Height)).Ratio, 2);
        Assert.Equal(0.30, RoiReader.Read(rightToLeft, new FrameView(pixels, Width, Height)).Ratio, 2);
    }

    private static readonly Rgb ArcOrange = new(0xF7, 0xB2, 0x2E);
    private static readonly Rgb TintedFloor = new(0x7E, 0x54, 0x33);
    private static readonly Rgb OrangeStripe = new(0xB4, 0x79, 0x49);

    private static readonly DiscStateRoi Ultimate = new() { Id = "ult", Bounds = new NormalizedRect(0, 0, 1, 1) };

    /// <summary>
    /// An 88-pixel ultimate slot as Endfield draws it: <paramref name="inside"/>
    /// fills the translucent disc, <paramref name="outside"/> the rest, a charge
    /// arc sweeps clockwise from 12 o'clock through <paramref name="charge"/> of
    /// the circle at r 16-25, and the ready ring at r 39-43 is lit through
    /// <paramref name="ring"/> of it.
    /// </summary>
    private static byte[] Slot(double charge, double ring, Rgb inside, Rgb outside, Rgb arc)
    {
        const int Side = 88;
        byte[] pixels = new byte[Side * Side * 4];
        for (int y = 0; y < Side; y++)
        {
            for (int x = 0; x < Side; x++)
            {
                double dx = x - 43.5;
                double dy = y - 43.5;
                double r = Math.Sqrt((dx * dx) + (dy * dy));
                double turn = (Math.Atan2(dx, -dy) + (2 * Math.PI)) % (2 * Math.PI) / (2 * Math.PI);

                Rgb colour = r switch
                {
                    >= 16 and <= 25 when turn < charge => arc,
                    >= 39 and <= 43 when turn < ring => arc,
                    < 32 => inside,
                    _ => outside,
                };

                int p = (y * Side * 4) + (x * 4);
                pixels[p] = colour.B;
                pixels[p + 1] = colour.G;
                pixels[p + 2] = colour.R;
                pixels[p + 3] = 255;
            }
        }

        return pixels;
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.40)]
    [InlineData(0.85)]
    public void TheChargeArcIsMeasuredClockwiseFromTheTop(double charge)
    {
        DiscStateReading reading = RoiReader.Read(Ultimate, new FrameView(Slot(charge, 0, TintedFloor, OrangeStripe, ArcOrange), 88, 88));

        // Spokes are five degrees apart, so the reading is good to about 1/72.
        Assert.False(reading.IsReady);
        Assert.InRange(reading.Charge, charge - 0.03, charge + 0.03);
    }

    [Fact]
    public void AFullyLitOuterRingMeansReady()
    {
        DiscStateReading reading = RoiReader.Read(Ultimate, new FrameView(Slot(1, 1, ArcOrange, TintedFloor, ArcOrange), 88, 88));

        Assert.True(reading.IsReady);
        Assert.Equal(1, reading.Charge);
    }

    [Fact]
    public void ABrightFloorSeenThroughTheDiscIsNotReady()
    {
        // The failure the first calibration had on a sunlit map: nothing lit at
        // all, but the inner disc is bright and orange.
        DiscStateReading reading = RoiReader.Read(Ultimate, new FrameView(Slot(0, 0, OrangeStripe, OrangeStripe, ArcOrange), 88, 88));

        Assert.False(reading.IsReady);
        Assert.Equal(0, reading.Charge);
        Assert.Equal(0, reading.RingCoverage);
    }

    [Fact]
    public void SomethingVividCrossingPartOfTheRingIsNotReady()
    {
        DiscStateReading reading = RoiReader.Read(Ultimate, new FrameView(Slot(0.6, 0.35, TintedFloor, OrangeStripe, ArcOrange), 88, 88));

        Assert.False(reading.IsReady);
        Assert.InRange(reading.RingCoverage, 0.3, 0.4);
    }

    [Fact]
    public void ASignatureSentinelNeedsItsColourToCoverThePatch()
    {
        var roi = new PresenceRoi
        {
            Id = "hud",
            Bounds = new NormalizedRect(0, 0, 1, 1),
            Signature = new ColourGate(new Rgb(0x12, 0xCC, 0xF9), 0.15),
            MinimumCoverage = 0.6,
        };

        byte[] cyan = Fill(new Rgb(0x10, 0xC8, 0xF8));
        byte[] mostlyGrey = Fill(new Rgb(0x6E, 0x71, 0x77));
        Paint(mostlyGrey, new PixelRect(0, 0, 60, Height), new Rgb(0x10, 0xC8, 0xF8));

        Assert.True(RoiReader.Read(roi, new FrameView(cyan, Width, Height)).IsPresent);
        Assert.False(RoiReader.Read(roi, new FrameView(mostlyGrey, Width, Height)).IsPresent);
        Assert.False(RoiReader.Read(roi, new FrameView(Fill(White), Width, Height)).IsPresent);
    }

    [Fact]
    public void PresenceNeedsBothDarkAndLit()
    {
        var roi = new PresenceRoi
        {
            Id = "hud",
            Bounds = new NormalizedRect(0, 0, 1, 1),
            DarkLuma = 40,
            MinimumDark = 0.08,
            MinimumLit = 0.10,
        };

        byte[] allDark = Fill(Track);
        byte[] allLit = Fill(White);
        byte[] mixed = Fill(Track);
        Paint(mixed, new PixelRect(0, 0, 60, Height), White);

        Assert.False(RoiReader.Read(roi, new FrameView(allDark, Width, Height)).IsPresent);
        Assert.False(RoiReader.Read(roi, new FrameView(allLit, Width, Height)).IsPresent);
        Assert.True(RoiReader.Read(roi, new FrameView(mixed, Width, Height)).IsPresent);
    }

    [Fact]
    public void HashIsStableUnderBrightnessChange()
    {
        // The same portrait under an effect glow has to hash the same, which is
        // why the hash compares neighbours instead of levels.
        byte[] plain = Gradient(1.0);
        byte[] bright = Gradient(1.6);

        var rect = new PixelRect(0, 0, Width, Height);
        ulong a = PerceptualHash.Compute(new FrameView(plain, Width, Height), rect);
        ulong b = PerceptualHash.Compute(new FrameView(bright, Width, Height), rect);

        Assert.Equal(a, b);
    }

    [Fact]
    public void HashSeparatesDifferentPatches()
    {
        var rect = new PixelRect(0, 0, Width, Height);
        ulong a = PerceptualHash.Compute(new FrameView(Gradient(1.0), Width, Height), rect);
        ulong b = PerceptualHash.Compute(new FrameView(Gradient(1.0, reversed: true), Width, Height), rect);

        Assert.True(PerceptualHash.Distance(a, b) > 20, $"distance was only {PerceptualHash.Distance(a, b)}");
    }

    [Fact]
    public void PortraitSlotReportsUnknownWhenNothingIsCloseEnough()
    {
        byte[] pixels = Fill(Track);
        var roi = new PortraitSlotRoi
        {
            Id = "prompt",
            Bounds = new NormalizedRect(0, 0, 1, 1),
            MaxHashDistance = 4,
        };

        PortraitReading reading = RoiReader.Read(
            roi,
            new FrameView(pixels, Width, Height),
            [new PortraitReference { Id = "someone", DisplayName = "Someone", Hash = 0xA5A5A5A5A5A5A5A5 }]);

        Assert.False(reading.IsPresent);
        Assert.Null(reading.PortraitId);
    }

    [Fact]
    public void PortraitSlotNamesTheNearestReference()
    {
        byte[] pixels = Gradient(1.0);
        var rect = new PixelRect(0, 0, Width, Height);
        ulong hash = PerceptualHash.Compute(new FrameView(pixels, Width, Height), rect);

        var roi = new PortraitSlotRoi
        {
            Id = "prompt",
            Bounds = new NormalizedRect(0, 0, 1, 1),
            InnerRadius = 1.0,
            MaxHashDistance = 12,
        };

        PortraitReading reading = RoiReader.Read(
            roi,
            new FrameView(pixels, Width, Height),
            [
                new PortraitReference { Id = "far", DisplayName = "Far", Hash = ~hash },
                new PortraitReference { Id = "near", DisplayName = "Near", Hash = hash },
            ]);

        Assert.Equal("near", reading.PortraitId);
        Assert.Equal(0, reading.Distance);
    }

    private static byte[] Gradient(double scale, bool reversed = false)
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int column = reversed ? Width - 1 - x : x;
                byte value = (byte)Math.Clamp(column * 255.0 / Width * scale, 0, 255);
                int p = (y * Width * 4) + (x * 4);
                pixels[p] = value;
                pixels[p + 1] = value;
                pixels[p + 2] = value;
                pixels[p + 3] = 255;
            }
        }

        return pixels;
    }
}
