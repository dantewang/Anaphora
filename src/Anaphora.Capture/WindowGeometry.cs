using System.Drawing;
using Anaphora.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.HiDpi;

namespace Anaphora.Capture;

/// <summary>Where the game's client area is, in the coordinates of its window capture.</summary>
internal static class WindowGeometry
{
    /// <summary>
    /// A window capture spans the DWM extended frame bounds -- the visible
    /// window, without the invisible resize borders GetWindowRect includes.
    /// The client area sits inside that, offset by the title bar and frame.
    /// For a borderless game the two coincide and the offset is zero.
    /// </summary>
    public static unsafe PixelRect ClientAreaInCapture(nint handle, int textureWidth, int textureHeight)
    {
        var hwnd = new HWND(handle);

        if (!PInvoke.GetClientRect(hwnd, out RECT client) || client.right <= 0 || client.bottom <= 0)
        {
            return default;
        }

        var origin = new Point(0, 0);
        if (!PInvoke.ClientToScreen(hwnd, ref origin))
        {
            return default;
        }

        RECT frame;
        if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &frame, (uint)sizeof(RECT)).Failed
            && !PInvoke.GetWindowRect(hwnd, out frame))
        {
            return default;
        }

        int left = Math.Clamp(origin.X - frame.left, 0, textureWidth);
        int top = Math.Clamp(origin.Y - frame.top, 0, textureHeight);
        int width = Math.Clamp(client.right, 0, textureWidth - left);
        int height = Math.Clamp(client.bottom, 0, textureHeight - top);

        return new PixelRect(left, top, width, height);
    }

    public static bool IsLiveWindow(nint handle) => PInvoke.IsWindow(new HWND(handle));

    public static bool IsMinimised(nint handle) => PInvoke.IsIconic(new HWND(handle));

    /// <summary>
    /// Per-monitor v2 is not optional. Under anything less, GetClientRect and the
    /// DWM bounds come back in logical pixels while the capture texture is
    /// physical, and at 150% scaling every ROI lands two-thirds of the way to
    /// where it should.
    /// </summary>
    public static bool IsPerMonitorV2()
    {
        var perMonitorV2 = new DPI_AWARENESS_CONTEXT(-4);
        return PInvoke.AreDpiAwarenessContextsEqual(PInvoke.GetThreadDpiAwarenessContext(), perMonitorV2);
    }
}
