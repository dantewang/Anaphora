using System.Collections.ObjectModel;
using System.Text.Json;
using Anaphora.Analysis;
using Anaphora.Core;
using Anaphora.Overlay;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anaphora.App.Editor;

/// <summary>Where the window can reach files and the clipboard; kept out of the view model's way.</summary>
internal interface IFileAccess
{
    Task<string?> OpenTextAsync();

    Task<bool> SaveTextAsync(string suggestedName, string text);

    Task<string?> ReadClipboardAsync();

    Task WriteClipboardAsync(string text);
}

/// <summary>A rotation in the library list. Its editable document is made on first use and kept while the app runs.</summary>
internal sealed partial class LibraryItem : ObservableObject
{
    private RotationDocument? document;

    public LibraryItem(Rotation rotation, bool saved)
    {
        Saved = rotation;
        Dirty = !saved;
        Id = rotation.Id;
        Refresh(rotation);
    }

    public string Id { get; }

    /// <summary>What is on disk, or what was loaded.</summary>
    public Rotation Saved { get; private set; }

    public RotationDocument Document
    {
        get
        {
            if (document is null)
            {
                document = RotationDocument.From(Saved);
                document.Changed += () =>
                {
                    Dirty = true;
                    Refresh(document.ToRotation());
                };
            }

            return document;
        }
    }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<SlotMark> Slots { get; set; } = [];

    [ObservableProperty]
    public partial bool Dirty { get; set; }

    [ObservableProperty]
    public partial bool InUse { get; set; }

    public void MarkSaved(Rotation rotation)
    {
        Saved = rotation;
        Dirty = false;
    }

    private void Refresh(Rotation rotation)
    {
        Name = rotation.DisplayName;
        Summary = $"主控 {rotation.ControlledSlot} · {rotation.Steps.Count} 步";
        var used = rotation.Steps.Select(s => s.Action.Slot).ToHashSet();
        Slots = [.. Enumerable.Range(1, 4).Select(i => new SlotMark(i, used.Contains(i)))];
    }
}

internal sealed record SlotMark(int Slot, bool Used);

