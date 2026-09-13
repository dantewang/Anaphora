using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Anaphora.Capture;

/// <summary>
/// The three seams between WinRT and D3D11: making a capture item from an HWND,
/// wrapping a D3D11 device for WinRT, and unwrapping a captured surface back
/// into a texture.
///
/// Hand-written rather than generated. These are COM interfaces that have to
/// be cast from CsWinRT-projected objects, which is where CsWin32's COM output
/// and CsWinRT's marshalling disagree about who owns what. This exact code has
/// already been proven against the game in the capture probe; plain Win32
/// functions still go through CsWin32.
/// </summary>
internal static class WinRtInterop
{
    private static Guid iidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static Guid iidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static Guid iidD3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    public static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        const string ClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";

        WindowsCreateString(ClassName, ClassName.Length, out nint classId);
        IGraphicsCaptureItemInterop interop;
        try
        {
            RoGetActivationFactory(classId, ref iidGraphicsCaptureItemInterop, out object factory);
            interop = (IGraphicsCaptureItemInterop)factory;
        }
        finally
        {
            WindowsDeleteString(classId);
        }

        nint itemPointer = interop.CreateForWindow(hwnd, ref iidGraphicsCaptureItem);
        try
        {
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    public static IDirect3DDevice WrapDevice(ID3D11Device device)
    {
        using IDXGIDevice dxgi = device.QueryInterface<IDXGIDevice>();
        CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out nint wrapped);
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(wrapped);
        }
        finally
        {
            Marshal.Release(wrapped);
        }
    }

    public static ID3D11Texture2D TextureFrom(IDirect3DSurface surface)
    {
        nint unknown = MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(unknown);
            try
            {
                return new ID3D11Texture2D(access.GetInterface(ref iidD3D11Texture2D));
            }
            finally
            {
                Marshal.ReleaseComObject(access);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out nint hstring);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsDeleteString(nint hstring);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(
        nint activatableClassId,
        [In] ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    [DllImport("d3d11.dll", PreserveSig = false)]
    private static extern void CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, [In] ref Guid iid);

        nint CreateForMonitor(nint monitor, [In] ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface([In] ref Guid iid);
    }
}
