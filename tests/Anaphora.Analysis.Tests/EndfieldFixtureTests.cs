using Anaphora.Core;

namespace Anaphora.Analysis.Tests;

/// <summary>
/// The readers against real captured frames, with expectations taken from
/// reading the frames by eye and confirmed by measuring them with the probe.
/// These are what say the thresholds are right; the synthetic tests only say the
/// arithmetic is.
///
/// Two fights: plain numbers are captures/combat, on a dark map; "f1-" frames
/// are captures/live/fight1, on a bright one that broke the first calibration.
/// </summary>
public class EndfieldFixtureTests
{
    [Theory]
    [InlineData("008")]
    [InlineData("032")]
    [InlineData("036")]
    [InlineData("040")]
    // Mid-combat with an effect washing the skill-point strip out. The first
    // sentinel refused it; the HP bar underneath is intact, so it is readable.
    [InlineData("020")]
    // Bright floor seen through the translucent HUD tracks.
    [InlineData("f1-047")]
    [InlineData("f1-048")]
    [InlineData("f1-056")]
    [InlineData("f1-066")]
    [InlineData("f1-072")]
    public void CombatFramesAreReadable(string frame)
    {
        HudSnapshot snapshot = Fixture.Read(frame);

        Assert.True(snapshot.HudPresent, $"frame-{frame}: {snapshot.Presence}");
    }

    [Theory]
    // After the fight: the bottom bar is not drawn.
    [InlineData("048")]
    // A full-screen ultimate cut-in, bright white.
    [InlineData("024")]
    // The reward screen.
    [InlineData("084")]
    // Walking the map before the fight, on the bright floor.
    [InlineData("f1-020")]
    public void FramesWithoutAHudAreRejected(string frame)
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
    [InlineData("f1-047", 1)]
    [InlineData("f1-056", 0)]
    [InlineData("f1-066", 0)]
    [InlineData("f1-072", 0)]
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

    [Theory]
    [InlineData("008")]
    public void AllChainsReadyEarlyInTheFight(string frame)
    {
        Assert.All(Fixture.Read(frame).Chains, chain => Assert.True(chain.IsReady, chain.ToString()));
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

    [Fact]
    public void ChainBarsStillReadOverABrightFloor()
    {
        Assert.Equal([true, true, true, false], Fixture.Read("f1-047").Chains.Select(c => c.IsReady));
    }

    [Theory]
    [InlineData("008", true, true, true, true)]
    [InlineData("032", false, true, false, false)]
    [InlineData("036", false, true, false, false)]
    // Slot 2 is still ready here, but the whole HUD is fading out as the fight
    // ends and its ring has dimmed below the threshold. Accepted: the worst it
    // causes is one spurious "used" at the very end of a fight.
    [InlineData("040", false, false, false, false)]
    // Every slot charging, over floor stripes that made the first reader call
    // several of them ready.
    [InlineData("f1-047", false, false, false, false)]
    [InlineData("f1-066", false, false, false, false)]
    [InlineData("f1-072", false, false, false, false)]
    // And a genuinely ready slot on the same bright floor.
    [InlineData("f1-074", false, false, false, true)]
    public void UltimateSlotsReportWhoCanCast(string frame, bool a, bool b, bool c, bool d)
    {
        IReadOnlyList<DiscStateReading> ultimates = Fixture.Read(frame).Ultimates;

        Assert.Equal(4, ultimates.Count);
        Assert.Equal([a, b, c, d], ultimates.Select(u => u.IsReady));
    }

    [Fact]
    public void TheReadyRingIsUnambiguous()
    {
        // Ready rings measured 86-89% (the ring leaves a gap), charging ones 0%.
        IReadOnlyList<DiscStateReading> dark = Fixture.Read("036").Ultimates;
        Assert.True(dark[1].RingCoverage > 0.8, $"ready slot ring {dark[1].RingCoverage:P0}");
        Assert.All(dark.Where((_, i) => i != 1), slot => Assert.True(slot.RingCoverage < 0.3, slot.ToString()));

        Assert.All(Fixture.Read("f1-066").Ultimates, slot => Assert.True(slot.RingCoverage < 0.5, slot.ToString()));
    }

    [Fact]
    public void ChargeIsReadFromTheArc()
    {
        // frame f1-066: slot 4's lime arc is most of the way round, slot 3's
        // orange arc barely started.
        IReadOnlyList<DiscStateReading> ultimates = Fixture.Read("f1-066").Ultimates;

        Assert.True(ultimates[3].Charge > 0.7, ultimates[3].ToString());
        Assert.True(ultimates[2].Charge < 0.4, ultimates[2].ToString());
    }

    [Fact]
    public void TheChargeArcDoesNotMakeASlotLookReady()
    {
        // Slot 4 in frame-036 carries a bright yellow-green arc on its ring. It
        // reads as charging because only the outer ring decides readiness.
        Assert.False(Fixture.Read("036").Ultimates[3].IsReady);
    }

    [Theory]
    // Two offered at once: 1 leads, 2 waits in the smaller circle.
    [InlineData("f1-048", 1, 2)]
    [InlineData("f1-049", 2, 0)]
    [InlineData("f1-073", 2, 0)]
    [InlineData("f1-064", 4, 0)]
    [InlineData("f1-067", 1, 0)]
    // No prompt on screen: nobody, rather than the nearest stranger.
    [InlineData("f1-047", 0, 0)]
    [InlineData("036", 0, 0)]
    public void ChainPromptsNameTheSlotInOrder(string frame, int first, int second)
    {
        // 0 means no one. Same operator across frames hashes 0-5 apart, the
        // smaller second circle 8; different operators and empty backgrounds
        // are 18 or more, against a threshold of 12.
        IReadOnlyList<PortraitReading> prompts = Fixture.Read(frame).Prompts;

        Assert.Equal(2, prompts.Count);
        Assert.Equal(first == 0 ? null : first, prompts[0].Slot);
        Assert.Equal(second == 0 ? null : second, prompts[1].Slot);
    }

    [Fact]
    public void ThePromptFrom048HasTwoOperators()
    {
        IReadOnlyList<PortraitReading> prompts = Fixture.Read("f1-048").Prompts;

        Assert.Equal([1, 2], prompts.Select(p => p.Slot));
    }

    [Fact]
    public void TheProfileStillValidates()
    {
        IReadOnlyList<ProfileProblem> problems = ProfileValidator.Validate(Fixture.Profile);

        Assert.False(ProfileValidator.HasErrors(problems), string.Join(Environment.NewLine, problems));
    }
}
