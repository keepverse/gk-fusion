using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Web.WebView2.Core;
using FusionRpg.Core.Overlay;
using FusionRpg.Injector.Host;

namespace FusionRpg.Injector.Hud;

/// <summary>
/// The in-game web view (overlayHost=injector): a borderless top-level window owned by the game
/// process, showing the same SPA the Launcher would host. Not a Unity texture — a real HWND.
///
/// Threading is the whole design. WebView2's objects are apartment-bound (2026-08-22 spike), so
/// the environment, controller and CoreWebView2 are created *and* only ever touched on the one
/// STA thread this class owns and pumps. That pump is a second one beside Unity's; the Unity
/// thread never blocks on it, and this file never calls a Unity API.
///
/// Every failure degrades to "unavailable" and is logged once. Nothing here may crash the game.
/// </summary>
public static class OverlayViewHost
{
    enum Command { None = 0, Toggle, Hide, Shutdown }

    /// <summary>Bounded so a wedged view cannot hold the game open on quit.</summary>
    const int ShutdownGraceMs = 2000;

    static readonly ConcurrentQueue<Command> Commands = new();
    static Thread? _thread;
    static volatile bool _available;
    static volatile bool _visible;
    static volatile bool _started;
    static volatile bool _navigated;
    static string _url = "";

    /// <summary>The view initialised and can be shown. False until it is genuinely ready.</summary>
    public static bool Available => _available;

    /// <summary>Last known visibility, published from the host thread for the Unity thread to read.</summary>
    public static bool IsVisible => _visible;

    /// <summary>Begins background init. Safe to call repeatedly; only the first call does work.</summary>
    public static void Start(string url)
    {
        if (_started) return;
        _started = true;
        _url = url ?? "";

        try
        {
            _thread = new Thread(HostLoop)
            {
                Name = "FusionRpg overlay view",
                IsBackground = true // never keep the game alive
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
        catch (Exception ex)
        {
            SafeLog("Overlay view host could not start: " + ex.Message);
        }
    }

    public static void Toggle() => Commands.Enqueue(Command.Toggle);

    public static void Hide() => Commands.Enqueue(Command.Hide);

    /// <summary>
    /// Tears the view down and waits briefly for it. The host thread is a background thread, so
    /// without the join the process can exit before teardown runs — which is exactly how an
    /// orphaned msedgewebview2.exe happens, the one outcome that would revert wave 2.
    /// </summary>
    public static void Shutdown()
    {
        Commands.Enqueue(Command.Shutdown);
        try { _thread?.Join(ShutdownGraceMs); } catch { }
    }

    // ---- everything below runs on the owned STA thread ----

    static void HostLoop()
    {
        IntPtr hwnd = IntPtr.Zero;
        CoreWebView2Controller? controller = null;

        try
        {
            if (!Win32.TryPreloadWebView2Loader(RpgHost.PluginDir, out var loaderNote))
            {
                SafeLog("Overlay view host: " + loaderNote);
                return;
            }

            string browserVersion;
            try
            {
                browserVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (Exception ex)
            {
                // Missing Evergreen runtime is expected on some machines, not an error.
                SafeLog($"Overlay view host: no WebView2 runtime ({ex.GetType().Name}) — " +
                        "the in-game view stays off; install it or use overlayHost=launcher.");
                return;
            }

            hwnd = Win32.CreateOverlayWindow();
            if (hwnd == IntPtr.Zero)
            {
                SafeLog("Overlay view host: could not create the window.");
                return;
            }

            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                // Deliberately NOT the launcher's "webview2" folder: a WebView2 user-data folder
                // cannot be shared by two processes, and both hosts can be running at once.
                "FusionRpg", "webview2-game");

            var envTask = CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            if (!PumpUntil(() => envTask.IsCompleted, 60) || envTask.IsFaulted)
            {
                SafeLog("Overlay view host: environment failed — " + Describe(envTask.Exception));
                return;
            }
            var env = envTask.Result; // read on this thread: the object is apartment-bound

            var ctlTask = env.CreateCoreWebView2ControllerAsync(hwnd);
            if (!PumpUntil(() => ctlTask.IsCompleted, 60) || ctlTask.IsFaulted)
            {
                SafeLog("Overlay view host: controller failed — " + Describe(ctlTask.Exception));
                return;
            }
            controller = ctlTask.Result;
            controller.IsVisible = true; // visibility is the *window's* job, not the controller's

            // The view covers the game, so the button that opened it is underneath. Without this
            // there is no way back except killing the game.
            controller.AcceleratorKeyPressed += (_, e) =>
            {
                try
                {
                    if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown) return;
                    if (!OverlayViewPolicy.IsCloseKey((int)e.VirtualKey)) return;
                    e.Handled = true;
                    Commands.Enqueue(Command.Hide);
                }
                catch { }
            };

            // The view runs inside the game process, so an external link must not load here.
            controller.CoreWebView2.NavigationStarting += (_, e) =>
            {
                try
                {
                    if (OverlayViewPolicy.IsSameOrigin(_url, e.Uri)) return;
                    e.Cancel = true;
                    OpenExternally(e.Uri);
                }
                catch { }
            };

            controller.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                try
                {
                    e.Handled = true; // never spawn a second in-process window
                    if (!OverlayViewPolicy.IsSameOrigin(_url, e.Uri)) OpenExternally(e.Uri);
                }
                catch { }
            };

            controller.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                _navigated = e.IsSuccess;
                if (!e.IsSuccess)
                    SafeLog($"Overlay view could not load {_url} ({e.WebErrorStatus}) — will retry on next open.");
            };

            Win32.FitToClientArea(hwnd, controller);
            Navigate(controller);

            // In injector mode nothing else owns a global hotkey, so register our own against the
            // window we already pump. Without this F10 does nothing at all in this mode.
            var hotKeyOk = Win32.TryRegisterOverlayHotKey(hwnd, (uint)OverlayViewPolicy.VirtualKeyF10);

            _available = true;
            SafeLog((hotKeyOk ? "" : "Overlay hotkey F10 is taken by another app — use the button. ") +
                    $"Overlay view host ready (browser {browserVersion}) — in-game web UI available.");

            RunCommandPump(hwnd, controller);
        }
        catch (Exception ex)
        {
            SafeLog($"Overlay view host: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _available = false;
            _visible = false;
            try { controller?.Close(); } catch { }
            if (hwnd != IntPtr.Zero)
            {
                Win32.UnregisterOverlayHotKey(hwnd);
                try { Win32.DestroyWindow(hwnd); } catch { }
            }
        }
    }

