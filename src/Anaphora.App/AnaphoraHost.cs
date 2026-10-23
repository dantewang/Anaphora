using System.Diagnostics;
using Anaphora.Analysis;
using Anaphora.Capture;
using Anaphora.Core;
using Anaphora.Overlay;
using Avalonia.Threading;

namespace Anaphora.App;

/// <summary>
/// Wires the pipeline together: find the game, capture it, read the HUD, follow
/// the rotation, draw the overlay. Survives the game starting late, closing and
/// coming back, and the capture device going away.
///
/// Threads: snapshots arrive on the capture thread, where the tracker runs too
/// (frames are serialised there, so it sees them in order). Only the finished
/// state crosses to the UI thread, and only the newest one -- if the UI falls
/// behind, intermediate states are dropped rather than queued. Swapping the
/// rotation replaces the tracker under the same lock.
/// </summary>
internal sealed class AnaphoraHost : IDisposable
{
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PlacementInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// What --demo shows instead of an unreadable HUD: one skill point short of
    /// three, slot 2's chain ready and its ultimate up, and E leading on 1 then
    /// 2. Enough for every part of the overlay to have something to draw.
    /// </summary>
    private static readonly HudSnapshot DemoReadings = new()
    {
        HudPresent = true,
        SkillPoints = 2,
        Chains = [new FillBarReading(0.39, false), new FillBarReading(1, true), new FillBarReading(0.02, false), new FillBarReading(0.13, false)],
        Ultimates = [new DiscStateReading(false, 0.3, 0), new DiscStateReading(true, 1, 0.88), new DiscStateReading(false, 0, 0), new DiscStateReading(false, 0, 0)],
        Prompts = [new PortraitReading("demo-1", 0, 0) { Slot = 1 }, new PortraitReading("demo-2", 0, 0) { Slot = 2 }],
    };

    private readonly AppOptions options;
    private readonly AvaloniaOverlaySurface surface;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer discovery;
    private readonly DispatcherTimer placement;
    private readonly Lock trackerGate = new();

    private RotationTracker tracker;
    private GlobalHotkey? resetHotkey;
    private AppSettings settings;
    private HudCapture? capture;
    private nint game;
    private long lastReadableTicks;
    private Pending? pending;
    private int posted;
    private bool checkedClickThrough;

    public AnaphoraHost(AppOptions options, AppSettings settings, RotationLibrary library)
    {
        this.options = options;
        this.settings = settings;
        Library = library;

        Profile = ProfileStore.Load(options.ProfilePath);
        IReadOnlyList<ProfileProblem> problems = ProfileValidator.Validate(Profile);
        foreach (ProfileProblem problem in problems)
        {
            Log.Write(problem.ToString());
        }

        if (ProfileValidator.HasErrors(problems))
        {
            throw new InvalidOperationException($"the profile has errors; see {Log.Location}.");
        }

        Rotation rotation = FirstRotation();
        tracker = new RotationTracker(rotation);
        surface = new AvaloniaOverlaySurface(OverlayAnchor.AboveBottomHud);
        surface.Model.TrackVisible = settings.TrackVisible;
        surface.Model.Apply(HudSnapshot.Absent, tracker.State, TimeSpan.Zero);
        surface.Model.ShowWaiting("等待游戏窗口");

        discovery = new DispatcherTimer(DiscoveryInterval, DispatcherPriority.Background, (_, _) => Discover());
        placement = new DispatcherTimer(PlacementInterval, DispatcherPriority.Render, (_, _) => Place());
        RegisterHotkey();

        Log.Write($"started: profile {Profile.Id}, rotation {rotation.Id}, process {options.ProcessName ?? Profile.Window.ProcessName}");
    }

    /// <summary>Settings changed from somewhere -- the tray or the main window -- so the other can follow.</summary>
    public event Action? SettingsChanged;

    /// <summary>The overlay started following a different rotation.</summary>
    public event Action? RotationChanged;

    public GameProfile Profile { get; }

    public RotationLibrary Library { get; }

    public AppSettings Settings => settings;

    public Rotation ActiveRotation
    {
        get
        {
            lock (trackerGate)
            {
                return tracker.Rotation;
            }
        }
    }

    /// <summary>The reset hotkey as the tray should show it, or null when there is none.</summary>
    public string? ResetHotkeyText => resetHotkey?.Gesture.Text;

