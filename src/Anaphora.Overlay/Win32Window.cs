using System.Drawing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using CorePixelRect = Anaphora.Core.PixelRect;

namespace Anaphora.Overlay;

/// <summary>The Win32 side of the overlay: styles, placement, and where the game is.</summary>
internal static class Win32Window
{
    private static readonly HWND Topmost = new(-1);

    /// <summary>
    /// Makes a window ignore the mouse and never take focus. WS_EX_TRANSPARENT
    /// only passes hits through to other processes on a layered window, hence
    /// WS_EX_LAYERED with full alpha; NOACTIVATE keeps the game in the
    /// foreground, TOOLWINDOW keeps the overlay out of Alt+Tab.
    /// </summary>
    public static void MakeClickThrough(nint handle)
    {
        if (handle == 0)
        {
            return;
        }

        var hwnd = new HWND(handle);
        // GetWindowLong, not the Ptr variant: extended styles fit in 32 bits, and CsWin32
        // cannot emit the 64-bit-only Ptr exports for an AnyCPU build.
        int style = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        style |= (int)(WINDOW_EX_STYLE.WS_EX_TRANSPARENT
            | WINDOW_EX_STYLE.WS_EX_LAYERED
            | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
            | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
            | WINDOW_EX_STYLE.WS_EX_TOPMOST);
        PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, style);
        PInvoke.SetLayeredWindowAttributes(hwnd, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
    }

    public static void Show(nint handle, int x, int y) =>
        PInvoke.SetWindowPos(
            new HWND(handle),
            Topmost,
            x,
            y,
            0,
            0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);

    public static void Hide(nint handle) =>
        PInvoke.SetWindowPos(
            new HWND(handle),
            default,
            0,
            0,
            0,
            0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_HIDEWINDOW);

    /// <summary>
    /// The game's client area in screen pixels, or null when the overlay should
    /// not be showing: the window is gone, minimised, or -- when
    /// <paramref name="requireForeground"/> -- something else is in front.
    /// </summary>
    public static CorePixelRect? ClientAreaOnScreen(nint game, bool requireForeground)
    {
        var hwnd = new HWND(game);
        if (game == 0 || !PInvoke.IsWindow(hwnd) || PInvoke.IsIconic(hwnd))
        {
            return null;
        }

        if (requireForeground && PInvoke.GetForegroundWindow() != hwnd)
        {
            return null;
        }

        if (!PInvoke.GetClientRect(hwnd, out RECT client) || client.right <= 0 || client.bottom <= 0)
        {
            return null;
        }

        var origin = new Point(0, 0);
        return PInvoke.ClientToScreen(hwnd, ref origin)
            ? new CorePixelRect(origin.X, origin.Y, client.right, client.bottom)
            : null;
    }

    /// <summary>Whether a click at a screen point would land on the overlay. For the self-check.</summary>
    public static bool HitsWindow(nint handle, int x, int y) =>
        PInvoke.WindowFromPoint(new Point(x, y)) == new HWND(handle);
}
