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
/// behind, intermediate states are dropped rather than queued.
/// </summary>
internal sealed class AnaphoraHost : IDisposable
{
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PlacementInterval = TimeSpan.FromMilliseconds(100);

    private readonly AppOptions options;
    private readonly GameProfile profile;
    private readonly RotationTracker tracker;
    private readonly AvaloniaOverlaySurface surface;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer discovery;
    private readonly DispatcherTimer placement;
    private readonly Lock trackerGate = new();

    private AppSettings settings;
    private HudCapture? capture;
    private nint game;
    private TimeSpan lastReadable;
    private Pending? pending;
    private int posted;
    private bool checkedClickThrough;

    public AnaphoraHost(AppOptions options, AppSettings settings)
    {
        this.options = options;
        this.settings = settings;

        profile = ProfileStore.Load(options.ProfilePath);
        Rotation rotation = RotationStore.Load(options.RotationPath);

        IReadOnlyList<ProfileProblem> problems = [.. ProfileValidator.Validate(profile), .. RotationValidator.Validate(rotation, profile)];
        foreach (ProfileProblem problem in problems)
        {
            Log.Write(problem.ToString());
        }

        if (ProfileValidator.HasErrors(problems))
        {
            throw new InvalidOperationException($"the profile or rotation has errors; see {Log.Location}.");
        }

        tracker = new RotationTracker(rotation);
        surface = new AvaloniaOverlaySurface(OverlayAnchor.BelowOperator);
        surface.Model.StatusVisible = settings.StatusVisible;
        surface.Model.Apply(HudSnapshot.Absent, tracker.State, TimeSpan.Zero);
        surface.Model.ShowWaiting("等待游戏窗口");

        discovery = new DispatcherTimer(DiscoveryInterval, DispatcherPriority.Background, (_, _) => Discover());
        placement = new DispatcherTimer(PlacementInterval, DispatcherPriority.Render, (_, _) => Place());

        Log.Write($"started: profile {profile.Id}, rotation {rotation.Id}, process {options.ProcessName ?? profile.Window.ProcessName}");
    }

    public bool StatusVisible => surface.Model.StatusVisible;

    public void Start()
    {
        discovery.Start();
        placement.Start();
        Discover();
    }

    /// <returns>The new state.</returns>
    public bool ToggleStatus()
    {
        bool visible = !surface.Model.StatusVisible;
        surface.Model.StatusVisible = visible;
        settings = settings with { StatusVisible = visible };
        settings.Save();
        return visible;
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

        surface.Model.Apply(HudSnapshot.Absent, state, clock.Elapsed - lastReadable);
    }

    public void Dispose()
    {
        discovery.Stop();
        placement.Stop();
        StopCapture("shutting down");
        surface.Dispose();
    }

    private void Discover()
    {
        if (capture is not null)
        {
            return;
        }

        string name = options.ProcessName ?? profile.Window.ProcessName;
        nint window = FindWindow(name);
        if (window == 0)
        {
            surface.Model.ShowWaiting($"等待 {name} 窗口");
            return;
        }

        try
        {
            HudCapture started = HudCapture.Create(window, profile, new CaptureOptions { Rate = options.Rate });
            started.SnapshotReady += OnSnapshot;
            started.Closed += () => Dispatcher.UIThread.Post(() => StopCapture("the game window closed"));
            started.Faulted += ex => Dispatcher.UIThread.Post(() => StopCapture($"capture faulted: {ex}"));
            started.Start();

            capture = started;
            game = window;
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

    private void Place()
    {
        surface.Place(game == 0 ? null : GameWindowGeometry.ClientAreaOnScreen(game, options.RequireForeground));

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
        TimeSpan now = clock.Elapsed;
        if (hud.HudPresent)
        {
            lastReadable = now;
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

        Volatile.Write(ref pending, new Pending(hud, state, now - lastReadable));
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
