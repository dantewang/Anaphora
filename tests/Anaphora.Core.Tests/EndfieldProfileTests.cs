using Anaphora.Core;

namespace Anaphora.Core.Tests;

/// <summary>
/// Guards the profile that actually ships. Its numbers are provisional, but its
/// shape is not: if this stops loading or validating, the readers have nothing
/// to run against.
/// </summary>
public class EndfieldProfileTests
{
    private const int FrameWidth = 3840;
    private const int FrameHeight = 2160;

    private static readonly GameProfile Profile =
        ProfileStore.Load(Path.Combine(AppContext.BaseDirectory, "profiles", "endfield.json"));

    [Fact]
    public void LoadsAndIdentifiesTheGame()
    {
        Assert.Equal("endfield", Profile.Id);
        Assert.Equal("Endfield", Profile.Window.ProcessName);
        Assert.Equal("UnityWndClass", Profile.Window.WindowClass);
    }

    [Fact]
    public void ValidatesWithoutErrors()
    {
        IReadOnlyList<ProfileProblem> problems = ProfileValidator.Validate(Profile);

        Assert.False(
            ProfileValidator.HasErrors(problems),
            string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void CoversAllFourWatchTargetsPlusTheSentinel()
    {
        string[] ids = [.. Profile.Rois.Select(r => r.Id)];

        Assert.Contains("hud.present", ids);
        Assert.Contains("skillPoints", ids);
        Assert.Equal(4, Profile.RoisOf<FillBarRoi>().Count());
        Assert.Equal(4, Profile.RoisOf<DiscStateRoi>().Count());
        Assert.Equal(2, Profile.RoisOf<PortraitSlotRoi>().Count());
    }

    [Fact]
    public void TheSentinelIsTheLeftEndOfTheHpBar()
    {
        // The HP bar drains from the right, so its left end stays cyan for as
        // long as the player is alive -- and it sits just under the skill points.
        var sentinel = (PresenceRoi)Profile.FindRoi("hud.present")!;
        PixelRect patch = sentinel.Bounds.ToPixels(FrameWidth, FrameHeight);
        PixelRect bar = Profile.FindRoi("skillPoints")!.Bounds.ToPixels(FrameWidth, FrameHeight);

        Assert.NotNull(sentinel.Signature);
        Assert.True(patch.Y > bar.Bottom, "the HP bar is below the skill points");
        Assert.True(patch.X >= bar.X && patch.Right < bar.X + (bar.Width / 4), "the patch should be the bar's left end");
    }

    [Fact]
    public void EverySlotLandsInsideTheCaptureTexture()
    {
        foreach (RoiDefinition roi in Profile.Rois)
        {
            PixelRect rect = roi.Bounds.ToPixels(FrameWidth, FrameHeight);

            Assert.False(rect.IsEmpty, $"{roi.Id} maps to an empty rectangle");
            Assert.InRange(rect.X, 0, FrameWidth);
            Assert.InRange(rect.Y, 0, FrameHeight);
            Assert.InRange(rect.Right, 0, FrameWidth);
            Assert.InRange(rect.Bottom, 0, FrameHeight);
        }
    }

    [Fact]
    public void SkillPointSegmentsAreWideEnoughToSample()
    {
        var bar = (SegmentedBarRoi)Profile.FindRoi("skillPoints")!;

        for (int i = 0; i < bar.SegmentCount; i++)
        {
            PixelRect cell = bar.Bounds
                .Segment(i, bar.SegmentCount, bar.Axis, bar.SegmentGap)
                .ToPixels(FrameWidth, FrameHeight);

            Assert.True(cell.Width > 20, $"segment {i} is only {cell.Width}px wide");
            Assert.True(cell.Height > 4, $"segment {i} is only {cell.Height}px tall");
        }
    }

    [Fact]
    public void ChainBarsDoNotOverlapEachOther()
    {
        PixelRect[] bars =
        [
            .. Profile.RoisOf<FillBarRoi>()
                .OrderBy(r => r.Bounds.X)
                .Select(r => r.Bounds.ToPixels(FrameWidth, FrameHeight)),
        ];

        for (int i = 1; i < bars.Length; i++)
        {
            Assert.True(
                bars[i - 1].Right < bars[i].X,
                $"chain bars {i - 1} and {i} overlap: {bars[i - 1]} then {bars[i]}");
        }
    }

    [Fact]
    public void UltimateSlotsAreRoughlySquareInPixels()
    {
        // They are circles, so a lopsided rectangle means the normalised width
        // and height were not both divided by the right dimension.
        foreach (DiscStateRoi disc in Profile.RoisOf<DiscStateRoi>())
        {
            PixelRect rect = disc.Bounds.ToPixels(FrameWidth, FrameHeight);
            double ratio = (double)rect.Width / rect.Height;

            Assert.InRange(ratio, 0.9, 1.1);
        }
    }

    [Fact]
    public void ThePrimaryChainPromptIsLeftOfAndLargerThanTheSecond()
    {
        PortraitSlotRoi[] slots = [.. Profile.RoisOf<PortraitSlotRoi>().OrderBy(s => s.Priority)];

        Assert.True(slots[0].Bounds.X < slots[1].Bounds.X, "priority 0 should be the leftmost prompt");
        Assert.True(slots[0].Bounds.Width > slots[1].Bounds.Width, "priority 0 should be the larger prompt");
    }

    [Fact]
    public void ScalesToOtherResolutionsWithoutLeavingTheFrame()
    {
        foreach ((int width, int height) in new[] { (1920, 1080), (2560, 1440), (3440, 1440) })
        {
            foreach (RoiDefinition roi in Profile.Rois)
            {
                PixelRect rect = roi.Bounds.ToPixels(width, height);

                Assert.InRange(rect.Right, 0, width);
                Assert.InRange(rect.Bottom, 0, height);
            }
        }
    }
}
