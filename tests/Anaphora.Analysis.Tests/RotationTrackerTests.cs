using Anaphora.Core;

namespace Anaphora.Analysis.Tests;

/// <summary>
/// Walks the user's example rotation -- 3, 2, 4E, (1重击) 1E, 1, 2E, 1 -- through
/// scripted HUD snapshots, the way the capture pipeline would feed them.
/// </summary>
public class RotationTrackerTests
{
    private static readonly Rotation Example = RotationStore.Load(
        Path.Combine(AppContext.BaseDirectory, "rotations", "endfield-example.json"));

    private TimeSpan clock;

    [Fact]
    public void StartsAtTheFirstStep()
    {
        var tracker = new RotationTracker(Example);

        Assert.Equal(new RotationCursor(0, 1), tracker.State.Cursor);
        Assert.Equal("3", Now(tracker));
        Assert.Equal(["2", "4E", "~1重击 1E"], tracker.State.Next.Select(n => n.Step.ToString()));
        Assert.Equal("开局", tracker.State.SegmentName);
        Assert.Equal((1, 7), (tracker.State.Position, tracker.State.SegmentLength));
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
        Assert.Equal("1E", Now(tracker));
        Assert.Equal([SoftCue.Heavy(1)], tracker.State.Current.Step.Before);

        // The heavy attack shows nothing; it is a cue on 1E, which it triggers.
        RotationState afterChain = See(tracker, hud with { Sp = 1, Chain4 = false, Chain1 = false });
        Assert.Equal(["1E"], afterChain.Completed.Select(s => s.Action.ToString()));
        Assert.Equal("1", Now(tracker));

        See(tracker, hud with { Sp = 0, Chain4 = false, Chain1 = false });                 // 1
        Assert.Equal("2E", Now(tracker));

        See(tracker, hud with { Sp = 0, Chain4 = false, Chain1 = false, Chain2 = false }); // 2E
        Assert.Equal("1", Now(tracker));

        See(tracker, hud with { Sp = 1, Chain4 = false, Chain1 = false, Chain2 = false }); // regen: no step
        Assert.Equal("1", Now(tracker));

        See(tracker, hud with { Sp = 0, Chain4 = false, Chain1 = false, Chain2 = false }); // 1
        Assert.Equal(new RotationCursor(Example.IndexOf("c1"), 2), tracker.State.Cursor);
        Assert.Equal("循环", tracker.State.SegmentName);
        Assert.Equal("3", Now(tracker));
        Assert.Empty(tracker.State.Unexpected);
    }

    [Fact]
    public void RoundTwoShowsTheHeavyAttackAsBlockedUntilASkillPointReturns()
    {
        var tracker = new RotationTracker(Example);
        AdvanceTo(tracker, "c4");

        RotationState waiting = See(tracker, new Hud(0));
        Assert.Equal("1E", waiting.Current.Step.Action.ToString());
        Assert.Equal([SoftCue.Heavy(1)], waiting.Current.Step.Before);
        Assert.True(waiting.Current.Blocked);
        Assert.Equal("等 2 可放再重击", waiting.Current.Step.Note);
        Assert.Equal(2, waiting.Cursor.Round);

        RotationState clear = See(tracker, new Hud(1));
        Assert.False(clear.Current.Blocked);
    }

    [Fact]
    public void UpcomingConditionsAreEvaluatedToo()
    {
        var tracker = new RotationTracker(Example);
        AdvanceTo(tracker, "c2");

        RotationState state = See(tracker, new Hud(0));

        StepView chain = state.Next.Single(n => n.Step.Id == "c4");
        Assert.True(chain.Blocked);
    }

    [Fact]
    public void AnAnonymousSkillPointNeverJumpsAChain()
    {
        var tracker = new RotationTracker(Example);
        AdvanceTo(tracker, "o3"); // at 4E

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
        RotationState state = See(tracker, hud with { Chain4 = false });

        Assert.Equal(["3", "2", "4E"], state.Completed.Select(s => s.Action.ToString()));
        Assert.Equal("1E", Now(tracker));
    }

    [Fact]
    public void AChainTooFarAheadIsReportedNotChased()
    {
        var tracker = new RotationTracker(Example);
        var hud = new Hud(3);

        See(tracker, hud);
        RotationState state = See(tracker, hud with { Chain2 = false }); // 2E is five steps away

        Assert.Equal("3", Now(tracker));
        Assert.Equal([new Observation(ObservedKind.ChainUsed, 2)], state.Unexpected);
    }

