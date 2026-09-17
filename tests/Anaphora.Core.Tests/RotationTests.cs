using System.Text.Json;
using Anaphora.Core;

namespace Anaphora.Core.Tests;

public class RotationActionTests
{
    [Theory]
    [InlineData("3", 3, ActionKind.Skill)]
    [InlineData("4E", 4, ActionKind.Chain)]
    [InlineData("4e", 4, ActionKind.Chain)]
    [InlineData("2大招", 2, ActionKind.Ultimate)]
    [InlineData("2u", 2, ActionKind.Ultimate)]
    [InlineData("  2E ", 2, ActionKind.Chain)]
    public void ParsesTheNotation(string text, int slot, ActionKind kind)
    {
        Assert.Equal(new RotationAction(slot, kind), RotationAction.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("5")]
    [InlineData("E")]
    [InlineData("1Q")]
    [InlineData("12")]
    // Cues are not steps: nothing on screen confirms them.
    [InlineData("1重击")]
    [InlineData("1H")]
    public void RejectsAnythingElse(string text)
    {
        Assert.Throws<FormatException>(() => RotationAction.Parse(text));
    }

    [Theory]
    [InlineData("3")]
    [InlineData("4E")]
    [InlineData("2大招")]
    public void FormatsBackToTheCanonicalNotation(string text)
    {
        Assert.Equal(text, RotationAction.Parse(text).ToString());
    }

    [Fact]
    public void SaysWhichKeyToPress()
    {
        Assert.Equal("按 3", RotationAction.Parse("3").KeyText);
        Assert.Equal("按 E", RotationAction.Parse("4E").KeyText);
        Assert.Equal("长按 2", RotationAction.Parse("2大招").KeyText);
    }
}

public class SoftCueTests
{
    [Theory]
    [InlineData("1重击", CueKind.Heavy, 1, 0)]
    [InlineData("1h", CueKind.Heavy, 1, 0)]
    [InlineData("3普攻2", CueKind.AttackAfterSkill, 3, 2)]
    [InlineData("3A2", CueKind.AttackAfterSkill, 3, 2)]
    public void ParsesTheNotation(string text, CueKind kind, int slot, int count)
    {
        Assert.Equal(new SoftCue(kind, slot, count), SoftCue.Parse(text));
    }

    [Theory]
    [InlineData("3")]
    [InlineData("3普攻")]
    [InlineData("3普攻0")]
    [InlineData("3A12")]
    [InlineData("5重击")]
    public void RejectsAnythingElse(string text)
    {
        Assert.False(SoftCue.TryParse(text, out _));
    }

    [Fact]
    public void DescribesItselfForPeople()
    {
        Assert.Equal("1 重击", SoftCue.Heavy(1).Description);
        Assert.Equal("3 技能后第 2 下普攻", SoftCue.AttackAfterSkill(3, 2).Description);
        Assert.Equal("3普攻2", SoftCue.AttackAfterSkill(3, 2).ToString());
    }
}

public class StepConditionTests
{
    [Theory]
    [InlineData("sp>=1", ConditionKind.SkillPoints, 1)]
    [InlineData("chain:2", ConditionKind.ChainReady, 2)]
    [InlineData("ULT:3", ConditionKind.UltimateReady, 3)]
    [InlineData("prompt:4", ConditionKind.PromptLeads, 4)]
    public void ParsesAndRoundTrips(string text, ConditionKind kind, int value)
    {
        StepCondition condition = StepCondition.Parse(text);

        Assert.Equal(new StepCondition(kind, value), condition);
        Assert.Equal(text.ToLowerInvariant(), condition.ToString());
    }

    [Theory]
    [InlineData("sp>1")]
    [InlineData("chain2")]
    [InlineData("hp>=1")]
    public void RejectsUnknownForms(string text)
    {
        Assert.Throws<FormatException>(() => StepCondition.Parse(text));
    }
}

public class RotationStoreTests
{
    private static readonly Rotation Example =
        RotationStore.Load(Path.Combine(AppContext.BaseDirectory, "rotations", "endfield-example.json"));

    /// <summary>The same rotation as it was written before steps became states.</summary>
    private const string LegacyExample = """
        {
          "schemaVersion": 1,
          "id": "endfield-example",
          "displayName": "示例队伍（captures/combat）",
          "profileId": "endfield",
          "controlledSlot": 1,
          "notes": "用户给出的示例轴。第二轮与第一轮按键相同，区别只在 1重击 之前要等技能点；这是根据用户描述的推断，第二轮如果还有别的差异需要补上。",
          "opening": ["3", "2", "4E", "1重击", "1E", "1", "2E", "1"],
          "cycle": ["3", "2", "4E", { "do": "1重击", "when": ["sp>=1"], "note": "等 2 可放再重击" }, "1E", "1", "2E", "1"]
        }
        """;

