using Avalonia.Controls;
using Avalonia.Media;

namespace Anaphora.Overlay;

public partial class OverlayWindow : Window
{
    /// <summary>The game width, in DIPs, the AXAML sizes are drawn for.</summary>
    public const double ReferenceWidth = 1920;

    private double scale = 1;

    public OverlayWindow()
    {
        InitializeComponent();
    }

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
}
