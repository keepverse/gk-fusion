using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace FusionRpg.Bridge.Hud;

/// <summary>
/// All P/Invoke for the in-game overlay window lives here, so the rest of the injector never
/// handles a raw HWND — the same rule the Launcher applies to <c>GameWindowInterop</c>.
/// </summary>
static class Win32
{
    // Structural (tunables-ssot.md T2) — a fixed Win32 API constant, not balance.
    public const uint PM_REMOVE = 0x0001;

    /// <summary>Sent to our pumped thread when the registered overlay hotkey fires.</summary>
    // Structural (tunables-ssot.md T2) — a fixed Win32 message id, not balance.
    public const uint WM_HOTKEY = 0x0312;

    /// <summary>Our hotkey id. Scoped to this thread, so it cannot clash with the launcher's.</summary>
    public const int OverlayHotKeyId = 0xF05A;

    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_EX_TOOLWINDOW = 0x00000080; // keep it out of the alt-tab list
    const int SW_HIDE = 0;
    // Structural (tunables-ssot.md T2), both below — fixed Win32 ShowWindow() command codes, not balance.
    const int SW_SHOWNOACTIVATE = 4;
    // Structural (tunables-ssot.md T2) — see SW_SHOWNOACTIVATE above.
    const int SW_SHOW = 5;
    const uint MOD_NOREPEAT = 0x4000;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_SHOWWINDOW = 0x0040;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    /// <summary>
    /// The SDK P/Invokes <c>WebView2Loader.dll</c> by bare name, and the process search path is
    /// rooted at the game exe — not our plugin folder. Loading it by absolute path first means
    /// the later bare-name import binds to the already-loaded module.
    /// </summary>
    public static bool TryPreloadWebView2Loader(string pluginDir, out string note)
    {
        const string dll = "WebView2Loader.dll";
        var candidates = new[]
        {
            Path.Combine(pluginDir ?? "", dll),
            Path.Combine(pluginDir ?? "", "runtimes", "win-x64", "native", dll)
        };

        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                if (NativeLibrary.TryLoad(path, out _))
                {
                    note = "loaded " + path;
                    return true;
                }
            }
            catch { /* try the next candidate */ }
        }

        note = $"{dll} not found beside the plugin (looked in {string.Join(" and ", candidates)}) — " +
               "the in-game view cannot start; overlayHost=launcher still works.";
        return false;
    }

    public static IntPtr CreateOverlayWindow()
    {
        _keepAlive = DefWindowProc;
        var cls = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_keepAlive),
            hInstance = GetModuleHandle(null),
            lpszClassName = "FusionRpgOverlayView"
        };
        if (RegisterClass(ref cls) == 0)
        {
            var err = Marshal.GetLastWin32Error();
            if (err != 1410) return IntPtr.Zero; // 1410 = class already registered
        }

        return CreateWindowEx(WS_EX_TOOLWINDOW, cls.lpszClassName, "Garden Keeper and his Multiverse", WS_POPUP,
            0, 0, 1280, 720, IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
    }

    /// <summary>Matches the game's own top-level window rect so the view covers it exactly.</summary>
    public static void PositionOverGameWindow(IntPtr hwnd)
    {
        var target = FindGameWindow(hwnd);
        var r = new RECT();
        if (target != IntPtr.Zero && GetWindowRect(target, ref r) && r.Right > r.Left && r.Bottom > r.Top)
        {
            SetWindowPos(hwnd, HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            return;
        }

        // No game window found: cover the primary display rather than showing a 0x0 window.
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0,
            GetSystemMetrics(0), GetSystemMetrics(1), SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public static void FitToClientArea(IntPtr hwnd, CoreWebView2Controller controller)
    {
        var r = new RECT();
        if (!GetClientRect(hwnd, ref r)) return;
        controller.Bounds = new System.Drawing.Rectangle(0, 0, r.Right - r.Left, r.Bottom - r.Top);
    }

    public static void Show(IntPtr hwnd)
    {
        ShowWindow(hwnd, SW_SHOW);
        SetForegroundWindow(hwnd);
    }

    public static void Hide(IntPtr hwnd) => ShowWindow(hwnd, SW_HIDE);

    /// <summary>
    /// Registers the overlay hotkey against our own pumped window. In injector mode there is no
    /// launcher to own a global hotkey, so without this the key simply does nothing.
    /// Fails quietly when another app already owns the combination — the button still works.
    /// </summary>
    public static bool TryRegisterOverlayHotKey(IntPtr hwnd, uint virtualKey)
    {
        try { return RegisterHotKey(hwnd, OverlayHotKeyId, MOD_NOREPEAT, virtualKey); }
        catch { return false; }
    }

    public static void UnregisterOverlayHotKey(IntPtr hwnd)
    {
        try { UnregisterHotKey(hwnd, OverlayHotKeyId); } catch { }
    }

    /// <summary>True while the foreground window belongs to the game process (us).</summary>
    public static bool ForegroundIsThisProcess()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            GetWindowThreadProcessId(fg, out var owner);
            return owner == (uint)Environment.ProcessId;
        }
        catch
        {
            return true; // never hide the view because a probe failed
        }
    }

    /// <summary>Our own visible top-level window in this process, excluding the overlay itself.</summary>
    static IntPtr FindGameWindow(IntPtr exclude)
    {
        var pid = (uint)Environment.ProcessId;
        var found = IntPtr.Zero;
        var best = 0;

        EnumWindows((h, _) =>
        {
            if (h == exclude) return true;
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var owner);
            if (owner != pid) return true;

            var r = new RECT();
            if (!GetWindowRect(h, ref r)) return true;
            var area = (r.Right - r.Left) * (r.Bottom - r.Top);
            if (area <= best) return true; // the game window is the biggest one we own
            best = area;
            found = h;
            return true;
        }, IntPtr.Zero);

        return found;
    }

    // ---- signatures ----

    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    static WndProcDelegate? _keepAlive; // must outlive the window or the GC collects the thunk

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, ref RECT rect);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, ref RECT rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
