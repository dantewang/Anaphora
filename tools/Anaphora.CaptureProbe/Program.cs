using System.Globalization;
using Windows.Graphics.Capture;

namespace Anaphora.CaptureProbe;

/// <summary>
/// Capture jobs and file jobs, one binary.
///
/// <c>probe</c> is the feasibility check: point WGC at the game window and prove
/// real pixels come back -- display affinity, frame rate, luma statistics, one
/// still.
///
/// <c>burst</c> collects material: one frame every N ms for a fixed stretch, so
/// ROIs can be marked against real gameplay instead of a menu screen.
///
/// <c>serve</c> puts both behind HTTP, so the machine that runs the game only has
/// to capture and a different machine can do the rest.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(Native.DpiAwarenessContextPerMonitorAwareV2);

        // The verb is optional so the original "probe <process> <dir>" form still works.
        string verb = "probe";
        int offset = 0;
        if (args.Length > 0 && args[0] is "probe" or "burst" or "serve" or "hud" or "replay" or "radial" or "hash" or "crop" or "montage" or "sample" or "mask")
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

        if (verb == "sample")
        {
            return SampleCommand.Run(rest);
        }

        if (verb == "mask")
        {
            return MaskCommand.Run(rest);
        }

        // The server looks the window up per request; the game need not be running yet.
        if (verb == "serve")
        {
            return ServeCommand.Run(rest);
        }

        if (verb == "hud")
        {
            return HudCommand.Run(rest);
        }

        if (verb == "replay")
        {
            return ReplayCommand.Run(rest);
        }

        if (verb == "radial")
        {
            return RadialCommand.Run(rest);
        }

        if (verb == "hash")
        {
            return HashCommand.Run(rest);
        }

        string processName = rest.Length > 0 ? rest[0] : "Endfield";
        string outputDirectory = rest.Length > 1
            ? Path.GetFullPath(rest[1])
            : Path.Combine(AppContext.BaseDirectory, "captures");

        GameWindow? window = GameWindow.Find(processName);
        if (window is null)
        {
            Console.Error.WriteLine($"[FAIL] no process named '{processName}' with a top-level window.");
            return 1;
        }

        Console.WriteLine($"process        : {window.ProcessName} (pid {window.ProcessId})");
        if (!DescribeWindow(window, out int failure))
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
            return BurstCommand.Run(window.Handle, outputDirectory, duration, interval, leadIn, fullEvery);
        }

        return ProbeCommand.Run(window.Handle, outputDirectory);
    }

    private static int Parse(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;

    private static bool DescribeWindow(GameWindow window, out int failure)
    {
        failure = 0;

        Console.WriteLine($"hwnd           : 0x{window.Handle:X}");
        Console.WriteLine($"title          : {window.Title}");
        Console.WriteLine($"class          : {window.Class}");

        if (window.WindowRect is Native.Rect windowRect)
        {
            Console.WriteLine($"window rect    : {windowRect}");
        }

        if (window.ClientRect is Native.Rect clientRect)
        {
            Console.WriteLine($"client rect    : {clientRect}");
        }

        Console.WriteLine($"window dpi     : {window.Dpi} (96 = 100%)");
        Console.WriteLine($"display affin. : {window.AffinityText}");

        if (!window.Capturable)
        {
            Console.Error.WriteLine("[FAIL] the window opts out of capture; WGC can only return black frames.");
            failure = 2;
            return false;
        }

        Console.WriteLine($"WGC supported  : {GraphicsCaptureSession.IsSupported()}");
        return true;
    }
}