    [Fact]
    public void TheShippedExampleIsTheUsersRotation()
    {
        Assert.Equal(1, Example.ControlledSlot);
        Assert.Equal(["开局", "循环"], Example.Segments.Select(s => s.Name));
        Assert.Equal(
            "3 → 2 → 4E → ~1重击 1E → 1 → 2E → 1",
            RotationText.Format(Example.Steps.Where(s => s.Segment == "opening")));
        Assert.Equal(
            "3 → 2 → 4E → ~1重击 1E → 1 → 2E → 1",
            RotationText.Format(Example.Steps.Where(s => s.Segment == "cycle")));
    }

    [Fact]
    public void RoundTwoHoldsTheHeavyAttackForASkillPoint()
    {
        RotationStep opening = Example.Steps[Example.IndexOf("o4")];
        RotationStep cycle = Example.Steps[Example.IndexOf("c4")];

        Assert.Empty(opening.When);
        Assert.Equal([StepCondition.Parse("sp>=1")], cycle.When);
        Assert.Equal("等 2 可放再重击", cycle.Note);
    }

    [Fact]
    public void BothSegmentsLeadIntoTheCycleAsANewRound()
    {
        Assert.Equal(new RotationEdge(Example.IndexOf("c1"), true, []), Example.DefaultEdge(Example.IndexOf("o7")), EdgeComparer.Instance);
        Assert.Equal(new RotationEdge(Example.IndexOf("c1"), true, []), Example.DefaultEdge(Example.IndexOf("c7")), EdgeComparer.Instance);
        Assert.Equal(new RotationEdge(Example.IndexOf("o2"), false, []), Example.DefaultEdge(Example.IndexOf("o1")), EdgeComparer.Instance);
    }

    [Fact]
    public void TheLastStepOfASegmentWithoutNextStartsOver()
    {
        Rotation open = Example with { Steps = [.. Example.Steps.Select(s => s with { Next = null, NewRound = false })] };

        Assert.Equal(new RotationEdge(0, true, []), open.DefaultEdge(open.IndexOf("o7")), EdgeComparer.Instance);
    }

    [Fact]
    public void SchemaOneIsConvertedToTheSameRotation()
    {
        Assert.Equal(Example, RotationStore.Deserialize(LegacyExample));
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        Assert.Equal(Example, RotationStore.Deserialize(RotationStore.Serialize(Example)));
    }