    [Fact]
    public void AnUltimateCutInFreezesTheCursorAndItsUseIsSeenAfterwards()
    {
        var tracker = new RotationTracker(Linear("3 2大招 4E"));
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
    public void ALongAbsenceStartsTheNextFightAtTheFirstStep()
    {
        var tracker = new RotationTracker(Example);
        var hud = new Hud(3);

        See(tracker, hud);
        See(tracker, hud with { Sp = 2 });
        Assert.Equal("2", Now(tracker));

        See(tracker, Hud.Absent, TimeSpan.FromSeconds(10));
        See(tracker, hud with { Sp = 1 }, TimeSpan.FromSeconds(1));

        // Reset, and the new baseline is taken without reading the drop as a step.
        Assert.Equal(new RotationCursor(0, 1), tracker.State.Cursor);
    }

    [Fact]
    public void StartingOverMidFightIsPickedUp()
    {
        // The user's report: off the rotation, then back to the top without
        // leaving the fight. Stepping forward from the old cursor never catches
        // up; the tracker has to notice where the inputs fit.
        var tracker = new RotationTracker(Example);
        var hud = new Hud(3);

        See(tracker, hud);
        See(tracker, hud with { Sp = 2 });                                 // 3
        See(tracker, hud with { Sp = 1 });                                 // 2
        See(tracker, hud with { Sp = 1, Chain4 = false });                 // 4E
        See(tracker, hud with { Sp = 1, Chain4 = false, Chain1 = false }); // 1重击 1E
        See(tracker, hud with { Sp = 0, Chain4 = false, Chain1 = false }); // 1
        Assert.Equal("2E", Now(tracker));

        // Lost the plot: start over with 3, 2, 4E while 2E is still expected.
        See(tracker, hud with { Sp = 3, Chain4 = true, Chain1 = false });  // points and 4's chain came back
        See(tracker, hud with { Sp = 2, Chain1 = false });                 // 3 -- unexplained
        See(tracker, hud with { Sp = 1, Chain1 = false });                 // 2 -- unexplained
        Assert.Equal("2E", Now(tracker));
        Assert.False(tracker.State.Resynced);

        RotationState state = See(tracker, hud with { Sp = 1, Chain1 = false, Chain4 = false }); // 4E

        // The opening happens once, so starting over is the cycle's next round.
        Assert.True(state.Resynced);
        Assert.Equal("1E", Now(tracker));
        Assert.Equal(new RotationCursor(Example.IndexOf("c4"), 2), state.Cursor);
    }

    [Fact]
    public void TwoFittingInputsAreNotEnoughToRealign()
    {
        // 3 then 2E fits "…1, 2E" -- but two inputs are too little evidence to
        // throw the cursor across the rotation.
        var tracker = new RotationTracker(Example);
        var hud = new Hud(3);

        See(tracker, hud);
        See(tracker, hud with { Sp = 2 });
        RotationState state = See(tracker, hud with { Sp = 2, Chain2 = false });

        Assert.False(state.Resynced);
        Assert.Equal("2", Now(tracker));
    }

    [Fact]
    public void APlayerWhoIsOnTrackIsNeverMoved()
    {
        // Following the rotation exactly produces no realignments at all.
        var tracker = new RotationTracker(Example);
        var hud = new Hud(3);

        var states = new List<RotationState>
        {
            See(tracker, hud),
            See(tracker, hud with { Sp = 2 }),
            See(tracker, hud with { Sp = 1 }),
            See(tracker, hud with { Sp = 1, Chain4 = false }),
            See(tracker, hud with { Sp = 1, Chain4 = false, Chain1 = false }),
            See(tracker, hud with { Sp = 0, Chain4 = false, Chain1 = false }),
            See(tracker, hud with { Sp = 0, Chain4 = false, Chain1 = false, Chain2 = false }),
        };

        Assert.DoesNotContain(states, s => s.Resynced);
    }

    [Fact]
    public void ABranchIsSuggestedWhileItsConditionHolds()
    {
        // After 2E: if 4 leads the chain prompt, take 4E; otherwise press 1.
        var tracker = new RotationTracker(Branching);
        var hud = new Hud(3);

        See(tracker, hud);
        See(tracker, hud with { Sp = 2 });                          // 3
        See(tracker, hud with { Sp = 2, Chain2 = false });          // 2E
        Assert.Equal("1", Now(tracker));

        See(tracker, hud with { Sp = 2, Chain2 = false, Lead = 4 });
        Assert.Equal("4E", Now(tracker));

        // The prompt going away changes the suggestion back.
        See(tracker, hud with { Sp = 2, Chain2 = false });
        Assert.Equal("1", Now(tracker));
    }

    [Fact]
    public void TheWayThePlayerActuallyGoesWins()
    {
        var tracker = new RotationTracker(Branching);
        var hud = new Hud(3);

        See(tracker, hud);
        See(tracker, hud with { Sp = 2 });                          // 3
        See(tracker, hud with { Sp = 2, Chain2 = false });          // 2E
        Assert.Equal("1", Now(tracker));

        // 1 was suggested, but 4's chain went: that is the branch.
        RotationState state = See(tracker, hud with { Sp = 2, Chain2 = false, Chain4 = false });

        Assert.Empty(state.Unexpected);
        Assert.Equal("3大招", Now(tracker));
    }

    [Fact]
    public void AKeyPressCompletesASkillWithNoHudChange()
    {
        // The whole reason for listening: a skill that spends less than a whole
        // segment moves nothing on the skill-point bar.
        var tracker = new RotationTracker(Example);
        See(tracker, new Hud(3));

        RotationState state = See(tracker, new Hud(3), [Skill(3)]);

        Assert.Equal("2", Now(tracker));
        Assert.Equal(["3"], state.Completed.Select(s => s.Action.ToString()));
    }

    [Fact]
    public void AHudTraceWithoutAKeyDoesNotMoveTheCursor()
    {
        // Key wins: the skill point went on screen, but no key was pressed, so
        // the step is not taken to have happened.
        var tracker = new RotationTracker(Example);
        See(tracker, new Hud(3));

        RotationState state = See(tracker, new Hud(2), []);

        Assert.Equal("3", Now(tracker));
        Assert.Empty(state.Completed);
        Assert.Empty(state.Unexpected);
    }

    [Fact]
    public void AKeyNamesItsSlotAndCannotBeMistakenForAnother()
    {
        var tracker = new RotationTracker(Linear("3 2"));
        See(tracker, new Hud(3));

        RotationState state = See(tracker, new Hud(3), [Skill(1)]);

        Assert.Equal("3", Now(tracker));
        Assert.Equal([new Observation(ObservedKind.SkillUsed, 1)], state.Unexpected);
    }

    [Fact]
    public void ASlotSpecificKeyRecoversAMissedEarlierSkill()
    {
        // Same recovery the HUD path has for a chain, now for a skill: the key
        // says 2, and 2 is one step on, so 3 is taken as already done.
        var tracker = new RotationTracker(Linear("3 2 4E"));
        See(tracker, new Hud(3));

        RotationState state = See(tracker, new Hud(3), [Skill(2)]);

        Assert.Equal("4E", Now(tracker));
        Assert.Equal(["3", "2"], state.Completed.Select(s => s.Action.ToString()));
    }

    [Fact]
    public void AnUnknownChainTakesTheNearestChainStep()
    {
        var tracker = new RotationTracker(Linear("3 2 4E"));
        See(tracker, new Hud(3));
        See(tracker, new Hud(3), [Skill(3)]);
        See(tracker, new Hud(3), [Skill(2)]);
        Assert.Equal("4E", Now(tracker));

        RotationState state = See(tracker, new Hud(3), [Chain(0)]);

        Assert.Equal("3", Now(tracker));
        Assert.Equal(["4E"], state.Completed.Select(s => s.Action.ToString()));
    }

    [Fact]
    public void EIsNamedByTheLeadingPrompt()
    {
        // E alone cannot say whose chain it was. The prompt does: 2 leads, so
        // this is 2E even if 1E was the nearest chain step.
        var tracker = new RotationTracker(Linear("1E 2E"));
        var hud = new Hud(3) with { Lead = 2 };
        See(tracker, hud);

        RotationState state = See(tracker, hud, [Chain(0)]);

        Assert.Equal(["1E", "2E"], state.Completed.Select(s => s.Action.ToString()));
    }

    [Fact]
    public void AKeyFromTheOldFightDoesNotStartTheNewOne()
    {
        var tracker = new RotationTracker(Linear("3 2"));
        See(tracker, new Hud(3));
        See(tracker, Hud.Absent, TimeSpan.FromSeconds(10));

        RotationState state = See(tracker, new Hud(3), [Skill(3)]);

        Assert.Equal(new RotationCursor(0, 1), state.Cursor);
        Assert.Empty(state.Completed);
    }

    [Fact]
    public void ACastDuringACutInIsNotLost()
    {
        var tracker = new RotationTracker(Linear("3 2 4E"));
        See(tracker, new Hud(3));
        See(tracker, new Hud(3), [Skill(3)]);
        Assert.Equal("2", Now(tracker));

        RotationState during = See(tracker, Hud.Absent, [Skill(2)]);
        Assert.True(during.Stale);

        RotationState after = See(tracker, new Hud(3), []);
        Assert.Equal("4E", Now(tracker));
        Assert.False(after.Stale);
    }

    [Fact]
    public void SkipMovesPastAStepByHand()
    {
        var tracker = new RotationTracker(Example);
        tracker.Skip();

        Assert.Equal("2", Now(tracker));
    }

    /// <summary>3, (3普攻2) 2E, then 1 -- or 4E when 4 leads the prompt -- then 3大招.</summary>
    private static readonly Rotation Branching = Example with
    {
        Segments = [new RotationSegment("m", "主线"), new RotationSegment("x", "分支")],
        Steps =
        [
            new RotationStep { Id = "m1", Segment = "m", Action = RotationAction.Parse("3") },
            new RotationStep
            {
                Id = "m2",
                Segment = "m",
                Action = RotationAction.Parse("2E"),
                Before = [SoftCue.AttackAfterSkill(3, 2)],
                Branches = [new RotationBranch { When = [StepCondition.Parse("prompt:4")], To = "x1" }],
            },
            new RotationStep { Id = "m3", Segment = "m", Action = RotationAction.Parse("1") },
            new RotationStep { Id = "m4", Segment = "m", Action = RotationAction.Parse("3大招"), Next = "m1", NewRound = true },
            new RotationStep { Id = "x1", Segment = "x", Action = RotationAction.Parse("4E"), Next = "m4" },
        ],
    };

    private static Rotation Linear(string line) => Example with
    {
        Segments = [],
        Steps = [.. RotationText.Parse(line).Select((e, i) => new RotationStep { Id = $"s{i}", Action = e.Action, Before = e.Before })],
    };

    private static string Now(RotationTracker tracker) => tracker.State.Current.Step.Action.ToString();

    private RotationState See(RotationTracker tracker, HudSnapshot snapshot, TimeSpan? after = null)
    {
        clock += after ?? TimeSpan.FromMilliseconds(50);
        return tracker.Observe(snapshot, clock);
    }

    private RotationState See(RotationTracker tracker, HudSnapshot snapshot, IReadOnlyList<CastInput> casts)
    {
        clock += TimeSpan.FromMilliseconds(50);
        return tracker.Observe(snapshot, clock, casts, keyboardAuthoritative: true);
    }

    private static CastInput Skill(int slot) => new(CastKind.Skill, slot, TimeSpan.Zero);

    private static CastInput Chain(int slot) => new(CastKind.Chain, slot, TimeSpan.Zero);

    private static void AdvanceTo(RotationTracker tracker, string id)
    {
        int target = tracker.Rotation.IndexOf(id);
        for (int guard = 0; tracker.State.Cursor.Step != target || (id.StartsWith('c') && tracker.State.Cursor.Round < 2); guard++)
        {
            Assert.True(guard < 100, $"never reached {id}");
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

        /// <summary>The slot leading the chain prompt, or 0 for none.</summary>
        public int Lead { get; init; }

        public static implicit operator HudSnapshot(Hud hud) => new()
        {
            HudPresent = true,
            SkillPoints = hud.Sp,
            Chains = [Bar(hud.Chain1), Bar(hud.Chain2), Bar(hud.Chain3), Bar(hud.Chain4)],
            Ultimates = [Disc(hud.Ult1), Disc(hud.Ult2), Disc(hud.Ult3), Disc(hud.Ult4)],
            Prompts = hud.Lead == 0 ? [] : [new PortraitReading($"slot{hud.Lead}", 0, 0) { Slot = hud.Lead }],
        };

        private static FillBarReading Bar(bool ready) => new(ready ? 1 : 0.1, ready);

        private static DiscStateReading Disc(bool ready) => new(ready, ready ? 1 : 0.3, ready ? 1 : 0.1);
    }
}
