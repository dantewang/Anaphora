using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Anaphora.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anaphora.App.Editor;

/// <summary>
/// A rotation being edited. The Core records are immutable, which is right for
/// the tracker and wrong for a form, so the editor works on this mutable mirror
/// and converts back with <see cref="ToRotation"/>. Any change anywhere below
/// raises <see cref="Changed"/>, which is what redraws the graph and marks the
/// document dirty.
/// </summary>
internal sealed partial class RotationDocument : ObservableObject
{
    private int suspended;
    private bool refreshing;

    private const int UndoDepth = 200;

    private readonly Stack<Rotation> undo = new();
    private readonly Stack<Rotation> redo = new();
    private Rotation last;
    private bool restoring;

    private RotationDocument(Rotation source)
    {
        Id = source.Id;
        ProfileId = source.ProfileId;
        Notes = source.Notes;

        Segments.CollectionChanged += OnCollectionChanged;
        Steps.CollectionChanged += OnCollectionChanged;

        suspended++;
        Load(source);
        suspended--;

        RefreshOptions();
        last = ToRotation();
    }

    public bool CanUndo => undo.Count > 0;

    public bool CanRedo => redo.Count > 0;

    public void Undo()
    {
        if (undo.TryPop(out Rotation? previous))
        {
            redo.Push(last);
            Restore(previous);
        }
    }

    public void Redo()
    {
        if (redo.TryPop(out Rotation? next))
        {
            undo.Push(last);
            Restore(next);
        }
    }

    private void Restore(Rotation snapshot)
    {
        restoring = true;
        try
        {
            Batch(() => Load(snapshot));
        }
        finally
        {
            restoring = false;
        }
    }

    /// <summary>Replaces everything editable with <paramref name="source"/>; new items are watched as they are added.</summary>
    private void Load(Rotation source)
    {
        DisplayName = source.DisplayName;
        ControlledSlot = source.ControlledSlot;
        Segments.Clear();
        Steps.Clear();

        foreach (RotationSegment segment in source.Segments)
        {
            Segments.Add(new SegmentModel(segment.Id, segment.Name));
        }

        // Steps written without a segment get one, so every step has a lane.
        if (source.Steps.Any(s => s.Segment is null || Segments.All(g => g.Id != s.Segment)))
        {
            Segments.Add(new SegmentModel(FreeSegmentId(), Segments.Count == 0 ? "主线" : "其他"));
        }

        foreach (RotationStep step in source.Steps)
        {
            string segment = Segments.Any(g => g.Id == step.Segment) ? step.Segment! : Segments[^1].Id;
            Steps.Add(StepModel.From(step, segment, this));
        }
    }

    /// <summary>Anything in the document changed: a property, a step, a cue.</summary>
    public event Action? Changed;

    public string Id { get; }

    public string ProfileId { get; }

    public string? Notes { get; }

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int ControlledSlot { get; set; }

    public ObservableCollection<SegmentModel> Segments { get; } = [];

    public ObservableCollection<StepModel> Steps { get; } = [];

    /// <summary>Every step as a combo box offers it, in order.</summary>
    public ObservableCollection<StepOption> Options { get; } = [];

    public static RotationDocument From(Rotation rotation) => new(rotation);

    public static RotationDocument New(string id, string profileId) => new(new Rotation
    {
        Id = id,
        DisplayName = "新的轴",
        ProfileId = profileId,
        Segments = [new RotationSegment("main", "主线")],
        Steps = [new RotationStep { Id = "s1", Segment = "main", Action = new RotationAction(1, ActionKind.Skill) }],
    });

    public Rotation ToRotation() => new()
    {
        Id = Id,
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName.Trim(),
        ProfileId = ProfileId,
        ControlledSlot = ControlledSlot,
        Notes = Notes,
        Segments = [.. Segments.Select(s => new RotationSegment(s.Id, s.Name))],
        Steps = [.. Steps.Select(s => s.ToStep())],
    };

