using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anaphora.Core;

/// <summary>
/// What kind of input a rotation step asks for. Only inputs the game can
/// confirm -- from the HUD, or from the key press itself -- are steps; heavy
/// attacks and the like are <see cref="SoftCue"/>s.
/// </summary>
public enum ActionKind
{
    /// <summary>Slot N's skill: press N.</summary>
    Skill,

    /// <summary>Slot N's chain: press E while N's portrait leads the prompt.</summary>
    Chain,

    /// <summary>Slot N's ultimate: hold N.</summary>
    Ultimate,
}

/// <summary>
/// One input, addressed by slot rather than by character. Rotations are written
/// against slots 1-4 so the same rotation reads the same whichever operators fill
/// the team -- and so it matches the keys the player actually presses.
/// </summary>
[JsonConverter(typeof(RotationActionJsonConverter))]
public readonly record struct RotationAction(int Slot, ActionKind Kind)
{
    public const int MinSlot = 1;
    public const int MaxSlot = 4;

    public bool IsValid => Slot is >= MinSlot and <= MaxSlot && Enum.IsDefined(Kind);

    /// <summary>What the player presses: "按 3", "按 E", "长按 2".</summary>
    public string KeyText => Kind switch
    {
        ActionKind.Chain => "按 E",
        ActionKind.Ultimate => $"长按 {Slot}",
        _ => $"按 {Slot}",
    };

    /// <summary>
    /// "3", "4E", "2大招"; "2U" is accepted as an ASCII alias. Surrounding
    /// whitespace is ignored, case is not significant.
    /// </summary>
    public static RotationAction Parse(string text)
    {
        if (!TryParse(text, out RotationAction action))
        {
            throw new FormatException(
                $"'{text}' is not a rotation step. Write N, NE or N大招 with N in 1-4 (NU also works).");
        }

        return action;
    }

    public static bool TryParse(string? text, out RotationAction action)
    {
        action = default;
        if (!Notation.TrySplitSlot(text, out int slot, out ReadOnlySpan<char> suffix))
        {
            return false;
        }

        ActionKind? kind = suffix switch
        {
            [] => ActionKind.Skill,
            ['E' or 'e'] => ActionKind.Chain,
            ['U' or 'u'] => ActionKind.Ultimate,
            _ when suffix.SequenceEqual("大招") => ActionKind.Ultimate,
            _ => null,
        };

        if (kind is null)
        {
            return false;
        }

        action = new RotationAction(slot, kind.Value);
        return true;
    }

    /// <summary>The canonical notation: N, NE, N大招.</summary>
    public override string ToString() => Kind switch
    {
        ActionKind.Skill => Slot.ToString(CultureInfo.InvariantCulture),
        ActionKind.Chain => $"{Slot}E",
        ActionKind.Ultimate => $"{Slot}大招",
        _ => $"{Slot}?",
    };
}

public enum CueKind
{
    /// <summary>Slot N's heavy attack: the last hit of a basic-attack string. No key of its own.</summary>
    Heavy,

    /// <summary>
    /// The Nth basic attack after slot N's skill. Some operators' skills put them
    /// in a state where basic attacks change, and the rotation may continue after
    /// a particular one rather than the last.
    /// </summary>
    AttackAfterSkill,

    /// <summary>Free text: anything else worth saying before the next input.</summary>
    Text,
}

