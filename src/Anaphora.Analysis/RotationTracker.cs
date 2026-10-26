using Anaphora.Core;

namespace Anaphora.Analysis;

/// <summary>Where in the rotation the player is: the step to do next, and which round of the fight.</summary>
public readonly record struct RotationCursor(int Step, int Round);

/// <summary>
/// A cast the tracker has resolved to a slot: a <see cref="CastInput"/> whose
/// chain has been given the slot the prompt said E would trigger.
/// </summary>
/// <param name="Slot">1-4, or 0 for a chain whose prompt named nobody.</param>
public readonly record struct Observation(CastKind Kind, int Slot)
{
    public override string ToString() => Kind switch
    {
        CastKind.Skill => $"{Slot} 技能",
        CastKind.Chain when Slot is >= RotationAction.MinSlot and <= RotationAction.MaxSlot => $"{Slot}E 连携",
        CastKind.Chain => "E 连携（槽位未知）",
        CastKind.Ultimate => $"{Slot} 大招",
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

    /// <summary>
    /// Every step of the segment the cursor is in, in order. This is what the
    /// overlay's rotation strip draws: one segment at a time, with the cursor
    /// highlighted at <see cref="Position"/>. How long it is, is the user's
    /// editing decision, so nothing here truncates it.
    /// </summary>
    public IReadOnlyList<StepView> Segment { get; init; } = [];

    /// <summary>The name of the current step's segment: "开局", "循环".</summary>
    public string SegmentName { get; init; } = string.Empty;

    /// <summary>1-based position of the current step in its segment, for "4/7" style progress.</summary>
    public int Position { get; init; }

    public int SegmentLength { get; init; }

    /// <summary>
    /// The HUD is not readable. The cursor still follows the keys; the readings
    /// behind its conditions are the last readable ones.
    /// </summary>
    public bool Stale { get; init; }

    /// <summary>Steps this update recognised as done, oldest first.</summary>
    public IReadOnlyList<RotationStep> Completed { get; init; } = [];

    /// <summary>Keys no nearby step explains -- the player pressed something else.</summary>
    public IReadOnlyList<Observation> Unexpected { get; init; } = [];

    /// <summary>
    /// This update moved the cursor to where the player's recent inputs actually
    /// fit, rather than one step on from where it was.
    /// </summary>
    public bool Resynced { get; init; }
}

public sealed record RotationTrackerOptions
{
    /// <summary>How many steps after the current one to show.</summary>
    public int Upcoming { get; init; } = 3;

    /// <summary>
    /// With neither a readable HUD nor a key for this long, the fight is taken
    /// to be over and the next one starts at the first step. Both halves matter:
    /// a cut-in hides the HUD for seconds at exactly the moment the player is
    /// busiest, so absence alone must never restart the rotation.
    /// </summary>
    public TimeSpan ResetAfterAbsence { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How far back observed inputs are remembered when working out where a
    /// player who has left the rotation has rejoined it.
    /// </summary>
    public TimeSpan ResyncWindow { get; init; } = TimeSpan.FromSeconds(12);
}

/// <summary>
/// Follows a rotation from the keys the player actually pressed.
///
/// The keyboard is the only thing that completes a step. The HUD cannot do it:
/// a skill point is shared by the whole team, so "a point went" never says
/// whose skill it was, and the player pressing the wrong slot has to leave the
/// cursor where it is. Chains and ultimates do name a slot on screen, but only
/// as an edge between two readable frames -- and the frames around a cast are
/// the ones a cut-in eats. So the readings are used for what is possible right
/// now (skill points, cooldowns, ultimate rings, prompts) and for naming the
/// slot behind a press of E, and for nothing else.
///
/// Soft cues -- heavy attacks, the Nth attack after a skill -- have no key of
/// their own and are never waited for; they ride along with their step.
///
/// The rotation is a state machine. After a step, the cursor goes the way the
/// branch conditions point, and keeps re-deciding while the player has not moved
/// on, since the HUD may change its mind (a chain prompt appearing, say). What
/// the player actually does wins: a cast matching another way out of the last
/// step is taken as that way.
///
/// When the player leaves the rotation and starts over mid-fight, stepping
/// forward from the old cursor can never catch up. So whenever a cast no nearby
/// step explains arrives, the recent inputs are laid against every step, and the
/// cursor moves to wherever they fit -- provided the fit is long enough that a
/// stray press is not mistaken for a restart.
/// </summary>
public sealed class RotationTracker
{
    /// <summary>A realignment needs this many consecutive inputs to fit.</summary>
    private const int ResyncMinimumLength = 3;

    private readonly Rotation rotation;
    private readonly RotationTrackerOptions options;
    private readonly List<(Observation Seen, TimeSpan At)> history = [];

    private RotationCursor cursor;

    /// <summary>The step last completed, while the way on from it is still being decided.</summary>
    private RotationCursor? from;

    /// <summary>The last frame the HUD could be read in, for conditions and prompts.</summary>
    private HudSnapshot? readable;

    private TimeSpan lastPresent;
    private TimeSpan lastInput;
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

    /// <summary>The last moment there was any sign of a fight: a readable HUD, or a key.</summary>
    private TimeSpan Quiet => lastPresent > lastInput ? lastPresent : lastInput;

    /// <summary>
    /// Advances the rotation from one captured frame and the casts seen since
    /// the previous one. Casts are followed while the HUD is hidden by a cut-in
    /// too, because the press that caused the cut-in is exactly the one that
    /// must not be swallowed.
    /// </summary>
    /// <param name="timestamp">Any monotonic clock; only differences are used.</param>
    /// <param name="casts">Casts reported by the keyboard, oldest first.</param>
    public RotationState Observe(HudSnapshot snapshot, TimeSpan timestamp, IReadOnlyList<CastInput>? casts = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        bool fighting = !everPresent || timestamp - Quiet < options.ResetAfterAbsence;
        if (!fighting)
        {
            Restart();
        }

        if (snapshot.HudPresent)
        {
            everPresent = true;
            lastPresent = timestamp;
            readable = snapshot;
        }

        // Out of a fight, only the HUD coming back says the next one has
        // started: keys pressed in a menu or on the way there are not a
        // rotation. Inside one, every key counts, HUD or no HUD.
        IReadOnlyList<Observation> pending = fighting || snapshot.HudPresent
            ? Resolve(casts, readable)
            : [];

        if (pending.Count > 0)
        {
            lastInput = timestamp;
        }

        var completed = new List<RotationStep>();
        var unexpected = new List<Observation>();
        bool resynced = Process(pending, timestamp, readable, completed, unexpected);

        if (readable is not null)
        {
            Redecide(readable);
        }

        State = Describe(readable, !snapshot.HudPresent, completed, unexpected) with { Resynced = resynced };
        return State;
    }

    /// <summary>Back to the first step, as at the start of a fight.</summary>
    public void Reset()
    {
        Restart();
        State = Describe(null, stale: true, [], []);
    }

    /// <summary>Moves past the current step by hand -- for a step no key can confirm.</summary>
    public void Skip()
    {
        Complete(cursor, readable);
        State = Describe(readable, State.Stale, [], []);
    }

    /// <summary>Matches observations to steps, remembers them, and realigns if none fit.</summary>
    private bool Process(
        IReadOnlyList<Observation> pending,
        TimeSpan timestamp,
        HudSnapshot? snapshot,
        List<RotationStep> completed,
        List<Observation> unexpected)
    {
        if (pending.Count == 0)
        {
            return false;
        }

        // Match whatever can be matched, in whichever order the steps want
        // them: two casts drained in the same frame need not have happened in
        // the order they were queued.
        var remaining = new List<Observation>(pending);
        bool progress = true;
        while (progress && remaining.Count > 0)
        {
            progress = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                if (TryComplete(remaining[i], snapshot, completed))
                {
                    remaining.RemoveAt(i);
                    progress = true;
                    break;
                }
            }
        }

        // What is remembered for a realignment is the unbroken run of presses
        // no step explained. A press that did fit means the player is on the
        // rotation, so everything before it is spent evidence: leaving it in
        // would let two presses after a good one add up to a restart.
        if (remaining.Count < pending.Count)
        {
            history.Clear();
        }

        foreach (Observation seen in remaining)
        {
            history.Add((seen, timestamp));
        }

        history.RemoveAll(entry => timestamp - entry.At > options.ResyncWindow);

        unexpected.AddRange(remaining);
        return remaining.Count > 0 && TryResync();
    }

    /// <summary>
    /// Keyboard casts as tracker observations. A chain's slot comes from E's
    /// target -- the leading prompt -- when the key itself cannot name it.
    /// </summary>
    private static IReadOnlyList<Observation> Resolve(IReadOnlyList<CastInput>? casts, HudSnapshot? snapshot)
    {
        if (casts is not { Count: > 0 })
        {
            return [];
        }

        var seen = new List<Observation>(casts.Count);
        foreach (CastInput cast in casts)
        {
            seen.Add(new Observation(cast.Kind, cast.Kind == CastKind.Chain ? ChainSlot(cast, snapshot) : cast.Slot));
        }

        return seen;
    }

    private static int ChainSlot(CastInput cast, HudSnapshot? snapshot)
    {
        if (cast.Slot is >= RotationAction.MinSlot and <= RotationAction.MaxSlot)
        {
            return cast.Slot;
        }

        // E triggers whatever leads the prompt. This is the one place the
        // keyboard needs the picture; if that portrait is not registered, the
        // slot stays 0 and the tracker takes the nearest chain step.
        return snapshot is { Prompts.Count: > 0 } && snapshot.Prompts[0].Slot is int slot ? slot : 0;
    }

    private void Restart()
    {
        cursor = Start;
        from = null;
        readable = null;
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

    private bool TryComplete(Observation observation, HudSnapshot? snapshot, List<RotationStep> completed)
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

        // Nothing further ahead is tried. A cast that is not this step is the
        // player pressing something else, and the cursor staying put is the
        // whole point: walking forward to the next step that happens to match
        // the same slot would quietly reward a mispress. A player who really
        // has moved on is picked up by the realignment below, which wants
        // several inputs in a row before it believes it.
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
            int length = Fit(candidate, history.Count - 1, 0);
            if (length < ResyncMinimumLength)
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
    /// rotation.
    /// </summary>
    private int Fit(int step, int newest, int depth)
    {
        if (newest < 0 || depth > 64)
        {
            return 0;
        }

        Observation seen = history[newest].Seen;
        int best = 0;

        for (int before = 0; before < rotation.Steps.Count; before++)
        {
            if (!rotation.Edges(before).Any(e => e.To == step) || !Explains(rotation.Steps[before].Action, seen))
            {
                continue;
            }

            best = Math.Max(best, Fit(before, newest - 1, depth + 1) + 1);
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

    private static bool Explains(RotationAction action, Observation observation) => (action.Kind, observation.Kind) switch
    {
        (ActionKind.Skill, CastKind.Skill) => action.Slot == observation.Slot,
        (ActionKind.Chain, CastKind.Chain) => observation.Slot == 0 || action.Slot == observation.Slot,
        (ActionKind.Ultimate, CastKind.Ultimate) => action.Slot == observation.Slot,
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
            Segment = [.. segment.Select(View)],
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
