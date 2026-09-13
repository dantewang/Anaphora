using CorePixelRect = Anaphora.Core.PixelRect;

namespace Anaphora.Overlay;

/// <summary>Where the game window is on screen, as the overlay needs to know it.</summary>
public static class GameWindowGeometry
{
    /// <summary>
    /// The game's client area in screen pixels, or null when the overlay should
    /// not be showing: the window is gone, minimised, or -- when
    /// <paramref name="requireForeground"/> -- something else is in front of it.
    /// </summary>
    public static CorePixelRect? ClientAreaOnScreen(nint game, bool requireForeground) =>
        Win32Window.ClientAreaOnScreen(game, requireForeground);
}