/// <summary>
/// Something the player should do before a step that the HUD cannot confirm. The
/// tracker never waits for a cue; the overlay shows it together with the step it
/// leads into.
///
/// Written "1重击" (or "1H"), "3普攻2" (or "3A2") for the second basic attack
/// after slot 3's skill, or <c>{ "text": "…" }</c>.
/// </summary>
[JsonConverter(typeof(SoftCueJsonConverter))]
public readonly record struct SoftCue(CueKind Kind, int Slot, int Count = 0, string? Text = null)
{
    public const int MaxCount = 9;

    public static SoftCue Heavy(int slot) => new(CueKind.Heavy, slot);

    public static SoftCue AttackAfterSkill(int slot, int count) => new(CueKind.AttackAfterSkill, slot, count);

    public static SoftCue Say(string text) => new(CueKind.Text, 0, 0, text);

    public bool IsValid => Kind switch
    {
        CueKind.Heavy => Slot is >= RotationAction.MinSlot and <= RotationAction.MaxSlot,
        CueKind.AttackAfterSkill => Slot is >= RotationAction.MinSlot and <= RotationAction.MaxSlot && Count is >= 1 and <= MaxCount,
        CueKind.Text => !string.IsNullOrWhiteSpace(Text),
        _ => false,
    };

    /// <summary>For people: "1 重击", "3 技能后第 2 下普攻".</summary>
    public string Description => Kind switch
    {
        CueKind.Heavy => $"{Slot} 重击",
        CueKind.AttackAfterSkill => $"{Slot} 技能后第 {Count} 下普攻",
        _ => Text ?? string.Empty,
    };

    public static SoftCue Parse(string text)
    {
        if (!TryParse(text, out SoftCue cue))
        {
            throw new FormatException($"'{text}' is not a cue. Write N重击 or N普攻K (NH and NAK also work).");
        }

        return cue;
    }

    /// <summary>The notation forms only; free text is not parsed from a bare string.</summary>
    public static bool TryParse(string? text, out SoftCue cue)
    {
        cue = default;
        if (!Notation.TrySplitSlot(text, out int slot, out ReadOnlySpan<char> suffix))
        {
            return false;
        }

        if (suffix is ['H' or 'h'] || suffix.SequenceEqual("重击"))
        {
            cue = Heavy(slot);
            return true;
        }

        ReadOnlySpan<char> count =
            suffix.StartsWith("普攻") ? suffix[2..]
            : suffix is ['A' or 'a', ..] ? suffix[1..]
            : [];

        if (count is [>= '1' and <= '9'])
        {
            cue = AttackAfterSkill(slot, count[0] - '0');
            return true;
        }

        return false;
    }

    /// <summary>The notation, or the text itself for a free-text cue.</summary>
    public override string ToString() => Kind switch
    {
        CueKind.Heavy => $"{Slot}重击",
        CueKind.AttackAfterSkill => $"{Slot}普攻{Count}",
        _ => Text ?? string.Empty,
    };
}

public enum ConditionKind
{
    /// <summary>At least N whole skill points banked.</summary>
    SkillPoints,

    /// <summary>Slot N's chain is off cooldown.</summary>
    ChainReady,

    /// <summary>Slot N's ultimate can be cast.</summary>
    UltimateReady,

    /// <summary>Slot N's portrait leads the chain prompt: E would trigger N.</summary>
    PromptLeads,
}

