using Anaphora.Core;

namespace Anaphora.Analysis.Tests;

/// <summary>
/// The atlas is only worth having if reading it is indistinguishable from
/// reading the whole frame. These build the atlas on the CPU the way the GPU
/// path does and demand identical snapshots.
/// </summary>
public class RoiAtlasTests
{
    public static TheoryData<string> AllFixtures => ["008", "020", "024", "032", "036", "040", "048", "084"];

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void AtlasReadingsMatchWholeFrameReadings(string frame)
    {
        byte[] pixels = Fixture.Load(frame, out int width, out int height);
        var reader = new HudReader(Fixture.Profile);

        HudSnapshot expected = reader.Read(new FrameView(pixels, width, height));

        RoiAtlas atlas = RoiAtlas.Create(Fixture.Profile, width, height, new PixelRect(0, 0, width, height));
        byte[] packed = Pack(atlas, pixels, width * 4);
        HudSnapshot actual = reader.Read(new FrameView(packed, atlas.Width, atlas.Height), atlas);

        AssertSame(expected, actual);
    }

    [Fact]
    public void WindowChromeAroundTheClientAreaIsAccountedFor()
    {
        // A windowed game is captured with its title bar and borders. Paste a
        // borderless frame into a larger canvas and the readings must not move.
        byte[] client = Fixture.Load("036", out int width, out int height);
        const int Left = 8;
        const int Top = 31;
        int canvasWidth = width + (Left * 2);
        int canvasHeight = height + Top + 8;

        byte[] canvas = new byte[canvasWidth * canvasHeight * 4];
        canvas.AsSpan().Fill(0x7F);
        for (int row = 0; row < height; row++)
        {
            client.AsSpan(row * width * 4, width * 4)
                .CopyTo(canvas.AsSpan(((row + Top) * canvasWidth * 4) + (Left * 4)));
        }

        var reader = new HudReader(Fixture.Profile);
        HudSnapshot expected = reader.Read(new FrameView(client, width, height));
        var area = new PixelRect(Left, Top, width, height);

        HudSnapshot viaClientView = reader.Read(new FrameView(canvas, canvasWidth, canvasHeight).ClientArea(area));
        AssertSame(expected, viaClientView);

        RoiAtlas atlas = RoiAtlas.Create(Fixture.Profile, canvasWidth, canvasHeight, area);
        byte[] packed = Pack(atlas, canvas, canvasWidth * 4);
        AssertSame(expected, reader.Read(new FrameView(packed, atlas.Width, atlas.Height), atlas));
    }

    [Fact]
    public void TheAtlasIsASmallFractionOfTheFrame()
    {
        RoiAtlas atlas = RoiAtlas.Create(Fixture.Profile, 3840, 2160, new PixelRect(0, 0, 3840, 2160));

        double share = (double)atlas.Width * atlas.Height / (3840 * 2160);
        Assert.True(share < 0.02, $"atlas is {atlas.Width}x{atlas.Height}, {share:P2} of the frame");
    }

    [Fact]
    public void SlotsFitTheAtlasAndNeverOverlap()
    {
        RoiAtlas atlas = RoiAtlas.Create(Fixture.Profile, 3840, 2160, new PixelRect(0, 0, 3840, 2160));

        Assert.Equal(Fixture.Profile.Rois.Count, atlas.Regions.Count);
        foreach (AtlasRegion region in atlas.Regions)
        {
            PixelRect slot = region.Destination;
            Assert.True(slot.Right <= atlas.Width && slot.Bottom <= atlas.Height, $"{region.RoiId} spills out: {slot}");
        }

        for (int i = 0; i < atlas.Regions.Count; i++)
        {
            for (int j = i + 1; j < atlas.Regions.Count; j++)
            {
                PixelRect a = atlas.Regions[i].Destination;
                PixelRect b = atlas.Regions[j].Destination;
                bool overlap = a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
                Assert.False(overlap, $"{atlas.Regions[i].RoiId} {a} overlaps {atlas.Regions[j].RoiId} {b}");
            }
        }
    }

    [Fact]
    public void LayoutScalesWithResolution()
    {
        RoiAtlas fourK = RoiAtlas.Create(Fixture.Profile, 3840, 2160, new PixelRect(0, 0, 3840, 2160));
        RoiAtlas fullHd = RoiAtlas.Create(Fixture.Profile, 1920, 1080, new PixelRect(0, 0, 1920, 1080));

        Assert.True(fullHd.Width * fullHd.Height < fourK.Width * fourK.Height);
    }

    [Fact]
    public void AnAtlasFromAnotherProfileIsRefused()
    {
        GameProfile other = Fixture.Profile with { Id = "other" };
        RoiAtlas atlas = RoiAtlas.Create(other, 3840, 2160, new PixelRect(0, 0, 3840, 2160));
        byte[] packed = new byte[atlas.Width * atlas.Height * 4];

        Assert.Throws<ArgumentException>(() =>
            new HudReader(Fixture.Profile).Read(new FrameView(packed, atlas.Width, atlas.Height), atlas));
    }

    [Fact]
    public void ARoiOffTheFrameReadsAsNothingRatherThanThrowing()
    {
        // A hand-edited profile nudges the ultimate row past the right edge. The
        // validator flags it; the pipeline must still not walk off the atlas.
        GameProfile nudged = Fixture.Profile with
        {
            Rois =
            [
                .. Fixture.Profile.Rois.Select(roi => roi is DiscStateRoi disc
                    ? disc with { Bounds = disc.Bounds with { X = 1.05 } }
                    : roi),
            ],
        };

        byte[] pixels = Fixture.Load("036", out int width, out int height);
        RoiAtlas atlas = RoiAtlas.Create(nudged, width, height, new PixelRect(0, 0, width, height));
        byte[] packed = Pack(atlas, pixels, width * 4);

        HudSnapshot snapshot = new HudReader(nudged).Read(new FrameView(packed, atlas.Width, atlas.Height), atlas);

        Assert.True(snapshot.HudPresent);
        Assert.Equal(4, snapshot.Ultimates.Count);
        Assert.All(snapshot.Ultimates, u => Assert.False(u.IsReady));
    }

    private static byte[] Pack(RoiAtlas atlas, byte[] texture, int stride)
    {
        byte[] packed = new byte[atlas.Width * atlas.Height * 4];
        atlas.CopyFrom(texture, stride, packed);
        return packed;
    }

    private static void AssertSame(HudSnapshot expected, HudSnapshot actual)
    {
        Assert.Equal(expected.HudPresent, actual.HudPresent);
        Assert.Equal(expected.Presence, actual.Presence);
        Assert.Equal(expected.SkillPoints, actual.SkillPoints);
        Assert.Equal(expected.Chains, actual.Chains);
        Assert.Equal(expected.Ultimates, actual.Ultimates);
        Assert.Equal(expected.Prompts, actual.Prompts);
    }
}