    public IEnumerable<StepModel> StepsIn(SegmentModel segment) => Steps.Where(s => s.SegmentId == segment.Id);

    public SegmentModel SegmentOf(StepModel step) => Segments.First(s => s.Id == step.SegmentId);

    public StepModel? Find(string? id) => id is null ? null : Steps.FirstOrDefault(s => s.Id == id);

    /// <summary>Where a step goes when no branch is taken, as the tracker will see it.</summary>
    public (StepModel To, bool NewRound) DefaultNext(StepModel step)
    {
        Rotation rotation = ToRotation();
        RotationEdge edge = rotation.DefaultEdge(Steps.IndexOf(step));
        return (Steps[edge.To], edge.NewRound);
    }

    /// <summary>"开局 第4步（1E）" -- how a step is named in lists.</summary>
    public string Label(StepModel step)
    {
        SegmentModel segment = SegmentOf(step);
        int position = StepsIn(segment).TakeWhile(s => s != step).Count() + 1;
        return string.Create(CultureInfo.InvariantCulture, $"{segment.Name} 第{position}步（{step.Action}）");
    }

    public string FreeStepId()
    {
        for (int n = Steps.Count + 1; ; n++)
        {
            string id = string.Create(CultureInfo.InvariantCulture, $"s{n}");
            if (Find(id) is null)
            {
                return id;
            }
        }
    }

    public string FreeSegmentId()
    {
        for (int n = Segments.Count + 1; ; n++)
        {
            string id = string.Create(CultureInfo.InvariantCulture, $"seg{n}");
            if (Segments.All(s => s.Id != id))
            {
                return id;
            }
        }
    }

    /// <summary>Inserts a new step right after <paramref name="after"/>, in its segment.</summary>
    public StepModel InsertAfter(StepModel after)
    {
        var step = new StepModel(this, FreeStepId(), after.SegmentId, new RotationAction(ControlledSlot, ActionKind.Skill));
        Batch(() =>
        {
            // The new step takes over where the old one went.
            step.NextId = after.NextId;
            step.NewRound = after.NewRound;
            after.NextId = null;
            after.NewRound = false;
            Steps.Insert(Steps.IndexOf(after) + 1, step);
        });
        return step;
    }

    /// <returns>The step to select afterwards, or null when the step is the last one and was kept.</returns>
    public StepModel? Remove(StepModel step)
    {
        if (Steps.Count == 1)
        {
            return null;
        }

        int index = Steps.IndexOf(step);
        (StepModel next, bool newRound) = DefaultNext(step);
        StepModel? heir = next == step ? null : next;

        Batch(() =>
        {
            // Whatever pointed at the step now points where the step went.
            foreach (StepModel other in Steps)
            {
                if (other.NextId == step.Id)
                {
                    other.NextId = heir?.Id;
                    other.NewRound |= newRound;
                }

                foreach (BranchModel branch in other.Branches.Where(b => b.To == step.Id).ToList())
                {
                    if (heir is null)
                    {
                        other.Branches.Remove(branch);
                    }
                    else
                    {
                        branch.To = heir.Id;
                    }
                }
            }

            // A step that relied on "the following step" keeps its target.
            if (index > 0 && Steps[index - 1].NextId is null && Steps[index - 1].SegmentId == step.SegmentId && step.NextId is not null)
            {
                Steps[index - 1].NextId = step.NextId;
                Steps[index - 1].NewRound = step.NewRound;
            }

            Steps.Remove(step);

            SegmentModel? segment = Segments.FirstOrDefault(s => s.Id == step.SegmentId);
            if (segment is not null && !StepsIn(segment).Any() && Segments.Count > 1)
            {
                Segments.Remove(segment);
            }
        });

        return Steps[Math.Min(index, Steps.Count - 1)];
    }

