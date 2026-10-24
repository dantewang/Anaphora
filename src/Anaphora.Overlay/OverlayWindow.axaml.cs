using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Anaphora.Overlay;

public partial class OverlayWindow : Window
{
    /// <summary>The game width, in DIPs, the AXAML sizes are drawn for.</summary>
    public const double ReferenceWidth = 1920;

    private double scale = 1;
    private PixelPoint? dragFrom;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    /// <summary>How far the pointer moved, in screen pixels, while dragging the overlay.</summary>
    public event Action<double, double>? Dragged;

    public nint Handle => TryGetPlatformHandle()?.Handle ?? 0;

    /// <summary>Scales the whole overlay so it keeps the same share of the game's width.</summary>
    public void SetScale(double value)
    {
        if (Math.Abs(value - scale) < 0.001)
        {
            return;
        }

        scale = value;
        Scaler.LayoutTransform = new ScaleTransform(value, value);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.MakeClickThrough(Handle);
    }

    /// <summary>
    /// Dragging is done by hand rather than with BeginMoveDrag: that runs the
    /// system's own move loop and can pull the foreground off the game. The
    /// anchor is the only record of where the overlay belongs, so a drag only
    /// reports how far the pointer went and the surface moves the anchor; the
    /// next placement pass puts the window there.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // The buttons in the positioning bar are not handles.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null)
        {
            return;
        }

        dragFrom = ToScreen(e);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (dragFrom is not PixelPoint from)
        {
            return;
        }

        PixelPoint now = ToScreen(e);
        if (now != from)
        {
            dragFrom = now;
            Dragged?.Invoke(now.X - from.X, now.Y - from.Y);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        dragFrom = null;
        e.Pointer.Capture(null);
    }

    // An extension on Visual, so it needs the receiver spelled out.
    private PixelPoint ToScreen(PointerEventArgs e) => this.PointToScreen(e.GetPosition(this));
}
