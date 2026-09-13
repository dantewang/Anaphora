using Anaphora.Analysis;
using Anaphora.Core;

namespace Anaphora.Capture;

/// <summary>
/// Capture and reading joined: a window in, immutable <see cref="HudSnapshot"/>s
/// out. Snapshots are plain objects with no ties to the frame they came from,
/// so the overlay can marshal them to its UI thread at leisure.
/// </summary>
public sealed class HudCapture : IDisposable
{
    private readonly HudReader reader;
    private readonly CaptureSession session;

    private HudCapture(nint window, GameProfile profile, CaptureOptions? options)
    {
        reader = new HudReader(profile);
        session = CaptureSession.Create(window, profile, OnAtlas, options);
        session.Closed += () => Closed?.Invoke();
        session.Faulted += ex => Faulted?.Invoke(ex);
    }

    /// <summary>
    /// Raised on the capture thread for every frame that makes it through the
    /// rate limit, including frames whose HUD is absent -- those arrive with
    /// <see cref="HudSnapshot.HudPresent"/> false so the overlay can mark its
    /// values stale.
    /// </summary>
    public event Action<HudSnapshot>? SnapshotReady;

    public event Action? Closed;

    public event Action<Exception>? Faulted;

    public CaptureStats Stats => session.Stats;

    public GameProfile Profile => reader.Profile;

    /// <summary>Prepares the capture. Subscribe, then <see cref="Start"/>.</summary>
    public static HudCapture Create(nint window, GameProfile profile, CaptureOptions? options = null) =>
        new(window, profile, options);

    public void Start() => session.Start();

    public void Dispose() => session.Dispose();

    private void OnAtlas(in AtlasFrame frame) =>
        SnapshotReady?.Invoke(reader.Read(frame.Pixels, frame.Atlas));
}
