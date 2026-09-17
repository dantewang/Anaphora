using Anaphora.App.Editor;
using Anaphora.Core;

namespace Anaphora.App.Tests;

public class RotationDocumentTests
{
    private static readonly Rotation Example = Load("endfield-example.json");
    private static readonly Rotation Demo = Load("demo-branch.json");

    [Theory]
    [InlineData("endfield-example.json")]
    [InlineData("demo-branch.json")]
    public void EditingNothingChangesNothing(string file)
    {
        Rotation rotation = Load(file);

        Assert.Equal(rotation, RotationDocument.From(rotation).ToRotation());
    }

    [Fact]
    public void RetypingASegmentKeepsWhatWasSetUpAroundIt()
    {
        var document = RotationDocument.From(Example);
        SegmentModel cycle = document.Segments.Single(s => s.Id == "cycle");

        document.ReplaceSegment(cycle, RotationText.Parse("3 → 2 → 4E → ~1重击 1E → 1 → 2E → 1"));

        Assert.Equal(Example, document.ToRotation());
    }

    [Fact]
    public void ALongerSegmentRunsOnAndTheNewLastStepLoops()
    {
        var document = RotationDocument.From(Example);
        SegmentModel cycle = document.Segments.Single(s => s.Id == "cycle");

        document.ReplaceSegment(cycle, RotationText.Parse("3 2 4E ~1重击 1E 1 2E 1 3大招"));
        Rotation rotation = document.ToRotation();

        List<RotationStep> steps = [.. rotation.Steps.Where(s => s.Segment == "cycle")];
        Assert.Equal(8, steps.Count);
        Assert.Null(steps[6].Next);
        Assert.Equal(("c1", true), (steps[7].Next, steps[7].NewRound));
        Assert.Equal([StepCondition.Parse("sp>=1")], steps[3].When);
        Assert.False(ProfileValidator.HasErrors(RotationValidator.Validate(rotation)));
    }

    [Fact]
    public void AShorterSegmentRedirectsWhatPointedAtTheDroppedSteps()
    {
        var document = RotationDocument.From(Demo);
        SegmentModel main = document.Segments.Single(s => s.Id == "main");

        document.ReplaceSegment(main, RotationText.Parse("3 ~3普攻2 2E"));
        Rotation rotation = document.ToRotation();

        // The branch lane went to m4, which is gone: it now goes to the first step.
        Assert.Equal("m1", rotation.Steps[rotation.IndexOf("b1")].Next);
        Assert.Equal(("m1", true), (rotation.Steps[rotation.IndexOf("m2")].Next, rotation.Steps[rotation.IndexOf("m2")].NewRound));
        Assert.False(ProfileValidator.HasErrors(RotationValidator.Validate(rotation)));
    }

    [Fact]
    public void UndoAndRedoWalkThroughEdits()
    {
        var document = RotationDocument.From(Example);
        SegmentModel cycle = document.Segments.Single(s => s.Id == "cycle");

        document.ReplaceSegment(cycle, RotationText.Parse("3"));
        Rotation shortened = document.ToRotation();
        document.DisplayName = "改过";

        Assert.True(document.CanUndo);
        document.Undo();
        Assert.Equal(shortened, document.ToRotation());
        document.Undo();
        Assert.Equal(Example, document.ToRotation());
        Assert.False(document.CanUndo);

        document.Redo();
        Assert.Equal(shortened, document.ToRotation());
    }

    [Fact]
    public void AFreshDocumentHasNothingToUndo()
    {
        Assert.False(RotationDocument.From(Example).CanUndo);
    }

    [Fact]
    public void InsertingTakesOverTheWayOut()
    {
        var document = RotationDocument.From(Example);
        StepModel last = document.Find("o7")!;

        StepModel inserted = document.InsertAfter(last);
        Rotation rotation = document.ToRotation();

        Assert.Null(rotation.Steps[rotation.IndexOf("o7")].Next);
        Assert.Equal(("c1", true), (rotation.Steps[rotation.IndexOf(inserted.Id)].Next, rotation.Steps[rotation.IndexOf(inserted.Id)].NewRound));
        Assert.Equal(rotation.IndexOf(inserted.Id), rotation.DefaultEdge(rotation.IndexOf("o7")).To);
    }

    [Fact]
    public void RemovingALoopingStepHandsTheLoopBack()
    {
        var document = RotationDocument.From(Example);

        document.Remove(document.Find("o7")!);
        Rotation rotation = document.ToRotation();

        Assert.Equal(("c1", true), (rotation.Steps[rotation.IndexOf("o6")].Next, rotation.Steps[rotation.IndexOf("o6")].NewRound));
    }

    [Fact]
    public void RemovingABranchTargetRetargetsTheBranch()
    {
        var document = RotationDocument.From(Demo);

        document.Remove(document.Find("b1")!);
        Rotation rotation = document.ToRotation();

        Assert.Equal("m4", rotation.Steps[rotation.IndexOf("m2")].Branches.Single().To);
        Assert.DoesNotContain(rotation.Segments, s => s.Id == "branch");
    }

    [Fact]
    public void ABranchLaneStartsAtTheStepItLeaves()
    {
        var document = RotationDocument.From(Example);
        StepModel from = document.Find("c3")!;

        StepModel added = document.AddSegment(from);
        Rotation rotation = document.ToRotation();

        Assert.Equal(added.Id, rotation.Steps[rotation.IndexOf("c3")].Branches.Single().To);
        Assert.Equal("c4", rotation.Steps[rotation.IndexOf(added.Id)].Next);
        Assert.False(ProfileValidator.HasErrors(RotationValidator.Validate(rotation)));
    }

    [Fact]
    public void RemovingACueFromItsListWorks()
    {
        var document = RotationDocument.From(Example);
        StepModel step = document.Find("c4")!;

        step.Cues[0].RemoveCommand.Execute(null);

        Assert.Empty(document.ToRotation().Steps[Example.IndexOf("c4")].Before);
    }

    [Fact]
    public void OptionsFollowTheStepsAndBranchesFindTheirTarget()
    {
        var document = RotationDocument.From(Demo);
        BranchModel branch = document.Find("m2")!.Branches.Single();

        Assert.Equal(Demo.Steps.Count, document.Options.Count);
        Assert.Equal("b1", branch.Target?.Id);

        document.Find("b1")!.Slot = 1;
        Assert.Contains(document.Options, o => o.Id == "b1" && o.Label.Contains("1E", StringComparison.Ordinal));
        Assert.Equal("b1", branch.Target?.Id);
    }

    private static Rotation Load(string file) =>
        RotationStore.Load(Path.Combine(AppContext.BaseDirectory, "rotations", file));
}

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+R", 0x52u)]
    [InlineData("shift + f9", 0x78u)]
    [InlineData("Ctrl+5", 0x35u)]
    public void ParsesCombinations(string text, uint key)
    {
        Assert.Equal(key, HotkeyGesture.Parse(text)?.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Enter")]
    [InlineData("Ctrl+R+T")]
    [InlineData("F25")]
    public void RejectsWhatItCannotRegister(string text)
    {
        Assert.Null(HotkeyGesture.Parse(text));
    }
}
