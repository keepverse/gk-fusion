using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using FusionRpg.Core.Overlay;
using FusionRpg.Injector.Host;

namespace FusionRpg.Injector.Hud;

/// <summary>
/// Sends the in-game button's toggle to whichever process hosts the web overlay window.
/// Decisions live in <see cref="OverlaySwitchState"/>; this class is only I/O.
///
/// The pipe work runs on the thread pool — this is the injector's only background work, so it
/// swallows everything and never touches a Unity API. Results come back through two volatile
/// flags and are applied on the Unity thread in <see cref="Tick"/>, keeping the state single-threaded.
/// </summary>
public static class OverlaySwitch
{
    const string PipeName = "FusionRpg.Overlay";
    const int ConnectTimeoutMs = 250;

    /// <summary>
    /// A named-pipe write has no timeout of its own, so a stalled reader on the launcher side
    /// would park this thread forever. Async pipes support real cancellation, so bound it.
    /// </summary>
    const int WriteTimeoutMs = 1_000;

    static readonly OverlaySwitchState State = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    static volatile bool _clickPending;
    static volatile bool _hidePending;
    static volatile bool _probeInFlight;
    static volatile bool _sendDone;
    static volatile bool _sendOk;
    static volatile bool _probeDone;
    static volatile bool _probeOk;
    static bool _viewStarted;
    static volatile bool _toggleSendPending;

    /// <summary>Read by the GUI each pass — must stay allocation-free.</summary>
    public static bool ButtonVisible => State.ButtonVisible;

    /// <summary>Called from OnGUI. Sets a flag only: no pipe, no log, no Unity work on the click.</summary>
    public static void RequestToggle() => _clickPending = true;

    /// <summary>
    /// Close the view on the page's behalf (`overlay.hide`). Same split as the toggle — the injector
    /// host hides in-process, the launcher host is asked over the existing pipe — and the request is
    /// only a flag, so no Unity API is touched off the main thread.
    ///
    /// Story-neutral by construction: this reaches neither the onboarding ledger nor any story write.
    /// Closing the window must never be read as "the player finished or skipped the prologue".
    /// </summary>
    public static void RequestHide() => _hidePending = true;

    /// <summary>The launcher may have started or stopped between matches — re-probe.</summary>
    public static void OnMatchStart() => State.OnMatchStart();

    /// <summary>Board over — the button is in-match chrome, so it goes away with the board.</summary>
    public static void OnMatchEnd()
    {
        State.OnMatchEnd();
        OverlayPause.Clear(); // never leave a finished board frozen
        // Leaving the lawn should not leave the web UI covering the menu.
        if (_viewStarted && OverlayViewHost.IsVisible) OverlayViewHost.Hide();
    }

    static bool InjectorHosted =>
        RpgHost.OverlayHost == FusionRpg.Core.Overlay.OverlayHostMode.Injector;

    /// <summary>Per-frame, Unity thread. Cheap: two flag reads and an integer compare when idle.</summary>
    public static void Tick()
    {
        var now = Clock.ElapsedMilliseconds;

        ApplyPause();

        if (InjectorHosted)
        {
            TickInjectorHosted(now);
            return;
        }

        // Results are produced off-thread and applied here, so the state object stays single-threaded.
        if (_sendDone)
        {
            _sendDone = false;
            State.MarkSendComplete();
            ApplyReachability(_sendOk, now); // a failed toggle is also an answer about the host
            // The launcher went away between the probe and this write. Same rule as the click path:
            // the player asked for the overlay, so they get the overlay.
            var wasToggle = _toggleSendPending;
            _toggleSendPending = false;
            if (wasToggle && !_sendOk) ToggleOwnView();
        }
        if (_probeDone)
        {
            _probeDone = false;
            ApplyReachability(_probeOk, now);
            if (!_viewStarted && OverlayFallbackPolicy.ShouldPreloadInProcessFallback(RpgHost.OverlayHost, _probeOk))
                EnsureOwnView();
        }

        State.SettingsEnabled = OverlaySettings.OverlayButtonEnabled;

        if (_hidePending)
        {
            _hidePending = false;
            if (_viewStarted && OverlayViewHost.IsVisible) OverlayViewHost.Hide();
            Send("hide", isProbe: false);
        }

        if (_clickPending)
        {
            _clickPending = false;
            if (State.TryClick(now))
            {
                // ⚠️ A toggle with no launcher listening used to be a SILENT no-op: the write to
                // the overlay named pipe failed, nothing opened, and the player got no
                // feedback at all. Found live 2026-09-17 on the rift menu gate — the click was
                // landing correctly the whole time (the hit test logged it) and there was simply
                // nothing on the other end, because the game had been started directly rather than
                // through the Launcher.
                //
                // The gate is the menu's only way in, so "no launcher" must not mean "dead button".
                // `overlayHost=injector` already ships a working in-process view; falling back to it
                // is using a supported path, not inventing one. Reachability is known here because
                // the probe below runs on a fresh state before the player can reach the menu.
                if (State.HostReachable)
                {
                    _toggleSendPending = true;
                    Send("toggle", isProbe: false);
                }
                else
                {
                    ToggleOwnView();
                    State.MarkSendComplete(); // no wire, so the send is over as soon as it is queued
                }
            }
        }

        if (!_probeInFlight && State.ShouldProbe(now))
        {
            State.MarkProbeSent(now);
            Send("ping", isProbe: true);
        }
    }

