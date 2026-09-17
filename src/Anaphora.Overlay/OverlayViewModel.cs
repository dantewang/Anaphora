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

public sealed partial class SlotStatusViewModel(int slot) : ObservableObject
{
    /// <summary>Width of a slot's chain bar at the reference size, in DIPs.</summary>
    public const double ChainBarWidth = 45;

    public int Slot { get; } = slot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChainFillWidth))]
    public partial double ChainRatio { get; set; }

    [ObservableProperty]
    public partial bool ChainReady { get; set; }

    [ObservableProperty]
    public partial bool UltimateReady { get; set; }

    public double ChainFillWidth => Math.Clamp(ChainRatio, 0, 1) * ChainBarWidth;
}

/// <summary>
/// Everything the overlay draws, updated from the capture thread's results on
/// the UI thread. The top half answers "what now"; the status half shows the
/// readings it was decided from, and can be folded away once they are trusted.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    public OverlayViewModel()
    {
        Slots = [.. Enumerable.Range(1, 4).Select(i => new SlotStatusViewModel(i))];
    }

    public IReadOnlyList<SlotStatusViewModel> Slots { get; }

    /// <summary>False while the HUD is unreadable; everything shown is the last good reading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NowLabel))]
    public partial bool Live { get; set; }

    [ObservableProperty]
    public partial bool StatusVisible { get; set; } = true;

    /// <summary>What to do now: the current step's cues, then the step.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<StepChip> Now { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<StepChip> NextSteps { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint), nameof(ShowNote), nameof(ShowRound))]
    public partial string? Hint { get; set; }

    /// <summary>The current step's note, shown while nothing is blocking.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNote), nameof(ShowRound))]
    public partial string? Note { get; set; }

    [ObservableProperty]
    public partial string RoundText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StaleText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Pip1), nameof(Pip2), nameof(Pip3))]
    public partial int SkillPoints { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrompt))]
    public partial string? PromptText { get; set; }

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    public bool ShowNote => !HasHint && !string.IsNullOrEmpty(Note);

    public bool ShowRound => !HasHint && string.IsNullOrEmpty(Note);

    public bool HasPrompt => !string.IsNullOrEmpty(PromptText);

    public string NowLabel => Live ? "现在" : "暂停";

    public bool Pip1 => SkillPoints >= 1;

    public bool Pip2 => SkillPoints >= 2;

    public bool Pip3 => SkillPoints >= 3;

    /// <param name="sinceReadable">How long ago the last readable frame was, for the stale tag.</param>
    public void Apply(HudSnapshot hud, RotationState rotation, TimeSpan sinceReadable)
    {
        ArgumentNullException.ThrowIfNull(hud);
        ArgumentNullException.ThrowIfNull(rotation);

        Live = hud.HudPresent;
        StaleText = string.Create(
            CultureInfo.InvariantCulture,
            $"HUD 不可见 · 保留 {sinceReadable.TotalSeconds:F1} 秒前的读数");

        Now = [.. StepChip.Sequence(rotation.Current)];
        NextSteps = [.. rotation.Next.SelectMany(StepChip.Sequence)];
        Hint = HintFor(rotation);
        Note = rotation.Current.Step.Note;
        RoundText = string.Create(
            CultureInfo.InvariantCulture,
            $"第 {rotation.Cursor.Round} 轮 · {rotation.SegmentName} {rotation.Position}/{rotation.SegmentLength}");

        // An absent HUD keeps the last good numbers on screen, dimmed.
        if (!hud.HudPresent)
        {
            return;
        }

        SkillPoints = hud.SkillPoints;
        for (int i = 0; i < Slots.Count; i++)
        {
            SlotStatusViewModel slot = Slots[i];
            if (i < hud.Chains.Count)
            {
                slot.ChainRatio = hud.Chains[i].Ratio;
                slot.ChainReady = hud.Chains[i].IsReady;
            }

            if (i < hud.Ultimates.Count)
            {
                slot.UltimateReady = hud.Ultimates[i].IsReady;
            }
        }

        string[] prompts =
        [
            .. hud.Prompts
                .Where(p => p.IsPresent)
                .Select(p => p.Slot?.ToString(CultureInfo.InvariantCulture) ?? p.PortraitId!),
        ];
        PromptText = prompts.Length == 0 ? null : $"E › {string.Join("  ", prompts)}";
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
            return rotation.Current.Step.Note ?? $"等待 {Unmet(rotation.Current)}";
        }

        StepView? upcoming = rotation.Next.FirstOrDefault(n => n.Blocked);
        return upcoming is null
            ? null
            : $"{upcoming.Step.Action}：{upcoming.Step.Note ?? $"等待 {Unmet(upcoming)}"}";
    }

    private static string Unmet(StepView view) =>
        string.Join("，", view.Conditions.Where(c => !c.Met).Select(c => c.Condition.Description));
}
