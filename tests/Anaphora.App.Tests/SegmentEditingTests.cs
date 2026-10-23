using Anaphora.App.Editor;
using Anaphora.Core;

namespace Anaphora.App.Tests;

/// <summary>
/// Carving the rotation into segments. A segment is what the overlay shows one
/// of at a time, so these are real editing decisions -- and every one of them
/// moves steps around, which is exactly what the "go to the next step along"
/// edge is defined in terms of. So the test every operation has to pass is the
/// same one: <see cref="Edges"/> before equals <see cref="Edges"/> after.
/// </summary>
public class SegmentEditingTests
{
    private static readonly Rotation Example = Load("endfield-example.json");

    [Fact]
    public void SplittingMovesTheStepAndTheOnesAfterIt()
    {
        var document = RotationDocument.From(Example);
        StepModel at = document.Steps.Single(s => s.Id == "o4");

        SegmentModel? fresh = document.SplitAt(at);

        Assert.NotNull(fresh);
        Rotation after = document.ToRotation();
        Assert.Equal(["o1", "o2", "o3"], Ids(after, "opening"));
        Assert.Equal(["o4", "o5", "o6", "o7"], Ids(after, fresh.Id));
        Assert.Equal(["opening", fresh.Id, "cycle"], after.Segments.Select(s => s.Id));
    }

    [Fact]
    public void SplittingLeavesEveryEdgeWhereItWas()
    {
        var document = RotationDocument.From(Example);
        Dictionary<string, (string, bool)> before = Edges(document.ToRotation());

        document.SplitAt(document.Steps.Single(s => s.Id == "o4"));

        Assert.Equal(before, Edges(document.ToRotation()));
    }

    [Fact]
    public void SplittingAtTheStartOfASegmentDoesNothing()
    {
        var document = RotationDocument.From(Example);

        Assert.Null(document.SplitAt(document.Steps.Single(s => s.Id == "c1")));
        Assert.Equal(Example, document.ToRotation());
    }

    [Fact]
    public void AStepMovedToAnotherSegmentJoinsItsEndAndKeepsItsEdges()
    {
        var document = RotationDocument.From(Example);
        Dictionary<string, (string, bool)> before = Edges(document.ToRotation());
        StepModel moving = document.Steps.Single(s => s.Id == "o7");

        Assert.True(document.MoveToSegment(moving, document.Segments.Single(s => s.Id == "cycle")));

        Rotation after = document.ToRotation();
        Assert.Equal(["o1", "o2", "o3", "o4", "o5", "o6"], Ids(after, "opening"));
        Assert.Equal(["c1", "c2", "c3", "c4", "c5", "c6", "c7", "o7"], Ids(after, "cycle"));
        Assert.Equal(before, Edges(after));
        AssertSegmentsAreContiguous(after);
    }

    [Fact]
    public void EmptyingASegmentByMovingItsLastStepOutDropsIt()
    {
        var document = RotationDocument.From(Example);
        SegmentModel cycle = document.Segments.Single(s => s.Id == "cycle");
        foreach (StepModel step in document.Steps.Where(s => s.SegmentId == "opening").ToList())
        {
            document.MoveToSegment(step, cycle);
        }

        Rotation after = document.ToRotation();
        Assert.Equal(["cycle"], after.Segments.Select(s => s.Id));
        Assert.Equal(14, after.Steps.Count);
    }

    [Fact]
    public void MovingASegmentTakesItsStepsAndKeepsEveryEdge()
    {
        var document = RotationDocument.From(Example);
        Dictionary<string, (string, bool)> before = Edges(document.ToRotation());

        Assert.True(document.MoveSegment(document.Segments.Single(s => s.Id == "cycle"), -1));

        Rotation after = document.ToRotation();
        Assert.Equal(["cycle", "opening"], after.Segments.Select(s => s.Id));
        Assert.Equal("c1", after.Steps[0].Id);
        Assert.Equal(before, Edges(after));
        AssertSegmentsAreContiguous(after);
    }

    [Fact]
    public void ASegmentWillNotMovePastTheEnds()
    {
        var document = RotationDocument.From(Example);

        Assert.False(document.MoveSegment(document.Segments.Single(s => s.Id == "opening"), -1));
        Assert.False(document.MoveSegment(document.Segments.Single(s => s.Id == "cycle"), 1));
        Assert.Equal(Example, document.ToRotation());
    }

    [Fact]
    public void DeletingASegmentTakesItsStepsAndRepointsWhatAimedAtThem()
    {
        var document = RotationDocument.From(Example);

        Assert.True(document.RemoveSegment(document.Segments.Single(s => s.Id == "opening")));

        Rotation after = document.ToRotation();
        Assert.Equal(["cycle"], after.Segments.Select(s => s.Id));
        Assert.Equal(["c1", "c2", "c3", "c4", "c5", "c6", "c7"], after.Steps.Select(s => s.Id));
        Assert.DoesNotContain(after.Steps, s => s.Next is { } next && after.Steps.All(t => t.Id != next));
    }

    [Fact]
    public void TheLastSegmentWillNotBeDeleted()
    {
        var document = RotationDocument.From(Example);
        document.RemoveSegment(document.Segments.Single(s => s.Id == "opening"));

        Assert.False(document.RemoveSegment(document.Segments.Single(s => s.Id == "cycle")));
        Assert.NotEmpty(document.Steps);
    }

    [Fact]
    public void SplittingIsOneUndoStep()
    {
        var document = RotationDocument.From(Example);

        document.SplitAt(document.Steps.Single(s => s.Id == "o4"));
        Assert.True(document.CanUndo);
        document.Undo();

        Assert.Equal(Example, document.ToRotation());
    }

    /// <summary>Every step's default way out, by id, as the tracker would follow it.</summary>
    private static Dictionary<string, (string To, bool NewRound)> Edges(Rotation rotation)
    {
        var edges = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        for (int i = 0; i < rotation.Steps.Count; i++)
        {
            RotationEdge edge = rotation.DefaultEdge(i);
            edges[rotation.Steps[i].Id] = (rotation.Steps[edge.To].Id, edge.NewRound);
        }

        return edges;
    }

    private static void AssertSegmentsAreContiguous(Rotation rotation)
    {
        List<string?> runs = [];
        foreach (RotationStep step in rotation.Steps)
        {
            if (runs.Count == 0 || runs[^1] != step.Segment)
            {
                runs.Add(step.Segment);
            }
        }

        Assert.Equal(runs.Distinct().Count(), runs.Count);
    }

    private static IEnumerable<string> Ids(Rotation rotation, string segment) =>
        rotation.Steps.Where(s => s.Segment == segment).Select(s => s.Id);

    private static Rotation Load(string file) =>
        RotationStore.Load(Path.Combine(AppContext.BaseDirectory, "rotations", file));
}