    /// <summary>
    /// overlayHost=injector: the view lives in this process, so there is no pipe, no probe and no
    /// debounce worth keeping — a toggle is a queue push. Reachability becomes "the view came up".
    /// </summary>
    static void TickInjectorHosted(long now)
    {
        State.SettingsEnabled = OverlaySettings.OverlayButtonEnabled;

        // The view must exist for the menu tombstone to work, and the tombstone is a DIFFERENT
        // affordance from the in-match button: rift-gate's menu entry is always live, while the
        // in-match button is what the button preference turns off (OverlaySwitchState.ButtonVisible
        // keeps SettingsEnabled). So the start condition no longer keys off the preference — earlier
        // it did, on the reasoning that "with the button off there is no other way to open it in this
        // mode", which stopped being true once the menu affordance shipped.
        if (!_viewStarted)
        {
            _viewStarted = true;
            OverlayViewHost.Start(RpgHost.ServerUrl);
        }

        if (State.ApplyProbeResult(OverlayViewHost.Available, now))
        {
            RpgHost.Log.Info(State.HostReachable
                ? "In-game overlay view ready — button enabled."
                : "In-game overlay view unavailable — hiding the button.");
        }

        if (_hidePending)
        {
            _hidePending = false;
            OverlayViewHost.Hide(); // in-process host: no wire, and hiding twice is already a no-op
        }

        if (!_clickPending) return;
        _clickPending = false;
        if (State.TryClick(now))
        {
            OverlayViewHost.Toggle();
            State.MarkSendComplete(); // no wire, so the send is over as soon as it is queued
        }
    }

    /// <summary>
    /// Hold the lawn still while the player is in the web UI. The launcher's F10 never reaches the
    /// injector, so "is the player looking at the game" is the only signal that covers every way in
    /// — hotkey, in-game button, or the launcher's own Overlay button. It also covers a plain
    /// alt-tab, which is the same situation: nobody is defending the lawn.
    /// </summary>
    static void ApplyPause()
    {
        try
        {
            // NOT `InjectorHosted && ...`: since the launcher-absent fallback landed, the in-process
            // view can be up in launcher mode too, and a lawn left running under it is the exact
            // thing this pause exists to prevent.
            var away = !Win32.ForegroundIsThisProcess()
                       || (_viewStarted && OverlayViewHost.IsVisible);

            OverlayPause.Apply(OverlayPausePolicy.ShouldPause(
                enabled: OverlaySettings.PauseWhileAway,
                matchActive: State.MatchActive,
                playerAway: away));
        }
        catch { }
    }

    /// <summary>
    /// Starts the in-process fallback without showing it. The initial failed Launcher probe calls
    /// this so WebView2 registers F10 before the player needs the menu affordance; the view remains
    /// hidden until F10 or the in-game button toggles it.
    /// </summary>
    static void EnsureOwnView()
    {
        try
        {
            if (!_viewStarted)
            {
                _viewStarted = true;
                OverlayViewHost.Start(RpgHost.ServerUrl);
            }
        }
        catch (Exception ex) { RpgHost.Log.Warning("[overlay] in-process fallback failed: " + ex.Message); }
    }

    /// <summary>Open or close the in-process fallback after ensuring its host is running.</summary>
    static void ToggleOwnView()
    {
        EnsureOwnView();
        OverlayViewHost.Toggle();
    }

    /// <summary>Game is quitting — tear the view down so no browser process outlives us.</summary>
    public static void Shutdown()
    {
        OverlayPause.Clear();
        if (_viewStarted) OverlayViewHost.Shutdown();
    }

    /// <summary>Logs only when reachability flips, so an absent launcher costs one line, not one per probe.</summary>
    static void ApplyReachability(bool reachable, long nowMs)
    {
        if (!State.ApplyProbeResult(reachable, nowMs)) return;
        RpgHost.Log.Info(State.HostReachable
            ? "Overlay host found — in-game overlay button enabled."
            : "Overlay host gone — hiding the in-game overlay button.");
    }

    static void Send(string verb, bool isProbe)
    {
        if (isProbe) _probeInFlight = true;

        _ = Task.Run(() =>
        {
            var ok = false;
            try { ok = TrySend(verb); }
            finally
            {
                // Always clear the in-flight gate, or one hiccup wedges the button forever.
                if (isProbe)
                {
                    _probeInFlight = false;
                    _probeOk = ok;
                    _probeDone = true;
                }
                else
                {
                    _sendOk = ok;
                    _sendDone = true;
                }
            }
        });
    }

    static bool TrySend(string verb)
    {
        try
        {
            // Asynchronous so the bounded write below can actually be cancelled.
            using var pipe = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            pipe.Connect(ConnectTimeoutMs);

            using var cts = new System.Threading.CancellationTokenSource(WriteTimeoutMs);
            var bytes = Encoding.ASCII.GetBytes(verb + "\n");
            pipe.WriteAsync(bytes, 0, bytes.Length, cts.Token).GetAwaiter().GetResult();
            pipe.FlushAsync(cts.Token).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false; // no host listening is the normal case, not an error worth logging per attempt
        }
    }
}
