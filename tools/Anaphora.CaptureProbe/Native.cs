using System.Runtime.InteropServices;
using System.Text;

namespace Anaphora.CaptureProbe;

/// <summary>
/// Hand-written interop for the probe. The real projects use CsWin32; this tool
/// stays self-contained so a source-generator hiccup can never be mistaken for a
/// capture failure.
/// </summary>
internal static class Native
{
    internal static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    internal const uint WdaNone = 0x00;
    internal const uint WdaMonitor = 0x01;
    internal const uint WdaExcludeFromCapture = 0x11;

    // The two IIDs the WinRT/D3D bridge needs. GraphicsCaptureItem's is passed to
    // the interop factory; the other unwraps IDirect3DSurface back into a texture.
    internal static Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    internal static Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    internal static Guid IidD3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public override string ToString() => $"{Width}x{Height} @ ({Left},{Top})";
    }

    [DllImport("user32.dll")]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    [DllImport("user32.dll")]
    internal static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hwnd);

    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("combase.dll", PreserveSig = false)]
    internal static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = false)]
    internal static extern void WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = false)]
    internal static extern void RoGetActivationFactory(
        IntPtr activatableClassId,
        [In] ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    [DllImport("d3d11.dll", PreserveSig = false)]
    internal static extern void CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    internal static string WindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "(none)";
    }

    internal static string WindowClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "(none)";
    }

    internal static string DescribeAffinity(uint affinity) => affinity switch
    {
        WdaNone => "WDA_NONE (capturable)",
        WdaMonitor => "WDA_MONITOR (capture blocked, legacy)",
        WdaExcludeFromCapture => "WDA_EXCLUDEFROMCAPTURE (capture blocked)",
        _ => $"unknown (0x{affinity:X})",
    };

    internal static IGraphicsCaptureItemInterop GetCaptureItemInterop()
    {
        WindowsCreateString(
            "Windows.Graphics.Capture.GraphicsCaptureItem",
            "Windows.Graphics.Capture.GraphicsCaptureItem".Length,
            out IntPtr classId);
        try
        {
            RoGetActivationFactory(classId, ref IidGraphicsCaptureItemInterop, out object factory);
            return (IGraphicsCaptureItemInterop)factory;
        }
        finally
        {
            WindowsDeleteString(classId);
        }
    }
}

[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow(IntPtr window, [In] ref Guid iid);

    IntPtr CreateForMonitor(IntPtr monitor, [In] ref Guid iid);
}

[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface([In] ref Guid iid);
}
