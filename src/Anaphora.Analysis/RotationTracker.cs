using Anaphora.Core;

namespace Anaphora.Analysis;

public enum RotationPhase
{
    Opening,
    Cycle,
}

/// <summary>Where in the rotation the player is. Round 1 is the opening when there is one.</summary>
public readonly record struct RotationCursor(RotationPhase Phase, int Round, int Index);

/// <summary>Something the HUD showed happening between two readable frames.</summary>
public enum ObservedKind
{
    /// <summary>A whole skill point went. Which slot spent it is not visible.</summary>
    SkillSpent,

    /// <summary>A chain went from ready to cooling down.</summary>
    ChainUsed,

    /// <summary>An ultimate went from castable to charging.</summary>
    UltimateUsed,
}

/// <param name="Slot">1-4, or 0 when the HUD does not say whose it was.</param>
public readonly record struct Observation(ObservedKind Kind, int Slot)
{
    public override string ToString() => Kind switch
    {
        ObservedKind.SkillSpent => "技能点 -1",
        ObservedKind.ChainUsed => $"{Slot}E 已用",
        ObservedKind.UltimateUsed => $"{Slot}大招 已用",
        _ => Kind.ToString(),
    };
}

public readonly record struct ConditionStatus(StepCondition Condition, bool Met);

/// <summary>A step as the overlay should show it: the step, and whether its conditions hold right now.</summary>
public sealed record StepView(RotationStep Step, IReadOnlyList<ConditionStatus> Conditions)
{
    /// <summary>True when some condition does not hold yet; the overlay shows the step's note.</summary>
    public bool Blocked => Conditions.Any(c => !c.Met);
}

public sealed record RotationState
{
    public required RotationCursor Cursor { get; init; }

    public required StepView Current { get; init; }

    public IReadOnlyList<StepView> Next { get; init; } = [];

    /// <summary>The steps of the round the cursor is in, for "4/8" style progress.</summary>
    public int StepsInRound { get; init; }

    /// <summary>The HUD is not readable; the cursor is frozen where it was.</summary>
    public bool Stale { get; init; }

    /// <summary>Steps this update recognised as done, oldest first.</summary>
    public IReadOnlyList<RotationAction> Completed { get; init; } = [];

    /// <summary>Things that happened on screen that no nearby step explains -- the player is off the rotation.</summary>
    public IReadOnlyList<Observation> Unexpected { get; init; } = [];

    /// <summary>
    /// This update moved the cursor to where the player's recent inputs actually
    /// fit, rather than one step on from where it was.
    /// </summary>
    public bool Resynced { get; init; }
}

public sealed record RotationTrackerOptions
{
    /// <summary>
    /// How far ahead a slot-specific observation may reach to find its step.
    /// A chain or ultimate names its slot, so matching it a few steps on is safe
    /// and recovers from a missed skill; an anonymous skill point is never
    /// allowed to jump past a step it cannot account for.
    /// </summary>
    public int LookAhead { get; init; } = 3;

    /// <summary>How many steps after the current one to show.</summary>
    public int Upcoming { get; init; } = 3;

