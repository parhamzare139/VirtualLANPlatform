using System.Runtime.InteropServices;
using System.Text;

namespace VirtualLANPlatform.Core.Services;

public enum Presence { Online, InGame, Away }

/// <summary>
/// Works out what the local user is doing, for the status dot next to their name on
/// everyone else's screen. Two signals, both cheap and both local:
/// <list type="bullet">
///   <item>"In game": the foreground window covers its whole monitor and is not a
///   maximized ordinary window. Exclusive and borderless fullscreen games both look
///   like that; a maximized browser does not (it carries WS_MAXIMIZE).</item>
///   <item>"Away": no keyboard or mouse input for <see cref="AppSettings.AfkMinutes"/>.
///   Fullscreen wins over idle — a controller player never touches the keyboard.</item>
/// </list>
/// Nothing is inspected beyond window geometry; no process names, no titles are sent.
/// </summary>
public sealed class PresenceMonitor : IDisposable
{
    public Presence Current { get; private set; } = Presence.Online;
    public event Action<Presence>? Changed;

    private readonly System.Threading.Timer _timer;
    private readonly int _myPid = Environment.ProcessId;

    public PresenceMonitor()
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4));
    }

    private void Tick()
    {
        try
        {
            Presence now = Evaluate();
            if (now == Current) return;
            Current = now;
            Changed?.Invoke(now);
        }
        catch { }
    }

    public static string Code(Presence p) => p switch
    {
        Presence.InGame => "game",
        Presence.Away   => "away",
        _               => "online"
    };

    public static Presence Parse(string? code) => code switch
    {
        "game" => Presence.InGame,
        "away" => Presence.Away,
        _      => Presence.Online
    };

    private Presence Evaluate()
    {
        if (IsForegroundFullscreen()) return Presence.InGame;

        int afkMinutes = Math.Max(1, AppSettings.I.AfkMinutes);
        if (IdleTime() >= TimeSpan.FromMinutes(afkMinutes)) return Presence.Away;

        return Presence.Online;
    }

    // ── Idle ─────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        uint ticks = unchecked((uint)Environment.TickCount) - info.dwTime;
        return TimeSpan.FromMilliseconds(ticks);
    }

    // ── Fullscreen detection ─────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool   GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool   GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern uint   GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern int    GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int  GWL_STYLE     = -16;
    private const int  WS_MAXIMIZE   = 0x0100_0000;

    private bool IsForegroundFullscreen()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return false;

        GetWindowThreadProcessId(h, out uint pid);
        if (pid == _myPid) return false;

        var cls = new StringBuilder(64);
        GetClassName(h, cls, cls.Capacity);
        string className = cls.ToString();
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow")
            return false;

        if ((GetWindowLong(h, GWL_STYLE) & WS_MAXIMIZE) != 0) return false;

        if (!GetWindowRect(h, out RECT r)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), ref mi)) return false;

        RECT m = mi.rcMonitor;
        // A pixel or two of slack: some engines size the window to the monitor minus
        // a border, and some over-size it by one.
        return r.Left <= m.Left + 2 && r.Top <= m.Top + 2 && r.Right >= m.Right - 2 && r.Bottom >= m.Bottom - 2
            && (m.Right - m.Left) >= 640;
    }

    public void Dispose() => _timer.Dispose();
}
