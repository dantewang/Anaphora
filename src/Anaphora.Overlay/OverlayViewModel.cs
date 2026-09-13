using System.Globalization;
using Anaphora.Analysis;
using Anaphora.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anaphora.Overlay;

/// <summary>
/// One step as a chip: what to press, drawn by slot. A skill is the digit, a
/// chain is E with the slot as a badge, a heavy attack is 重击 with the slot, an
/// ultimate is the digit badged 长按.
/// </summary>
public sealed record StepChip(ActionKind Kind, int Slot, bool Blocked)
{
    public string Main => Kind switch
    {
        ActionKind.Chain => "E",
        ActionKind.Heavy => "重击",
        _ => Slot.ToString(CultureInfo.InvariantCulture),
    };

    public string Badge => Kind switch
    {
        ActionKind.Chain or ActionKind.Heavy => Slot.ToString(CultureInfo.InvariantCulture),
        ActionKind.Ultimate => "长按",
        _ => string.Empty,
    };

    public bool HasBadge => Badge.Length > 0;

    public bool IsChain => Kind == ActionKind.Chain;

    public bool IsHeavy => Kind == ActionKind.Heavy;

    public bool IsUltimate => Kind == ActionKind.Ultimate;

    public static StepChip From(StepView view) => new(view.Step.Action.Kind, view.Step.Action.Slot, view.Blocked);
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

    [ObservableProperty]
    public partial StepChip? Current { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<StepChip> NextSteps { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    public partial string? Hint { get; set; }

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

        Current = StepChip.From(rotation.Current);
        NextSteps = [.. rotation.Next.Select(StepChip.From)];
        Hint = HintFor(rotation);
        RoundText = string.Create(
            CultureInfo.InvariantCulture,
            $"第 {rotation.Cursor.Round} 轮 · {rotation.Cursor.Index + 1}/{rotation.StepsInRound}");

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
            return rotation.Current.Step.Note ?? $"等待 {string.Join(", ", rotation.Current.Conditions.Where(c => !c.Met).Select(c => c.Condition))}";
        }

        StepView? upcoming = rotation.Next.FirstOrDefault(n => n.Blocked);
        return upcoming is null
            ? null
            : $"{upcoming.Step.Action}：{upcoming.Step.Note ?? string.Join(", ", upcoming.Conditions.Where(c => !c.Met).Select(c => c.Condition))}";
    }
}