    /// <summary>
    /// Without a readable HUD for this long, the fight is taken to be over and
    /// the next one starts at the opening. Long enough to sit out an ultimate
    /// cut-in, short enough that a new fight does not inherit the old cursor.
    /// </summary>
    public TimeSpan ResetAfterAbsence { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How far back observed inputs are remembered when working out where a
    /// player who has left the rotation has rejoined it.
    /// </summary>
    public TimeSpan ResyncWindow { get; init; } = TimeSpan.FromSeconds(12);
}

/// <summary>
/// Follows a rotation by watching the HUD, never the keyboard.
///
/// Each readable snapshot is compared with the previous readable one. A skill
/// point disappearing, a chain starting its cooldown and an ultimate going dark
/// are the observable traces of the player's inputs, and each is matched to the
/// step it completes.
///
/// A heavy attack leaves no trace of its own -- the game has no key for it and
/// nothing on the HUD changes. It is stepped over as soon as a later step is
/// seen, which is the right answer for the example team, where the heavy attack
/// exists to trigger the chains that come next. How to see it directly is an
/// open question.
///
/// Known blind spot: a skill that costs less than a whole segment does not
/// change the whole-segment count, so it goes unseen until a later chain or
/// ultimate pulls the cursor past it.
///
/// When the player leaves the rotation and starts over mid-fight, stepping
/// forward from the old cursor can never catch up. So whenever something
/// happens that no nearby step explains, the recent inputs are laid against
/// every position in the rotation, and the cursor moves to wherever they fit --
/// provided the fit is long enough and names at least one slot, so a stray
/// input is not mistaken for a restart.
/// </summary>
public sealed class RotationTracker
{
    /// <summary>A realignment needs this many consecutive inputs to fit...</summary>
    private const int ResyncMinimumLength = 3;

    private readonly Rotation rotation;
    private readonly RotationTrackerOptions options;
    private readonly List<(Observation Seen, TimeSpan At)> history = [];

    private RotationCursor cursor;
    private HudSnapshot? baseline;
    private TimeSpan lastPresent;
    private bool everPresent;

    public RotationTracker(Rotation rotation, RotationTrackerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        if (rotation.Opening.Count == 0 && rotation.Cycle.Count == 0)
        {
            throw new ArgumentException("the rotation has no steps.", nameof(rotation));
        }

        this.rotation = rotation;
        this.options = options ?? new RotationTrackerOptions();
        cursor = Start;
        State = Describe(null, stale: true, [], []);
    }

    public Rotation Rotation => rotation;

    public RotationState State { get; private set; }

    private RotationCursor Start => rotation.Opening.Count > 0
        ? new RotationCursor(RotationPhase.Opening, 1, 0)
        : new RotationCursor(RotationPhase.Cycle, 1, 0);

    /// <param name="timestamp">Any monotonic clock; only differences are used.</param>
    public RotationState Observe(HudSnapshot snapshot, TimeSpan timestamp)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.HudPresent)
        {
            State = Describe(baseline, stale: true, [], []);
            return State;
        }

        if (everPresent && timestamp - lastPresent >= options.ResetAfterAbsence)
        {
            cursor = Start;
            baseline = null;
            history.Clear();
        }

        everPresent = true;
        lastPresent = timestamp;

        var completed = new List<RotationAction>();
        var unexpected = new List<Observation>();
        bool resynced = false;

        if (baseline is not null)
        {
            var pending = Diff(baseline, snapshot);

            foreach (Observation seen in pending)
            {
                history.Add((seen, timestamp));
            }

            history.RemoveAll(entry => timestamp - entry.At > options.ResyncWindow);

            // Match whatever can be matched, in whichever order the steps want
            // them: two things seen in the same frame need not have happened in
            // the order they were diffed.
            bool progress = true;
            while (progress && pending.Count > 0)
            {
                progress = false;
                for (int i = 0; i < pending.Count; i++)
                {
                    if (TryComplete(pending[i], completed))
                    {
                        pending.RemoveAt(i);
                        progress = true;
                        break;
                    }
                }
            }

            unexpected.AddRange(pending);

            if (unexpected.Count > 0)
            {
                resynced = TryResync();
            }
        }

