using Anaphora.Core;

namespace Anaphora.Analysis;

/// <summary>Where in the rotation the player is: the step to do next, and which round of the fight.</summary>
public readonly record struct RotationCursor(int Step, int Round);

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
    public bool NamesItsSlot => Kind != ObservedKind.SkillSpent;

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

    /// <summary>The steps after the current one, along the way the overlay suggests.</summary>
    public IReadOnlyList<StepView> Next { get; init; } = [];

    /// <summary>The name of the current step's segment: "开局", "循环".</summary>
    public string SegmentName { get; init; } = string.Empty;

    /// <summary>1-based position of the current step in its segment, for "4/7" style progress.</summary>
    public int Position { get; init; }

    public int SegmentLength { get; init; }

    /// <summary>The HUD is not readable; the cursor is frozen where it was.</summary>
    public bool Stale { get; init; }

    /// <summary>Steps this update recognised as done, oldest first.</summary>
    public IReadOnlyList<RotationStep> Completed { get; init; } = [];

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
    /// the next one starts at the first step. Long enough to sit out an ultimate
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
/// step it completes. Soft cues -- heavy attacks, the Nth attack after a skill --
/// leave no trace and are never waited for; they ride along with their step.
///
/// The rotation is a state machine. After a step, the cursor goes the way the
/// branch conditions point, and keeps re-deciding while the player has not moved
/// on, since the HUD may change its mind (a chain prompt appearing, say). What
/// the player actually does wins: an input matching another way out of the last
/// step is taken as that way.
///
/// Known blind spot: a skill that costs less than a whole segment does not
/// change the whole-segment count, so it goes unseen until a later chain or
/// ultimate pulls the cursor past it.
///
/// When the player leaves the rotation and starts over mid-fight, stepping
/// forward from the old cursor can never catch up. So whenever something
/// happens that no nearby step explains, the recent inputs are laid against
/// every step, and the cursor moves to wherever they fit -- provided the fit is
/// long enough and names at least one slot, so a stray input is not mistaken for
/// a restart.
/// </summary>
public sealed class RotationTracker
{
    /// <summary>A realignment needs this many consecutive inputs to fit...</summary>
    private const int ResyncMinimumLength = 3;

    private readonly Rotation rotation;
    private readonly RotationTrackerOptions options;
    private readonly List<(Observation Seen, TimeSpan At)> history = [];

    private RotationCursor cursor;

    /// <summary>The step last completed, while the way on from it is still being decided.</summary>
    private RotationCursor? from;

    private HudSnapshot? baseline;
    private TimeSpan lastPresent;
    private bool everPresent;

    public RotationTracker(Rotation rotation, RotationTrackerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        if (rotation.Steps.Count == 0)
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

    private static RotationCursor Start => new(0, 1);

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
            Restart();
        }

        everPresent = true;
        lastPresent = timestamp;

        var completed = new List<RotationStep>();
        var unexpected = new List<Observation>();
        bool resynced = false;

        if (baseline is not null)
        {
            List<Observation> pending = Diff(baseline, snapshot);

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
                    if (TryComplete(pending[i], snapshot, completed))
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

        Redecide(snapshot);
        baseline = snapshot;
        State = Describe(snapshot, stale: false, completed, unexpected) with { Resynced = resynced };
        return State;
    }

    /// <summary>Back to the first step, as at the start of a fight.</summary>
    public void Reset()
    {
        Restart();
        State = Describe(null, stale: true, [], []);
    }

    /// <summary>Moves past the current step by hand -- for a step the HUD cannot confirm.</summary>
    public void Skip()
    {
        Complete(cursor, baseline);
        State = Describe(baseline, State.Stale, [], []);
    }

    private void Restart()
    {
        cursor = Start;
        from = null;
        baseline = null;
        history.Clear();
    }

    private void Complete(RotationCursor done, HudSnapshot? snapshot)
    {
        from = done;
        cursor = Follow(done, Suggested(done.Step, snapshot));
    }

    /// <summary>While the player has not moved on, the suggested way out may change with the HUD.</summary>
    private void Redecide(HudSnapshot snapshot)
    {
        if (from is RotationCursor done)
        {
            cursor = Follow(done, Suggested(done.Step, snapshot));
        }
    }

    private static RotationCursor Follow(RotationCursor at, RotationEdge edge) =>
        new(edge.To, edge.NewRound ? at.Round + 1 : at.Round);

    /// <summary>The first branch whose conditions all hold, else the default way.</summary>
    private RotationEdge Suggested(int step, HudSnapshot? snapshot)
    {
        foreach (RotationEdge edge in rotation.Edges(step))
        {
            if (edge.IsDefault || (snapshot is not null && edge.When.All(c => Holds(c, snapshot))))
            {
                return edge;
            }
        }

        return rotation.DefaultEdge(step);
    }