/// <summary>A step as a combo box offers it.</summary>
internal sealed record StepOption(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The main window: the rotation library, the editor, and the settings page.</summary>
internal sealed partial class MainViewModel : ObservableObject
{
    private readonly AnaphoraHost host;
    private IFileAccess? files;

    public MainViewModel(AnaphoraHost host)
    {
        this.host = host;
        Settings = new SettingsViewModel(host);

        foreach (LibraryEntry entry in host.Library.Load())
        {
            if (entry.Rotation is { } rotation)
            {
                Library.Add(new LibraryItem(rotation, saved: true));
            }
            else
            {
                Log.Write($"library file {entry.Path} skipped: {entry.Error}");
            }
        }

        host.RotationChanged += MarkInUse;
        MarkInUse();
        Selected = Library.FirstOrDefault(i => i.InUse) ?? Library.FirstOrDefault();
    }

    public SettingsViewModel Settings { get; }

    public ObservableCollection<LibraryItem> Library { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Document), nameof(HasSelection), nameof(UseLabel))]
    public partial LibraryItem? Selected { get; set; }

    public RotationDocument? Document => Selected?.Document;

    public bool HasSelection => Selected is not null;

    public string InUseName => host.ActiveRotation.DisplayName;

    public string UseLabel => Selected?.InUse == true ? "overlay 正在使用" : "设为 overlay 使用";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStep))]
    public partial StepModel? SelectedStep { get; set; }

    public bool HasStep => SelectedStep is not null;

    [ObservableProperty]
    public partial string StepTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<StepOption> NextOptions { get; set; } = [];

    /// <summary>The selected step's next step, as the combo box shows it.</summary>
    public StepOption? NextOption
    {
        get => NextOptions.FirstOrDefault(o => o.Id == SelectedStep?.NextChoice);
        set
        {
            if (value is not null && SelectedStep is { } step)
            {
                step.NextChoice = value.Id;
            }
        }
    }

    [ObservableProperty]
    public partial string DefaultNextText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SegmentModel? QuickSegment { get; set; }

    [ObservableProperty]
    public partial string QuickText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? QuickError { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    /// <summary>Problems in the rotation as it stands; errors stop it from being saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial string? Problems { get; set; }

    public bool HasProblems => !string.IsNullOrEmpty(Problems);

    [ObservableProperty]
    public partial string? Status { get; set; }

    /// <summary>The soft cues leading into the selected step, as the overlay draws them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewCues))]
    public partial IReadOnlyList<StepChip> PreviewCues { get; set; } = [];

    /// <summary>The key the overlay's title card would show at the selected step.</summary>
    [ObservableProperty]
    public partial PrimaryAction PreviewAction { get; set; } = PrimaryAction.Standby;

    [ObservableProperty]
    public partial IReadOnlyList<StepChip> PreviewNext { get; set; } = [];

    public bool HasPreviewCues => PreviewCues.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewHint))]
    public partial string? PreviewHint { get; set; }

    public bool HasPreviewHint => !string.IsNullOrEmpty(PreviewHint);

    public void AttachFiles(IFileAccess access) => files = access;

    partial void OnSelectedChanged(LibraryItem? oldValue, LibraryItem? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.Document.Changed -= OnDocumentChanged;
        }

        if (newValue is not null)
        {
            newValue.Document.Changed += OnDocumentChanged;
        }

        // Combo boxes bound to the document must see the new one before their selection changes.
        OnPropertyChanged(nameof(Document));
        QuickSegment = newValue?.Document.Segments.FirstOrDefault();
        SelectedStep = newValue?.Document.Steps.FirstOrDefault();
        QuickError = null;
        Status = null;
        OnDocumentChanged();
    }

    partial void OnSelectedStepChanged(StepModel? value) => RefreshStep();

    partial void OnQuickSegmentChanged(SegmentModel? value)
    {
        QuickText = value is null || Document is null
            ? string.Empty
            : RotationText.Format(Document.StepsIn(value).Select(s => s.ToStep()));
        QuickError = null;
    }

    private void OnDocumentChanged()
    {
        if (Document is not { } document)
        {
            Summary = string.Empty;
            Problems = null;
            return;
        }

        Rotation rotation = document.ToRotation();
        int cues = rotation.Steps.Sum(s => s.Before.Count);
        int branches = rotation.Steps.Sum(s => s.Branches.Count);
        Summary = $"{rotation.Steps.Count} 个操作 · {cues} 个软提示 · {branches} 个分支 · 用到槽位 {string.Join(' ', rotation.Steps.Select(s => s.Action.Slot).Distinct().Order())}";

        IReadOnlyList<ProfileProblem> problems = RotationValidator.Validate(rotation, host.Profile);
        Problems = problems.Count == 0 ? null : string.Join(Environment.NewLine, problems.Select(p => p.ToString()));

        if (SelectedStep is not null && !document.Steps.Contains(SelectedStep))
        {
            // Undo rebuilds the steps: keep the same one selected if it still exists.
            SelectedStep = document.Find(SelectedStep.Id) ?? document.Steps.FirstOrDefault();
        }

        if (QuickSegment is not null && !document.Segments.Contains(QuickSegment))
        {
            QuickSegment = document.Segments.FirstOrDefault(s => s.Id == QuickSegment.Id) ?? document.Segments.FirstOrDefault();
        }

        RefreshStep();
    }

    private void RefreshStep()
    {
        OnPropertyChanged(nameof(StepSegment));
        if (Document is not { } document || SelectedStep is not { } step)
        {
            PreviewCues = [];
            PreviewAction = PrimaryAction.Standby;
            PreviewNext = [];
            PreviewHint = null;
            return;
        }

        Rotation rotation = document.ToRotation();
        StepTitle = document.Label(step);

        (StepModel next, bool newRound) = document.DefaultNext(step);
        DefaultNextText = $"不指定时走到：{document.Label(next)}{(newRound ? "，算新的一轮" : string.Empty)}";

        List<StepOption> options = [new StepOption(StepModel.DefaultChoice, "（不指定：顺序下一步）"), .. document.Options];
        if (!options.SequenceEqual(NextOptions))
        {
            NextOptions = options;
        }

        // A combo box whose items were just replaced settles its selection later; reselect after that.
        OnPropertyChanged(nameof(NextOption));
        Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(NextOption)), Avalonia.Threading.DispatcherPriority.Background);

        // What the overlay would show at this step, going the default way.
        int index = document.Steps.IndexOf(step);
        RotationStep current = rotation.Steps[index];
        PreviewCues = [.. current.Before.Select(c => StepChip.From(c, false))];
        PreviewAction = PrimaryAction.From(new StepView(current, []), string.Empty);

        var upcoming = new List<StepChip>();
        for (int k = 0, at = index; k < 3; k++)
        {
            at = rotation.DefaultEdge(at).To;
            upcoming.AddRange(StepChip.Sequence(new StepView(rotation.Steps[at], [])));
        }

        PreviewNext = upcoming;
        string waits = string.Join("，", current.When.Select(w => w.Description));
        PreviewHint = (waits.Length, current.Note) switch
        {
            (> 0, { } note) => $"等待 {waits} · {note}",
            (> 0, null) => $"等待 {waits}",
            (_, { } note) => note,
            _ => null,
        };
    }

    [RelayCommand]
    private void ApplyQuick()
    {
        if (Document is not { } document || QuickSegment is not { } segment)
        {
            return;
        }

        try
        {
            document.ReplaceSegment(segment, RotationText.Parse(QuickText));
            QuickError = null;
            OnQuickSegmentChanged(segment);
        }
        catch (FormatException ex)
        {
            QuickError = ex.Message;
        }
    }

    [RelayCommand]
    private void Undo()
    {
        Document?.Undo();
        OnQuickSegmentChanged(QuickSegment);
    }

    [RelayCommand]
    private void Redo()
    {
        Document?.Redo();
        OnQuickSegmentChanged(QuickSegment);
    }

    [RelayCommand]
    private void InsertStep()
    {
        if (Document is { } document && SelectedStep is { } step)
        {
            SelectedStep = document.InsertAfter(step);
            OnQuickSegmentChanged(QuickSegment);
        }
    }

    [RelayCommand]
    private void DeleteStep()
    {
        if (Document is { } document && SelectedStep is { } step)
        {
            SelectedStep = document.Remove(step) ?? step;
            OnQuickSegmentChanged(QuickSegment);
        }
    }

    [RelayCommand]
    private void BranchFromStep()
    {
        if (Document is { } document && SelectedStep is { } step)
        {
            SelectedStep = document.AddSegment(step);
        }
    }

    /// <summary>
    /// The selected step's segment, as the inspector's combo box sets it. A
    /// segment is what the overlay shows one of at a time, so which one a step
    /// belongs to is a real editing decision, not just a lane in the graph.
    /// </summary>
    public SegmentModel? StepSegment
    {
        get => Document is { } document && SelectedStep is { } step ? document.SegmentOf(step) : null;
        set
        {
            // Avalonia writes null back whenever a combo box's items are replaced.
            if (value is null || Document is not { } document || SelectedStep is not { } step)
            {
                return;
            }

            if (document.MoveToSegment(step, value))
            {
                QuickSegment = value;
                OnPropertyChanged();
            }
        }
    }

    [RelayCommand]
    private void SplitSegment()
    {
        if (Document is not { } document || SelectedStep is not { } step)
        {
            return;
        }

        if (document.SplitAt(step) is { } fresh)
        {
            QuickSegment = fresh;
            Status = $"从这一步起分成了新的一段：{fresh.Name}";
        }
        else
        {
            Status = "这一步已经是本段的第一步了";
        }
    }

    [RelayCommand]
    private void MoveSegmentUp() => Shift(-1);

    [RelayCommand]
    private void MoveSegmentDown() => Shift(1);

    private void Shift(int delta)
    {
        if (Document is { } document && QuickSegment is { } segment && !document.MoveSegment(segment, delta))
        {
            Status = delta < 0 ? "已经是第一段" : "已经是最后一段";
        }
    }

    [RelayCommand]
    private void DeleteSegment()
    {
        if (Document is not { } document || QuickSegment is not { } segment)
        {
            return;
        }

        string name = segment.Name;
        if (document.RemoveSegment(segment))
        {
            QuickSegment = document.Segments.FirstOrDefault();
            SelectedStep = document.Steps.FirstOrDefault();
            Status = $"删掉了「{name}」和它的所有步骤";
        }
        else
        {
            Status = "删不掉：轴至少要留一段、一步";
        }
    }

    [RelayCommand]
    private void SetControlled(string slot)
    {
        if (Document is { } document)
        {
            document.ControlledSlot = int.Parse(slot, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (Selected is not { } item)
        {
            return;
        }

        Rotation rotation = item.Document.ToRotation();
        IReadOnlyList<ProfileProblem> problems = RotationValidator.Validate(rotation, host.Profile);
        if (ProfileValidator.HasErrors(problems))
        {
            Status = "有错误，没有保存";
            return;
        }

        try
        {
            host.Library.Save(rotation);
            item.MarkSaved(rotation);
            Status = "已保存";
            if (item.InUse)
            {
                host.UseRotation(rotation);
                Status = "已保存，overlay 已更新";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void UseForOverlay()
    {
        if (Selected is not { } item)
        {
            return;
        }

        if (item.Dirty)
        {
            Status = "先保存，再给 overlay 用";
            return;
        }

        IReadOnlyList<ProfileProblem> refused = host.UseRotation(item.Saved);
        Status = refused.Count == 0 ? "overlay 正在使用这条轴" : $"不能使用：{refused[0].Message}";
    }

    [RelayCommand]
    private void New()
    {
        string id = FreeId("rotation");
        var item = new LibraryItem(RotationDocument.New(id, host.Profile.Id).ToRotation(), saved: false);
        Library.Add(item);
        Selected = item;
        Status = "新的轴还没保存";
    }

    [RelayCommand]
    private async Task ImportFile()
    {
        if (files is not null && await files.OpenTextAsync() is { } text)
        {
            Import(text);
        }
    }

    [RelayCommand]
    private async Task ImportClipboard()
    {
        if (files is null)
        {
            return;
        }

        string? text = await files.ReadClipboardAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            Status = "剪贴板里没有文字";
            return;
        }

        Import(text);
    }

    [RelayCommand]
    private async Task ExportFile()
    {
        if (files is not null && Selected is { } item)
        {
            Rotation rotation = item.Document.ToRotation();
            if (await files.SaveTextAsync($"{rotation.Id}.json", RotationStore.Serialize(rotation)))
            {
                Status = "已导出";
            }
        }
    }

    [RelayCommand]
    private async Task CopyJson()
    {
        if (files is not null && Selected is { } item)
        {
            await files.WriteClipboardAsync(RotationStore.Serialize(item.Document.ToRotation()));
            Status = "已复制到剪贴板，发给别人后对方可以从剪贴板导入";
        }
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is not { } item)
        {
            return;
        }

        Rotation copy = item.Document.ToRotation();
        var duplicate = new LibraryItem(copy with { Id = FreeId(copy.Id), DisplayName = $"{copy.DisplayName}（副本）" }, saved: false);
        Library.Add(duplicate);
        Selected = duplicate;
        Status = "副本还没保存";
    }

    private void Import(string text)
    {
        Rotation rotation;
        try
        {
            rotation = RotationStore.Deserialize(text);
        }
        catch (JsonException ex)
        {
            Status = $"导入失败：{ex.Message}";
            return;
        }

        if (Library.Any(i => i.Id == rotation.Id) || File.Exists(Path.Combine(host.Library.Directory, $"{rotation.Id}.json")))
        {
            rotation = rotation with { Id = FreeId(rotation.Id) };
        }

        IReadOnlyList<ProfileProblem> problems = RotationValidator.Validate(rotation, host.Profile);
        var item = new LibraryItem(rotation, saved: false);
        Library.Add(item);
        Selected = item;
        Status = ProfileValidator.HasErrors(problems) ? "已导入，但有错误要先改" : "已导入，还没保存";
    }

    private string FreeId(string wanted)
    {
        string id = host.Library.FreeId(wanted);
        for (int n = 2; Library.Any(i => i.Id == id); n++)
        {
            id = host.Library.FreeId($"{wanted}-{n}");
        }

        return id;
    }

    private void MarkInUse()
    {
        string active = host.ActiveRotation.Id;
        foreach (LibraryItem item in Library)
        {
            item.InUse = item.Id == active;
        }

        OnPropertyChanged(nameof(InUseName));
        OnPropertyChanged(nameof(UseLabel));
    }
}

/// <summary>The settings page, written straight through to the host.</summary>
internal sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AnaphoraHost host;
    private bool loading;

    public SettingsViewModel(AnaphoraHost host)
    {
        this.host = host;
        Load();
        host.SettingsChanged += Load;
    }

    public string ProfileName => $"{host.Profile.DisplayName}（{host.Profile.Id}）";

    public string LibraryFolder => host.Library.Directory;

    [ObservableProperty]
    public partial bool TrackVisible { get; set; }

    [ObservableProperty]
    public partial bool HideWhenHudAbsent { get; set; }

    [ObservableProperty]
    public partial decimal HideAfterSeconds { get; set; }

    [ObservableProperty]
    public partial string ResetHotkey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? HotkeyError { get; set; }

    partial void OnTrackVisibleChanged(bool value) => Store(host.Settings with { TrackVisible = value });

    partial void OnHideWhenHudAbsentChanged(bool value) => Store(host.Settings with { HideWhenHudAbsent = value });

    partial void OnHideAfterSecondsChanged(decimal value) => Store(host.Settings with { HideAfterSeconds = (double)Math.Clamp(value, 1, 60) });

    partial void OnResetHotkeyChanged(string value)
    {
        if (!string.IsNullOrEmpty(value) && HotkeyGesture.Parse(value) is null)
        {
            HotkeyError = "写成 Ctrl+Alt+R 这样：修饰键加一个字母、数字或 F1–F24";
            return;
        }

        HotkeyError = null;
        Store(host.Settings with { ResetHotkey = value });
    }

    private void Store(AppSettings updated)
    {
        if (!loading)
        {
            host.UpdateSettings(updated);
        }
    }

    private void Load()
    {
        loading = true;
        TrackVisible = host.Settings.TrackVisible;
        HideWhenHudAbsent = host.Settings.HideWhenHudAbsent;
        HideAfterSeconds = (decimal)host.Settings.HideAfterSeconds;
        ResetHotkey = host.Settings.ResetHotkey ?? string.Empty;
        loading = false;
    }
}