        baseline = snapshot;
        State = Describe(snapshot, stale: false, completed, unexpected) with { Resynced = resynced };
        return State;
    }

    /// <summary>Back to the opening, as at the start of a fight.</summary>
    public void Reset()
    {
        cursor = Start;
        baseline = null;
        history.Clear();
        State = Describe(null, stale: true, [], []);
    }

    /// <summary>
    /// Lays the remembered inputs against every position and moves the cursor to
    /// the best fit. Longer fits win; on a tie the current position wins (so a
    /// player who is actually on track is never moved), then a position in the
    /// same phase, then the earliest -- which, with an invisible heavy attack
    /// right after the last matched input, keeps the heavy attack as the step to
    /// do rather than skipping it.
    /// </summary>
    private bool TryResync()
    {
        RotationCursor? best = null;
        int bestLength = 0;

        foreach (RotationCursor candidate in Candidates())
        {
            (int length, bool named) = FitLength(candidate);
            if (length < ResyncMinimumLength || !named)
            {
                continue;
            }

            bool better = best is not RotationCursor current
                || length > bestLength
                || (length == bestLength && Rank(candidate) < Rank(current));

            if (better)
            {
                best = candidate;
                bestLength = length;
            }
        }

        if (best is not RotationCursor target || (target.Phase == cursor.Phase && target.Index == cursor.Index))
        {
            return false;
        }

        int round = target.Phase switch
        {
            RotationPhase.Opening => 1,
            _ when cursor.Phase == RotationPhase.Cycle => target.Index <= cursor.Index ? cursor.Round + 1 : cursor.Round,
            _ => rotation.Opening.Count > 0 ? 2 : 1,
        };

        cursor = target with { Round = round };
        history.Clear();
        return true;

        // Lower is preferred.
        int Rank(RotationCursor c) =>
            c.Phase == cursor.Phase && c.Index == cursor.Index ? 0 : c.Phase == cursor.Phase ? 1 : 2;
    }

    private IEnumerable<RotationCursor> Candidates()
    {
        for (int i = 0; i < rotation.Opening.Count; i++)
        {
            yield return new RotationCursor(RotationPhase.Opening, 1, i);
        }

        for (int i = 0; i < rotation.Cycle.Count; i++)
        {
            yield return new RotationCursor(RotationPhase.Cycle, 0, i);
        }
    }

    /// <summary>
    /// How many of the most recent inputs, newest first, are exactly the steps
    /// leading up to <paramref name="candidate"/> -- heavy attacks skipped, since
    /// they leave nothing to match -- and whether any of them named a slot.
    /// </summary>
    private (int Length, bool Named) FitLength(RotationCursor candidate)
    {
        int newest = history.Count - 1;
        int length = 0;
        bool named = false;
        RotationCursor at = candidate;

        for (int guard = 0; newest >= 0 && guard < 64; guard++)
        {
            if (!Retreat(at, out at))
            {
                break;
            }

            RotationAction action = StepAt(at).Action;
            if (action.Kind == ActionKind.Heavy)
            {
                continue;
            }

            Observation seen = history[newest].Seen;
            if (!Explains(action, seen))
            {
                break;
            }

            length++;
            named |= seen.Kind != ObservedKind.SkillSpent;
            newest--;
        }

        return (length, named);
    }

    /// <summary>The step before. The cycle runs back into its previous round; the opening starts the fight.</summary>
    private bool Retreat(RotationCursor current, out RotationCursor previous)
    {
        if (current.Index > 0)
        {
            previous = current with { Index = current.Index - 1 };
            return true;
        }

        if (current.Phase == RotationPhase.Cycle && rotation.Cycle.Count > 0)
        {
            previous = current with { Index = rotation.Cycle.Count - 1 };
            return true;
        }

        previous = current;
        return false;
    }

    /// <summary>Moves past the current step by hand -- for a step the HUD cannot confirm.</summary>
    public void Skip()
    {
        cursor = Advance(cursor, 1);
        State = Describe(baseline, State.Stale, [], []);
    }

    private static List<Observation> Diff(HudSnapshot before, HudSnapshot after)
    {
        var seen = new List<Observation>();

        for (int i = before.SkillPoints - after.SkillPoints; i > 0; i--)
        {
            seen.Add(new Observation(ObservedKind.SkillSpent, 0));
        }

        for (int i = 0; i < Math.Min(before.Chains.Count, after.Chains.Count); i++)
        {
            if (before.Chains[i].IsReady && !after.Chains[i].IsReady)
            {
                seen.Add(new Observation(ObservedKind.ChainUsed, i + 1));
            }
        }

        for (int i = 0; i < Math.Min(before.Ultimates.Count, after.Ultimates.Count); i++)
        {
            if (before.Ultimates[i].IsReady && !after.Ultimates[i].IsReady)
            {
                seen.Add(new Observation(ObservedKind.UltimateUsed, i + 1));
            }
        }

        return seen;
    }

    private bool TryComplete(Observation observation, List<RotationAction> completed)
    {
        bool namesItsSlot = observation.Kind != ObservedKind.SkillSpent;

        for (int k = 0; k <= options.LookAhead; k++)
        {
            RotationStep step = StepAt(Advance(cursor, k));

            if (Explains(step.Action, observation))
            {
                for (int skipped = 0; skipped <= k; skipped++)
                {
                    completed.Add(StepAt(Advance(cursor, skipped)).Action);
                }

                cursor = Advance(cursor, k + 1);
                return true;
            }

            // A heavy attack is invisible, so it never blocks the search. Any
            // other step may only be jumped by an observation that says whose
            // input it was.
            if (step.Action.Kind != ActionKind.Heavy && !namesItsSlot)
            {
                return false;
            }
        }

        return false;
    }

    private static bool Explains(RotationAction action, Observation observation) => (action.Kind, observation.Kind) switch
    {
        (ActionKind.Skill, ObservedKind.SkillSpent) => true,
        (ActionKind.Chain, ObservedKind.ChainUsed) => action.Slot == observation.Slot,
        (ActionKind.Ultimate, ObservedKind.UltimateUsed) => action.Slot == observation.Slot,
        _ => false,
    };

    private RotationCursor Advance(RotationCursor from, int steps)
    {
        RotationCursor at = from;
        for (int i = 0; i < steps; i++)
        {
            int length = at.Phase == RotationPhase.Opening ? rotation.Opening.Count : rotation.Cycle.Count;
            if (at.Index + 1 < length)
            {
                at = at with { Index = at.Index + 1 };
            }
            else if (rotation.Cycle.Count > 0)
            {
                at = new RotationCursor(RotationPhase.Cycle, at.Round + 1, 0);
            }
            else
            {
                // Opening only: it simply starts over.
                at = new RotationCursor(RotationPhase.Opening, at.Round + 1, 0);
            }
        }

        return at;
    }

    private RotationStep StepAt(RotationCursor at) =>
        at.Phase == RotationPhase.Opening ? rotation.Opening[at.Index] : rotation.Cycle[at.Index];

    private RotationState Describe(
        HudSnapshot? snapshot,
        bool stale,
        IReadOnlyList<RotationAction> completed,
        IReadOnlyList<Observation> unexpected)
    {
        StepView View(RotationStep step) => new(
            step,
            [.. step.When.Select(c => new ConditionStatus(c, snapshot is not null && Holds(c, snapshot)))]);

        return new RotationState
        {
            Cursor = cursor,
            Current = View(StepAt(cursor)),
            Next = [.. Enumerable.Range(1, options.Upcoming).Select(k => View(StepAt(Advance(cursor, k))))],
            StepsInRound = cursor.Phase == RotationPhase.Opening ? rotation.Opening.Count : rotation.Cycle.Count,
            Stale = stale,
            Completed = completed,
            Unexpected = unexpected,
        };
    }

    private static bool Holds(StepCondition condition, HudSnapshot snapshot) => condition.Kind switch
    {
        ConditionKind.SkillPoints => snapshot.SkillPoints >= condition.Value,
        ConditionKind.ChainReady => condition.Value - 1 < snapshot.Chains.Count && snapshot.Chains[condition.Value - 1].IsReady,
        ConditionKind.UltimateReady => condition.Value - 1 < snapshot.Ultimates.Count && snapshot.Ultimates[condition.Value - 1].IsReady,
        _ => false,
    };
}
