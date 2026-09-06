using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace Anaphora.CaptureProbe;

/// <summary>
/// Two jobs, one binary.
///
/// <c>probe</c> is the feasibility check: point WGC at the game window and prove
/// real pixels come back -- display affinity, frame rate, luma statistics, one
/// still.
///
/// <c>burst</c> collects material: one frame every N ms for a fixed stretch, so
/// ROIs can be marked against real gameplay instead of a menu screen.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(Native.DpiAwarenessContextPerMonitorAwareV2);

        // The verb is optional so the original "probe <process> <dir>" form still works.
        string verb = "probe";
        int offset = 0;
        if (args.Length > 0 && args[0] is "probe" or "burst" or "crop" or "montage")
        {
            verb = args[0];
            offset = 1;
        }

        string[] rest = args[offset..];

        // These work on files that already exist; no game, no window needed.
        if (verb == "crop")
        {
            return CropCommand.Run(rest);
        }

        if (verb == "montage")
        {
            return MontageCommand.Run(rest);
        }

        string processName = rest.Length > 0 ? rest[0] : "Endfield";
        string outputDirectory = rest.Length > 1
            ? Path.GetFullPath(rest[1])
            : Path.Combine(AppContext.BaseDirectory, "captures");

        IntPtr hwnd = ResolveWindow(processName);
        if (hwnd == IntPtr.Zero)
        {
            return 1;
        }

        if (!DescribeWindow(hwnd, out int failure))
        {
            return failure;
        }

        Console.WriteLine();

        if (verb == "burst")
        {
            int duration = rest.Length > 2 ? Parse(rest[2], 60) : 60;
            int interval = rest.Length > 3 ? Parse(rest[3], 1000) : 1000;
            int leadIn = rest.Length > 4 ? Parse(rest[4], 0) : 0;
            int fullEvery = rest.Length > 5 ? Math.Max(1, Parse(rest[5], 4)) : 4;
            return BurstCommand.Run(hwnd, outputDirectory, duration, interval, leadIn, fullEvery);
        }

        return ProbeCommand.Run(hwnd, outputDirectory);
    }

    private static int Parse(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;

    private static IntPtr ResolveWindow(string processName)
    {
        Process? game = Process.GetProcessesByName(processName)
            .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (game is null)
        {
            Console.Error.WriteLine($"[FAIL] no process named '{processName}' with a top-level window.");
            return IntPtr.Zero;
        }

        Console.WriteLine($"process        : {game.ProcessName} (pid {game.Id})");
        return game.MainWindowHandle;
    }

    private static bool DescribeWindow(IntPtr hwnd, out int failure)
    {
        failure = 0;

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
                failure = 2;
                return false;
            }
        }
        else
        {
            Console.WriteLine($"display affin. : query failed (win32 error {Marshal.GetLastWin32Error()})");
        }

        Console.WriteLine($"WGC supported  : {GraphicsCaptureSession.IsSupported()}");
        return true;
    }
}
