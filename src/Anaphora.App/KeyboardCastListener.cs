using System.Diagnostics;
using Anaphora.Analysis;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Anaphora.App;

/// <summary>
/// Watches the keys the rotation uses -- 1, 2, 3, 4 and E -- and turns real
/// presses into <see cref="CastInput"/>s.
///
/// This is a poll of <c>GetAsyncKeyState</c>, not a keyboard hook and not
/// injection: it asks Windows whether five known keys are down, and does so
/// only while the game window owns the foreground. It reads no other key and
/// does not take any key away from the game. That distinction is what keeps the
/// anti-cheat story the same as the capture pipeline's.
///
/// A thread polls a few hundred times a second. The low bit of
/// <c>GetAsyncKeyState</c> also says whether a key went down since the previous
/// poll, so a tap shorter than the poll interval is not dropped.
/// </summary>
internal sealed class KeyboardCastListener : IDisposable
{
    private const int PressedSinceLastPoll = 0x0001;
    private const int CurrentlyDown = 0x8000;
    private const int PollMilliseconds = 4;
    private const int IdleMilliseconds = 25;

    private static readonly (GameKey Key, int VirtualKey)[] Watched =
    [
        (GameKey.Slot1, 0x31),
        (GameKey.Slot2, 0x32),
        (GameKey.Slot3, 0x33),
        (GameKey.Slot4, 0x34),
        (GameKey.Chain, 0x45),
    ];

    private readonly Stopwatch clock;
    private readonly KeyboardCastReader reader;
    private readonly Thread thread;
    private readonly AutoResetEvent stop = new(false);
    private readonly bool[] down = new bool[6];

    private long target;
    private volatile bool running = true;
    private bool disposed;

    public KeyboardCastListener(Stopwatch clock, TimeSpan ultimateHold)
    {
        this.clock = clock;
        reader = new KeyboardCastReader(ultimateHold);
        reader.Cast += cast => Cast?.Invoke(cast);

        thread = new Thread(Poll) { IsBackground = true, Name = "keyboard casts" };
        thread.Start();
    }

    /// <summary>Raised on the polling thread.</summary>
    public event Action<CastInput>? Cast;

    /// <summary>The game window whose foreground ownership gates reading; 0 disables it.</summary>
    public nint TargetWindow
    {
        get => (nint)Interlocked.Read(ref target);
        set => Interlocked.Exchange(ref target, value);
    }

    /// <summary>How long a number key must be held to count as an ultimate.</summary>
    public TimeSpan UltimateHold
    {
        get => reader.UltimateHold;
        set => reader.UltimateHold = value;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        running = false;
        stop.Set();
        thread.Join(TimeSpan.FromSeconds(1));
        stop.Dispose();
    }

    private void Poll()
    {
        bool active = false;
        while (running)
        {
            if (!IsForeground(TargetWindow))
            {
                if (active)
                {
                    reader.Reset();
                    active = false;
                }

                stop.WaitOne(IdleMilliseconds);
                continue;
            }

            if (!active)
            {
                // A key already held when the game came forward is not a new
                // press. Record it as down so its release cannot fire either.
                foreach ((GameKey key, int virtualKey) in Watched)
                {
                    down[(int)key] = IsDown(virtualKey);
                }

                active = true;
            }

            TimeSpan now = clock.Elapsed;
            foreach ((GameKey key, int virtualKey) in Watched)
            {
                short state = PInvoke.GetAsyncKeyState(virtualKey);
                bool isDown = (state & CurrentlyDown) != 0;
                bool pressed = (state & PressedSinceLastPoll) != 0;
                bool wasDown = down[(int)key];

                if (isDown && !wasDown)
                {
                    down[(int)key] = true;
                    reader.Feed(new KeyTransition(key, true, now));
                }
                else if (!isDown && wasDown)
                {
                    down[(int)key] = false;
                    reader.Feed(new KeyTransition(key, false, now));
                }
                else if (!isDown && !wasDown && pressed)
                {
                    // The whole press happened between two polls. The low bit
                    // is the only trace of it; treat it as a quick tap.
                    reader.Feed(new KeyTransition(key, true, now));
                    reader.Feed(new KeyTransition(key, false, now));
                }
            }

            reader.Tick(now);
            stop.WaitOne(PollMilliseconds);
        }

        reader.Reset();
    }

    private static bool IsDown(int virtualKey) => (PInvoke.GetAsyncKeyState(virtualKey) & CurrentlyDown) != 0;

    private static bool IsForeground(nint target)
    {
        if (target == 0)
        {
            return false;
        }

        HWND foreground = PInvoke.GetForegroundWindow();
        if (foreground.IsNull)
        {
            return false;
        }

        HWND window = new(target);
        if (foreground == window)
        {
            return true;
        }

        return PInvoke.GetAncestor(foreground, GET_ANCESTOR_FLAGS.GA_ROOT) == window;
    }
}
