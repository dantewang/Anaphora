using System.Collections;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Anaphora.Core;

/// <summary>Reads and writes rotations as JSON.</summary>
public static class RotationStore
{
    /// <summary>
    /// Relaxed escaping on purpose: rotations are written by hand and read by
    /// people, and "1重击" is legible where "1重击" is not. Empty lists,
    /// nulls and false flags are left out so a plain step stays one line's worth.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SkipEmptyLists } },
    };

    public static string Serialize(Rotation rotation)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        return JsonSerializer.Serialize(rotation, Options);
    }

    /// <summary>Reads the current schema, and converts schema 1 (opening + cycle lists).</summary>
    public static Rotation Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonNode? root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        if (root is JsonObject legacy && (legacy.ContainsKey("opening") || legacy.ContainsKey("cycle")))
        {
            return LegacyRotation.Convert(legacy.Deserialize<LegacyRotation>(Options)
                ?? throw new JsonException("rotation JSON deserialised to null."));
        }

        return root.Deserialize<Rotation>(Options)
            ?? throw new JsonException("rotation JSON deserialised to null.");
    }

    public static Rotation Load(string path) => Deserialize(File.ReadAllText(path));

    public static void Save(string path, Rotation rotation) =>
        File.WriteAllText(path, Serialize(rotation), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static void SkipEmptyLists(JsonTypeInfo type)
    {
        foreach (JsonPropertyInfo property in type.Properties)
        {
            if (property.PropertyType.IsGenericType
                && property.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            {
                property.ShouldSerialize = (_, value) => value is ICollection { Count: > 0 };
            }
        }
    }
}

/// <summary>
/// Schema 1: an opening played once and a cycle that repeats, as flat lists
/// where a heavy attack was a step of its own. Kept so older files and shared
/// rotations still open; they are written back in the current schema.
/// </summary>
internal sealed record LegacyRotation
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string ProfileId { get; init; }

    public int ControlledSlot { get; init; } = 1;

    public string? Notes { get; init; }

    public List<LegacyStep> Opening { get; init; } = [];

    public List<LegacyStep> Cycle { get; init; } = [];

    public static Rotation Convert(LegacyRotation old)
    {
        var steps = new List<RotationStep>();
        var carry = new Carry();

        List<RotationStep> opening = Lay(old.Opening, "opening", "o", carry);
        List<RotationStep> cycle = Lay(old.Cycle, "cycle", "c", carry);

        // A cue left over at the very end leads into wherever the loop goes next.
        List<RotationStep> loop = cycle.Count > 0 ? cycle : opening;
        if (carry.Pending && loop.Count > 0)
        {
            loop[0] = carry.Onto(loop[0]);
        }

        if (opening.Count > 0 && cycle.Count > 0)
        {
            opening[^1] = opening[^1] with { Next = cycle[0].Id, NewRound = true };
        }

        if (cycle.Count > 0)
        {
            cycle[^1] = cycle[^1] with { Next = cycle[0].Id, NewRound = true };
        }

        steps.AddRange(opening);
        steps.AddRange(cycle);

        var segments = new List<RotationSegment>();
        if (opening.Count > 0)
        {
            segments.Add(new RotationSegment("opening", "开局"));
        }

        if (cycle.Count > 0)
        {
            segments.Add(new RotationSegment("cycle", "循环"));
        }

        return new Rotation
        {
            Id = old.Id,
            DisplayName = old.DisplayName,
            ProfileId = old.ProfileId,
            ControlledSlot = old.ControlledSlot,
            Notes = old.Notes,
            Segments = segments,
            Steps = steps,
        };
    }

    private static List<RotationStep> Lay(List<LegacyStep> list, string segment, string prefix, Carry carry)
    {
        var laid = new List<RotationStep>();
        foreach (LegacyStep item in list)
        {
            if (SoftCue.TryParse(item.Do, out SoftCue cue))
            {
                carry.Add(cue, item);
                continue;
            }

            if (!RotationAction.TryParse(item.Do, out RotationAction action))
            {
                throw new JsonException($"'{item.Do}' is not a rotation step. Write N, NE, N重击 or N大招 with N in 1-4.");
            }

            var step = new RotationStep
            {
                Id = $"{prefix}{laid.Count + 1}",
                Segment = segment,
                Action = action,
                When = item.When,
                Note = item.Note,
            };

            laid.Add(carry.Pending ? carry.Onto(step) : step);
        }

        return laid;
    }

    /// <summary>Cues waiting for the next real step, with whatever gated them.</summary>
    private sealed class Carry
    {
        private readonly List<SoftCue> cues = [];
        private readonly List<StepCondition> when = [];
        private string? note;

        public bool Pending => cues.Count > 0;

        public void Add(SoftCue cue, LegacyStep from)
        {
            cues.Add(cue);
            when.AddRange(from.When);
            note ??= from.Note;
        }

        public RotationStep Onto(RotationStep step)
        {
            RotationStep joined = step with
            {
                Before = [.. cues, .. step.Before],
                When = [.. when, .. step.When],
                Note = note ?? step.Note,
            };

            cues.Clear();
            when.Clear();
            note = null;
            return joined;
        }
    }
}

