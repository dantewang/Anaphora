using System.Diagnostics;
using Anaphora.Analysis;
using Anaphora.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Anaphora.Capture;

public sealed record CaptureOptions
{
    /// <summary>
    /// Readings per second. WGC delivers at the game's present rate with no
    /// throttle of its own; everything above this is dropped on arrival.
    /// </summary>
    public double Rate { get; init; } = 20;

    /// <summary>Ask for no yellow capture border. Free where the OS allows it, harmless where not.</summary>
    public bool HideBorder { get; init; } = true;

    /// <summary>
    /// Every Nth processed frame, also read the whole texture back and check the
    /// GPU atlas against a CPU-built one. Zero disables it. Diagnostic only: it
    /// is exactly the full-frame readback the atlas exists to avoid.
    /// </summary>
    public int VerifyEvery { get; init; }
}

/// <summary>The atlas for one frame, valid only for the duration of the handler call.</summary>
public readonly ref struct AtlasFrame
{
    internal AtlasFrame(FrameView pixels, RoiAtlas atlas, TimeSpan timestamp)
    {
        Pixels = pixels;
        Atlas = atlas;
        Timestamp = timestamp;
    }

    /// <summary>Backed by a mapped staging texture. Do not let it, or anything read lazily from it, escape.</summary>
    public FrameView Pixels { get; }

    public RoiAtlas Atlas { get; }

    /// <summary>When the frame was presented, on the QueryPerformanceCounter clock.</summary>
    public TimeSpan Timestamp { get; }
}

public delegate void AtlasFrameHandler(in AtlasFrame frame);

public readonly record struct CaptureStats(
    long Arrived,
    long Processed,
    long Throttled,
    long Busy,
    long Skipped,
    long Verified,
    long VerificationFailures,
    double LastProcessMilliseconds,
    int AtlasWidth,
    int AtlasHeight,
    PixelRect ClientArea,
    bool MinUpdateIntervalApplied);