    [Fact]
    public void WritesOnlyWhatIsThereAndKeepsTheNotationLegible()
    {
        string json = RotationStore.Serialize(Example);

        Assert.Contains("\"do\": \"4E\"", json, StringComparison.Ordinal);
        Assert.Contains("\"1重击\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"branches\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"newRound\": false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ACueWrittenAsAStepSaysWhereItBelongs()
    {
        const string Json = """{ "id": "x", "displayName": "x", "profileId": "endfield", "steps": [{ "id": "a", "do": "1重击" }] }""";

        JsonException ex = Assert.Throws<JsonException>(() => RotationStore.Deserialize(Json));
        Assert.Contains("before", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABadLegacyStepNamesItselfInTheError()
    {
        const string Json = """{ "id": "x", "displayName": "x", "profileId": "endfield", "opening": ["3", "9E"] }""";

        JsonException ex = Assert.Throws<JsonException>(() => RotationStore.Deserialize(Json));
        Assert.Contains("9E", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FreeTextCuesAndBranchesRoundTrip()
    {
        Rotation rotation = Example with
        {
            Steps =
            [
                new RotationStep { Id = "a", Action = RotationAction.Parse("3") },
                new RotationStep
                {
                    Id = "b",
                    Action = RotationAction.Parse("2E"),
                    Before = [SoftCue.AttackAfterSkill(3, 2), SoftCue.Say("拉开距离")],
                    Branches = [new RotationBranch { When = [StepCondition.Parse("prompt:4")], To = "c" }],
                },
                new RotationStep { Id = "c", Action = RotationAction.Parse("4E"), Next = "a", NewRound = true },
            ],
            Segments = [],
        };

        string json = RotationStore.Serialize(rotation);

        Assert.Contains("\"text\": \"拉开距离\"", json, StringComparison.Ordinal);
        Assert.Equal(rotation, RotationStore.Deserialize(json));
    }

    [Fact]
    public void TheExampleValidatesAgainstTheEndfieldProfile()
    {
        GameProfile profile = ProfileStore.Load(Path.Combine(AppContext.BaseDirectory, "profiles", "endfield.json"));
        IReadOnlyList<ProfileProblem> problems = RotationValidator.Validate(Example, profile);

        Assert.Empty(problems);
    }

    private sealed class EdgeComparer : IEqualityComparer<RotationEdge>
    {
        public static readonly EdgeComparer Instance = new();

        public bool Equals(RotationEdge x, RotationEdge y) => x.To == y.To && x.NewRound == y.NewRound && x.When.SequenceEqual(y.When);

        public int GetHashCode(RotationEdge obj) => obj.To;
    }
}

public class RotationTextTests
{
    [Fact]
    public void ParsesTheExampleLine()
    {
        IReadOnlyList<RotationText.Entry> entries = RotationText.Parse("3 → 2 → 4E → ~1重击 1E → 1 → 2E → 1");

        Assert.Equal(["3", "2", "4E", "1E", "1", "2E", "1"], entries.Select(e => e.Action.ToString()));
        Assert.Equal([SoftCue.Heavy(1)], entries[3].Before);
    }

    [Fact]
    public void AcceptsLooseSeparatorsAndFreeText()
    {
        IReadOnlyList<RotationText.Entry> entries = RotationText.Parse("3 -> ~3普攻2 ~\"拉开 距离\" 2E, 3大招");

        Assert.Equal(3, entries.Count);
        Assert.Equal([SoftCue.AttackAfterSkill(3, 2), SoftCue.Say("拉开 距离")], entries[1].Before);
        Assert.Equal(ActionKind.Ultimate, entries[2].Action.Kind);
    }

    [Fact]
    public void FormatsWhatItParses()
    {
        const string Line = "3 → ~3普攻2 ~\"拉开距离\" 2E → 3大招";
        IReadOnlyList<RotationText.Entry> entries = RotationText.Parse(Line);
        IEnumerable<RotationStep> steps = entries.Select((e, i) => new RotationStep { Id = $"s{i}", Action = e.Action, Before = e.Before });

        Assert.Equal(Line, RotationText.Format(steps));
    }

    [Theory]
    [InlineData("3 1重击 1E", "~1重击")]
    [InlineData("3 ~1重击", "后面要跟")]
    [InlineData("3 9E", "9E")]
    [InlineData("", "至少")]
    [InlineData("~\"abc", "引号")]
    public void ExplainsWhatIsWrong(string line, string hint)
    {
        FormatException ex = Assert.Throws<FormatException>(() => RotationText.Parse(line));
        Assert.Contains(hint, ex.Message, StringComparison.Ordinal);
    }
}

public class RotationValidatorTests
{
    private static Rotation Make(params RotationStep[] steps) => new()
    {
        Id = "t",
        DisplayName = "t",
        ProfileId = "endfield",
        ControlledSlot = 1,
        Steps = steps,
    };

    private static RotationStep Step(string id, string action) => new() { Id = id, Action = RotationAction.Parse(action) };

    [Fact]
    public void AnEmptyRotationIsAnError()
    {
        Assert.True(ProfileValidator.HasErrors(RotationValidator.Validate(Make())));
    }

    [Fact]
    public void DuplicateIdsAndMissingTargetsAreErrors()
    {
        Assert.True(ProfileValidator.HasErrors(RotationValidator.Validate(Make(Step("a", "3"), Step("a", "2")))));
        Assert.True(ProfileValidator.HasErrors(RotationValidator.Validate(Make(Step("a", "3") with { Next = "zz" }))));
        Assert.True(ProfileValidator.HasErrors(RotationValidator.Validate(Make(
            Step("a", "3") with { Branches = [new RotationBranch { When = [StepCondition.Parse("sp>=1")], To = "zz" }] }))));
    }

    [Fact]
    public void ACueFromAnotherSlotIsFlagged()
    {
        IReadOnlyList<ProfileProblem> problems = RotationValidator.Validate(Make(Step("a", "3") with { Before = [SoftCue.Heavy(2)] }));

        Assert.False(ProfileValidator.HasErrors(problems));
        Assert.Contains(problems, p => p.Message.Contains("slot 1 is the one in control", StringComparison.Ordinal));
    }

    [Fact]
    public void AStepNothingLeadsToIsFlagged()
    {
        IReadOnlyList<ProfileProblem> problems = RotationValidator.Validate(Make(
            Step("a", "3") with { Next = "a" },
            Step("b", "2")));

        Assert.Contains(problems, p => p.Message.Contains("'b' cannot be reached", StringComparison.Ordinal));
    }

    [Fact]
    public void AMismatchedProfileIsAnError()
    {
        GameProfile other = new() { Id = "other", DisplayName = "o", Window = new WindowMatch { ProcessName = "o" } };

        Assert.True(ProfileValidator.HasErrors(RotationValidator.Validate(Make(Step("a", "3")), other)));
    }
}
