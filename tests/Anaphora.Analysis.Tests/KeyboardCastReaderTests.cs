using Anaphora.Analysis;

namespace Anaphora.Analysis.Tests;

public class KeyboardCastReaderTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void AShortNumberPressIsASkillWhenReleased()
    {
        var reader = new KeyboardCastReader(Hold);
        var casts = Listen(reader);

        reader.Feed(Down(GameKey.Slot3, 0));
        Assert.Empty(casts);

        reader.Feed(Up(GameKey.Slot3, 80));

        CastInput cast = Assert.Single(casts);
        Assert.Equal(CastKind.Skill, cast.Kind);
        Assert.Equal(3, cast.Slot);
        Assert.Equal(TimeSpan.FromMilliseconds(80), cast.Held);
    }

    [Fact]
    public void AHeldNumberIsAnUltimateAtTheThresholdNotOnRelease()
    {
        var reader = new KeyboardCastReader(Hold);
        var casts = Listen(reader);

        reader.Feed(Down(GameKey.Slot2, 0));
        reader.Tick(TimeSpan.FromMilliseconds(499));
        Assert.Empty(casts);

        reader.Tick(TimeSpan.FromMilliseconds(500));

        CastInput cast = Assert.Single(casts);
        Assert.Equal(CastKind.Ultimate, cast.Kind);
        Assert.Equal(2, cast.Slot);
        Assert.Equal(TimeSpan.FromMilliseconds(500), cast.At);

        reader.Tick(TimeSpan.FromMilliseconds(900));
        reader.Feed(Up(GameKey.Slot2, 1000));
        Assert.Single(casts);
    }

    [Fact]
    public void AReleaseAfterTheThresholdStillReportsOneUltimate()
    {
        // The polling thread can be delayed. If the threshold was crossed but
        // Tick never ran, the release still has to become an ultimate, not a
        // skill.
        var reader = new KeyboardCastReader(Hold);
        var casts = Listen(reader);

        reader.Feed(Down(GameKey.Slot4, 0));
        reader.Feed(Up(GameKey.Slot4, 700));

        CastInput cast = Assert.Single(casts);
        Assert.Equal(CastKind.Ultimate, cast.Kind);
        Assert.Equal(4, cast.Slot);
    }

    [Fact]
    public void EIsAChainOnTheWayDownAndHoldingItDoesNotRepeat()
    {
        var reader = new KeyboardCastReader(Hold);
        var casts = Listen(reader);

        reader.Feed(Down(GameKey.Chain, 0));
        reader.Feed(Down(GameKey.Chain, 200));
        reader.Tick(TimeSpan.FromMilliseconds(700));
        reader.Feed(Up(GameKey.Chain, 800));

        CastInput cast = Assert.Single(casts);
        Assert.Equal(CastKind.Chain, cast.Kind);
        Assert.Equal(0, cast.Slot);
        Assert.Equal(TimeSpan.Zero, cast.At);
    }

    [Fact]
    public void AKeyReleasedAfterResetReportsNothing()
    {
        var reader = new KeyboardCastReader(Hold);
        var casts = Listen(reader);

        reader.Feed(Down(GameKey.Slot1, 0));
        reader.Reset();
        reader.Feed(Up(GameKey.Slot1, 50));
        reader.Tick(TimeSpan.FromSeconds(1));

        Assert.Empty(casts);
    }

    [Fact]
    public void TwoKeysAreTrackedIndependently()
    {
        var reader = new KeyboardCastReader(Hold);
        var casts = Listen(reader);

        reader.Feed(Down(GameKey.Slot1, 0));
        reader.Feed(Down(GameKey.Slot3, 100));
        reader.Feed(Up(GameKey.Slot1, 150));
        reader.Tick(TimeSpan.FromMilliseconds(600));

        Assert.Equal(2, casts.Count);
        Assert.Equal((CastKind.Skill, 1), (casts[0].Kind, casts[0].Slot));
        Assert.Equal((CastKind.Ultimate, 3), (casts[1].Kind, casts[1].Slot));
    }

    [Fact]
    public void ATapShorterThanAPollStillReadsAsASkill()
    {
        var reader = new KeyboardCastReader(Hold);
        var casts = Listen(reader);

        reader.Feed(Down(GameKey.Slot4, 10));
        reader.Feed(Up(GameKey.Slot4, 10));

        CastInput cast = Assert.Single(casts);
        Assert.Equal(CastKind.Skill, cast.Kind);
        Assert.Equal(4, cast.Slot);
    }

    private static List<CastInput> Listen(KeyboardCastReader reader)
    {
        var casts = new List<CastInput>();
        reader.Cast += casts.Add;
        return casts;
    }

    private static KeyTransition Down(GameKey key, double milliseconds) =>
        new(key, true, TimeSpan.FromMilliseconds(milliseconds));

    private static KeyTransition Up(GameKey key, double milliseconds) =>
        new(key, false, TimeSpan.FromMilliseconds(milliseconds));
}
