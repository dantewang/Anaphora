using Anaphora.Core;

namespace Anaphora.Analysis.Tests;

/// <summary>
/// The readers against real captured frames, with expectations taken from
/// reading the frames by eye and confirmed by measuring them with the probe.
/// These are what say the thresholds are right; the synthetic tests only say the
/// arithmetic is.
/// </summary>
public class EndfieldFixtureTests
{
    [Theory]
    [InlineData("008")]
    [InlineData("032")]
    [InlineData("036")]
    [InlineData("040")]
    public void CombatFramesAreReadable(string frame)
    {
        Assert.True(Fixture.Read(frame).HudPresent);
    }

    [Theory]
    // Out of combat, and after it: the bottom bar is simply not drawn.
    [InlineData("048")]
    // A full-screen ultimate cut-in. The character is bright white and covers
    // the whole strip, which is why a "is it lit?" sentinel would be fooled.
    [InlineData("024")]
    // The reward screen, likewise bright.
    [InlineData("084")]
    // Mid-combat, but an effect has washed the strip out: no dark left anywhere.
    // Refusing this frame is the point -- the bars underneath are unreadable.
    [InlineData("020")]
    public void FramesWithoutAReadableHudAreRejected(string frame)
    {
        HudSnapshot snapshot = Fixture.Read(frame);

        Assert.False(snapshot.HudPresent, $"frame-{frame} read as present: {snapshot.Presence}");
        Assert.Empty(snapshot.Chains);
        Assert.Empty(snapshot.Ultimates);
    }

    [Theory]
    [InlineData("008", 2)]
    [InlineData("032", 1)]
    [InlineData("036", 2)]
    [InlineData("040", 1)]
    public void SkillPointsCountWholeSegments(string frame, int expected)
    {
        Assert.Equal(expected, Fixture.Read(frame).SkillPoints);
    }

    [Fact]
    public void FullSegmentsSitWellClearOfTheThreshold()
    {
        // Measured 0.95 and 1.00 for banked segments against 0.00 for an empty
        // one. If this ever narrows, the bar has moved or an effect is bleeding
        // in, and the count is about to start flickering.
        var roi = Fixture.Profile.RoisOf<SegmentedBarRoi>().First();
        byte[] pixels = Fixture.Load("036", out int width, out int height);

        Span<double> coverage = stackalloc double[roi.SegmentCount];
        RoiReader.ReadSegments(roi, new FrameView(pixels, width, height), coverage);

        Assert.True(coverage[0] > 0.85, $"segment 0 covered only {coverage[0]:F3}");
        Assert.True(coverage[1] > 0.85, $"segment 1 covered only {coverage[1]:F3}");
        Assert.True(coverage[2] < 0.10, $"empty segment 2 covered {coverage[2]:F3}");
    }

    [Fact]
    public void AllChainsReadyEarlyInTheFight()
    {
        Assert.All(Fixture.Read("008").Chains, chain => Assert.True(chain.IsReady, chain.ToString()));
    }

    [Fact]
    public void ChainCooldownsMatchWhatThePixelsSay()
    {
        // Measured directly off frame-036: fills reach x=148, 321, the full bar,
        // and 807 within slots starting at 85, 318, 552 and 786, each 166 wide.
        IReadOnlyList<FillBarReading> chains = Fixture.Read("036").Chains;

        Assert.Equal(4, chains.Count);
        Assert.Equal(0.39, chains[0].Ratio, 2);
        Assert.Equal(0.02, chains[1].Ratio, 2);
        Assert.Equal(1.00, chains[2].Ratio, 2);
        Assert.Equal(0.13, chains[3].Ratio, 2);

        Assert.Equal([false, false, true, false], chains.Select(c => c.IsReady));
    }

    [Theory]
    [InlineData("008", true, true, true, true)]
    [InlineData("032", false, true, false, false)]
    [InlineData("036", false, true, false, false)]
    [InlineData("040", false, true, false, false)]
    public void UltimateSlotsReportWhoCanCast(string frame, bool a, bool b, bool c, bool d)
    {
        IReadOnlyList<DiscStateReading> ultimates = Fixture.Read(frame).Ultimates;

        Assert.Equal(4, ultimates.Count);
        Assert.Equal([a, b, c, d], ultimates.Select(u => u.IsReady));
    }

    [Fact]
    public void ReadyAndChargingUltimatesAreNowhereNearEachOther()
    {
        // A ready slot measured luma 191 against 10-49 while charging. The
        // threshold sits at 100 with room on both sides, which is what lets the
        // saturation test stay switched off -- see DiscStateRoi.
        IReadOnlyList<DiscStateReading> ultimates = Fixture.Read("036").Ultimates;

        Assert.True(ultimates[1].MeanLuma > 150, $"ready slot measured {ultimates[1].MeanLuma:F0}");
        Assert.All(
            ultimates.Where((_, i) => i != 1),
            slot => Assert.True(slot.MeanLuma < 60, $"charging slot measured {slot.MeanLuma:F0}"));
    }

    [Fact]
    public void TheChargeArcDoesNotMakeASlotLookReady()
    {
        // Slot 4 in frame-036 carries a bright yellow-green arc on its ring. It
        // reads as charging because only the inner disc is sampled.
        DiscStateReading slot = Fixture.Read("036").Ultimates[3];

        Assert.False(slot.IsReady);
    }

    [Fact]
    public void PromptsComeBackUnknownWhileNoPortraitsAreRegistered()
    {
        // The profile ships with an empty portrait set, so every slot should say
        // "nobody" rather than guess. Real matching needs a burst that keeps
        // full-resolution frames through a chain prompt.
        Assert.All(Fixture.Read("036").Prompts, prompt => Assert.False(prompt.IsPresent));
    }

    [Fact]
    public void TheProfileStillValidates()
    {
        IReadOnlyList<ProfileProblem> problems = ProfileValidator.Validate(Fixture.Profile);

        Assert.False(ProfileValidator.HasErrors(problems), string.Join(Environment.NewLine, problems));
    }
}