    public void Start()
    {
        discovery.Start();
        placement.Start();
        Discover();
    }

    public void UpdateSettings(AppSettings updated)
    {
        if (updated == settings)
        {
            return;
        }

        AppSettings previous = settings;
        settings = updated;
        settings.Save();

        surface.Model.TrackVisible = settings.TrackVisible;
        if (previous.ResetHotkey != settings.ResetHotkey)
        {
            RegisterHotkey();
        }

        Place();
        SettingsChanged?.Invoke();
    }

    /// <returns>Problems that stopped the switch; empty when the overlay now follows <paramref name="rotation"/>.</returns>
    public IReadOnlyList<ProfileProblem> UseRotation(Rotation rotation)
    {
        IReadOnlyList<ProfileProblem> problems = RotationValidator.Validate(rotation, Profile);
        if (ProfileValidator.HasErrors(problems))
        {
            return [.. problems.Where(p => p.Severity == ProfileProblemSeverity.Error)];
        }

        RotationState state;
        lock (trackerGate)
        {
            tracker = new RotationTracker(rotation);
            state = tracker.State;
        }

        surface.Model.Apply(HudSnapshot.Absent, state, SinceReadable);
        Log.Write($"following rotation {rotation.Id}");

        if (settings.ActiveRotation != rotation.Id)
        {
            UpdateSettings(settings with { ActiveRotation = rotation.Id });
        }

        RotationChanged?.Invoke();
        return [];
    }

    public void ResetRotation()
    {
        RotationState state;
        lock (trackerGate)
        {
            tracker.Reset();
            state = tracker.State;
        }

        surface.Model.Apply(HudSnapshot.Absent, state, TimeSpan.Zero);
    }

    public void SkipStep()
    {
        RotationState state;
        lock (trackerGate)
        {
            tracker.Skip();
            state = tracker.State;
        }

        surface.Model.Apply(HudSnapshot.Absent, state, SinceReadable);
    }

    /// <summary>
    /// --rotation wins; otherwise the active rotation in the library; otherwise
    /// any usable one there. The library is seeded with the shipped rotations,
    /// so there is always something.
    /// </summary>
    private Rotation FirstRotation()
    {
        if (options.RotationPath is { } path)
        {
            return Checked(RotationStore.Load(path))
                ?? throw new InvalidOperationException($"the rotation {path} has errors; see {Log.Location}.");
        }

        IEnumerable<Rotation> candidates = Library.Load()
            .Where(e => e.Rotation is not null)
            .OrderBy(e => e.Id == settings.ActiveRotation ? 0 : 1)
            .Select(e => e.Rotation!);

        foreach (Rotation candidate in candidates)
        {
            if (Checked(candidate) is { } usable)
            {
                return usable;
            }
        }

        throw new InvalidOperationException($"no usable rotation in {Library.Directory}; see {Log.Location}.");

        Rotation? Checked(Rotation rotation)
        {
            IReadOnlyList<ProfileProblem> problems = RotationValidator.Validate(rotation, Profile);
            foreach (ProfileProblem problem in problems)
            {
                Log.Write($"rotation {rotation.Id}: {problem}");
            }

            return ProfileValidator.HasErrors(problems) ? null : rotation;
        }
    }

    private void RegisterHotkey()
    {
        resetHotkey?.Dispose();
        resetHotkey = null;

        if (HotkeyGesture.Parse(settings.ResetHotkey) is { } gesture)
        {
            resetHotkey = new GlobalHotkey(gesture);
            resetHotkey.Pressed += () => Dispatcher.UIThread.Post(() =>
            {
                Log.Write($"rotation reset by {gesture}");
                ResetRotation();
            });
        }
        else if (!string.IsNullOrWhiteSpace(settings.ResetHotkey))
        {
            Log.Write($"[warn] reset hotkey \"{settings.ResetHotkey}\" not understood; expected e.g. Ctrl+Alt+R or Shift+F9");
        }
    }


    public void Dispose()
    {
        discovery.Stop();
        placement.Stop();
        resetHotkey?.Dispose();
        StopCapture("shutting down");
        surface.Dispose();
    }

