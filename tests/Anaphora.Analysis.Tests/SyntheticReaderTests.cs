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

    [Fact]
    public void DiscStateIgnoresTheChargeArcOnTheRing()
    {
        // A dark slot with a bright arc around its rim is charging, not ready.
        const int Side = 72;
        byte[] pixels = new byte[Side * Side * 4];
        for (int y = 0; y < Side; y++)
        {
            for (int x = 0; x < Side; x++)
            {
                double dx = x - 35.5;
                double dy = y - 35.5;
                double distance = Math.Sqrt((dx * dx) + (dy * dy));
                Rgb colour = distance is > 30 and < 36 ? new Rgb(0xA8, 0xC0, 0x00) : new Rgb(0x00, 0x10, 0x18);

                int p = (y * Side * 4) + (x * 4);
                pixels[p] = colour.B;
                pixels[p + 1] = colour.G;
                pixels[p + 2] = colour.R;
                pixels[p + 3] = 255;
            }
        }

        var roi = new DiscStateRoi
        {
            Id = "ult",
            Bounds = new NormalizedRect(0, 0, 1, 1),
            InnerRadius = 0.62,
            ReadyLuma = 100,
            ReadySaturation = 0,
        };

        DiscStateReading reading = RoiReader.Read(roi, new FrameView(pixels, Side, Side));

        Assert.False(reading.IsReady);
        Assert.True(reading.MeanLuma < 40, $"arc leaked into the inner disc: luma {reading.MeanLuma:F1}");
    }

    [Fact]
    public void DiscStateSeesAFilledSlot()
    {
        const int Side = 72;
        byte[] pixels = new byte[Side * Side * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x28;
            pixels[i + 1] = 0x98;
            pixels[i + 2] = 0xD0;
            pixels[i + 3] = 255;
        }

        var roi = new DiscStateRoi
        {
            Id = "ult",
            Bounds = new NormalizedRect(0, 0, 1, 1),
            ReadyLuma = 100,
            ReadySaturation = 0,
        };

        Assert.True(RoiReader.Read(roi, new FrameView(pixels, Side, Side)).IsReady);
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
