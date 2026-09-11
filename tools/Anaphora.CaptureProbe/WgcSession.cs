using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Anaphora.CaptureProbe;

/// <summary>A single captured frame, already copied out to managed BGRA.</summary>
internal sealed record Frame(byte[] Pixels, int Width, int Height, int Stride);

/// <summary>
/// D3D11 device plus a running WGC session for one window. A rehearsal for
/// Anaphora.Capture, minus the ROI atlas: here every readback still drags the
/// whole frame to the CPU, which is exactly what the real pipeline must not do.
/// </summary>
internal sealed class WgcSession : IDisposable
{
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly Direct3D11CaptureFramePool framePool;
    private readonly GraphicsCaptureSession session;

    private WgcSession(
        ID3D11Device device,
        ID3D11DeviceContext context,
        GraphicsCaptureItem item,
        Direct3D11CaptureFramePool framePool,
        GraphicsCaptureSession session)
    {
        this.device = device;
        this.context = context;
        this.framePool = framePool;
        this.session = session;
        Item = item;
    }

    public GraphicsCaptureItem Item { get; }

    public static WgcSession Create(IntPtr hwnd, bool suppressBorder, Action<string>? log = null)
    {
        // BgraSupport is required before a D3D11 device may back a WinRT surface.
        ID3D11Device device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        ID3D11DeviceContext context = device.ImmediateContext;

        using IDXGIDevice dxgi = device.QueryInterface<IDXGIDevice>();
        Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr devicePtr);
        IDirect3DDevice winrtDevice;
        try
        {
            winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(devicePtr);
        }
        finally
        {
            Marshal.Release(devicePtr);
        }

        GraphicsCaptureItem item = CreateItemForWindow(hwnd);

        var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            item.Size);
        GraphicsCaptureSession session = framePool.CreateCaptureSession(item);

        if (suppressBorder)
        {
            try
            {
                session.IsBorderRequired = false;
                log?.Invoke("border         : suppressed (IsBorderRequired = false accepted)");
            }
            catch (Exception ex)
            {
                log?.Invoke($"border         : left on ({ex.GetType().Name}: {ex.Message.Trim()})");
            }
        }

        return new WgcSession(device, context, item, framePool, session);
    }

    /// <summary>
    /// Opens a session, reads back the first frame that arrives and tears the
    /// session down again. Null if nothing arrived within the timeout.
    /// </summary>
    public static Frame? CaptureOne(IntPtr hwnd, TimeSpan timeout)
    {
        using WgcSession capture = Create(hwnd, suppressBorder: true);

        Frame? result = null;
        Exception? failure = null;
        int taken = 0;
        using var done = new ManualResetEventSlim(false);

        capture.Start(pool =>
        {
            try
            {
                using Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
                if (frame is null || Interlocked.Exchange(ref taken, 1) == 1)
                {
                    return;
                }

                try
                {
                    result = capture.ReadBack(frame);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    done.Set();
                }
            }
            catch (ObjectDisposedException)
            {
                // A late frame racing the teardown below. Nothing to do.
            }
        });

        if (!done.Wait(timeout))
        {
            if (Interlocked.Exchange(ref taken, 1) == 0)
            {
                return null;
            }

            // A readback started right at the deadline; let it finish before the
            // device goes away underneath it.
            done.Wait(timeout);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        return result;
    }

    public void Start(TypedEventHandlerShim onFrame)
    {
        framePool.FrameArrived += (pool, _) => onFrame(pool);
        session.StartCapture();
    }

    /// <summary>
    /// Copies a capture frame into managed memory. Row pitch is honoured rather
    /// than assumed equal to width * 4 -- it happens to match on this machine, but
    /// that is a driver detail, not a guarantee.
    /// </summary>
    public Frame ReadBack(Direct3D11CaptureFrame frame)
    {
        using ID3D11Texture2D source = GetTexture(frame.Surface);
        Texture2DDescription description = source.Description;

        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;

        using ID3D11Texture2D staging = device.CreateTexture2D(description);
        context.CopyResource(staging, source);

        MappedSubresource map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int width = (int)description.Width;
            int height = (int)description.Height;
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];

            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(map.DataPointer + (y * (int)map.RowPitch), pixels, y * stride, stride);
            }

            return new Frame(pixels, width, height, stride);
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    public void Dispose()
    {
        session.Dispose();
        framePool.Dispose();
        context.Dispose();
        device.Dispose();
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        IGraphicsCaptureItemInterop interop = Native.GetCaptureItemInterop();
        IntPtr itemPtr = interop.CreateForWindow(hwnd, ref Native.IidGraphicsCaptureItem);
        try
        {
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
        }
        finally
        {
            Marshal.Release(itemPtr);
        }
    }

    private static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IntPtr unknown = MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(unknown);
            try
            {
                return new ID3D11Texture2D(access.GetInterface(ref Native.IidD3D11Texture2D));
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

    internal delegate void TypedEventHandlerShim(Direct3D11CaptureFramePool pool);
}
