using System.Runtime.InteropServices;

namespace FusionRpg.Injector.Hud;

/// <summary>
/// Real-cursor input (game-control module <c>control-cursor</c>). Win32
/// <c>SendInput</c> moves the operator's actual mouse — the only tier that can prove the
/// true UX input path (white-box verbs prove wiring). Disruptive by nature: every entry
/// point refuses unless explicitly confirmed, foreground, throttled, and clamped to the
/// game window. P/Invoke lives here and nowhere else, per the <c>Win32.cs</c> rule.
/// </summary>
static class Win32Input
{
    // Structural (not tunable): bounds OS-cursor hijack. Not the balance surface.
    internal const int CursorMinGapMs = 2000;

    const int INPUT_MOUSE = 0;
    const int MOUSEEVENTF_MOVE = 0x0001;
    const int MOUSEEVENTF_LEFTDOWN = 0x0002;
    const int MOUSEEVENTF_LEFTUP = 0x0004;
    const int MOUSEEVENTF_ABSOLUTE = 0x8000;
    // Structural (tunables-ssot.md T2) — fixed Win32 GetSystemMetrics() codes below,
    // not balance. Same rule Hud/Win32.cs already applies to its own constants.
    const int SM_XVIRTUALSCREEN = 76;
    const int SM_YVIRTUALSCREEN = 77;
    const int SM_CXVIRTUALSCREEN = 78;
    const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public int dwFlags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public int type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);

    /// <returns>Null when the move/click may proceed; otherwise the refusal reason.</returns>
    public static string? Check(bool confirmed, long nowMs, ref long lastMs)
    {
        if (!confirmed) return "pass confirmedLiveCursor:true to move the real cursor";
        var game = GameWindow();
        if (game == IntPtr.Zero) return "game window handle unavailable";
        if (GetForegroundWindow() != game) return "game window is not foreground";
        if (nowMs - Volatile.Read(ref lastMs) < CursorMinGapMs) return "throttled (one cursor action per 2 s)";
        return null;
    }

    public static bool TryMoveClick(int x, int y, bool click, out string error)
    {
        error = "";
        try
        {
            var game = GameWindow();
            if (!ClientToAbsolute(x, y, out var ax, out var ay, out error)) return false;
            var size = Marshal.SizeOf<INPUT>();
            var move = new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = ax,
                    dy = ay,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
                }
            };
            if (SendInput(1, new[] { move }, size) == 0) { error = "SendInput move failed"; return false; }
            if (click)
            {
                var down = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } };
                var up = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } };
                if (SendInput(1, new[] { down }, size) == 0) { error = "SendInput button-down failed"; return false; }
                if (SendInput(1, new[] { up }, size) == 0) { error = "SendInput button-up failed"; return false; }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    static IntPtr GameWindow()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; }
        catch { return IntPtr.Zero; }
    }

    static bool ClientToAbsolute(int x, int y, out int ax, out int ay, out string error)
    {
        ax = 0;
        ay = 0;
        error = "";
        var game = GameWindow();
        if (game == IntPtr.Zero) { error = "game window handle unavailable"; return false; }
        // Client coords, not window-rect coords: the rect includes title bar and borders,
        // which would misalign every click by the chrome size. Clamp against the client box.
        if (!GetClientRect(game, out var client)) { error = "game client rect unreadable"; return false; }
        var cw = Math.Max(1, client.Right - client.Left);
        var ch = Math.Max(1, client.Bottom - client.Top);
        if (x < 0 || x >= cw || y < 0 || y >= ch)
        {
            error = $"point ({x},{y}) is outside the game client area {cw}x{ch} — refused, never clamped into a corner click";
            return false;
        }
        var origin = new POINT { X = x, Y = y };
        if (!ClientToScreen(game, ref origin)) { error = "client-to-screen failed"; return false; }
        var px = origin.X;
        var py = origin.Y;
        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        var vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        ax = (int)Math.Round((px - vx) * (65535.0 / (vw - 1)));
        ay = (int)Math.Round((py - vy) * (65535.0 / (vh - 1)));
        return true;
    }
}