/// <summary>
/// A WGC capture of one window that yields, at a bounded rate, the ROI atlas
/// for a profile.
///
/// Frames arrive on a thread-pool thread and are handled right there: WGC's
/// pool is free-threaded, the work is a handful of GPU copies and one small map,
/// and a hand-off would only add latency. Anything arriving while a frame is
/// still being handled, or ahead of the rate, is disposed at once. Nothing is
/// ever queued.
/// </summary>
public sealed class CaptureSession : IDisposable
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private const int PoolBuffers = 2;

    private readonly Lock gate = new();
    private readonly nint window;
    private readonly GameProfile profile;
    private readonly AtlasFrameHandler handler;
    private readonly CaptureOptions options;
    private readonly TimeSpan interval;

    private readonly ID3D11Device device;
    private readonly IDirect3DDevice winrtDevice;
    private readonly GraphicsCaptureItem item;
    private readonly Direct3D11CaptureFramePool pool;
    private readonly GraphicsCaptureSession session;

    private SizeInt32 poolSize;
    private AtlasReadback? readback;
    private TimeSpan nextDue;
    private bool started;
    private volatile bool stopped;

    private long arrived;
    private long processed;
    private long throttled;
    private long busy;
    private long skipped;
    private long verified;
    private long verificationFailures;
    private double lastProcessMilliseconds;
    private bool minUpdateIntervalApplied;

    private CaptureSession(nint window, GameProfile profile, AtlasFrameHandler handler, CaptureOptions options)
    {
        this.window = window;
        this.profile = profile;
        this.handler = handler;
        this.options = options;
        interval = TimeSpan.FromSeconds(1 / options.Rate);

        // BgraSupport is required before a D3D11 device may back a WinRT surface.
        device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        winrtDevice = WinRtInterop.WrapDevice(device);
        item = WinRtInterop.CreateItemForWindow(window);
        poolSize = item.Size;
        pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, PixelFormat, PoolBuffers, poolSize);
        session = pool.CreateCaptureSession(item);
    }

    /// <summary>Raised when the captured window goes away, typically because the game exited.</summary>
    public event Action? Closed;

    /// <summary>
    /// Raised once when capture can no longer continue -- a removed device, most
    /// likely after a driver reset. The session stops; create a new one.
    /// </summary>
    public event Action<Exception>? Faulted;

    public CaptureStats Stats
    {
        get
        {
            RoiAtlas? atlas = readback?.Atlas;
            return new CaptureStats(
                Interlocked.Read(ref arrived),
                Interlocked.Read(ref processed),
                Interlocked.Read(ref throttled),
                Interlocked.Read(ref busy),
                Interlocked.Read(ref skipped),
                Interlocked.Read(ref verified),
                Interlocked.Read(ref verificationFailures),
                Volatile.Read(ref lastProcessMilliseconds),
                atlas?.Width ?? 0,
                atlas?.Height ?? 0,
                atlas?.ClientArea ?? default,
                minUpdateIntervalApplied);
        }
    }

    /// <summary>
    /// Prepares a capture of <paramref name="window"/>. Subscribe to
    /// <see cref="Closed"/> and <see cref="Faulted"/>, then call <see cref="Start"/>.
    /// </summary>
    public static CaptureSession Create(nint window, GameProfile profile, AtlasFrameHandler handler, CaptureOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new CaptureOptions();

        if (options.Rate is <= 0 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"rate {options.Rate} is outside (0, 240].");
        }

        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException("Windows.Graphics.Capture is not supported on this system.");
        }

        if (!WindowGeometry.IsPerMonitorV2())
        {
            throw new InvalidOperationException(
                "the capturing thread is not per-monitor-v2 DPI aware. Client-area coordinates would come back " +
                "in logical pixels against a physical-pixel capture, and every ROI would be misplaced on a scaled " +
                "display. Declare PerMonitorV2 in the application manifest.");
        }

        if (!WindowGeometry.IsLiveWindow(window))
        {
            throw new ArgumentException($"0x{window:X} is not a window.", nameof(window));
        }

        return new CaptureSession(window, profile, handler, options);
    }

    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (started)
            {
                return;
            }

            started = true;
        }

        if (options.HideBorder)
        {
            try
            {
                session.IsBorderRequired = false;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException or InvalidCastException)
            {
                // Older Windows, or access not granted. The border is cosmetic.
            }
        }

        // Newer Windows can throttle delivery itself, which saves surfacing
        // frames only to drop them. Slightly under the interval so the software
        // throttle below still decides; it is the one that keeps the long-run
        // rate exact.
        if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.MinUpdateInterval)))
        {
            try
            {
                session.MinUpdateInterval = interval * 0.9;
                minUpdateIntervalApplied = true;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidCastException or UnauthorizedAccessException)
            {
            }
        }

        item.Closed += OnItemClosed;
        pool.FrameArrived += OnFrameArrived;
        session.StartCapture();
    }

    public void Dispose()
    {
        stopped = true;

        pool.FrameArrived -= OnFrameArrived;
        item.Closed -= OnItemClosed;

        // Waits out a frame already being handled. The handler never waits on
        // this lock -- it drops frames it cannot enter -- so this cannot deadlock.
        lock (gate)
        {
            session.Dispose();
            pool.Dispose();
            readback?.Dispose();
            readback = null;
            device.Dispose();
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        if (!stopped)
        {
            Closed?.Invoke();
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Interlocked.Increment(ref arrived);

        if (!gate.TryEnter())
        {
            // Still handling the previous frame. Take this one off the pool so it
            // does not hold a buffer, and let it go.
            Interlocked.Increment(ref busy);
            Drain(sender);
            return;
        }

        try
        {
            if (stopped)
            {
                return;
            }

            using Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            Handle(frame);
        }
        catch (Exception ex) when (!stopped)
        {
            Fault(ex);
        }
        catch (ObjectDisposedException)
        {
            // Raced Dispose.
        }
        finally
        {
            gate.Exit();
        }
    }

    private void Handle(Direct3D11CaptureFrame frame)
    {
        SizeInt32 size = frame.ContentSize;
        if (size.Width != poolSize.Width || size.Height != poolSize.Height)
        {
            // The window was resized. Frames already in flight carry the old
            // buffer size; rebuild and pick up from the next one.
            poolSize = size;
            pool.Recreate(winrtDevice, PixelFormat, PoolBuffers, size);
            Interlocked.Increment(ref skipped);
            return;
        }

        TimeSpan now = frame.SystemRelativeTime;
        if (now < nextDue)
        {
            Interlocked.Increment(ref throttled);
            return;
        }

        // Advance by whole intervals rather than from "now", so the long-run rate
        // is the configured one instead of whatever multiple of the game's frame
        // time happens to exceed it. After a stall, resync instead of bursting.
        nextDue += interval;
        if (nextDue <= now)
        {
            nextDue = now + interval;
        }

        long started = Stopwatch.GetTimestamp();

        PixelRect client = WindowGeometry.ClientAreaInCapture(window, size.Width, size.Height);
        if (client.IsEmpty)
        {
            Interlocked.Increment(ref skipped);
            return;
        }

        if (readback is null || !readback.Matches(size.Width, size.Height, client))
        {
            readback?.Dispose();
            readback = new AtlasReadback(device, RoiAtlas.Create(profile, size.Width, size.Height, client));
        }

        using ID3D11Texture2D texture = WinRtInterop.TextureFrom(frame.Surface);
        readback.Process(texture, now, handler);

        long count = Interlocked.Increment(ref processed);
        Volatile.Write(ref lastProcessMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        if (options.VerifyEvery > 0 && count % options.VerifyEvery == 0)
        {
            Interlocked.Increment(ref verified);
            if (!readback.Verify(texture))
            {
                Interlocked.Increment(ref verificationFailures);
            }
        }
    }

    private static void Drain(Direct3D11CaptureFramePool pool)
    {
        try
        {
            pool.TryGetNextFrame()?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Fault(Exception ex)
    {
        stopped = true;
        pool.FrameArrived -= OnFrameArrived;
        ThreadPool.UnsafeQueueUserWorkItem(_ => Faulted?.Invoke(ex), null);
    }
}