    /// <summary>
    /// Cuts a segment in two at <paramref name="at"/>: that step and everything
    /// after it in the same segment move into a new segment, which takes its
    /// place right after the old one.
    /// </summary>
    /// <returns>The new segment, or null when the step already starts its segment.</returns>
    public SegmentModel? SplitAt(StepModel at)
    {
        SegmentModel old = SegmentOf(at);
        List<StepModel> moving = [.. StepsIn(old).SkipWhile(s => s != at)];
        if (moving.Count == 0 || StepsIn(old).First() == at)
        {
            return null;
        }

        var fresh = new SegmentModel(FreeSegmentId(), "新的一段");
        Batch(() =>
        {
            Pin();
            Segments.Insert(Segments.IndexOf(old) + 1, fresh);
            foreach (StepModel step in moving)
            {
                step.SegmentId = fresh.Id;
            }

            Unpin();
        });

        return fresh;
    }

    /// <summary>
    /// Moves a step into another segment, at the end of that segment's run.
    /// Where it and its neighbours go is unchanged.
    /// </summary>
    public bool MoveToSegment(StepModel step, SegmentModel target)
    {
        if (step.SegmentId == target.Id)
        {
            return false;
        }

        Batch(() =>
        {
            Pin();
            step.SegmentId = target.Id;

            List<StepModel> order = [.. Steps];
            order.Remove(step);
            int last = order.FindLastIndex(s => s.SegmentId == target.Id);
            order.Insert(last < 0 ? order.Count : last + 1, step);
            Reorder(order);

            Unpin();
            DropEmptySegments();
        });

        return true;
    }

    /// <summary>Moves a segment, and the steps in it, one place up or down.</summary>
    public bool MoveSegment(SegmentModel segment, int delta)
    {
        int from = Segments.IndexOf(segment);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= Segments.Count)
        {
            return false;
        }

        Batch(() =>
        {
            Pin();
            Segments.Move(from, to);
            Reorder([.. Segments.SelectMany(StepsIn)]);
            Unpin();
        });

