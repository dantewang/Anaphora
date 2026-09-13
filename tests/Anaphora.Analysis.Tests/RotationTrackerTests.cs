using Anaphora.Core;

namespace Anaphora.Analysis.Tests;

/// <summary>
/// Walks the user's example rotation -- 3, 2, 4E, 1重击, 1E, 1, 2E, 1 -- through
/// scripted HUD snapshots, the way the capture pipeline would feed them.
/// </summary>
public class RotationTrackerTests
{
    private static readonly Rotation Example = RotationStore.Load(
        Path.Combine(AppContext.BaseDirectory, "rotations", "endfield-example.json"));

    private TimeSpan clock;

    [Fact]
    public void StartsAtTheOpening()
    {
        var tracker = new RotationTracker(Example);

        Assert.Equal(new RotationCursor(RotationPhase.Opening, 1, 0), tracker.State.Cursor);
        Assert.Equal("3", tracker.State.Current.Step.Action.ToString());
        Assert.Equal(["2", "4E", "1重击"], tracker.State.Next.Select(n => n.Step.Action.ToString()));
    }

    [Fact]
    public void FollowsAWholeRoundOfTheExample()
    {
        var tracker = new RotationTracker(Example);
        var hud = new Hud(3);

        See(tracker, hud);                                // first readable frame: baseline only
        Assert.Equal("3", Now(tracker));

        See(tracker, hud with { Sp = 2 });                // 3
        Assert.Equal("2", Now(tracker));

        See(tracker, hud with { Sp = 1 });                // 2 -> offers 4's chain
        Assert.Equal("4E", Now(tracker));

        See(tracker, hud with { Sp = 1, Chain4 = false }); // 4E
        Assert.Equal("1重击", Now(tracker));

        // The heavy attack itself shows nothing. 1E, which it triggered, is
        // what moves the cursor past it.
        RotationState afterChain = See(tracker, hud with { Sp = 1, Chain4 = false, Chain1 = false });
        Assert.Equal(["1重击", "1E"], afterChain.Completed.Select(a => a.ToString()));
        Assert.Equal("1", Now(tracker));

        See(tracker, hud with { Sp = 0, Chain4 = false, Chain1 = false });                 // 1
        Assert.Equal("2E", Now(tracker));

        See(tracker, hud with { Sp = 0, Chain4 = false, Chain1 = false, Chain2 = false }); // 2E
        Assert.Equal("1", Now(tracker));

        See(tracker, hud with { Sp = 1, Chain4 = false, Chain1 = false, Chain2 = false }); // regen: no step
        Assert.Equal("1", Now(tracker));

        See(tracker, hud with { Sp = 0, Chain4 = false, Chain1 = false, Chain2 = false }); // 1
        Assert.Equal(new RotationCursor(RotationPhase.Cycle, 2, 0), tracker.State.Cursor);
        Assert.Equal("3", Now(tracker));
        Assert.Empty(tracker.State.Unexpected);
    }

    [Fact]
    public void RoundTwoShowsTheHeavyAttackAsBlockedUntilASkillPointReturns()
    {
        var tracker = new RotationTracker(Example);
        AdvanceTo(tracker, new RotationCursor(RotationPhase.Cycle, 2, 3));

        RotationState waiting = See(tracker, new Hud(0));
        Assert.Equal("1重击", waiting.Current.Step.Action.ToString());
        Assert.True(waiting.Current.Blocked);
        Assert.Equal("等 2 可放再重击", waiting.Current.Step.Note);

        RotationState clear = See(tracker, new Hud(1));
        Assert.False(clear.Current.Blocked);
    }

    [Fact]
    public void UpcomingConditionsAreEvaluatedToo()
    {
        var tracker = new RotationTracker(Example);
        AdvanceTo(tracker, new RotationCursor(RotationPhase.Cycle, 2, 1));

        RotationState state = See(tracker, new Hud(0));

        StepView heavy = state.Next.Single(n => n.Step.Action.Kind == ActionKind.Heavy);
        Assert.True(heavy.Blocked);
    }

    [Fact]
    public void AnAnonymousSkillPointNeverJumpsAChain()
    {
        var tracker = new RotationTracker(Example);
        AdvanceTo(tracker, new RotationCursor(RotationPhase.Opening, 1, 2)); // at 4E

        See(tracker, new Hud(2));
        RotationState state = See(tracker, new Hud(1));

        Assert.Equal("4E", Now(tracker));
        Assert.Equal([new Observation(ObservedKind.SkillSpent, 0)], state.Unexpected);
    }

