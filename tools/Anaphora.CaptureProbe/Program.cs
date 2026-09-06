using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Anaphora.CaptureProbe;

/// <summary>
/// First-priority feasibility check: point WGC at the game window and prove that
/// real pixels come back. Answers, in order: is the window excluded from capture,
/// does a frame pool produce frames at all, at what rate, and is the content
/// actually there rather than a black rectangle.
/// </summary>
internal static class Program
{
    private const int ObserveSeconds = 3;

    private static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(Native.DpiAwarenessContextPerMonitorAwareV2);

        string processName = args.Length > 0 ? args[0] : "Endfield";
        string outputDirectory = args.Length > 1
            ? args[1]
            : Path.Combine(AppContext.BaseDirectory, "captures");

        Process? game = Process.GetProcessesByName(processName)
            .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (game is null)
        {
            Console.Error.WriteLine($"[FAIL] no process named '{processName}' with a top-level window.");
            return 1;
        }

        IntPtr hwnd = game.MainWindowHandle;
        Console.WriteLine($"process        : {game.ProcessName} (pid {game.Id})");
        Console.WriteLine($"hwnd           : 0x{hwnd:X}");
        Console.WriteLine($"title          : {Native.WindowText(hwnd)}");
        Console.WriteLine($"class          : {Native.WindowClass(hwnd)}");

        if (Native.GetWindowRect(hwnd, out Native.Rect windowRect))
        {
            Console.WriteLine($"window rect    : {windowRect}");
        }

        if (Native.GetClientRect(hwnd, out Native.Rect clientRect))
        {
            Console.WriteLine($"client rect    : {clientRect}");
        }

        Console.WriteLine($"window dpi     : {Native.GetDpiForWindow(hwnd)} (96 = 100%)");

        // The single make-or-break flag. If the game sets WDA_EXCLUDEFROMCAPTURE
        // there is no workaround short of not being a screen capture tool.
        if (Native.GetWindowDisplayAffinity(hwnd, out uint affinity))
        {
            Console.WriteLine($"display affin. : {Native.DescribeAffinity(affinity)}");
            if (affinity != Native.WdaNone)
            {
                Console.Error.WriteLine("[FAIL] the window opts out of capture; WGC can only return black frames.");
                return 2;
            }
        }
        else
        {
            Console.WriteLine($"display affin. : query failed (win32 error {Marshal.GetLastWin32Error()})");
        }

        Console.WriteLine($"WGC supported  : {GraphicsCaptureSession.IsSupported()}");
        Console.WriteLine();

