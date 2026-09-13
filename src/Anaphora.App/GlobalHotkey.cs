using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Anaphora.App;

/// <summary>A key combination as written in settings: "Ctrl+Alt+R", "Shift+F9".</summary>
internal sealed record HotkeyGesture(HOT_KEY_MODIFIERS Modifiers, uint VirtualKey, string Text)
{
    /// <returns>Null for an empty or unrecognised gesture.</returns>
    public static HotkeyGesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        HOT_KEY_MODIFIERS modifiers = 0;
        uint? key = null;
        foreach (string part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= HOT_KEY_MODIFIERS.MOD_CONTROL;
                    break;
                case "ALT":
                    modifiers |= HOT_KEY_MODIFIERS.MOD_ALT;
                    break;
                case "SHIFT":
                    modifiers |= HOT_KEY_MODIFIERS.MOD_SHIFT;
                    break;
                case "WIN":
                    modifiers |= HOT_KEY_MODIFIERS.MOD_WIN;
                    break;
                case var single when key is null && single.Length == 1 && char.IsAsciiLetterOrDigit(single[0]):
                    // VK codes for A-Z and 0-9 are their ASCII upper-case values.
                    key = single[0];
                    break;
                case var function when key is null && function.Length is 2 or 3 && function[0] == 'F'
                    && int.TryParse(function.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 24:
                    key = (uint)VIRTUAL_KEY.VK_F1 + (uint)(n - 1);
                    break;
                default:
                    return null;
            }
        }

        return key is uint vk ? new HotkeyGesture(modifiers, vk, text.Trim()) : null;
    }

    public override string ToString() => Text;
}

/// <summary>
/// A system-wide hotkey through RegisterHotKey -- the same documented mechanism
/// chat clients use for their screenshot shortcut. Windows tells the app only
/// that this one combination was pressed; no other keystroke is seen, and
/// nothing hooks the keyboard.
///
/// A registered combination is taken from every other program, the game
/// included, so it is only held while <see cref="Enabled"/> (the host enables it
/// while the game is in front).
///
/// RegisterHotKey delivers WM_HOTKEY to the thread that registered it. Avalonia
/// owns the UI thread's message loop, so the hotkey gets a small thread and loop
/// of its own, and <see cref="Pressed"/> is raised there.
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int Id = 1;
    private const uint Register = PInvoke.WM_APP + 1;
    private const uint Unregister = PInvoke.WM_APP + 2;

    private readonly HotkeyGesture gesture;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private uint threadId;
    private bool enabled;
    private bool reported;

    public GlobalHotkey(HotkeyGesture gesture)
    {
        this.gesture = gesture;
        thread = new Thread(Pump) { IsBackground = true, Name = $"hotkey {gesture}" };
        thread.Start();
        ready.Wait();
    }

    /// <summary>Hotkey thread.</summary>
    public event Action? Pressed;

    public HotkeyGesture Gesture => gesture;

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (value != enabled)
            {
                enabled = value;
                PInvoke.PostThreadMessage(threadId, value ? Register : Unregister, default, default);
            }
        }
    }

    public void Dispose()
    {
        PInvoke.PostThreadMessage(threadId, PInvoke.WM_QUIT, default, default);
        thread.Join(TimeSpan.FromSeconds(1));
        ready.Dispose();
    }

    private void Pump()
    {
        // The queue exists only once the thread has touched it; posts before that are lost.
        PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
        threadId = PInvoke.GetCurrentThreadId();
        ready.Set();

        bool registered = false;
        while (PInvoke.GetMessage(out MSG message, HWND.Null, 0, 0) > 0)
        {
            switch (message.message)
            {
                case PInvoke.WM_HOTKEY when (int)message.wParam.Value == Id:
                    Pressed?.Invoke();
                    break;

                case Register when !registered:
                    registered = PInvoke.RegisterHotKey(HWND.Null, Id, gesture.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT, gesture.VirtualKey);
                    if (!reported)
                    {
                        // Once either way: the host toggles this every time the game loses focus.
                        reported = true;
                        Log.Write(registered
                            ? $"hotkey {gesture} registered"
                            : $"[warn] hotkey {gesture} not registered (error {Marshal.GetLastPInvokeError()}); another program probably holds it");
                    }

                    break;

                case Unregister when registered:
                    PInvoke.UnregisterHotKey(HWND.Null, Id);
                    registered = false;
                    break;
            }
        }

        if (registered)
        {
            PInvoke.UnregisterHotKey(HWND.Null, Id);
        }
    }
}
