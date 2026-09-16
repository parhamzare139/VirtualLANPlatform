using System.Runtime.InteropServices;

namespace VirtualLANPlatform.Core.Services;

/// <summary>
/// System-wide key/mouse-button watcher for push-to-talk. Low-level hooks rather than
/// RegisterHotKey because a hotkey only reports the press, and talking needs the
/// release too; and because the key must keep working while a fullscreen game has
/// the focus, which a window-scoped handler never sees.
///
/// The hooks live on their own thread with their own message loop. Windows delivers
/// a low-level hook on the thread that installed it and silently removes a hook whose
/// thread stops answering; a UI thread that stalls on netsh or a dialog for a moment
/// would lose push-to-talk without anyone knowing. A dedicated thread never stalls.
/// Consequently every event here fires on that thread — marshal to the UI as needed.
/// </summary>
public sealed class PushToTalkHook : IDisposable
{
    public const int VK_MBUTTON  = 0x04;
    public const int VK_XBUTTON1 = 0x05;
    public const int VK_XBUTTON2 = 0x06;

    /// <summary>Virtual-key code being watched. Mouse buttons use the VK_*BUTTON codes.</summary>
    public int  Key     { get; set; }
    public bool Enabled { get; set; }

    /// <summary>True while the key is physically held.</summary>
    public bool IsDown { get; private set; }

    /// <summary>Raised on the hook thread on every down / up transition of <see cref="Key"/>.</summary>
    public event Action<bool>? Changed;

    /// <summary>While set, the next key or mouse-button press is reported here instead of
    /// being matched — the settings page uses it to let the user pick a key.</summary>
    public bool Capturing { get; set; }
    public event Action<int>? Captured;

    private IntPtr _kbHook, _mouseHook;
    private Thread? _thread;
    private uint    _threadId;
    // Kept in fields so the GC never collects the delegates behind the native hooks.
    private readonly LowLevelProc _kbProc;
    private readonly LowLevelProc _mouseProc;

    public PushToTalkHook()
    {
        _kbProc    = KeyboardProc;
        _mouseProc = MouseProc;
    }

    public void Install()
    {
        if (_thread != null) return;
        _thread = new Thread(HookThread) { IsBackground = true, Name = "PTT-Hook" };
        _thread.Start();
    }

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();
        IntPtr module = GetModuleHandle(null);
        _kbHook    = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc,    module, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL,    _mouseProc, module, 0);
        if (_kbHook == IntPtr.Zero) AppLog.Warn("ptt", $"keyboard hook failed ({Marshal.GetLastWin32Error()})");

        // Plain message pump; WM_QUIT from Dispose ends it.
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_kbHook    != IntPtr.Zero) { UnhookWindowsHookEx(_kbHook);    _kbHook    = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }

    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            int vk  = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode is the first field
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up   = msg is WM_KEYUP   or WM_SYSKEYUP;

            if (Capturing && down)
            {
                Capturing = false;
                if (vk != VK_ESCAPE) Captured?.Invoke(vk);
            }
            else if (Enabled && vk == Key && (down || up))
            {
                Transition(down);
            }
        }
        return CallNextHookEx(_kbHook, code, wParam, lParam);
    }

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            int vk = 0; bool down = false, up = false;
            switch (msg)
            {
                case WM_MBUTTONDOWN: vk = VK_MBUTTON; down = true; break;
                case WM_MBUTTONUP:   vk = VK_MBUTTON; up   = true; break;
                case WM_XBUTTONDOWN:
                case WM_XBUTTONUP:
                {
                    // MSLLHOOKSTRUCT: POINT pt (8 bytes) then DWORD mouseData; HIWORD says which X button.
                    int mouseData = Marshal.ReadInt32(lParam, 8);
                    int which = (mouseData >> 16) & 0xFFFF;
                    vk   = which == 2 ? VK_XBUTTON2 : VK_XBUTTON1;
                    down = msg == WM_XBUTTONDOWN;
                    up   = !down;
                    break;
                }
            }

            if (vk != 0)
            {
                if (Capturing && down)
                {
                    Capturing = false;
                    Captured?.Invoke(vk);
                }
                else if (Enabled && vk == Key && (down || up))
                {
                    Transition(down);
                }
            }
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void Transition(bool down)
    {
        // Key repeat delivers WM_KEYDOWN over and over while held; only edges matter.
        if (IsDown == down) return;
        IsDown = down;
        Changed?.Invoke(down);
    }

    /// <summary>Human-readable name for a watched key, for the settings page.</summary>
    public static string KeyName(int vk)
    {
        switch (vk)
        {
            case VK_MBUTTON:  return "Mouse 3";
            case VK_XBUTTON1: return "Mouse 4";
            case VK_XBUTTON2: return "Mouse 5";
            case 0x20:        return "Space";
            case 0x11: case 0xA2: return "Ctrl";
            case 0xA3:        return "Right Ctrl";
            case 0x10: case 0xA0: return "Shift";
            case 0xA1:        return "Right Shift";
            case 0x12: case 0xA4: return "Alt";
            case 0xA5:        return "Right Alt";
            case 0x14:        return "Caps Lock";
            case 0x09:        return "Tab";
            case 0xC0:        return "`";
        }
        try
        {
            var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk);
            if (key != System.Windows.Input.Key.None) return key.ToString();
        }
        catch { }
        return $"0x{vk:X2}";
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(500);
        _thread = null;
    }

    // ── Native ───────────────────────────────────────────────────────────────

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public int ptX; public int ptY;
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL    = 14;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_SYSKEYUP    = 0x0105;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP   = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP   = 0x020C;
    private const int VK_ESCAPE      = 0x1B;
    private const uint WM_QUIT       = 0x0012;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
}