        return true;
    }

    /// <summary>Deletes a segment and every step in it; refuses to empty the rotation.</summary>
    public bool RemoveSegment(SegmentModel segment)
    {
        List<StepModel> doomed = [.. StepsIn(segment)];
        if (Segments.Count < 2 || doomed.Count == 0 || doomed.Count >= Steps.Count)
        {
            return false;
        }

        // Remove() already repoints whatever pointed at each step, and drops the
        // segment once its last step is gone.
        Batch(() =>
        {
            foreach (StepModel step in doomed)
            {
                Remove(step);
            }
        });

        return true;
    }

    /// <summary>
    /// Writes every implicit "the step after this one" edge out in full.
    ///
    /// That edge means "the next step in the same segment, or back to the start
    /// as a new round", so it changes meaning the moment steps move between
    /// segments. Rearrangements pin the rotation first and unpin it after, which
    /// leaves every edge pointing where it pointed before.
    /// </summary>
    private void Pin()
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].NextId is not null)
            {
                continue;
            }

            (StepModel to, bool newRound) = Positional(i);
            Steps[i].NextId = to.Id;
            Steps[i].NewRound = newRound;
        }
    }

    /// <summary>Drops the explicit edges that now say exactly what the default says.</summary>
    private void Unpin()
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            (StepModel to, bool newRound) = Positional(i);
            if (Steps[i].NextId == to.Id && Steps[i].NewRound == newRound)
            {
                Steps[i].NextId = null;
                Steps[i].NewRound = false;
            }
        }
    }

    /// <summary>Where step <paramref name="index"/> goes with no explicit next, by position alone.</summary>
    private (StepModel To, bool NewRound) Positional(int index) =>
        index + 1 < Steps.Count && Steps[index + 1].SegmentId == Steps[index].SegmentId
            ? (Steps[index + 1], false)
            : (Steps[0], true);

    /// <summary>Rearranges <see cref="Steps"/> to match <paramref name="order"/>, by moves only.</summary>
    private void Reorder(IReadOnlyList<StepModel> order)
    {
        for (int i = 0; i < order.Count; i++)
        {
            int at = Steps.IndexOf(order[i]);
            if (at != i)
            {
                Steps.Move(at, i);
            }
        }
    }

    private void DropEmptySegments()
    {
        foreach (SegmentModel segment in Segments.Where(s => !StepsIn(s).Any()).ToList())
        {
            if (Segments.Count > 1)
            {
                Segments.Remove(segment);
            }
        }
    }

    /// <summary>A new lane with one step in it, reached as a branch from <paramref name="from"/>.</summary>
    public StepModel AddSegment(StepModel from)
    {
        var segment = new SegmentModel(FreeSegmentId(), "分支");
        var step = new StepModel(this, FreeStepId(), segment.Id, new RotationAction(ControlledSlot, ActionKind.Skill));

        Batch(() =>
        {
            Segments.Add(segment);
            (StepModel afterFrom, _) = DefaultNext(from);
            step.NextId = afterFrom.Id;
            step.NewRound = afterFrom == Steps[0];
            Steps.Add(step);
            from.Branches.Add(new BranchModel(new ConditionModel(ConditionKind.PromptLeads, step.Action.Slot), step.Id));
        });

        return step;
    }

    /// <summary>
    /// Replaces a segment's steps with the one-line form. Existing steps keep
    /// their ids, conditions, notes and branches position by position, so
    /// retyping a line does not lose what was set up around it; the segment's
    /// last step keeps the old last step's way out.
    /// </summary>
    public void ReplaceSegment(SegmentModel segment, IReadOnlyList<RotationText.Entry> entries)
    {
        List<StepModel> old = [.. StepsIn(segment)];
        (string? lastNext, bool lastNewRound) = (old[^1].NextId, old[^1].NewRound);

        Batch(() =>
        {
            var fresh = new List<StepModel>();
            for (int i = 0; i < entries.Count; i++)
            {
                bool reused = i < old.Count;
                StepModel step = reused ? old[i] : new StepModel(this, FreeStepId(), segment.Id, entries[i].Action);

                step.Action = entries[i].Action;
                step.Cues.Clear();
                foreach (SoftCue cue in entries[i].Before)
                {
                    step.Cues.Add(CueModel.From(cue));
                }

                if (i == entries.Count - 1)
                {
                    step.NextId = lastNext;
                    step.NewRound = lastNewRound;
                }
                else if (i == old.Count - 1)
                {
                    // The old last step now has steps after it: let it run on into them.
                    step.NextId = null;
                }

                if (!reused)
                {
                    Steps.Insert(Steps.IndexOf(fresh[^1]) + 1, step);
                }

                fresh.Add(step);
            }

            foreach (StepModel dropped in old.Skip(entries.Count))
            {
                foreach (StepModel other in Steps)
                {
                    if (other.NextId == dropped.Id)
                    {
                        other.NextId = fresh[0].Id;
                    }

                    foreach (BranchModel branch in other.Branches.Where(b => b.To == dropped.Id))
                    {
                        branch.To = fresh[0].Id;
                    }
                }

                Steps.Remove(dropped);
            }
        });
    }

    /// <summary>Makes many edits count as one change.</summary>
    public void Batch(Action edit)
    {
        suspended++;
        try
        {
            edit();
        }
        finally
        {
            suspended--;
        }

        Raise();
    }

    internal void Raise()
    {
        if (suspended == 0 && !refreshing)
        {
            RefreshOptions();
            Rotation now = ToRotation();
            if (!now.Equals(last))
            {
                if (!restoring)
                {
                    undo.Push(last);
                    redo.Clear();
                    if (undo.Count > UndoDepth)
                    {
                        Rotation[] kept = [.. undo.Take(UndoDepth)];
                        undo.Clear();
                        foreach (Rotation snapshot in kept.Reverse())
                        {
                            undo.Push(snapshot);
                        }
                    }
                }

                last = now;
            }

            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Keeps <see cref="Options"/> in step with the steps' names. Combo boxes
    /// drop their selection when their items are replaced, so every branch is
    /// told to show its target again afterwards.
    /// </summary>
    private void RefreshOptions()
    {
        List<StepOption> fresh = [.. Steps.Select(s => new StepOption(s.Id, Label(s)))];
        if (fresh.SequenceEqual(Options))
        {
            return;
        }

        refreshing = true;
        try
        {
            Options.Clear();
            foreach (StepOption option in fresh)
            {
                Options.Add(option);
            }

            foreach (BranchModel branch in Steps.SelectMany(s => s.Branches))
            {
                branch.Reselect();
            }
        }
        finally
        {
            refreshing = false;
        }
    }

    internal void Watch(INotifyPropertyChanged item)
    {
        item.PropertyChanged += (_, _) => Raise();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        Raise();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (INotifyPropertyChanged item in e.NewItems?.OfType<INotifyPropertyChanged>() ?? [])
        {
            Watch(item);
        }

        Raise();
    }
}

internal sealed partial class SegmentModel(string id, string name) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty]
    public partial string Name { get; set; } = name;

    public override string ToString() => Name;
}