        return Capture(hwnd, outputDirectory);
    }

    private static int Capture(IntPtr hwnd, string outputDirectory)
    {
        Frame? captured;

        using (ID3D11Device device = CreateDevice(out ID3D11DeviceContext context, out IDirect3DDevice winrtDevice))
        using (context)
        {
            GraphicsCaptureItem item = CreateItemForWindow(hwnd);
            Console.WriteLine($"item           : \"{item.DisplayName}\" {item.Size.Width}x{item.Size.Height}");

            using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                item.Size);
            using GraphicsCaptureSession session = framePool.CreateCaptureSession(item);

            // Not needed for this project -- the yellow border is acceptable -- but
            // worth knowing whether the machine grants it.
            try
            {
                session.IsBorderRequired = false;
                Console.WriteLine("border         : suppressed (IsBorderRequired = false accepted)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"border         : left on ({ex.GetType().Name}: {ex.Message.Trim()})");
            }

            int frameCount = 0;
            int wantCapture = 0;
            Frame? readBack = null;
            using var captureDone = new ManualResetEventSlim(false);

            framePool.FrameArrived += (pool, _) =>
            {
                using Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }

                Interlocked.Increment(ref frameCount);

                // Drop every frame until the observation window is over, then read
                // exactly one back. Same "never queue, never block the callback"
                // discipline the real capture loop will need.
                if (Interlocked.CompareExchange(ref wantCapture, 2, 1) != 1)
                {
                    return;
                }

                try
                {
                    readBack = ReadBack(device, context, frame);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[FAIL] readback threw: {ex}");
                }
                finally
                {
                    captureDone.Set();
                }
            };

            var stopwatch = Stopwatch.StartNew();
            session.StartCapture();
            Thread.Sleep(TimeSpan.FromSeconds(ObserveSeconds));
            double observed = stopwatch.Elapsed.TotalSeconds;
            int observedFrames = Volatile.Read(ref frameCount);

            Console.WriteLine($"frames         : {observedFrames} in {observed:F2}s ({observedFrames / observed:F1}/s)");

            if (observedFrames == 0)
            {
                Console.Error.WriteLine("[FAIL] the frame pool never fired. Nothing is being presented, or capture is blocked.");
                return 3;
            }

            Volatile.Write(ref wantCapture, 1);
            if (!captureDone.Wait(TimeSpan.FromSeconds(5)))
            {
                Console.Error.WriteLine("[FAIL] frames arrive but none could be read back.");
                return 4;
            }

            captured = readBack;
        }

        if (captured is null)
        {
            Console.Error.WriteLine("[FAIL] readback produced nothing.");
            return 4;
        }

        return Report(captured, outputDirectory);
    }

    private static int Report(Frame frame, string outputDirectory)
    {
        Console.WriteLine($"texture        : {frame.Width}x{frame.Height}, row pitch {frame.Stride}");
        Console.WriteLine();

        Statistics stats = Analyse(frame);
        Console.WriteLine($"luma mean      : {stats.MeanLuma:F2} / 255");
        Console.WriteLine($"luma range     : {stats.MinLuma} .. {stats.MaxLuma}");
        Console.WriteLine($"non-black      : {stats.NonBlackFraction * 100:F2}% of pixels");
        Console.WriteLine($"distinct cols  : {stats.DistinctColours} (sampled, capped at 4096)");
        Console.WriteLine();

        Directory.CreateDirectory(outputDirectory);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        string fullPath = Path.Combine(outputDirectory, $"{stamp}-full.png");
        Png.WriteBgra(fullPath, frame.Pixels, frame.Width, frame.Height, frame.Stride);
        Console.WriteLine($"wrote          : {fullPath}");

        int factor = Math.Max(1, frame.Width / 1280);
        byte[] small = Png.Downscale(frame.Pixels, frame.Width, frame.Height, frame.Stride, factor, out int sw, out int sh);
        string previewPath = Path.Combine(outputDirectory, $"{stamp}-preview.png");
        Png.WriteBgra(previewPath, small, sw, sh, sw * 4);
        Console.WriteLine($"wrote          : {previewPath} ({sw}x{sh})");

        // The top-left corner is where readable text is expected, so keep a
        // native-resolution crop that survives the downscale.
        int cropWidth = Math.Min(1400, frame.Width);
        int cropHeight = Math.Min(500, frame.Height);
        byte[] crop = Png.Crop(frame.Pixels, frame.Stride, 0, 0, cropWidth, cropHeight);
        string cropPath = Path.Combine(outputDirectory, $"{stamp}-topleft.png");
        Png.WriteBgra(cropPath, crop, cropWidth, cropHeight, cropWidth * 4);
        Console.WriteLine($"wrote          : {cropPath} ({cropWidth}x{cropHeight})");

        Console.WriteLine();
        if (stats.NonBlackFraction < 0.001)
        {
            Console.Error.WriteLine("[FAIL] the frame is black. Capture is being blocked or the surface is protected.");
            return 5;
        }

        Console.WriteLine("[OK] WGC returned real pixels from the game window.");
        return 0;
    }

    private static Statistics Analyse(Frame frame)
    {
        long lumaSum = 0;
        long total = 0;
        long nonBlack = 0;
        int min = 255;
        int max = 0;
        var colours = new HashSet<int>();

        // Every 4th pixel on every 4th row: plenty for a sanity check, 16x cheaper.
        for (int y = 0; y < frame.Height; y += 4)
        {
            int row = y * frame.Stride;
            for (int x = 0; x < frame.Width; x += 4)
            {
                int p = row + (x * 4);
                int b = frame.Pixels[p];
                int g = frame.Pixels[p + 1];
                int r = frame.Pixels[p + 2];
                int luma = ((r * 54) + (g * 183) + (b * 19)) >> 8;

                lumaSum += luma;
                total++;
                if (luma > 8)
                {
                    nonBlack++;
                }

                min = Math.Min(min, luma);
                max = Math.Max(max, luma);
                if (colours.Count < 4096)
                {
                    colours.Add((r << 16) | (g << 8) | b);
                }
            }
        }

        return new Statistics((double)lumaSum / total, min, max, (double)nonBlack / total, colours.Count);
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

    private static ID3D11Device CreateDevice(out ID3D11DeviceContext context, out IDirect3DDevice winrtDevice)
    {
        // BgraSupport is required before a D3D11 device may back a WinRT surface.
        ID3D11Device device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        context = device.ImmediateContext;

        using IDXGIDevice dxgi = device.QueryInterface<IDXGIDevice>();
        Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr devicePtr);
        try
        {
            winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(devicePtr);
        }
        finally
        {
            Marshal.Release(devicePtr);
        }

        return device;
    }

    private static Frame ReadBack(ID3D11Device device, ID3D11DeviceContext context, Direct3D11CaptureFrame frame)
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

    private sealed record Frame(byte[] Pixels, int Width, int Height, int Stride);

    private sealed record Statistics(
        double MeanLuma,
        int MinLuma,
        int MaxLuma,
        double NonBlackFraction,
        int DistinctColours);
}