/// <summary>
/// Something that must hold before a step should be taken, or a branch followed.
/// Conditions only ever refer to what the HUD readers can see.
///
/// Written "sp>=1", "chain:2", "ult:3", "prompt:4".
/// </summary>
[JsonConverter(typeof(StepConditionJsonConverter))]
public readonly record struct StepCondition(ConditionKind Kind, int Value)
{
    private static readonly (string Prefix, ConditionKind Kind)[] Prefixes =
    [
        ("sp>=", ConditionKind.SkillPoints),
        ("chain:", ConditionKind.ChainReady),
        ("ult:", ConditionKind.UltimateReady),
        ("prompt:", ConditionKind.PromptLeads),
    ];

    public bool IsValid => Kind switch
    {
        ConditionKind.SkillPoints => Value is >= 0 and <= 3,
        ConditionKind.ChainReady or ConditionKind.UltimateReady or ConditionKind.PromptLeads =>
            Value is >= RotationAction.MinSlot and <= RotationAction.MaxSlot,
        _ => false,
    };

    /// <summary>For people: "技能点 ≥ 1", "2 连携就绪", "连携提示 4 在前".</summary>
    public string Description => Kind switch
    {
        ConditionKind.SkillPoints => $"技能点 ≥ {Value}",
        ConditionKind.ChainReady => $"{Value} 连携就绪",
        ConditionKind.UltimateReady => $"{Value} 大招就绪",
        ConditionKind.PromptLeads => $"连携提示 {Value} 在前",
        _ => ToString(),
    };

    public static StepCondition Parse(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan().Trim();

        foreach ((string prefix, ConditionKind kind) in Prefixes)
        {
            if (span.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(span[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                return new StepCondition(kind, value);
            }
        }

        throw new FormatException($"'{text}' is not a condition. Write sp>=N, chain:N, ult:N or prompt:N.");
    }

    public override string ToString() => Kind switch
    {
        ConditionKind.SkillPoints => $"sp>={Value}",
        ConditionKind.ChainReady => $"chain:{Value}",
        ConditionKind.UltimateReady => $"ult:{Value}",
        ConditionKind.PromptLeads => $"prompt:{Value}",
        _ => $"?{Value}",
    };
}

/// <summary>How a branch's conditions add up.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BranchMatch>))]
public enum BranchMatch
{
    /// <summary>Every condition has to hold. The default, and what a bare list means.</summary>
    All,

    /// <summary>Any one of them is enough.</summary>
    Any,
}

/// <summary>An alternative way out of a step, suggested while its conditions hold.</summary>
public sealed record RotationBranch
{
    public IReadOnlyList<StepCondition> When { get; init; } = [];

    /// <summary>
    /// Whether <see cref="When"/> is an and or an or. "All" is the default, so
    /// a branch written as a plain list of conditions means what it looks like.
    /// There is no nesting: one branch is one and-list or one or-list, and
    /// anything more complicated is two branches to the same step.
    /// </summary>
    public BranchMatch Match { get; init; }

    /// <summary>The <see cref="RotationStep.Id"/> to go to.</summary>
    public required string To { get; init; }

    /// <summary>True when the conditions are met -- vacuously so when there are none.</summary>
    public bool Holds(Func<StepCondition, bool> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        return When.Count == 0 || (Match == BranchMatch.Any ? When.Any(test) : When.All(test));
    }

    public bool Equals(RotationBranch? other) =>
        other is not null && To == other.To && Match == other.Match && When.SequenceEqual(other.When);

    public override int GetHashCode() => HashCode.Combine(To, Match, When.Count);
}

/// <summary>
/// A named run of steps, drawn as one lane: "开局", "循环".
///
/// <see cref="Branches"/> are the segment's own ways out: unlike a step's branch,
/// which is only looked at when that step finishes, these are looked at after
/// every step in the segment. That is what "as soon as this holds, leave this
/// segment" needs -- waiting for the segment's last step would mean finishing
/// a whole round of it first.
/// </summary>
public sealed record RotationSegment(string Id, string Name)
{
    public IReadOnlyList<RotationBranch> Branches { get; init; } = [];

    public bool Equals(RotationSegment? other) =>
        other is not null && Id == other.Id && Name == other.Name && Branches.SequenceEqual(other.Branches);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Branches.Count);
}

/// <summary>
/// A state in the rotation: one input the HUD can confirm, the soft cues that
/// lead into it, what must hold before it, and where to go next.
///
/// Where to go: <see cref="Next"/> when given; otherwise the following step if it
/// is in the same segment; otherwise back to the rotation's first step as a new
/// round. <see cref="Branches"/> add alternatives. Which way the overlay suggests
/// comes from the branch conditions; which way the tracker goes comes from which
/// successor's input it actually sees.
/// </summary>
public sealed record RotationStep
{
    public required string Id { get; init; }

    /// <summary>The <see cref="RotationSegment.Id"/> this step is drawn in.</summary>
    public string? Segment { get; init; }

    [JsonPropertyName("do")]
    public required RotationAction Action { get; init; }

    /// <summary>Soft cues, in order, shown ahead of the action.</summary>
    public IReadOnlyList<SoftCue> Before { get; init; } = [];

    /// <summary>Conditions for doing this step at all -- including its cues.</summary>
    public IReadOnlyList<StepCondition> When { get; init; } = [];

    public string? Next { get; init; }

    /// <summary>Following <see cref="Next"/> starts a new round.</summary>
    public bool NewRound { get; init; }

    public IReadOnlyList<RotationBranch> Branches { get; init; } = [];

    /// <summary>Shown to the player with the step. Say what to watch for, not why.</summary>
    public string? Note { get; init; }

    public bool Equals(RotationStep? other) =>
        other is not null
        && Id == other.Id
        && Segment == other.Segment
        && Action == other.Action
        && Next == other.Next
        && NewRound == other.NewRound
        && Note == other.Note
        && Before.SequenceEqual(other.Before)
        && When.SequenceEqual(other.When)
        && Branches.SequenceEqual(other.Branches);

    public override int GetHashCode() => HashCode.Combine(Id, Action, Next);

    public override string ToString()
    {
        string cues = Before.Count == 0 ? string.Empty : $"~{string.Join(" ~", Before)} ";
        return When.Count == 0 ? $"{cues}{Action}" : $"{cues}{Action} [{string.Join(", ", When)}]";
    }
}

/// <summary>A way out of a step, resolved to an index into <see cref="Rotation.Steps"/>.</summary>
public readonly record struct RotationEdge(
    int To,
    bool NewRound,
    IReadOnlyList<StepCondition> When,
    BranchMatch Match = BranchMatch.All)
{
    public bool IsDefault => When.Count == 0;

    /// <summary>True when the conditions are met -- vacuously so for the default way.</summary>
    public bool Holds(Func<StepCondition, bool> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        return When.Count == 0 || (Match == BranchMatch.Any ? When.Any(test) : When.All(test));
    }
}

/// <summary>
/// A team's rotation, as a state machine over steps. The first step is where a
/// fight starts. The example team plays an opening once and then a cycle that
/// differs only in one condition; that is two segments and a "new round" edge.
/// </summary>
public sealed record Rotation
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>The <see cref="GameProfile.Id"/> whose readings this rotation is tracked against.</summary>
    public required string ProfileId { get; init; }

    /// <summary>The slot the player controls. Heavy attacks come from this slot.</summary>
    public int ControlledSlot { get; init; } = 1;

    public string? Notes { get; init; }

    public IReadOnlyList<RotationSegment> Segments { get; init; } = [];

    public IReadOnlyList<RotationStep> Steps { get; init; } = [];

    public int IndexOf(string id)
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Where step <paramref name="index"/> goes when no branch is taken.</summary>
    public RotationEdge DefaultEdge(int index)
    {
        RotationStep step = Steps[index];
        if (step.Next is not null)
        {
            int target = IndexOf(step.Next);
            if (target >= 0)
            {
                return new RotationEdge(target, step.NewRound, []);
            }
        }

        return index + 1 < Steps.Count && Steps[index + 1].Segment == step.Segment
            ? new RotationEdge(index + 1, false, [])
            : new RotationEdge(0, true, []);
    }

    /// <summary>
    /// Every way out of step <paramref name="index"/>: its own branches first,
    /// then the ways out of the segment it is in, then the default. The step's
    /// own branches win because they are the more specific statement.
    /// </summary>
    public IEnumerable<RotationEdge> Edges(int index)
    {
        foreach (RotationBranch branch in Steps[index].Branches)
        {
            if (IndexOf(branch.To) is >= 0 and int target)
            {
                yield return new RotationEdge(target, false, branch.When, branch.Match);
            }
        }

        foreach (RotationBranch branch in SegmentBranches(index))
        {
            // A segment's way out that leads back into the same segment would
            // be a loop with no end; the validator says so, and it is skipped.
            if (IndexOf(branch.To) is >= 0 and int target && Steps[target].Segment != Steps[index].Segment)
            {
                yield return new RotationEdge(target, false, branch.When, branch.Match);
            }
        }

        yield return DefaultEdge(index);
    }

    /// <summary>The ways out declared on the segment step <paramref name="index"/> is in.</summary>
    public IReadOnlyList<RotationBranch> SegmentBranches(int index)
    {
        string? id = Steps[index].Segment;
        return id is null ? [] : Segments.FirstOrDefault(s => s.Id == id)?.Branches ?? [];
    }

    public string SegmentName(int index)
    {
        string? id = Steps[index].Segment;
        return Segments.FirstOrDefault(s => s.Id == id)?.Name ?? string.Empty;
    }

    /// <summary>The steps drawn in the same lane as step <paramref name="index"/>, in order.</summary>
    public IReadOnlyList<int> SegmentSteps(int index)
    {
        string? id = Steps[index].Segment;
        return [.. Enumerable.Range(0, Steps.Count).Where(i => Steps[i].Segment == id)];
    }

    public bool Equals(Rotation? other) =>
        other is not null
        && SchemaVersion == other.SchemaVersion
        && Id == other.Id
        && DisplayName == other.DisplayName
        && ProfileId == other.ProfileId
        && ControlledSlot == other.ControlledSlot
        && Notes == other.Notes
        && Segments.SequenceEqual(other.Segments)
        && Steps.SequenceEqual(other.Steps);

    public override int GetHashCode() => HashCode.Combine(Id, Steps.Count);
}