/// <summary>A schema 1 step: "4E", or <c>{ "do": "1重击", "when": [...], "note": "..." }</c>.</summary>
[JsonConverter(typeof(LegacyStepJsonConverter))]
internal sealed record LegacyStep(string Do, List<StepCondition> When, string? Note);

internal sealed class LegacyStepJsonConverter : JsonConverter<LegacyStep>
{
    public override LegacyStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new LegacyStep(reader.GetString() ?? string.Empty, [], null);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("a rotation step is either a string like \"4E\" or an object with \"do\".");
        }

        string? action = null;
        var when = new List<StepCondition>();
        string? note = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string property = reader.GetString() ?? string.Empty;
            reader.Read();

            switch (property.ToLowerInvariant())
            {
                case "do":
                    action = reader.GetString();
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

        return new LegacyStep(action ?? throw new JsonException("a rotation step object needs \"do\"."), when, note);
    }

    public override void Write(Utf8JsonWriter writer, LegacyStep value, JsonSerializerOptions options) =>
        throw new NotSupportedException("schema 1 is read, never written.");
}

/// <summary>
/// The one-line form of a segment, for typing a rotation in quickly:
/// <c>3 → 2 → 4E → ~1重击 1E → 1 → 2E → 1</c>. A <c>~</c> marks a soft cue,
/// which belongs to the step after it; <c>~"…"</c> is free text.
/// Branches and conditions are not part of it.
/// </summary>
public static class RotationText
{
    public sealed record Entry(RotationAction Action, IReadOnlyList<SoftCue> Before);

    public static string Format(IEnumerable<RotationStep> steps) =>
        string.Join(" → ", steps.Select(step => string.Join(' ', [.. step.Before.Select(FormatCue), step.Action.ToString()])));

    /// <exception cref="FormatException">With a message meant for the person typing.</exception>
    public static IReadOnlyList<Entry> Parse(string text)
    {
        var entries = new List<Entry>();
        var pending = new List<SoftCue>();

        foreach (string token in Tokens(text ?? string.Empty))
        {
            if (token.StartsWith('~'))
            {
                string body = token[1..];
                if (body.Length >= 2 && body[0] == '"' && body[^1] == '"')
                {
                    string said = body[1..^1].Trim();
                    if (said.Length == 0)
                    {
                        throw new FormatException("~\"\" 里要写点什么");
                    }

                    pending.Add(SoftCue.Say(said));
                }
                else if (SoftCue.TryParse(body, out SoftCue cue))
                {
                    pending.Add(cue);
                }
                else
                {
                    throw new FormatException($"看不懂软提示“{token}”：写 ~N重击、~N普攻2 或 ~\"文字\"");
                }
            }
            else if (RotationAction.TryParse(token, out RotationAction action))
            {
                entries.Add(new Entry(action, [.. pending]));
                pending.Clear();
            }
            else if (SoftCue.TryParse(token, out SoftCue bare))
            {
                throw new FormatException($"“{token}”画面上看不到，要写成软提示 ~{bare}");
            }
            else
            {
                throw new FormatException($"看不懂“{token}”：操作写 N、NE 或 N大招（N 为 1–4），软提示前面加 ~");
            }
        }

        if (pending.Count > 0)
        {
            throw new FormatException("软提示后面要跟一个操作，它们会一起显示");
        }

        if (entries.Count == 0)
        {
            throw new FormatException("至少要有一个操作");
        }

        return entries;
    }

    private static string FormatCue(SoftCue cue) =>
        cue.Kind == CueKind.Text ? $"~\"{cue.Text}\"" : $"~{cue}";

    private static IEnumerable<string> Tokens(string text)
    {
        var token = new StringBuilder();
        bool quoted = false;

        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                token.Append(c);
            }
            else if (!quoted && (char.IsWhiteSpace(c) || c is '→' or '>' or ','))
            {
                if (token.Length > 0 && token.ToString() != "-")
                {
                    yield return token.ToString();
                }

                token.Clear();
            }
            else
            {
                token.Append(c);
            }
        }

        if (quoted)
        {
            throw new FormatException("引号没有配对");
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }
}

