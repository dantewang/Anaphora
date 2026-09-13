using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anaphora.Core;

/// <summary>What kind of input a rotation step asks for.</summary>
public enum ActionKind
{
    /// <summary>Slot N's skill: press N.</summary>
    Skill,

    /// <summary>Slot N's chain: press E while N's portrait leads the prompt.</summary>
    Chain,

    /// <summary>Slot N's heavy attack: the last hit of a basic-attack string. No key of its own.</summary>
    Heavy,

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

    /// <summary>
    /// "3", "4E", "1重击", "2大招"; "1H" and "2U" are accepted as ASCII aliases.
    /// Surrounding whitespace is ignored, case is not significant.
    /// </summary>
    public static RotationAction Parse(string text)
    {
        if (!TryParse(text, out RotationAction action))
        {
            throw new FormatException(
                $"'{text}' is not a rotation step. Write N, NE, N重击 or N大招 with N in 1-4 (NH and NU also work).");
        }

        return action;
    }

    public static bool TryParse(string? text, out RotationAction action)
    {
        action = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.Length == 0 || span[0] is < '1' or > '4')
        {
            return false;
        }

        int slot = span[0] - '0';
        ReadOnlySpan<char> suffix = span[1..].Trim();

        ActionKind? kind = suffix switch
        {
            [] => ActionKind.Skill,
            ['E' or 'e'] => ActionKind.Chain,
            ['H' or 'h'] => ActionKind.Heavy,
            ['U' or 'u'] => ActionKind.Ultimate,
            _ when suffix.SequenceEqual("重击") => ActionKind.Heavy,
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

    /// <summary>The canonical notation: N, NE, N重击, N大招.</summary>
    public override string ToString() => Kind switch
    {
        ActionKind.Skill => Slot.ToString(CultureInfo.InvariantCulture),
        ActionKind.Chain => $"{Slot}E",
        ActionKind.Heavy => $"{Slot}重击",
        ActionKind.Ultimate => $"{Slot}大招",
        _ => $"{Slot}?",
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
}

/// <summary>
/// Something that must hold before a step should be taken. Conditions only ever
/// refer to what the HUD readers can see.
///
/// Written "sp>=1", "chain:2", "ult:3".
/// </summary>
[JsonConverter(typeof(StepConditionJsonConverter))]
public readonly record struct StepCondition(ConditionKind Kind, int Value)
{
    public bool IsValid => Kind switch
    {
        ConditionKind.SkillPoints => Value is >= 0 and <= 3,
        ConditionKind.ChainReady or ConditionKind.UltimateReady => Value is >= RotationAction.MinSlot and <= RotationAction.MaxSlot,
        _ => false,
    };

    public static StepCondition Parse(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan().Trim();

        if (span.StartsWith("sp>=", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(span[4..], NumberStyles.None, CultureInfo.InvariantCulture, out int points))
        {
            return new StepCondition(ConditionKind.SkillPoints, points);
        }

        if (span.StartsWith("chain:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(span[6..], NumberStyles.None, CultureInfo.InvariantCulture, out int chainSlot))
        {
            return new StepCondition(ConditionKind.ChainReady, chainSlot);
        }

        if (span.StartsWith("ult:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(span[4..], NumberStyles.None, CultureInfo.InvariantCulture, out int ultimateSlot))
        {
            return new StepCondition(ConditionKind.UltimateReady, ultimateSlot);
        }

        throw new FormatException($"'{text}' is not a condition. Write sp>=N, chain:N or ult:N.");
    }

    public override string ToString() => Kind switch
    {
        ConditionKind.SkillPoints => $"sp>={Value}",
        ConditionKind.ChainReady => $"chain:{Value}",
        ConditionKind.UltimateReady => $"ult:{Value}",
        _ => $"?{Value}",
    };
}

/// <summary>
/// A step and what gates it. In JSON a step with no conditions is just its
/// notation, "4E"; one with conditions is an object:
/// <c>{ "do": "1重击", "when": ["sp>=1"], "note": "等 2 可放再重击" }</c>.
/// </summary>
[JsonConverter(typeof(RotationStepJsonConverter))]
public sealed record RotationStep
{
    public required RotationAction Action { get; init; }

    public IReadOnlyList<StepCondition> When { get; init; } = [];

    /// <summary>Shown to the player while a condition is unmet. Say what to wait for, not why.</summary>
    public string? Note { get; init; }

    public static implicit operator RotationStep(string notation) => new() { Action = RotationAction.Parse(notation) };

    public bool Equals(RotationStep? other) =>
        other is not null && Action == other.Action && Note == other.Note && When.SequenceEqual(other.When);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Action);
        hash.Add(Note);
        foreach (StepCondition condition in When)
        {
            hash.Add(condition);
        }

        return hash.ToHashCode();
    }

    public override string ToString() =>
        When.Count == 0 ? Action.ToString() : $"{Action} [{string.Join(", ", When)}]";
}

/// <summary>
/// A team's rotation: an opening played once at the start of a fight, then a
/// cycle that repeats until the fight ends. Rounds can differ only in their
/// conditions, which is exactly what the cycle is for -- the example team plays
/// the same eight inputs every round, but from round two its heavy attack has to
/// wait for a skill point.
/// </summary>
public sealed record Rotation
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>The <see cref="GameProfile.Id"/> whose readings this rotation is tracked against.</summary>
    public required string ProfileId { get; init; }

    /// <summary>The slot the player controls. Heavy attacks come from this slot.</summary>
    public int ControlledSlot { get; init; } = 1;

    public string? Notes { get; init; }

    public IReadOnlyList<RotationStep> Opening { get; init; } = [];

    public IReadOnlyList<RotationStep> Cycle { get; init; } = [];

    public bool Equals(Rotation? other) =>
        other is not null &&
        SchemaVersion == other.SchemaVersion &&
        Id == other.Id &&
        DisplayName == other.DisplayName &&
        ProfileId == other.ProfileId &&
        ControlledSlot == other.ControlledSlot &&
        Notes == other.Notes &&
        Opening.SequenceEqual(other.Opening) &&
        Cycle.SequenceEqual(other.Cycle);

    public override int GetHashCode() => HashCode.Combine(Id, ProfileId, Opening.Count, Cycle.Count);
}

internal sealed class RotationActionJsonConverter : JsonConverter<RotationAction>
{
    public override RotationAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return RotationAction.Parse(reader.GetString() ?? string.Empty);
        }
        catch (FormatException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, RotationAction value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
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

internal sealed class RotationStepJsonConverter : JsonConverter<RotationStep>
{
    public override RotationStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            try
            {
                return new RotationStep { Action = RotationAction.Parse(reader.GetString()!) };
            }
            catch (FormatException ex)
            {
                throw new JsonException(ex.Message, ex);
            }
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("a rotation step is either a string like \"4E\" or an object with \"do\".");
        }

        RotationAction? action = null;
        var when = new List<StepCondition>();
        string? note = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string property = reader.GetString() ?? string.Empty;
            reader.Read();

            switch (property.ToLowerInvariant())
            {
                case "do":
                    action = JsonSerializer.Deserialize<RotationAction>(ref reader, options);
                    break;
                case "when":
                    when.AddRange(JsonSerializer.Deserialize<List<StepCondition>>(ref reader, options) ?? []);
                    break;
                case "note":
                    note = reader.GetString();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new RotationStep
        {
            Action = action ?? throw new JsonException("a rotation step object needs \"do\"."),
            When = when,
            Note = note,
        };
    }

    public override void Write(Utf8JsonWriter writer, RotationStep value, JsonSerializerOptions options)
    {
        if (value.When.Count == 0 && value.Note is null)
        {
            writer.WriteStringValue(value.Action.ToString());
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("do", value.Action.ToString());
        if (value.When.Count > 0)
        {
            writer.WriteStartArray("when");
            foreach (StepCondition condition in value.When)
            {
                writer.WriteStringValue(condition.ToString());
            }

            writer.WriteEndArray();
        }

        if (value.Note is not null)
        {
            writer.WriteString("note", value.Note);
        }

        writer.WriteEndObject();
    }
}
