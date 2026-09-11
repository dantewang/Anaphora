using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Anaphora.CaptureProbe;

/// <summary>
/// Everything the probe checks about the game window before it tries to capture
/// it. Looked up fresh on every call: the server outlives game restarts, and a
/// stale HWND would just capture nothing.
/// </summary>
internal sealed record GameWindow(
    string ProcessName,
    int ProcessId,
    IntPtr Handle,
    string Title,
    string Class,
    Native.Rect? WindowRect,
    Native.Rect? ClientRect,
    uint Dpi,
    uint? DisplayAffinity,
    int AffinityError,
    bool Minimised)
{
    /// <summary>
    /// The single make-or-break flag. If the game sets WDA_EXCLUDEFROMCAPTURE
    /// there is no workaround short of not being a screen capture tool. A failed
    /// query is not proof of either, so it does not block.
    /// </summary>
    public bool Capturable => DisplayAffinity is null or Native.WdaNone;

    public string AffinityText => DisplayAffinity is uint affinity
        ? Native.DescribeAffinity(affinity)
        : $"query failed (win32 error {AffinityError})";

    public static GameWindow? Find(string processName)
    {
        Process[] candidates = Process.GetProcessesByName(processName);
        try
        {
            Process? game = candidates.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
            if (game is null)
            {
                return null;
            }

            IntPtr hwnd = game.MainWindowHandle;
            bool affinityKnown = Native.GetWindowDisplayAffinity(hwnd, out uint affinity);
            int affinityError = affinityKnown ? 0 : Marshal.GetLastWin32Error();

            return new GameWindow(
                game.ProcessName,
                game.Id,
                hwnd,
                Native.WindowText(hwnd),
                Native.WindowClass(hwnd),
                Native.GetWindowRect(hwnd, out Native.Rect windowRect) ? windowRect : null,
                Native.GetClientRect(hwnd, out Native.Rect clientRect) ? clientRect : null,
                Native.GetDpiForWindow(hwnd),
                affinityKnown ? affinity : null,
                affinityError,
                Native.IsIconic(hwnd));
        }
        finally
        {
            foreach (Process candidate in candidates)
            {
                candidate.Dispose();
            }
        }
    }
}
