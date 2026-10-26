using Anaphora.Core;

namespace Anaphora.Analysis;

/// <summary>A cast the keyboard can name, as opposed to one inferred from pixels.</summary>
public enum CastKind
{
    /// <summary>Slot N's skill: a short press of N.</summary>
    Skill,

    /// <summary>Slot N's chain: E while N leads the prompt.</summary>
    Chain,

    /// <summary>Slot N's ultimate: N held down.</summary>
    Ultimate,
}

/// <summary>
/// One cast reported by the keyboard. Unlike an <see cref="Observation"/> read
/// from the HUD, a skill names its slot and every cast carries the time the key
/// was pressed.
///
/// <see cref="Slot"/> is 0 only for a chain whose prompt could not be matched to
/// a reference portrait: E is shared by the whole team, so the key alone does
/// not say who it triggered. The tracker fills the slot in from the prompt when
/// it can, and otherwise falls back to the nearest chain step.
/// </summary>
public readonly record struct CastInput(CastKind Kind, int Slot, TimeSpan At, TimeSpan Held = default)
{
    public override string ToString() => Kind switch
    {
        CastKind.Skill => $"{Slot} 技能（按键）",
        CastKind.Chain when Slot is >= RotationAction.MinSlot and <= RotationAction.MaxSlot => $"{Slot}E 连携（按键）",
        CastKind.Chain => "E 连携（按键，槽位未知）",
        CastKind.Ultimate => $"{Slot} 大招（按键长按）",
        _ => Kind.ToString(),
    };
}

/// <summary>The keys a rotation can be played with: number keys 1-4 and E.</summary>
public enum GameKey
{
    Slot1 = 1,
    Slot2 = 2,
    Slot3 = 3,
    Slot4 = 4,
    Chain = 5,
}

/// <summary>A single make or break of a watched key.</summary>
public readonly record struct KeyTransition(GameKey Key, bool IsDown, TimeSpan At);

/// <summary>
/// Turns key transitions into casts.
///
/// A number key is a skill when released before <see cref="UltimateHold"/>, and
/// an ultimate once it has been held that long. The game acts on the hold, so
/// the ultimate is reported while the key is still down; waiting for the
/// release would make the overlay lag behind the cut-in. E is a chain the
/// moment it goes down.
///
/// This class only knows the keys, not whether the game accepted the input: a
/// skill pressed with no skill point banked, or an ultimate on cooldown, still
/// produces a cast. The host gates the obviously impossible ones against the
/// HUD before handing them to the tracker.
/// </summary>
public sealed class KeyboardCastReader
{
    private readonly Dictionary<GameKey, Hold> down = [];
    private readonly TimeSpan ultimateHold;
    private bool chainDown;

    public KeyboardCastReader(TimeSpan ultimateHold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ultimateHold, TimeSpan.Zero);
        this.ultimateHold = ultimateHold;
    }

    /// <summary>Raised on the thread that feeds transitions.</summary>
    public event Action<CastInput>? Cast;

    /// <summary>How long a number key must be held to count as an ultimate.</summary>
    public TimeSpan UltimateHold => ultimateHold;

    public void Feed(KeyTransition transition)
    {
        if (transition.IsDown)
        {
            if (transition.Key == GameKey.Chain)
            {
                // E fires the leading prompt at once; holding it does not queue a second.
                if (!chainDown)
                {
                    chainDown = true;
                    Cast?.Invoke(new CastInput(CastKind.Chain, 0, transition.At));
                }

                return;
            }

            down.TryAdd(transition.Key, new Hold(transition.At, false));
            return;
        }

        if (transition.Key == GameKey.Chain)
        {
            chainDown = false;
            return;
        }

        if (!down.Remove(transition.Key, out Hold hold) || hold.Fired)
        {
            return;
        }

        TimeSpan held = transition.At - hold.At;
        Cast?.Invoke(new CastInput(
            held >= ultimateHold ? CastKind.Ultimate : CastKind.Skill,
            (int)transition.Key,
            transition.At,
            held));
    }

    /// <summary>
    /// Reports an ultimate as soon as a held key crosses the threshold, without
    /// waiting for the player to let go.
    /// </summary>
    public void Tick(TimeSpan now)
    {
        if (down.Count == 0)
        {
            return;
        }

        foreach (GameKey key in down.Keys.ToArray())
        {
            Hold hold = down[key];
            if (!hold.Fired && now - hold.At >= ultimateHold)
            {
                down[key] = hold with { Fired = true };
                Cast?.Invoke(new CastInput(
                    CastKind.Ultimate,
                    (int)key,
                    hold.At + ultimateHold,
                    now - hold.At));
            }
        }
    }

    /// <summary>Forget every key without reporting anything: focus was lost, or a new fight started.</summary>
    public void Reset()
    {
        down.Clear();
        chainDown = false;
    }

    private readonly record struct Hold(TimeSpan At, bool Fired);
}
