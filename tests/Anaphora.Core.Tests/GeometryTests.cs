using Anaphora.Core;

namespace Anaphora.Core.Tests;

public class NormalizedRectTests
{
    [Fact]
    public void FullFrameMapsToTheWholeFrame()
    {
        PixelRect pixels = new NormalizedRect(0, 0, 1, 1).ToPixels(3840, 2160);

        Assert.Equal(new PixelRect(0, 0, 3840, 2160), pixels);
    }

    [Fact]
    public void AdjacentRectsStayAdjacentAfterRounding()
    {
        // Rounding the size instead of the edges leaves a one-pixel seam here,
        // which is exactly the drift a segmented bar cannot afford.
        var left = new NormalizedRect(0.0, 0, 1.0 / 3, 1);
        var middle = new NormalizedRect(1.0 / 3, 0, 1.0 / 3, 1);
        var right = new NormalizedRect(2.0 / 3, 0, 1.0 / 3, 1);

        PixelRect a = left.ToPixels(1000, 10);
        PixelRect b = middle.ToPixels(1000, 10);
        PixelRect c = right.ToPixels(1000, 10);

        Assert.Equal(a.Right, b.X);
        Assert.Equal(b.Right, c.X);
        Assert.Equal(1000, c.Right);
    }

    [Fact]
    public void PixelsRoundTripThroughNormalisedForm()
    {
        var rect = NormalizedRect.FromPixels(1590, 1945, 660, 25, 3840, 2160);

        Assert.Equal(new PixelRect(1590, 1945, 660, 25), rect.ToPixels(3840, 2160));
    }

    [Fact]
    public void SegmentsDivideTheBoundsEvenly()
    {
        var bar = new NormalizedRect(0.4, 0.9, 0.3, 0.01);

        NormalizedRect first = bar.Segment(0, 3, Axis.Horizontal);
        NormalizedRect last = bar.Segment(2, 3, Axis.Horizontal);

        Assert.Equal(0.4, first.X, 9);
        Assert.Equal(0.1, first.Width, 9);
        Assert.Equal(0.7, last.Right, 9);
        Assert.Equal(bar.Y, first.Y);
        Assert.Equal(bar.Height, first.Height);
    }

    [Fact]
    public void SegmentGapTrimsBothEndsOfEachCell()
    {
        var bar = new NormalizedRect(0, 0, 0.3, 1);

        NormalizedRect middle = bar.Segment(1, 3, Axis.Horizontal, gapFraction: 0.2);

        // Cell spans 0.1..0.2; a fifth of it is gutter, split evenly.
        Assert.Equal(0.11, middle.X, 9);
        Assert.Equal(0.08, middle.Width, 9);
    }

    [Fact]
    public void VerticalSegmentsSplitTheOtherWay()
    {
        var bar = new NormalizedRect(0, 0, 1, 0.6);

        NormalizedRect second = bar.Segment(1, 2, Axis.Vertical);

        Assert.Equal(0.3, second.Y, 9);
        Assert.Equal(0.3, second.Height, 9);
        Assert.Equal(1, second.Width, 9);
    }

    [Fact]
    public void InsetShrinksTowardsTheCentre()
    {
        var rect = new NormalizedRect(0.2, 0.2, 0.4, 0.4);

        NormalizedRect inner = rect.Inset(0.5);

        Assert.Equal(rect.CenterX, inner.CenterX, 9);
        Assert.Equal(rect.CenterY, inner.CenterY, 9);
        Assert.Equal(0.2, inner.Width, 9);
    }

    [Theory]
    [InlineData(0.0, 0.0, 1.0, 1.0, true)]
    [InlineData(0.4, 0.9, 0.2, 0.01, true)]
    [InlineData(0.9, 0.0, 0.2, 0.5, false)]
    [InlineData(-0.1, 0.0, 0.5, 0.5, false)]
    [InlineData(0.1, 0.1, 0.0, 0.5, false)]
    public void ValidityFollowsTheFrameBounds(double x, double y, double w, double h, bool expected)
    {
        Assert.Equal(expected, new NormalizedRect(x, y, w, h).IsValid);
    }

    [Fact]
    public void SegmentIndexIsRangeChecked()
    {
        var bar = new NormalizedRect(0, 0, 1, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => bar.Segment(3, 3, Axis.Horizontal));
        Assert.Throws<ArgumentOutOfRangeException>(() => bar.Segment(0, 0, Axis.Horizontal));
    }
}