internal static class Notation
{
    /// <summary>Splits "3普攻2" into 3 and "普攻2". The slot is a single digit 1-4.</summary>
    public static bool TrySplitSlot(string? text, out int slot, out ReadOnlySpan<char> suffix)
    {
        slot = 0;
        suffix = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.Length == 0 || span[0] is < '1' or > '4')
        {
            return false;
        }

        slot = span[0] - '0';
        suffix = span[1..].Trim();
        return true;
    }
}

internal sealed class RotationActionJsonConverter : JsonConverter<RotationAction>
{
    public override RotationAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string text = reader.GetString() ?? string.Empty;
        return RotationAction.TryParse(text, out RotationAction action)
            ? action
            : throw new JsonException(
                SoftCue.TryParse(text, out _)
                    ? $"'{text}' cannot be seen on screen, so it is not a step; put it in \"before\" of the step it leads into."
                    : $"'{text}' is not a rotation step. Write N, NE or N大招 with N in 1-4.");
    }

    public override void Write(Utf8JsonWriter writer, RotationAction value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

internal sealed class SoftCueJsonConverter : JsonConverter<SoftCue>
{
    public override SoftCue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string text = reader.GetString() ?? string.Empty;
            return SoftCue.TryParse(text, out SoftCue cue)
                ? cue
                : throw new JsonException($"'{text}' is not a cue. Write N重击 or N普攻K, or {{ \"text\": \"…\" }} for anything else.");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("a cue is a string like \"1重击\" or an object with \"text\".");
        }

        string? said = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string property = reader.GetString() ?? string.Empty;
            reader.Read();
            if (property.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                said = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        return SoftCue.Say(said ?? throw new JsonException("a cue object needs \"text\"."));
    }

    public override void Write(Utf8JsonWriter writer, SoftCue value, JsonSerializerOptions options)
    {
        if (value.Kind != CueKind.Text)
        {
            writer.WriteStringValue(value.ToString());
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("text", value.Text);
        writer.WriteEndObject();
    }
}

internal sealed class StepConditionJsonConverter : JsonConverter<StepCondition>
{
    public override StepCondition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return StepCondition.Parse(reader.GetString() ?? string.Empty);
        }
        catch (FormatException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, StepCondition value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
