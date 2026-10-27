using Anaphora.Core;

namespace Anaphora.Analysis.Tests;

/// <summary>
/// Walks the user's example rotation -- 3, 2, 4E, (1重击) 1E, 1, 2E, 1 -- through
/// scripted frames and key casts, the way the capture pipeline and the keyboard
/// listener would feed them. The keys are what move the cursor; the frames only
/// say what is possible at the time.
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

        See(tracker, hud);
        Assert.Equal("3", Now(tracker));

        See(tracker, hud with { Sp = 2 }, [Skill(3)]);
        Assert.Equal("2", Now(tracker));

        See(tracker, hud with { Sp = 1 }, [Skill(2)]);
        Assert.Equal("4E", Now(tracker));

        See(tracker, hud with { Sp = 1, Chain4 = false }, [Chain(4)]);
        Assert.Equal("1E", Now(tracker));
        Assert.Equal([SoftCue.Heavy(1)], tracker.State.Current.Step.Before);

        // The heavy attack has no key; it is a cue on 1E, which it triggers.
        RotationState afterChain = See(tracker, hud with { Sp = 1, Chain4 = false, Chain1 = false }, [Chain(1)]);
        Assert.Equal(["1E"], afterChain.Completed.Select(s => s.Action.ToString()));
        Assert.Equal("1", Now(tracker));

        See(tracker, hud with { Sp = 0, Chain4 = false, Chain1 = false }, [Skill(1)]);
        Assert.Equal("2E", Now(tracker));

        See(tracker, hud with { Sp = 0, Chain1 = false, Chain2 = false }, [Chain(2)]);
        Assert.Equal("1", Now(tracker));

        See(tracker, hud with { Sp = 1, Chain1 = false, Chain2 = false }); // regen: no key, no step
        Assert.Equal("1", Now(tracker));

        See(tracker, hud with { Sp = 0, Chain1 = false, Chain2 = false }, [Skill(1)]);
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
    public void TheHudAloneNeverCompletesAStep()
    {
        // Every trace a cast leaves on screen at once: a skill point gone, a
        // chain cooling down, an ultimate spent. Without a key, none of it
        // moves the cursor -- a point is shared by the team and cannot say
        // whose skill it was, so believing the picture is how a mispress gets
        // rewarded.
        var tracker = new RotationTracker(Example);
        See(tracker, new Hud(3));

        RotationState state = See(tracker, new Hud(2) with { Chain4 = false, Ult1 = true });

        Assert.Equal("3", Now(tracker));
        Assert.Empty(state.Completed);
        Assert.Empty(state.Unexpected);
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
    public void TheWrongSlotLeavesTheCursorWhereItIs()
    {
        var tracker = new RotationTracker(Linear("3 2"));
        See(tracker, new Hud(3));

        RotationState state = See(tracker, new Hud(3), [Skill(1)]);

        Assert.Equal("3", Now(tracker));
        Assert.Empty(state.Completed);
        Assert.Equal([new Observation(CastKind.Skill, 1)], state.Unexpected);
    }

    [Fact]
    public void AKeyForALaterStepDoesNotPullTheCursorForward()
    {
        // 2 is the step after this one. Taking it would mean a player who
        // pressed the wrong key, or pressed early, silently loses a step.
        var tracker = new RotationTracker(Linear("3 2 4E"));
        See(tracker, new Hud(3));

        RotationState state = See(tracker, new Hud(3), [Skill(2)]);

        Assert.Equal("3", Now(tracker));
        Assert.Equal([new Observation(CastKind.Skill, 2)], state.Unexpected);
    }

    [Fact]
    public void AChainFurtherAlongIsReportedNotChased()
    {
        var tracker = new RotationTracker(Example);
        See(tracker, new Hud(3));

        RotationState state = See(tracker, new Hud(3), [Chain(2)]);

        Assert.Equal("3", Now(tracker));
        Assert.Equal([new Observation(CastKind.Chain, 2)], state.Unexpected);
    }

    [Fact]
    public void AnUnnamedChainTakesTheChainStepAtHand()
    {
        var tracker = new RotationTracker(Linear("3 2 4E"));
        var hud = new Hud(3);
        See(tracker, hud);
        See(tracker, hud, [Skill(3)]);
        See(tracker, hud, [Skill(2)]);
        Assert.Equal("4E", Now(tracker));

        RotationState state = See(tracker, hud, [Chain(0)]);

        Assert.Equal(["4E"], state.Completed.Select(s => s.Action.ToString()));
    }

    [Fact]
    public void EIsNamedByTheLeadingPrompt()
    {
        // E alone cannot say whose chain it was; the prompt can. 2 leads, so
        // this press is 2E -- and is not 1E, however near that step is.
        var tracker = new RotationTracker(Linear("2E"));
        var hud = new Hud(3) with { Lead = 2 };
        See(tracker, hud);

        RotationState state = See(tracker, hud, [Chain(0)]);
        Assert.Equal(["2E"], state.Completed.Select(s => s.Action.ToString()));

        var other = new RotationTracker(Linear("2E"));
        See(other, hud with { Lead = 1 });
        RotationState wrong = See(other, hud with { Lead = 1 }, [Chain(0)]);

        Assert.Equal("2E", Now(other));
        Assert.Equal([new Observation(CastKind.Chain, 1)], wrong.Unexpected);
    }

    [Fact]
    public void AnUltimateIsSeenThroughItsOwnCutIn()
    {
        // The press that hides the HUD is exactly the one that must not be
        // dropped: the cut-in starts before the next frame is captured.
        var tracker = new RotationTracker(Linear("3 2大招 4E"));
        var hud = new Hud(3) with { Ult2 = true };

        See(tracker, hud);
        See(tracker, hud with { Sp = 2 }, [Skill(3)]);
        Assert.Equal("2大招", Now(tracker));

        RotationState during = See(tracker, Hud.Absent, [Ultimate(2)], TimeSpan.FromSeconds(2));

        Assert.True(during.Stale);
        Assert.Equal(["2大招"], during.Completed.Select(s => s.Action.ToString()));
        Assert.Equal("4E", Now(tracker));
    }

    [Fact]
    public void KeysKeepTheFightAliveWhileTheHudStaysHidden()
    {
        // Back-to-back cut-ins hide the HUD for longer than a fight's idle
        // timeout. The player is at their busiest, so this must not be read as
        // the fight being over.
        var tracker = new RotationTracker(Linear("3 2 4E"));
        var hud = new Hud(3);

        See(tracker, hud);
        See(tracker, hud, [Skill(3)]);
        See(tracker, Hud.Absent, [Skill(2)], TimeSpan.FromSeconds(5));
        Assert.Equal("4E", Now(tracker));

        See(tracker, Hud.Absent, TimeSpan.FromSeconds(5));

        Assert.Equal("4E", Now(tracker));
        Assert.Equal(1, tracker.State.Cursor.Round);
    }

    [Fact]
    public void AQuietSpellStartsTheNextFightAtTheFirstStep()
    {
        var tracker = new RotationTracker(Example);
        var hud = new Hud(3);

        See(tracker, hud);
        See(tracker, hud with { Sp = 2 }, [Skill(3)]);
        Assert.Equal("2", Now(tracker));

        See(tracker, Hud.Absent, TimeSpan.FromSeconds(10));
        See(tracker, hud, TimeSpan.FromSeconds(1));

        Assert.Equal(new RotationCursor(0, 1), tracker.State.Cursor);
    }

    [Fact]
    public void AKeyPressedOutsideAFightDoesNotStartOne()
    {
        // No HUD for a long while, then 1-4 or E in a menu or on the way
        // somewhere. Only the HUD coming back says the next fight has started.
        var tracker = new RotationTracker(Linear("3 2"));
        See(tracker, new Hud(3));
        See(tracker, Hud.Absent, TimeSpan.FromSeconds(10));

        RotationState state = See(tracker, Hud.Absent, [Skill(3)], TimeSpan.FromSeconds(1));
        Assert.Equal(new RotationCursor(0, 1), state.Cursor);
        Assert.Empty(state.Completed);

        See(tracker, new Hud(3), [Skill(3)]);
        Assert.Equal("2", Now(tracker));
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
        See(tracker, hud, [Skill(3)]);
        See(tracker, hud, [Skill(2)]);
        See(tracker, hud, [Chain(4)]);
        See(tracker, hud, [Chain(1)]);
        See(tracker, hud, [Skill(1)]);
        Assert.Equal("2E", Now(tracker));

        // Lost the plot: start over with 3, 2, 4E while 2E is still expected.
        See(tracker, hud, [Skill(3)]);
        See(tracker, hud, [Skill(2)]);
        Assert.Equal("2E", Now(tracker));
        Assert.False(tracker.State.Resynced);

        RotationState state = See(tracker, hud, [Chain(4)]);

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
        See(tracker, hud, [Skill(3)]);
        RotationState state = See(tracker, hud, [Chain(2)]);

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
            See(tracker, hud, [Skill(3)]),
            See(tracker, hud, [Skill(2)]),
            See(tracker, hud, [Chain(4)]),
            See(tracker, hud, [Chain(1)]),
            See(tracker, hud, [Skill(1)]),
            See(tracker, hud, [Chain(2)]),
        };

        Assert.DoesNotContain(states, s => s.Resynced);
        Assert.DoesNotContain(states, s => s.Unexpected.Count > 0);
    }

    [Fact]
    public void ABranchIsSuggestedWhileItsConditionHolds()
    {
        // After 2E: if 4 leads the chain prompt, take 4E; otherwise press 1.
        var tracker = new RotationTracker(Branching);
        var hud = new Hud(3);

        See(tracker, hud);
        See(tracker, hud, [Skill(3)]);
        See(tracker, hud, [Chain(2)]);
        Assert.Equal("1", Now(tracker));

        // A new way out has to hold for two frames before it is taken.
        See(tracker, hud with { Lead = 4 });
        Assert.Equal("1", Now(tracker));
        See(tracker, hud with { Lead = 4 });
        Assert.Equal("4E", Now(tracker));

        // The prompt going away changes the suggestion back, once it sticks.
        See(tracker, hud);
        See(tracker, hud);
        Assert.Equal("1", Now(tracker));
    }

    [Fact]
    public void AReadingThatWobblesDoesNotFlickTheCursorBetweenWays()
    {
        // The ultimate ring and the chain bars are colour thresholds, and a
        // frame on the wrong side of one must not throw the cursor into
        // another segment and back.
        var tracker = new RotationTracker(Branching);
        var hud = new Hud(3);

        See(tracker, hud);
        See(tracker, hud, [Skill(3)]);
        See(tracker, hud, [Chain(2)]);
        Assert.Equal("1", Now(tracker));

        for (int frame = 0; frame < 6; frame++)
        {
            See(tracker, frame % 2 == 0 ? hud with { Lead = 4 } : hud);
            Assert.Equal("1", Now(tracker));
        }
    }

    [Fact]
    public void TheWayThePlayerActuallyGoesWins()
    {
        var tracker = new RotationTracker(Branching);
        var hud = new Hud(3);

        See(tracker, hud);
        See(tracker, hud, [Skill(3)]);
        See(tracker, hud, [Chain(2)]);
        Assert.Equal("1", Now(tracker));

        // 1 was suggested, but 4's chain is what was pressed: that is the branch.
        RotationState state = See(tracker, hud, [Chain(4)]);

        Assert.Empty(state.Unexpected);
        Assert.Equal("3大招", Now(tracker));
    }

    [Fact]
    public void AnAndBranchWaitsForEveryCondition()
    {
        // The user's rotation: charge until 1, 2 and 4 can all ult, then burst.
        // Written as three one-condition branches this fires on the first of
        // them, which is a whole round and a half early.
        var tracker = new RotationTracker(Gated(BranchMatch.All));
        var hud = new Hud(3);

        See(tracker, hud);
        See(tracker, hud with { Ult1 = true }, [Skill(1)]);
        See(tracker, hud with { Ult1 = true }, [Skill(1)]);
        Assert.Equal("1", Now(tracker));

        See(tracker, hud with { Ult1 = true, Ult2 = true });
        See(tracker, hud with { Ult1 = true, Ult2 = true });
        Assert.Equal("1", Now(tracker));

        See(tracker, hud with { Ult1 = true, Ult2 = true, Ult4 = true });
        See(tracker, hud with { Ult1 = true, Ult2 = true, Ult4 = true });
        Assert.Equal("3", Now(tracker));
    }

    [Fact]
    public void AnOrBranchTakesTheFirstConditionThatHolds()
    {
        var tracker = new RotationTracker(Gated(BranchMatch.Any));
        var hud = new Hud(3);

        See(tracker, hud);
        See(tracker, hud, [Skill(1)]);
        See(tracker, hud with { Ult2 = true });
        See(tracker, hud with { Ult2 = true });

        Assert.Equal("3", Now(tracker));
    }

    [Fact]
    public void ASegmentsWayOutIsTakenFromAnyStepInIt()
    {
        // Hung on the last step, the way out is only looked at once a whole
        // round of the segment is done. The player topping an ultimate up
        // mid-round wants to leave as soon as it is full.
        var tracker = new RotationTracker(Interrupted);
        var hud = new Hud(3);

        See(tracker, hud);
        See(tracker, hud, [Skill(1)]);
        Assert.Equal("2", Now(tracker));

        See(tracker, hud with { Ult3 = true }, [Skill(2)]);
        See(tracker, hud with { Ult3 = true });

        Assert.Equal("3大招", Now(tracker));
        Assert.Equal("爆发", tracker.State.SegmentName);
    }

    [Fact]
    public void ASegmentsWayOutLeavesTheOrdinaryRunAlone()
    {
        var tracker = new RotationTracker(Interrupted);
        var hud = new Hud(3);

        See(tracker, hud);
        See(tracker, hud, [Skill(1)]);
        See(tracker, hud, [Skill(2)]);

        Assert.Equal("4", Now(tracker));
        Assert.Equal("主线", tracker.State.SegmentName);
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

    /// <summary>1, 1, 1 … and out to a burst segment once the ultimates say so.</summary>
    private static Rotation Gated(BranchMatch match) => Example with
    {
        Segments = [new RotationSegment("main", "主线"), new RotationSegment("burst", "爆发")],
        Steps =
        [
            new RotationStep
            {
                Id = "g1",
                Segment = "main",
                Action = RotationAction.Parse("1"),
                Next = "g1",
                NewRound = true,
                Branches =
                [
                    new RotationBranch
                    {
                        Match = match,
                        When = [StepCondition.Parse("ult:1"), StepCondition.Parse("ult:2"), StepCondition.Parse("ult:4")],
                        To = "b1",
                    },
                ],
            },
            new RotationStep { Id = "b1", Segment = "burst", Action = RotationAction.Parse("3") },
        ],
    };

    /// <summary>A three-step main segment that may be left at any point once 3 can ult.</summary>
    private static readonly Rotation Interrupted = Example with
    {
        Segments =
        [
            new RotationSegment("main", "主线")
            {
                Branches = [new RotationBranch { When = [StepCondition.Parse("ult:3")], To = "b1" }],
            },
            new RotationSegment("burst", "爆发"),
        ],
        Steps =
        [
            new RotationStep { Id = "m1", Segment = "main", Action = RotationAction.Parse("1") },
            new RotationStep { Id = "m2", Segment = "main", Action = RotationAction.Parse("2") },
            new RotationStep { Id = "m3", Segment = "main", Action = RotationAction.Parse("4"), Next = "m1", NewRound = true },
            new RotationStep { Id = "b1", Segment = "burst", Action = RotationAction.Parse("3大招") },
        ],
    };

    private static Rotation Linear(string line) => Example with
    {
        Segments = [],
        Steps = [.. RotationText.Parse(line).Select((e, i) => new RotationStep { Id = $"s{i}", Action = e.Action, Before = e.Before })],
    };

    private static string Now(RotationTracker tracker) => tracker.State.Current.Step.Action.ToString();

    private static CastInput Skill(int slot) => new(CastKind.Skill, slot, TimeSpan.Zero);

    private static CastInput Chain(int slot) => new(CastKind.Chain, slot, TimeSpan.Zero);

    private static CastInput Ultimate(int slot) => new(CastKind.Ultimate, slot, TimeSpan.Zero);

    private static void AdvanceTo(RotationTracker tracker, string id)
    {
        int target = tracker.Rotation.IndexOf(id);
        for (int guard = 0; tracker.State.Cursor.Step != target || (id.StartsWith('c') && tracker.State.Cursor.Round < 2); guard++)
        {
            Assert.True(guard < 100, $"never reached {id}");
            tracker.Skip();
        }
    }

    private RotationState See(RotationTracker tracker, HudSnapshot snapshot, TimeSpan? after = null) =>
        See(tracker, snapshot, [], after);

    private RotationState See(
        RotationTracker tracker,
        HudSnapshot snapshot,
        IReadOnlyList<CastInput> casts,
        TimeSpan? after = null)
    {
        clock += after ?? TimeSpan.FromMilliseconds(50);
        return tracker.Observe(snapshot, clock, casts);
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