    static void RunCommandPump(IntPtr hwnd, CoreWebView2Controller controller)
    {
        while (true)
        {
            // One bad frame must not kill the view for the rest of the session.
            try
            {
                if (PumpTick(hwnd, controller)) return;
            }
            catch (Exception ex)
            {
                SafeLog("Overlay view pump: " + ex.Message);
            }
            Thread.Sleep(OverlayViewPolicy.PumpIntervalMs(_visible));
        }
    }

    /// <summary>One pump iteration. Returns true when shutdown was requested.</summary>
    static bool PumpTick(IntPtr hwnd, CoreWebView2Controller controller)
    {
        while (Commands.TryDequeue(out var command))
        {
            switch (command)
            {
                case Command.Toggle:
                    if (_visible) HideWindow(hwnd);
                    else ShowOverGame(hwnd, controller);
                    break;
                case Command.Hide:
                    if (_visible) HideWindow(hwnd);
                    break;
                case Command.Shutdown:
                    return true;
            }
        }

        if (OverlayViewPolicy.ShouldAutoHide(_visible, Win32.ForegroundIsThisProcess()))
        {
            // Topmost and out of the alt-tab list: left up, it would cover whatever the
            // player switched to, with no way back to it.
            HideWindow(hwnd);
        }

        PumpOnce();
        return false;
    }

    /// <summary>Hands an off-origin link to the player's real browser instead of loading it in-game.</summary>
    static void OpenExternally(string uri)
    {
        try
        {
            if (!OverlayViewPolicy.IsExternallyOpenable(uri)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SafeLog("Overlay view could not open a link externally: " + ex.Message);
        }
    }

    static void Navigate(CoreWebView2Controller controller)
    {
        if (string.IsNullOrWhiteSpace(_url)) return;
        try
        {
            _navigated = false;
            // rift-gate decision 16: mark the visit as embedded, so the page knows Leave can work.
            // _url stays bare (it is also the same-origin reference); only the navigation carries it.
            controller.CoreWebView2.Navigate(
                FusionRpg.Core.Overlay.OverlayEmbedMarker.MarkedUrl(_url));
        }
        catch (Exception ex)
        {
            SafeLog("Overlay view navigate failed: " + ex.Message);
        }
    }

    static void ShowOverGame(IntPtr hwnd, CoreWebView2Controller controller)
    {
        try
        {
            // Init runs at game start, which can beat the server to being ready.
            if (OverlayViewPolicy.ShouldNavigateOnShow(_navigated))
                Navigate(controller);

            Win32.PositionOverGameWindow(hwnd);
            Win32.FitToClientArea(hwnd, controller);
            Win32.Show(hwnd);
            _visible = true;
        }
        catch (Exception ex)
        {
            SafeLog("Overlay view show failed: " + ex.Message);
        }
    }

    static void HideWindow(IntPtr hwnd)
    {
        try
        {
            Win32.Hide(hwnd);
            _visible = false;
        }
        catch (Exception ex)
        {
            SafeLog("Overlay view hide failed: " + ex.Message);
        }
    }

    /// <summary>WebView2 needs a pump it does not own; this is it.</summary>
    static bool PumpUntil(Func<bool> done, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!done() && DateTime.UtcNow < deadline)
        {
            PumpOnce();
            Thread.Sleep(5);
        }
        return done();
    }

    static void PumpOnce()
    {
        while (Win32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
        {
            // WM_HOTKEY is posted to the thread, not the window, so DefWindowProc never sees it:
            // it has to be handled here or the key silently does nothing.
            if (msg.message == Win32.WM_HOTKEY && msg.wParam.ToInt32() == Win32.OverlayHotKeyId)
            {
                Commands.Enqueue(Command.Toggle);
                continue;
            }

            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
    }

    static string Describe(Exception? ex)
    {
        var baseEx = ex is AggregateException agg ? agg.GetBaseException() : ex;
        return baseEx == null ? "timed out" : $"{baseEx.GetType().Name}: {baseEx.Message}";
    }

    static void SafeLog(string message)
    {
        try { RpgHost.Log.Info(message); } catch { }
    }
}