/// <summary>Catches the mistakes a hand-written rotation is prone to, all at once.</summary>
public static class RotationValidator
{
    public static IReadOnlyList<ProfileProblem> Validate(Rotation rotation, GameProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        var problems = new List<ProfileProblem>();

        void Error(string message) => problems.Add(new ProfileProblem(ProfileProblemSeverity.Error, message));
        void Warn(string message) => problems.Add(new ProfileProblem(ProfileProblemSeverity.Warning, message));

        if (rotation.SchemaVersion != Rotation.CurrentSchemaVersion)
        {
            Error($"schema version {rotation.SchemaVersion} is not the current {Rotation.CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(rotation.Id))
        {
            Error("rotation id is empty.");
        }

        if (rotation.ControlledSlot is < RotationAction.MinSlot or > RotationAction.MaxSlot)
        {
            Error($"controlledSlot {rotation.ControlledSlot} is outside 1-4.");
        }

        if (rotation.Steps.Count == 0)
        {
            Error("the rotation has no steps.");
        }

        if (profile is not null && !string.Equals(profile.Id, rotation.ProfileId, StringComparison.Ordinal))
        {
            Error($"the rotation is for profile '{rotation.ProfileId}', not '{profile.Id}'.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var segments = rotation.Segments.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        foreach (RotationSegment segment in rotation.Segments)
        {
            string where = $"segment '{segment.Name}'";
            foreach (RotationBranch branch in segment.Branches)
            {
                int target = rotation.IndexOf(branch.To);
                if (target < 0)
                {
                    Error($"{where}: way out to '{branch.To}' is not a step.");
                }
                else if (rotation.Steps[target].Segment == segment.Id)
                {
                    Error($"{where}: its way out leads to '{branch.To}', which is in the same segment.");
                }

                if (branch.When.Count == 0)
                {
                    Warn($"{where}: a way out with no conditions always applies, so the segment never runs past its first step.");
                }

                foreach (StepCondition condition in branch.When)
                {
                    if (!condition.IsValid)
                    {
                        Error($"{where}: condition '{condition}' is out of range.");
                    }
                }
            }
        }

        for (int i = 0; i < rotation.Steps.Count; i++)
        {
            RotationStep step = rotation.Steps[i];
            string where = $"step {i + 1} '{step.Id}' ({step.Action})";

            if (string.IsNullOrWhiteSpace(step.Id))
            {
                Error($"{where}: id is empty.");
            }
            else if (!ids.Add(step.Id))
            {
                Error($"{where}: id is used twice.");
            }

            if (!step.Action.IsValid)
            {
                Error($"{where}: slot must be 1-4.");
            }

            if (step.Segment is not null && segments.Count > 0 && !segments.Contains(step.Segment))
            {
                Error($"{where}: segment '{step.Segment}' is not declared.");
            }

            foreach (SoftCue cue in step.Before)
            {
                if (!cue.IsValid)
                {
                    Error($"{where}: cue '{cue}' is out of range or empty.");
                }
                else if (cue.Kind is CueKind.Heavy or CueKind.AttackAfterSkill && cue.Slot != rotation.ControlledSlot)
                {
                    Warn($"{where}: cue '{cue}' is from slot {cue.Slot}, but slot {rotation.ControlledSlot} is the one in control.");
                }
            }

            IEnumerable<StepCondition> conditions = [.. step.When, .. step.Branches.SelectMany(b => b.When)];
            foreach (StepCondition condition in conditions)
            {
                if (!condition.IsValid)
                {
                    Error($"{where}: condition '{condition}' is out of range.");
                }
            }

            if (step.Next is not null && rotation.IndexOf(step.Next) < 0)
            {
                Error($"{where}: next '{step.Next}' is not a step.");
            }

            foreach (RotationBranch branch in step.Branches)
            {
                if (rotation.IndexOf(branch.To) < 0)
                {
                    Error($"{where}: branch to '{branch.To}' is not a step.");
                }

                if (branch.When.Count == 0)
                {
                    Warn($"{where}: branch to '{branch.To}' has no condition, so it is never suggested.");
                }
            }
        }

        if (!ProfileValidator.HasErrors(problems))
        {
            foreach (int unreachable in Unreachable(rotation))
            {
                Warn($"step {unreachable + 1} '{rotation.Steps[unreachable].Id}' cannot be reached from the first step.");
            }
        }

        return problems;
    }

    private static IEnumerable<int> Unreachable(Rotation rotation)
    {
        if (rotation.Steps.Count == 0)
        {
            return [];
        }

        var seen = new bool[rotation.Steps.Count];
        var queue = new Queue<int>([0]);
        seen[0] = true;
        while (queue.TryDequeue(out int at))
        {
            foreach (RotationEdge edge in rotation.Edges(at))
            {
                if (!seen[edge.To])
                {
                    seen[edge.To] = true;
                    queue.Enqueue(edge.To);
                }
            }
        }

        return Enumerable.Range(0, seen.Length).Where(i => !seen[i]);
    }
}
