using System.Globalization;
using Anaphora.Analysis;
using Anaphora.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anaphora.Overlay;

public enum ChipTone
{
    Skill,
    Chain,
    Ultimate,

    /// <summary>A soft cue: nothing confirms it, it just leads into the step after it.</summary>
    Cue,
}

/// <summary>
/// One thing to do, as a chip, drawn by slot. A skill is the digit, a chain is E
/// badged with the slot, an ultimate is the digit badged 长按. Cues come before
/// their step with a dashed outline: 重击 badged with the slot, 第2下 badged
/// "3 技能后", or free text.
/// </summary>
public sealed record StepChip(ChipTone Tone, string Main, string Badge, bool Blocked)
{
    public bool HasBadge => Badge.Length > 0;

    public bool IsChain => Tone == ChipTone.Chain;

    public bool IsUltimate => Tone == ChipTone.Ultimate;

    public bool IsCue => Tone == ChipTone.Cue;

    public static StepChip From(RotationAction action, bool blocked)
    {
        string slot = action.Slot.ToString(CultureInfo.InvariantCulture);
        return action.Kind switch
        {
            ActionKind.Chain => new StepChip(ChipTone.Chain, "E", slot, blocked),
            ActionKind.Ultimate => new StepChip(ChipTone.Ultimate, slot, "长按", blocked),
            _ => new StepChip(ChipTone.Skill, slot, string.Empty, blocked),
        };
    }

    public static StepChip From(SoftCue cue, bool blocked) => cue.Kind switch
    {
        CueKind.Heavy => new StepChip(ChipTone.Cue, "重击", cue.Slot.ToString(CultureInfo.InvariantCulture), blocked),
        CueKind.AttackAfterSkill => new StepChip(ChipTone.Cue, $"第{cue.Count}下", $"{cue.Slot}技能后", blocked),
        _ => new StepChip(ChipTone.Cue, cue.Text ?? string.Empty, string.Empty, blocked),
    };

    /// <summary>A step's cues, then the step itself.</summary>
    public static IEnumerable<StepChip> Sequence(StepView view) =>
        [.. view.Step.Before.Select(c => From(c, view.Blocked)), From(view.Step.Action, view.Blocked)];
}

/// <summary>
/// The one key to press now, drawn the way Endfield announces a place or a boss:
/// a white title card, the key alone, with a line of smaller text under it.
///
/// Only hard operations reach here. A soft cue is never the key to press, so it
/// leads into the card instead of becoming one.
/// </summary>
/// <param name="Glyph">What the card says: a slot digit, or E for a chain.</param>
/// <param name="Second">
/// The line under it, which says only what the glyph does not. A skill's key is
/// the whole story, so it stays empty; an ultimate's key is the same digit as
/// the skill's and only the hold tells them apart; a chain fires whoever leads
/// the game's prompt, so which slot that has to be is worth a line. When the
/// step is held back, it says what it is waiting for instead.
/// </param>
/// <param name="Sub">The micro line under that, in the place the game puts its Sarkaz script.</param>
public sealed record PrimaryAction(string Glyph, string Second, string Sub, bool Blocked)
{
    /// <summary>Before the first reading: something has to be on the card.</summary>
    public static readonly PrimaryAction Standby = new("—", "等待抓取", "STANDBY", false);

    public bool HasSecond => Second.Length > 0;

    public static PrimaryAction From(StepView view, string waiting)
    {
        ArgumentNullException.ThrowIfNull(view);

        RotationAction action = view.Step.Action;
        string slot = action.Slot.ToString(CultureInfo.InvariantCulture);
        bool blocked = view.Blocked;
        return action.Kind switch
        {
            ActionKind.Chain => new PrimaryAction("E", blocked ? waiting : $"触发 {slot}", "CHAIN", blocked),
            ActionKind.Ultimate => new PrimaryAction(slot, blocked ? waiting : "长按", "ULTIMATE", blocked),
            _ => new PrimaryAction(slot, blocked ? waiting : string.Empty, "SKILL", blocked),
        };
    }
}