    private bool TryComplete(Observation observation, HudSnapshot snapshot, List<RotationStep> completed)
    {
        // The step at hand, or another way out of the step before it.
        IEnumerable<RotationCursor> here = from is RotationCursor done
            ? [cursor, .. rotation.Edges(done.Step).Select(e => Follow(done, e)).Where(c => c.Step != cursor.Step)]
            : [cursor];

        foreach (RotationCursor at in here)
        {
            if (Explains(rotation.Steps[at.Step].Action, observation))
            {
                completed.Add(rotation.Steps[at.Step]);
                Complete(at, snapshot);
                return true;
            }
        }

        if (!observation.NamesItsSlot)
        {
            return false;
        }

        // A slot-specific input may be a few steps on, along any way forward.
        // Breadth first, so the nearest match wins.
        var frontier = new List<(RotationCursor At, List<int> Path)> { (cursor, []) };
        for (int depth = 1; depth <= options.LookAhead; depth++)
        {
            var next = new List<(RotationCursor, List<int>)>();
            foreach ((RotationCursor at, List<int> path) in frontier)
            {
                foreach (RotationEdge edge in rotation.Edges(at.Step))
                {
                    RotationCursor onward = Follow(at, edge);
                    List<int> walked = [.. path, at.Step];

                    if (Explains(rotation.Steps[onward.Step].Action, observation))
                    {
                        completed.AddRange(walked.Select(i => rotation.Steps[i]));
                        completed.Add(rotation.Steps[onward.Step]);
                        Complete(onward, snapshot);
                        return true;
                    }

                    next.Add((onward, walked));
                }
            }

            frontier = next;
        }

        return false;
    }

    /// <summary>
    /// Lays the remembered inputs against every step and moves the cursor to the
    /// best fit. Longer fits win; on a tie the current step wins (so a player who
    /// is actually on track is never moved), then the step nearest ahead of the
    /// cursor, then the earliest in the file.
    /// </summary>
    private bool TryResync()
    {
        Dictionary<int, (int Distance, bool NewRound)> ahead = Ahead(cursor.Step);
        int? best = null;
        int bestLength = 0;

        for (int candidate = 0; candidate < rotation.Steps.Count; candidate++)
        {
            (int length, bool named) = Fit(candidate, history.Count - 1, 0);
            if (length < ResyncMinimumLength || !named)
            {
                continue;
            }

            if (best is not int current || length > bestLength || (length == bestLength && Rank(candidate).CompareTo(Rank(current)) < 0))
            {
                best = candidate;
                bestLength = length;
            }
        }

        if (best is not int target || target == cursor.Step)
        {
            return false;
        }

        int round = ahead.TryGetValue(target, out var way)
            ? (way.NewRound ? cursor.Round + 1 : cursor.Round)
            : 1;

        cursor = new RotationCursor(target, round);
        from = null;
        history.Clear();
        return true;

        ValueTuple<int, int> Rank(int step) =>
            step == cursor.Step ? (0, 0)
            : ahead.TryGetValue(step, out var a) ? (1, a.Distance)
            : (2, step);
    }

    /// <summary>
    /// The longest run of remembered inputs, newest first, that are exactly the
    /// steps leading into <paramref name="step"/> along some way through the
    /// rotation -- and whether that run named a slot.
    /// </summary>
    private (int Length, bool Named) Fit(int step, int newest, int depth)
    {
        if (newest < 0 || depth > 64)
        {
            return (0, false);
        }

        Observation seen = history[newest].Seen;
        (int Length, bool Named) best = (0, false);

        for (int before = 0; before < rotation.Steps.Count; before++)
        {
            if (!rotation.Edges(before).Any(e => e.To == step) || !Explains(rotation.Steps[before].Action, seen))
            {
                continue;
            }

            (int length, bool named) = Fit(before, newest - 1, depth + 1);
            (int Length, bool Named) here = (length + 1, named || seen.NamesItsSlot);
            if (here.Length > best.Length || (here.Length == best.Length && here.Named && !best.Named))
            {
                best = here;
            }
        }

        return best;
    }

    /// <summary>Every step reachable from <paramref name="start"/>: how far, and whether the shortest way starts a new round.</summary>
    private Dictionary<int, (int Distance, bool NewRound)> Ahead(int start)
    {
        var found = new Dictionary<int, (int, bool)>();
        var queue = new Queue<(int Step, int Distance, bool NewRound)>([(start, 0, false)]);
        while (queue.TryDequeue(out var at))
        {
            foreach (RotationEdge edge in rotation.Edges(at.Step))
            {
                if (edge.To != start && !found.ContainsKey(edge.To))
                {
                    var entry = (at.Distance + 1, at.NewRound || edge.NewRound);
                    found[edge.To] = entry;
                    queue.Enqueue((edge.To, entry.Item1, entry.Item2));
                }
            }
        }

        return found;
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

    private static bool Explains(RotationAction action, Observation observation) => (action.Kind, observation.Kind) switch
    {
        (ActionKind.Skill, ObservedKind.SkillSpent) => true,
        (ActionKind.Chain, ObservedKind.ChainUsed) => action.Slot == observation.Slot,
        (ActionKind.Ultimate, ObservedKind.UltimateUsed) => action.Slot == observation.Slot,
        _ => false,
    };

    private RotationState Describe(
        HudSnapshot? snapshot,
        bool stale,
        IReadOnlyList<RotationStep> completed,
        IReadOnlyList<Observation> unexpected)
    {
        StepView View(int step) => new(
            rotation.Steps[step],
            [.. rotation.Steps[step].When.Select(c => new ConditionStatus(c, snapshot is not null && Holds(c, snapshot)))]);

        var upcoming = new List<StepView>();
        for (int k = 0, at = cursor.Step; k < options.Upcoming; k++)
        {
            at = Suggested(at, snapshot).To;
            upcoming.Add(View(at));
        }

        IReadOnlyList<int> segment = rotation.SegmentSteps(cursor.Step);
        return new RotationState
        {
            Cursor = cursor,
            Current = View(cursor.Step),
            Next = upcoming,
            SegmentName = rotation.SegmentName(cursor.Step),
            Position = segment.TakeWhile(i => i != cursor.Step).Count() + 1,
            SegmentLength = segment.Count,
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
        ConditionKind.PromptLeads => snapshot.Prompts.Count > 0 && snapshot.Prompts[0].Slot == condition.Value,
        _ => false,
    };
}
