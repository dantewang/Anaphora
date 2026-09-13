using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anaphora.Core;

/// <summary>Reads and writes rotations as JSON.</summary>
public static class RotationStore
{
    /// <summary>
    /// Relaxed escaping on purpose: rotations are written by hand and read by
    /// people, and "1重击" is legible where "1重击" is not.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(Rotation rotation)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        return JsonSerializer.Serialize(rotation, Options);
    }

    public static Rotation Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<Rotation>(json, Options)
            ?? throw new JsonException("rotation JSON deserialised to null.");
    }

    public static Rotation Load(string path) => Deserialize(File.ReadAllText(path));

    public static void Save(string path, Rotation rotation) => File.WriteAllText(path, Serialize(rotation));
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

        if (rotation.Opening.Count == 0 && rotation.Cycle.Count == 0)
        {
            Error("the rotation has no steps: both opening and cycle are empty.");
        }

        Check(rotation.Opening, "opening");
        Check(rotation.Cycle, "cycle");

        if (profile is not null && !string.Equals(profile.Id, rotation.ProfileId, StringComparison.Ordinal))
        {
            Error($"the rotation is for profile '{rotation.ProfileId}', not '{profile.Id}'.");
        }

        if (rotation.Cycle.Count > 0 && rotation.Cycle.All(s => s.Action.Kind == ActionKind.Heavy))
        {
            Error("every cycle step is a heavy attack, which cannot be seen on screen; the cycle could never advance.");
        }

        return problems;

        void Check(IReadOnlyList<RotationStep> steps, string part)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                RotationStep step = steps[i];
                string where = $"{part}[{i}] '{step.Action}'";

                if (!step.Action.IsValid)
                {
                    Error($"{where}: slot must be 1-4.");
                }

                foreach (StepCondition condition in step.When)
                {
                    if (!condition.IsValid)
                    {
                        Error($"{where}: condition '{condition}' is out of range.");
                    }
                }

                if (step.Action.Kind == ActionKind.Heavy && step.Action.Slot != rotation.ControlledSlot)
                {
                    Warn($"{where}: a heavy attack from slot {step.Action.Slot}, but slot {rotation.ControlledSlot} is the one in control.");
                }

                if (step.When.Count > 0 && string.IsNullOrWhiteSpace(step.Note))
                {
                    Warn($"{where}: has conditions but no note, so the overlay can only show the raw condition.");
                }

                if (i > 0 && steps[i - 1].Action.Kind == ActionKind.Heavy && step.Action.Kind == ActionKind.Heavy)
                {
                    Warn($"{where}: two heavy attacks in a row cannot be told apart on screen.");
                }
            }
        }
    }
}