    private void Discover()
    {
        if (capture is not null)
        {
            return;
        }

        string name = options.ProcessName ?? Profile.Window.ProcessName;
        nint window = FindWindow(name);
        if (window == 0)
        {
            surface.Model.ShowWaiting($"等待 {name} 窗口");
            return;
        }

        try
        {
            HudCapture started = HudCapture.Create(window, Profile, new CaptureOptions { Rate = options.Rate });
            started.SnapshotReady += OnSnapshot;
            started.Closed += () => Dispatcher.UIThread.Post(() => StopCapture("the game window closed"));
            started.Faulted += ex => Dispatcher.UIThread.Post(() => StopCapture($"capture faulted: {ex}"));
            started.Start();

            capture = started;
            game = window;
            // Counts as readable for now, so a fresh capture is visible for a
            // moment before a missing HUD can hide it.
            Interlocked.Exchange(ref lastReadableTicks, clock.Elapsed.Ticks);
            Log.Write($"capturing {name} 0x{window:X}");
        }
        catch (Exception ex)
        {
            // Minimised, mid-launch, or not capturable yet; the next tick tries again.
            Log.Write($"cannot capture {name} 0x{window:X} yet: {ex.Message}");
            surface.Model.ShowWaiting("游戏窗口暂时无法抓取");
        }
    }

    private void StopCapture(string reason)
    {
        if (capture is null)
        {
            return;
        }

        Log.Write($"capture stopped: {reason}. Stats: {capture.Stats}");
        capture.Dispose();
        capture = null;
        game = 0;
        surface.Place(null);
        surface.Model.ShowWaiting("等待游戏窗口");
    }

    private TimeSpan SinceReadable => clock.Elapsed - TimeSpan.FromTicks(Interlocked.Read(ref lastReadableTicks));

    private void Place()
    {
        Anaphora.Core.PixelRect? client = game == 0 ? null : GameWindowGeometry.ClientAreaOnScreen(game, options.RequireForeground);

        // Held only while the game is in front: a registered combination is
        // taken away from every other program.
        if (resetHotkey is not null)
        {
            resetHotkey.Enabled = client is not null;
        }

        bool hudGone = settings.HideWhenHudAbsent && SinceReadable > TimeSpan.FromSeconds(settings.HideAfterSeconds);
        surface.Place(hudGone ? null : client);

        if (!checkedClickThrough && game != 0 && surface.Handle != 0)
        {
            checkedClickThrough = true;
            Dispatcher.UIThread.Post(
                () => Log.Write(surface.CatchesClicks()
                    ? "[warn] the overlay catches clicks at its centre; click-through did not take"
                    : "overlay is click-through"),
                DispatcherPriority.Background);
        }
    }

    /// <summary>Capture thread.</summary>
    private void OnSnapshot(HudSnapshot hud)
    {
        if (options.Demo && !hud.HudPresent)
        {
            hud = DemoReadings;
        }

        TimeSpan now = clock.Elapsed;
        if (hud.HudPresent)
        {
            Interlocked.Exchange(ref lastReadableTicks, now.Ticks);
        }

        RotationState state;
        lock (trackerGate)
        {
            state = tracker.Observe(hud, now);
        }

        foreach (Observation surprise in state.Unexpected)
        {
            Log.Write($"off rotation at {state.Cursor}: {surprise}");
        }

        if (state.Resynced)
        {
            Log.Write($"realigned to {state.Cursor}: now {state.Current.Step.Action}");
        }

        Volatile.Write(ref pending, new Pending(hud, state, SinceReadable));
        if (Interlocked.Exchange(ref posted, 1) == 0)
        {
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Render);
        }
    }

    /// <summary>UI thread: draws the newest state and nothing older.</summary>
    private void Flush()
    {
        Interlocked.Exchange(ref posted, 0);
        if (Volatile.Read(ref pending) is { } latest)
        {
            surface.Model.Apply(latest.Hud, latest.Rotation, latest.Age);
        }
    }

    private sealed record Pending(HudSnapshot Hud, RotationState Rotation, TimeSpan Age);

    private static nint FindWindow(string processName)
    {
        Process[] candidates = Process.GetProcessesByName(processName);
        try
        {
            return candidates.FirstOrDefault(p => p.MainWindowHandle != 0)?.MainWindowHandle ?? 0;
        }
        finally
        {
            foreach (Process candidate in candidates)
            {
                candidate.Dispose();
            }
        }
    }
}