/// <summary>One step of the rotation, as the inspector edits it.</summary>
internal sealed partial class StepModel : ObservableObject
{
    private readonly RotationDocument document;

    public StepModel(RotationDocument document, string id, string segmentId, RotationAction action)
    {
        this.document = document;
        Id = id;
        SegmentId = segmentId;
        Action = action;

        Cues.CollectionChanged += OnListChanged;
        Waits.CollectionChanged += OnListChanged;
        Branches.CollectionChanged += OnListChanged;
    }

    public string Id { get; }

    /// <summary>
    /// Which segment draws this step. Setting it is what the inspector's
    /// "所属段" does; go through <see cref="RotationDocument.MoveToSegment"/>
    /// instead, which also keeps the segment's steps together and its edges put.
    /// </summary>
    [ObservableProperty]
    public partial string SegmentId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Slot), nameof(Kind), nameof(KeyText), nameof(IsSkill), nameof(IsChain), nameof(IsUltimate))]
    public partial RotationAction Action { get; set; }

    /// <summary>Null: the following step in the segment, or back to the start after the last one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextChoice), nameof(HasExplicitNext))]
    public partial string? NextId { get; set; }

    /// <summary>What a combo box offers for "no explicit next": an empty string does not survive selection.</summary>
    public const string DefaultChoice = "(default)";

    /// <summary><see cref="NextId"/> for a combo box, with <see cref="DefaultChoice"/> standing for the default way.</summary>
    public string NextChoice
    {
        get => NextId ?? DefaultChoice;
        set => NextId = value is null or DefaultChoice ? null : value;
    }

    public bool HasExplicitNext => NextId is not null;

    [ObservableProperty]
    public partial bool NewRound { get; set; }

    [ObservableProperty]
    public partial string? Note { get; set; }

    public ObservableCollection<CueModel> Cues { get; } = [];

    public ObservableCollection<ConditionModel> Waits { get; } = [];

    public ObservableCollection<BranchModel> Branches { get; } = [];

    public int Slot
    {
        get => Action.Slot;
        set => Action = Action with { Slot = value };
    }

    public ActionKind Kind
    {
        get => Action.Kind;
        set => Action = Action with { Kind = value };
    }

    public bool IsSkill
    {
        get => Kind == ActionKind.Skill;
        set
        {
            if (value)
            {
                Kind = ActionKind.Skill;
            }
        }
    }

    public bool IsChain
    {
        get => Kind == ActionKind.Chain;
        set
        {
            if (value)
            {
                Kind = ActionKind.Chain;
            }
        }
    }

    public bool IsUltimate
    {
        get => Kind == ActionKind.Ultimate;
        set
        {
            if (value)
            {
                Kind = ActionKind.Ultimate;
            }
        }
    }

    public string KeyText => Kind == ActionKind.Chain
        ? $"{Action.KeyText}，{Slot} 的头像在连携提示最左边时"
        : Action.KeyText;

    public static StepModel From(RotationStep step, string segmentId, RotationDocument document)
    {
        var model = new StepModel(document, step.Id, segmentId, step.Action)
        {
            NextId = step.Next,
            NewRound = step.NewRound,
            Note = step.Note,
        };

        foreach (SoftCue cue in step.Before)
        {
            model.Cues.Add(CueModel.From(cue));
        }

        foreach (StepCondition condition in step.When)
        {
            model.Waits.Add(new ConditionModel(condition.Kind, condition.Value));
        }

        foreach (RotationBranch branch in step.Branches)
        {
            model.Branches.Add(BranchModel.From(branch));
        }

        return model;
    }

    public RotationStep ToStep() => new()
    {
        Id = Id,
        Segment = SegmentId,
        Action = Action,
        Before = [.. Cues.Select(c => c.ToCue())],
        When = [.. Waits.Select(w => w.ToCondition())],
        Next = NextId,
        NewRound = NextId is not null && NewRound,
        Branches = [.. Branches.Select(b => b.ToBranch())],
        Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(),
    };

    [RelayCommand]
    private void SetSlot(string slot) => Slot = int.Parse(slot, CultureInfo.InvariantCulture);

    [RelayCommand]
    private void AddCue(string kind) => Cues.Add(kind switch
    {
        "heavy" => new CueModel(CueKind.Heavy) { Slot = document.ControlledSlot },
        "attack" => new CueModel(CueKind.AttackAfterSkill) { Slot = document.ControlledSlot, Count = 2 },
        _ => new CueModel(CueKind.Text) { Text = "提示" },
    });

    [RelayCommand]
    private void RemoveCue(CueModel cue) => Cues.Remove(cue);

    [RelayCommand]
    private void AddWait(string kind) => Waits.Add(kind switch
    {
        "sp" => new ConditionModel(ConditionKind.SkillPoints, 1),
        "chain" => new ConditionModel(ConditionKind.ChainReady, Slot),
        "ult" => new ConditionModel(ConditionKind.UltimateReady, Slot),
        _ => new ConditionModel(ConditionKind.PromptLeads, Slot),
    });

    [RelayCommand]
    private void RemoveWait(ConditionModel wait) => Waits.Remove(wait);

    [RelayCommand]
    private void AddBranch()
    {
        StepModel target = document.Steps.FirstOrDefault(s => s != this && s != document.DefaultNext(this).To) ?? document.Steps[0];
        Branches.Add(new BranchModel(new ConditionModel(ConditionKind.PromptLeads, target.Slot), target.Id));
    }

    [RelayCommand]
    private void RemoveBranch(BranchModel branch) => Branches.Remove(branch);

    partial void OnNextIdChanged(string? value)
    {
        if (value is null)
        {
            NewRound = false;
        }
    }

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (INotifyPropertyChanged item in e.NewItems?.OfType<INotifyPropertyChanged>() ?? [])
        {
            document.Watch(item);
            if (item is ListItemModel entry && sender is System.Collections.IList list)
            {
                entry.Removal = () => list.Remove(entry);
            }

            if (item is BranchModel owned)
            {
                owned.Owner = document;
            }

            if (item is BranchModel branch)
            {
                document.Watch(branch.Condition);
            }
        }

        document.Raise();
    }
}