/// <summary>
/// One step as the rotation strip draws it: the key in a box, the soft cues that
/// lead into it above, and whether the cursor is on it, behind it or ahead.
/// </summary>
/// <param name="Glyph">The key: a slot digit, or E for a chain.</param>
/// <param name="Badge">What the key alone does not say: the slot for a chain, 长按 for an ultimate.</param>
public sealed record TrackCell(
    string Glyph,
    string Badge,
    bool IsHold,
    IReadOnlyList<StepChip> Cues,
    bool IsNow,
    bool IsDone,
    bool Blocked)
{
    public bool HasBadge => Badge.Length > 0;

    public bool HasCues => Cues.Count > 0;

    public static TrackCell From(StepView view, bool isNow, bool isDone)
    {
        ArgumentNullException.ThrowIfNull(view);

        RotationAction action = view.Step.Action;
        string slot = action.Slot.ToString(CultureInfo.InvariantCulture);
        (string glyph, string badge) = action.Kind switch
        {
            ActionKind.Chain => ("E", slot),
            ActionKind.Ultimate => (slot, "长按"),
            _ => (slot, string.Empty),
        };

        return new TrackCell(
            glyph,
            badge,
            action.Kind == ActionKind.Ultimate,
            [.. view.Step.Before.Select(c => StepChip.From(c, view.Blocked))],
            isNow,
            isDone,
            isNow && view.Blocked);
    }
}

/// <summary>
/// Everything the overlay draws, updated from the capture thread's results on
/// the UI thread. Two things only: the title card, which answers "what now",
/// and the rotation strip under it, which is the current segment with the
/// cursor on it.
///
/// Skill points, chain cooldowns and ultimate readiness are deliberately not
/// here. A player who can spare the attention reads them off the game's own
/// HUD; a player who cannot should be looking at the card.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    /// <summary>False while the HUD is unreadable; everything shown is the last good reading.</summary>
    [ObservableProperty]
    public partial bool Live { get; set; }

    /// <summary>Whether the rotation strip is drawn at all; the card always is.</summary>
    [ObservableProperty]
    public partial bool TrackVisible { get; set; } = true;

    /// <summary>The cues that lead into the current step; they are not the key to press.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCues))]
    public partial IReadOnlyList<StepChip> NowCues { get; set; } = [];

    /// <summary>The key to press now, as the title card.</summary>
    [ObservableProperty]
    public partial PrimaryAction NowAction { get; set; } = PrimaryAction.Standby;

    /// <summary>The whole current segment, in order. However long the user made it.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TrackCell> Track { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    public partial string? Hint { get; set; }

    [ObservableProperty]
    public partial string StaleText { get; set; } = string.Empty;

    public bool HasCues => NowCues.Count > 0;

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    /// <param name="sinceReadable">How long ago the last readable frame was, for the stale tag.</param>
    public void Apply(HudSnapshot hud, RotationState rotation, TimeSpan sinceReadable)
    {
        ArgumentNullException.ThrowIfNull(hud);
        ArgumentNullException.ThrowIfNull(rotation);

        Live = hud.HudPresent;
        StaleText = string.Create(
            CultureInfo.InvariantCulture,
            $"HUD 不可见 · 保留 {sinceReadable.TotalSeconds:F1} 秒前的读数");

        NowCues = [.. rotation.Current.Step.Before.Select(c => StepChip.From(c, rotation.Current.Blocked))];
        NowAction = PrimaryAction.From(rotation.Current, $"等 {Unmet(rotation.Current)}");

        // Position is 1-based within the segment: the cursor sits on it, the
        // steps before it are done this time around, the rest are ahead.
        Track =
        [
            .. rotation.Segment.Select((view, i) =>
                TrackCell.From(view, isNow: i + 1 == rotation.Position, isDone: i + 1 < rotation.Position)),
        ];
        Hint = HintFor(rotation);
    }

    /// <summary>Shown when there is nothing to read: no game, or capture not running.</summary>
    public void ShowWaiting(string message)
    {
        Live = false;
        StaleText = message;
    }

    private static string? HintFor(RotationState rotation)
    {
        if (rotation.Current.Blocked)
        {
            // The card already says what it waits for; the strip adds the note.
            return rotation.Current.Step.Note;
        }

        StepView? upcoming = rotation.Next.FirstOrDefault(n => n.Blocked);
        return upcoming is null
            ? null
            : $"{upcoming.Step.Action}：{upcoming.Step.Note ?? $"等待 {Unmet(upcoming)}"}";
    }

    private static string Unmet(StepView view) =>
        string.Join("，", view.Conditions.Where(c => !c.Met).Select(c => c.Condition.Description));
}
