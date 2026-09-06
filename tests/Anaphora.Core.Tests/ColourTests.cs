using Anaphora.Core;

namespace Anaphora.Core.Tests;

public class RgbTests
{
    [Theory]
    [InlineData("#E5D14B")]
    [InlineData("E5D14B")]
    [InlineData("  #e5d14b  ")]
    public void ParsesHexWithOrWithoutHashAndCase(string text)
    {
        Assert.Equal(new Rgb(0xE5, 0xD1, 0x4B), Rgb.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    [InlineData("#E5D14B00")]
    public void RejectsAnythingElse(string text)
    {
        Assert.Throws<FormatException>(() => Rgb.Parse(text));
    }

    [Fact]
    public void FormatsBackToUppercaseHex()
    {
        Assert.Equal("#E5D14B", new Rgb(0xE5, 0xD1, 0x4B).ToString());
    }

    [Fact]
    public void GreyHasNoSaturation()
    {
        Assert.Equal(0, new Rgb(128, 128, 128).Saturation, 9);
        Assert.Equal(0, new Rgb(0, 0, 0).Saturation, 9);
    }

    [Fact]
    public void SaturationRisesWithColour()
    {
        // The distinction an ultimate slot turns on: a coloured icon against an
        // empty dark disc.
        Assert.True(new Rgb(0xE5, 0xA0, 0x20).Saturation > 0.5);
        Assert.True(new Rgb(0x22, 0x24, 0x26).Saturation < 0.2);
    }
}

public class ColourGateTests
{
    private static readonly ColourGate Yellow = new(new Rgb(0xE5, 0xD1, 0x4B), 0.18);

    [Fact]
    public void AcceptsTheTargetItself()
    {
        Assert.True(Yellow.Accepts(0xE5, 0xD1, 0x4B));
    }

    [Fact]
    public void AcceptsANearbyShade()
    {
        Assert.True(Yellow.Accepts(0xD8, 0xC6, 0x55));
    }

    [Fact]
    public void RejectsTheBarBackground()
    {
        Assert.False(Yellow.Accepts(0x20, 0x20, 0x22));
    }

    [Fact]
    public void RejectsTheChargingWhite()
    {
        // White is what separates a full segment from the one still filling, so
        // the yellow gate must not swallow it.
        Assert.False(Yellow.Accepts(0xF0, 0xF0, 0xF0));
    }

    [Fact]
    public void ToleranceOfOneAcceptsEverything()
    {
        var everything = new ColourGate(new Rgb(0, 0, 0), 1);

        Assert.True(everything.Accepts(255, 255, 255));
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.5, true)]
    [InlineData(1.0, true)]
    [InlineData(1.5, false)]
    public void ValidityRequiresAToleranceInRange(double tolerance, bool expected)
    {
        Assert.Equal(expected, new ColourGate(new Rgb(1, 2, 3), tolerance).IsValid);
    }
}
