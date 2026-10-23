using Anaphora.Analysis;
using Anaphora.Core;
using Anaphora.Overlay;

namespace Anaphora.App.Tests;

/// <summary>
/// What the title card says, and what the rotation strip draws. Both are the
/// overlay's own mapping from a reading to something to press, so they are worth
/// pinning even though nothing here draws a pixel.
/// </summary>
public class OverlayViewModelTests
{
    [Fact]
    public void ASkillCardIsTheDigitAndNothingElse()
    {
        // Pressing 3 is the whole story; "slot 3's skill" only repeats it.
        PrimaryAction card = Card(new RotationAction(3, ActionKind.Skill));

        Assert.Equal("3", card.Glyph);
        Assert.Equal(string.Empty, card.Second);
        Assert.False(card.HasSecond);
        Assert.False(card.Blocked);
    }

    [Fact]
    public void AChainCardSaysWhoItHasToFire()
    {
        // 4E is pressed with E, but E fires whoever leads the game's prompt.
        PrimaryAction card = Card(new RotationAction(4, ActionKind.Chain));

        Assert.Equal("E", card.Glyph);
        Assert.Equal("触发 4", card.Second);
    }

    [Fact]
    public void AnUltimateCardSaysToHold()
    {
        // Same key as the skill; only the hold tells them apart.
        PrimaryAction card = Card(new RotationAction(2, ActionKind.Ultimate));

        Assert.Equal("2", card.Glyph);
        Assert.Equal("长按", card.Second);
    }

    [Fact]
    public void BlockedCardSaysWhatItWaitsFor()
    {
        var step = new RotationStep { Id = "a", Action = new RotationAction(1, ActionKind.Chain) };
        var view = new StepView(step, [new ConditionStatus(new StepCondition(ConditionKind.SkillPoints, 1), Met: false)]);

        PrimaryAction card = PrimaryAction.From(view, $"等 {view.Conditions[0].Condition.Description}");

        Assert.True(card.Blocked);
        Assert.Equal("E", card.Glyph);
        Assert.Equal("等 技能点 ≥ 1", card.Second);
    }

    [Fact]
    public void CuesLeadIntoTheCardInsteadOfBecomingOne()
    {
        var step = new RotationStep
        {
            Id = "a",
            Action = new RotationAction(1, ActionKind.Chain),
            Before = [SoftCue.Heavy(1)],
        };
        var model = new OverlayViewModel();

        model.Apply(Readable(), State([step], at: 1), TimeSpan.Zero);

        Assert.Equal("E", model.NowAction.Glyph);
        StepChip cue = Assert.Single(model.NowCues);
        Assert.True(cue.IsCue);
        Assert.Equal("重击", cue.Main);
        Assert.True(model.HasCues);
    }

    [Fact]
    public void TheStripIsTheWholeSegmentWithTheCursorOnOneStep()
    {
        var model = new OverlayViewModel();
        RotationStep[] steps =
        [
            Step("a", new RotationAction(3, ActionKind.Skill)),
            Step("b", new RotationAction(4, ActionKind.Chain)),
            Step("c", new RotationAction(2, ActionKind.Ultimate)),
        ];

        model.Apply(Readable(), State(steps, at: 2), TimeSpan.Zero);

        Assert.Equal(3, model.Track.Count);
        Assert.Equal([true, false, false], model.Track.Select(c => c.IsDone));
        Assert.Equal([false, true, false], model.Track.Select(c => c.IsNow));
    }

    [Fact]
    public void AStripCellCarriesOnlyWhatTheKeyDoesNotSay()
    {
        var model = new OverlayViewModel();
        RotationStep[] steps =
        [
            Step("a", new RotationAction(3, ActionKind.Skill)),
            Step("b", new RotationAction(4, ActionKind.Chain)),
            Step("c", new RotationAction(2, ActionKind.Ultimate)),
        ];

        model.Apply(Readable(), State(steps, at: 1), TimeSpan.Zero);

        Assert.Equal(("3", string.Empty, false), Cell(model.Track[0]));
        Assert.Equal(("E", "4", false), Cell(model.Track[1]));
        Assert.Equal(("2", "长按", true), Cell(model.Track[2]));
    }

    [Fact]
    public void ACellCarriesTheCuesThatLeadIntoIt()
    {
        var model = new OverlayViewModel();
        RotationStep[] steps =
        [
            Step("a", new RotationAction(1, ActionKind.Chain)) with { Before = [SoftCue.Heavy(1)] },
        ];

        model.Apply(Readable(), State(steps, at: 1), TimeSpan.Zero);

        TrackCell cell = Assert.Single(model.Track);
        Assert.True(cell.HasCues);
        Assert.Equal("重击", Assert.Single(cell.Cues).Main);
    }

    [Fact]
    public void AnAbsentHudStillDrawsTheRotation()
    {
        var model = new OverlayViewModel();
        RotationStep[] steps = [Step("a", new RotationAction(3, ActionKind.Skill))];

        model.Apply(HudSnapshot.Absent, State(steps, at: 1), TimeSpan.FromSeconds(1.4));

        Assert.False(model.Live);
        Assert.Single(model.Track);
        Assert.Contains("1.4", model.StaleText, StringComparison.Ordinal);
    }

    private static (string Glyph, string Badge, bool IsHold) Cell(TrackCell cell) =>
        (cell.Glyph, cell.Badge, cell.IsHold);

    private static RotationStep Step(string id, RotationAction action) => new() { Id = id, Action = action };

    private static PrimaryAction Card(RotationAction action) =>
        PrimaryAction.From(new StepView(new RotationStep { Id = "a", Action = action }, []), string.Empty);

    private static HudSnapshot Readable() => new()
    {
        HudPresent = true,
        SkillPoints = 2,
        Chains = [new FillBarReading(0.4, false), new FillBarReading(1, true), new FillBarReading(0, false), new FillBarReading(0.1, false)],
        Ultimates = [new DiscStateReading(false, 0.3, 0), new DiscStateReading(true, 1, 0.88), new DiscStateReading(false, 0, 0), new DiscStateReading(false, 0, 0)],
    };

    /// <param name="at">1-based position of the cursor in the segment.</param>
    private static RotationState State(IReadOnlyList<RotationStep> segment, int at)
    {
        StepView[] views = [.. segment.Select(s => new StepView(s, []))];
        return new RotationState
        {
            Cursor = new RotationCursor(at - 1, 1),
            Current = views[at - 1],
            Segment = views,
            SegmentName = "循环",
            Position = at,
            SegmentLength = views.Length,
        };
    }
}
