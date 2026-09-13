using System.Text.Json;
using Anaphora.Core;

namespace Anaphora.Core.Tests;

public class RotationActionTests
{
    [Theory]
    [InlineData("3", 3, ActionKind.Skill)]
    [InlineData("4E", 4, ActionKind.Chain)]
    [InlineData("4e", 4, ActionKind.Chain)]
    [InlineData("1重击", 1, ActionKind.Heavy)]
    [InlineData("1H", 1, ActionKind.Heavy)]
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
    [InlineData("1重")]
    public void RejectsAnythingElse(string text)
    {
        Assert.Throws<FormatException>(() => RotationAction.Parse(text));
    }

    [Theory]
    [InlineData("3")]
    [InlineData("4E")]
    [InlineData("1重击")]
    [InlineData("2大招")]
    public void FormatsBackToTheCanonicalNotation(string text)
    {
        Assert.Equal(text, RotationAction.Parse(text).ToString());
    }

    [Fact]
    public void AsciiAliasesFormatAsTheChineseForm()
    {
        Assert.Equal("1重击", RotationAction.Parse("1H").ToString());
        Assert.Equal("3大招", RotationAction.Parse("3U").ToString());
    }
}

public class StepConditionTests
{
    [Theory]
    [InlineData("sp>=1", ConditionKind.SkillPoints, 1)]
    [InlineData("chain:2", ConditionKind.ChainReady, 2)]
    [InlineData("ULT:3", ConditionKind.UltimateReady, 3)]
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

    [Fact]
    public void TheShippedExampleIsTheUsersRotation()
    {
        Assert.Equal(
            ["3", "2", "4E", "1重击", "1E", "1", "2E", "1"],
            Example.Opening.Select(s => s.Action.ToString()));
        Assert.Equal(Example.Opening.Select(s => s.Action), Example.Cycle.Select(s => s.Action));
        Assert.Equal(1, Example.ControlledSlot);
    }

    [Fact]
    public void RoundTwoHoldsTheHeavyAttackForASkillPoint()
    {
        RotationStep heavy = Example.Cycle.Single(s => s.Action.Kind == ActionKind.Heavy);

        Assert.Equal([new StepCondition(ConditionKind.SkillPoints, 1)], heavy.When);
        Assert.Equal("等 2 可放再重击", heavy.Note);
        Assert.Empty(Example.Opening.Single(s => s.Action.Kind == ActionKind.Heavy).When);
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        Assert.Equal(Example, RotationStore.Deserialize(RotationStore.Serialize(Example)));
    }

    [Fact]
    public void PlainStepsStayShorthandAndNotationStaysLegible()
    {
        string json = RotationStore.Serialize(Example);

        Assert.Contains("\"4E\"", json, StringComparison.Ordinal);
        Assert.Contains("\"do\": \"1重击\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ABadStepNamesItselfInTheError()
    {
        const string Json = """{ "id": "x", "displayName": "x", "profileId": "endfield", "opening": ["3", "9E"] }""";

        JsonException ex = Assert.Throws<JsonException>(() => RotationStore.Deserialize(Json));
        Assert.Contains("9E", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExampleValidatesAgainstTheEndfieldProfile()
    {
        GameProfile profile = ProfileStore.Load(Path.Combine(AppContext.BaseDirectory, "profiles", "endfield.json"));
        IReadOnlyList<ProfileProblem> problems = RotationValidator.Validate(Example, profile);

        Assert.False(ProfileValidator.HasErrors(problems), string.Join(Environment.NewLine, problems));
    }
}

public class RotationValidatorTests
{
    private static Rotation Make(params RotationStep[] cycle) => new()
    {
        Id = "t",
        DisplayName = "t",
        ProfileId = "endfield",
        ControlledSlot = 1,
        Cycle = cycle,
    };

    [Fact]
    public void AnEmptyRotationIsAnError()
    {
        Assert.True(ProfileValidator.HasErrors(RotationValidator.Validate(Make())));
    }

    [Fact]
    public void AnAllHeavyCycleCanNeverAdvance()
    {
        Assert.True(ProfileValidator.HasErrors(RotationValidator.Validate(Make("1重击"))));
    }

    [Fact]
    public void AHeavyAttackFromAnotherSlotIsFlagged()
    {
        IReadOnlyList<ProfileProblem> problems = RotationValidator.Validate(Make("3", "2重击"));

        Assert.False(ProfileValidator.HasErrors(problems));
        Assert.Contains(problems, p => p.Message.Contains("slot 1 is the one in control", StringComparison.Ordinal));
    }

    [Fact]
    public void AConditionWithoutANoteIsFlagged()
    {
        IReadOnlyList<ProfileProblem> problems = RotationValidator.Validate(Make(
            "3",
            new RotationStep { Action = RotationAction.Parse("1重击"), When = [StepCondition.Parse("sp>=1")] }));

        Assert.Contains(problems, p => p.Message.Contains("no note", StringComparison.Ordinal));
    }

    [Fact]
    public void AMismatchedProfileIsAnError()
    {
        GameProfile other = new() { Id = "other", DisplayName = "o", Window = new WindowMatch { ProcessName = "o" } };

        Assert.True(ProfileValidator.HasErrors(RotationValidator.Validate(Make("3"), other)));
    }
}
