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

/// <summary>Where on the game the overlay's top edge is centred, as fractions of the client area.</summary>
public readonly record struct OverlayAnchor(double X, double Y)
{
    /// <summary>
    /// Placement A from the mockup: below the controlled operator, above the
    /// bottom HUD, where the player is already looking.
    /// </summary>
    public static readonly OverlayAnchor BelowOperator = new(0.5, 0.605);
}

public sealed class AvaloniaOverlaySurface : IOverlaySurface
{
    private readonly OverlayWindow window;
    private readonly OverlayAnchor anchor;
    private bool visible;

    public AvaloniaOverlaySurface(OverlayAnchor? anchor = null)
    {
        this.anchor = anchor ?? OverlayAnchor.BelowOperator;
        Model = new OverlayViewModel();
        window = new OverlayWindow { DataContext = Model, Opacity = 0 };
        window.Show();
        Win32Window.Hide(window.Handle);
        window.Opacity = 1;
    }

    public OverlayViewModel Model { get; }

    public nint Handle => window.Handle;

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

        double scaling = window.RenderScaling;
        window.SetScale(client.Width / scaling / OverlayWindow.ReferenceWidth);

        int width = (int)Math.Round(window.Bounds.Width * scaling);
        int x = client.X + (int)Math.Round(client.Width * anchor.X) - (width / 2);
        int y = client.Y + (int)Math.Round(client.Height * anchor.Y);

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

    public void Dispose() => window.Close();
}