internal sealed partial class CueModel(CueKind kind) : ListItemModel
{
    public CueKind Kind { get; } = kind;

    public bool IsHeavy => Kind == CueKind.Heavy;

    public bool IsAttack => Kind == CueKind.AttackAfterSkill;

    public bool IsText => Kind == CueKind.Text;

    [ObservableProperty]
    public partial int Slot { get; set; } = 1;

    [ObservableProperty]
    public partial int Count { get; set; } = 1;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    public static CueModel From(SoftCue cue) => new(cue.Kind)
    {
        Slot = Math.Max(cue.Slot, 1),
        Count = Math.Max(cue.Count, 1),
        Text = cue.Text ?? string.Empty,
    };

    public SoftCue ToCue() => Kind switch
    {
        CueKind.Heavy => SoftCue.Heavy(Slot),
        CueKind.AttackAfterSkill => SoftCue.AttackAfterSkill(Slot, Count),
        _ => SoftCue.Say(Text.Trim()),
    };
}

internal sealed partial class ConditionModel(ConditionKind kind, int value) : ListItemModel
{
    public static IReadOnlyList<ConditionChoice> Choices { get; } =
    [
        new(ConditionKind.SkillPoints, "技能点 ≥"),
        new(ConditionKind.ChainReady, "连携就绪"),
        new(ConditionKind.UltimateReady, "大招就绪"),
        new(ConditionKind.PromptLeads, "连携提示在前"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Choice), nameof(Maximum), nameof(Values), nameof(ValueLabel))]
    public partial ConditionKind Kind { get; set; } = kind;

    [ObservableProperty]
    public partial int Value { get; set; } = value;

    public ConditionChoice Choice
    {
        get => Choices.First(c => c.Kind == Kind);
        set
        {
            if (value is not null)
            {
                Kind = value.Kind;
                Value = Math.Clamp(Value, 1, Maximum);
            }
        }
    }

    public int Maximum => Kind == ConditionKind.SkillPoints ? 3 : RotationAction.MaxSlot;

    public IReadOnlyList<int> Values => [.. Enumerable.Range(1, Maximum)];

    public string ValueLabel => Kind == ConditionKind.SkillPoints ? "点" : "号位";

    public StepCondition ToCondition() => new(Kind, Value);
}