    [Fact]
    public void AChainRecoversFromMissedSkills()
    {
        // Skills whose cost is less than a whole segment go unseen. The chain
        // they led to still names its slot, so the cursor catches up.
        var tracker = new RotationTracker(Example);
        var hud = new Hud(2);

        See(tracker, hud);
        See(tracker, hud with { Chain4 = false });

        Assert.Equal("1重击", Now(tracker));
    }

    [Fact]
    public void AChainTooFarAheadIsReportedNotChased()
    {
        var tracker = new RotationTracker(Example);
        var hud = new Hud(3);

        See(tracker, hud);
        RotationState state = See(tracker, hud with { Chain2 = false }); // 2E is six steps away

        Assert.Equal("3", Now(tracker));
        Assert.Equal([new Observation(ObservedKind.ChainUsed, 2)], state.Unexpected);
    }

    [Fact]
    public void AnUltimateCutInFreezesTheCursorAndItsUseIsSeenAfterwards()
    {
        Rotation withUltimate = Example with { Opening = ["3", "2大招", "4E"], Cycle = ["3"] };
        var tracker = new RotationTracker(withUltimate);
        var hud = new Hud(3) with { Ult2 = true };

        See(tracker, hud);
        See(tracker, hud with { Sp = 2 });
        Assert.Equal("2大招", Now(tracker));

        RotationState during = See(tracker, Hud.Absent, TimeSpan.FromSeconds(2));
        Assert.True(during.Stale);
        Assert.Equal("2大招", during.Current.Step.Action.ToString());

        See(tracker, hud with { Sp = 2, Ult2 = false }, TimeSpan.FromSeconds(1));
        Assert.Equal("4E", Now(tracker));
    }

    [Fact]
    public void ALongAbsenceStartsTheNextFightAtTheOpening()
    {
        var tracker = new RotationTracker(Example);
        var hud = new Hud(3);

        See(tracker, hud);
        See(tracker, hud with { Sp = 2 });
        Assert.Equal("2", Now(tracker));

        See(tracker, Hud.Absent, TimeSpan.FromSeconds(10));
        See(tracker, hud with { Sp = 1 }, TimeSpan.FromSeconds(1));

        // Reset, and the new baseline is taken without reading the drop as a step.
        Assert.Equal(new RotationCursor(RotationPhase.Opening, 1, 0), tracker.State.Cursor);
    }

    [Fact]
    public void SkipMovesPastAStepByHand()
    {
        var tracker = new RotationTracker(Example);
        tracker.Skip();

        Assert.Equal("2", Now(tracker));
    }

    private static string Now(RotationTracker tracker) => tracker.State.Current.Step.Action.ToString();

    private RotationState See(RotationTracker tracker, HudSnapshot snapshot, TimeSpan? after = null)
    {
        clock += after ?? TimeSpan.FromMilliseconds(50);
        return tracker.Observe(snapshot, clock);
    }

    private static void AdvanceTo(RotationTracker tracker, RotationCursor target)
    {
        for (int guard = 0; tracker.State.Cursor != target; guard++)
        {
            Assert.True(guard < 100, $"never reached {target}");
            tracker.Skip();
        }
    }

    private sealed record Hud(int Sp)
    {
        public static readonly HudSnapshot Absent = HudSnapshot.Absent;

        public bool Chain1 { get; init; } = true;

        public bool Chain2 { get; init; } = true;

        public bool Chain3 { get; init; } = true;

        public bool Chain4 { get; init; } = true;

        public bool Ult1 { get; init; }

        public bool Ult2 { get; init; }

        public bool Ult3 { get; init; }

        public bool Ult4 { get; init; }

        public static implicit operator HudSnapshot(Hud hud) => new()
        {
            HudPresent = true,
            SkillPoints = hud.Sp,
            Chains = [Bar(hud.Chain1), Bar(hud.Chain2), Bar(hud.Chain3), Bar(hud.Chain4)],
            Ultimates = [Disc(hud.Ult1), Disc(hud.Ult2), Disc(hud.Ult3), Disc(hud.Ult4)],
        };

        private static FillBarReading Bar(bool ready) => new(ready ? 1 : 0.1, ready);

        private static DiscStateReading Disc(bool ready) => new(ready, ready ? 1 : 0.3, ready ? 1 : 0.1);
    }
}
