using System.Globalization;
using CorePixelRect = Anaphora.Core.PixelRect;

namespace Anaphora.Overlay;

/// <summary>
/// What the app needs from an overlay, and all it gets. If Avalonia's
/// transparent windows ever misbehave, a hand-rolled DirectComposition surface
/// replaces this one implementation and nothing else changes.
/// </summary>
public interface IOverlaySurface : IDisposable
{
    OverlayViewModel Model { get; }

    /// <summary>
    /// Positions the overlay for a game whose client area occupies
    /// <paramref name="clientOnScreen"/>, in screen pixels. Null hides it.
    /// </summary>
    void Place(CorePixelRect? clientOnScreen);
}

/// <summary>Where on the game the overlay's bottom edge is centred, as fractions of the client area.</summary>
public readonly record struct OverlayAnchor(double X, double Y)
{
    /// <summary>
    /// Placement A from the mockup: below the controlled operator, where the
    /// player is already looking. Anchored by its bottom edge, just clear of the
    /// game's own bottom HUD band, so the title card grows upward when cues
    /// appear and the strip under it never moves.
    /// </summary>
    public static readonly OverlayAnchor AboveBottomHud = new(0.5, 0.67);

    /// <summary>How near the screen's centre line x snaps to it, as a fraction of the client width.</summary>
    public const double SnapTolerance = 0.01;

    /// <summary>True when x sits on the centre line, which is where it starts.</summary>
    public bool IsCentred => Math.Abs(X - 0.5) < 1e-9;

    /// <summary>"水平 50.0% · 底边 67.0%", for the readout while positioning.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"水平 {X * 100:F1}%{(IsCentred ? "（已吸附中线）" : string.Empty)} · 底边 {Y * 100:F1}%");

    /// <summary>
    /// Moves the anchor by a drag of so many screen pixels, keeping the whole
    /// overlay inside the client area.
    ///
    /// This does not snap. A drag arrives as a stream of small deltas, and
    /// snapping each one would pin a slow drag to the centre line for ever: a
    /// 30px step is inside the tolerance, so every step would be undone. The
    /// surface nudges an unsnapped anchor and snaps only what it shows.
    /// </summary>
    /// <param name="windowWidth">The overlay's width in screen pixels; x is its centre.</param>
    /// <param name="windowHeight">Its height; y is its bottom edge.</param>
    public OverlayAnchor Nudge(double dx, double dy, CorePixelRect client, int windowWidth, int windowHeight)
    {
        if (client.Width <= 0 || client.Height <= 0)
        {
            return this;
        }

        double x = X + (dx / client.Width);
        double y = Y + (dy / client.Height);

        // An overlay wider or taller than the game has nowhere to go.
        double half = windowWidth / 2.0 / client.Width;
        double tall = (double)windowHeight / client.Height;
        x = half >= 0.5 ? 0.5 : Math.Clamp(x, half, 1 - half);
        y = tall >= 1 ? 1 : Math.Clamp(y, tall, 1);

        return new OverlayAnchor(x, y);
    }

    /// <summary>
    /// Pulls x onto the centre line when it lands near it. The overlay starts
    /// centred, so getting back to exactly centred by hand has to be possible.
    /// </summary>
    public OverlayAnchor Snapped() =>
        Math.Abs(X - 0.5) <= SnapTolerance ? this with { X = 0.5 } : this;
}

public sealed class AvaloniaOverlaySurface : IOverlaySurface
{
    private readonly OverlayWindow window;
    private CorePixelRect lastClient;
    private bool visible;
    private bool positioning;

    /// <summary>Where the drag actually is, before the centre line pulls x onto itself.</summary>
    private OverlayAnchor raw;

    public AvaloniaOverlaySurface(OverlayAnchor? anchor = null)
    {
        raw = anchor ?? OverlayAnchor.AboveBottomHud;
        Anchor = raw.Snapped();
        Model = new OverlayViewModel();
        window = new OverlayWindow { DataContext = Model, Opacity = 0 };
        window.Show();
        Win32Window.Hide(window.Handle);
        window.Opacity = 1;
        window.Dragged += OnDragged;
    }

    /// <summary>The anchor settled on after a drag; the host persists it.</summary>
    public event Action<OverlayAnchor>? AnchorChanged;

    public OverlayViewModel Model { get; }

    public nint Handle => window.Handle;

    public OverlayAnchor Anchor { get; private set; }

    /// <summary>
    /// While on, the overlay takes the mouse so it can be dragged. The game keeps
    /// the foreground -- WS_EX_NOACTIVATE is left in place -- and the host keeps
    /// the overlay on screen whatever the HUD is doing, because an overlay that
    /// vanishes mid-drag cannot be positioned.
    /// </summary>
    public bool Positioning
    {
        get => positioning;
        set
        {
            if (positioning == value)
            {
                return;
            }

            positioning = value;
            Model.Positioning = value;
            Model.PositionText = Anchor.Describe();
            Win32Window.SetClickThrough(window.Handle, !value);
        }
    }

    public void MoveTo(OverlayAnchor anchor)
    {
        raw = anchor;
        Anchor = anchor.Snapped();
        Model.PositionText = Anchor.Describe();
        if (!lastClient.IsEmpty)
        {
            Place(lastClient);
        }

        AnchorChanged?.Invoke(Anchor);
    }

    public void Place(CorePixelRect? clientOnScreen)
    {
        if (clientOnScreen is not CorePixelRect client || client.IsEmpty)
        {
            if (visible)
            {
                Win32Window.Hide(window.Handle);
                visible = false;
            }

            return;
        }

        lastClient = client;
        double scaling = window.RenderScaling;
        window.SetScale(client.Width / scaling / OverlayWindow.ReferenceWidth);

        int width = (int)Math.Round(window.Bounds.Width * scaling);
        int height = (int)Math.Round(window.Bounds.Height * scaling);
        int x = client.X + (int)Math.Round(client.Width * Anchor.X) - (width / 2);
        int y = client.Y + (int)Math.Round(client.Height * Anchor.Y) - height;

        Win32Window.Show(window.Handle, x, y);
        visible = true;
    }

    /// <summary>True when a click at the overlay's centre would reach the overlay -- which it must not.</summary>
    public bool CatchesClicks()
    {
        double scaling = window.RenderScaling;
        int x = window.Position.X + (int)(window.Bounds.Width * scaling / 2);
        int y = window.Position.Y + (int)(window.Bounds.Height * scaling / 2);
        return Win32Window.HitsWindow(window.Handle, x, y);
    }

    public void Dispose()
    {
        window.Dragged -= OnDragged;
        window.Close();
    }

    /// <summary>
    /// The window reports how far the pointer moved; the anchor is the only
    /// record of where the overlay is, so the drag goes through it and the next
    /// Place() puts the window there.
    /// </summary>
    private void OnDragged(double dx, double dy)
    {
        if (!positioning || lastClient.IsEmpty)
        {
            return;
        }

        double scaling = window.RenderScaling;
        int width = (int)Math.Round(window.Bounds.Width * scaling);
        int height = (int)Math.Round(window.Bounds.Height * scaling);
        MoveTo(raw.Nudge(dx, dy, lastClient, width, height));
    }
}