internal sealed record ConditionChoice(ConditionKind Kind, string Label)
{
    public override string ToString() => Label;
}

internal sealed partial class BranchModel(ConditionModel condition, string to) : ListItemModel
{
    /// <summary>Conditions beyond the first, kept as they were: the form edits one.</summary>
    private IReadOnlyList<StepCondition> extra = [];

    public ConditionModel Condition { get; } = condition;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Target))]
    public partial string To { get; set; } = to;

    internal RotationDocument? Owner { get; set; }

    public IReadOnlyList<StepOption> Options => Owner?.Options ?? (IReadOnlyList<StepOption>)[];

    /// <summary>The target as a combo box item. A combo box clearing itself writes null, which is ignored.</summary>
    public StepOption? Target
    {
        get => Options.FirstOrDefault(o => o.Id == To);
        set
        {
            if (value is not null)
            {
                To = value.Id;
            }
        }
    }

    internal void Reselect() => OnPropertyChanged(nameof(Target));

    public static BranchModel From(RotationBranch branch)
    {
        StepCondition first = branch.When.Count > 0 ? branch.When[0] : new StepCondition(ConditionKind.PromptLeads, 1);
        return new BranchModel(new ConditionModel(first.Kind, first.Value), branch.To) { extra = [.. branch.When.Skip(1)] };
    }

    public RotationBranch ToBranch() => new() { When = [Condition.ToCondition(), .. extra], To = To };
}

/// <summary>An entry in one of a step's lists, able to take itself out of it.</summary>
internal abstract partial class ListItemModel : ObservableObject
{
    public static IReadOnlyList<int> SlotChoices { get; } = [1, 2, 3, 4];

    public static IReadOnlyList<int> CountChoices { get; } = [1, 2, 3, 4, 5, 6, 7, 8, 9];

    internal Action? Removal { get; set; }

    [RelayCommand]
    private void Remove() => Removal?.Invoke();
}
