# Spec: Game / web overlay

Status: **wave 1 built and live-verified** (toggle, in-game button — 2026-08-22). **Wave 2 built and opening confirmed** — the in-game view covers the game at correct z-order on 3.9 MelonLoader with no launcher running (2026-08-22). Focus-across-alt-tab and crash teardown remain unverified. This document is the contract for the feature — update it before changing behavior.

## Objective

Players flip between PVZ Fusion and the RPG web UI instantly, without alt-tabbing or a second monitor. A hotkey **or an on-screen button inside the game** shows the web control room exactly covering the game window; the same hotkey, the same button, or Esc returns to the game with focus restored — a Steam-overlay-style experience.

User story: *while defending the lawn I click the RPG button in the corner (or press F10), equip a specimen on `#/roster`, press F10, and I'm back in the game — the SPA never reloaded and the game never lost my match.*

### Where the view lives — two waves

| Wave | View host | Reachable when | Status |
|---|---|---|---|
| **1** | `FusionRpg.Launcher` WebView2 window | Game was started through the Launcher | Host built; button + transport designed |
| **2** | Injector-owned borderless window in the game process | Any way the game was started | Built, opt-in via `overlayHost=injector`; live-unverified |

Why the Launcher first: the game is Unity 2022.3 IL2CPP with no web view; the Launcher is already WPF, already supervises the game + server processes, and already knows the server URL. Wave 2 removes the "must launch through the Launcher" constraint but adds a browser runtime to the game process, so it earns its own gate rather than riding along with the button work.

**What stays rejected** (2026-08-20 investigation): rendering the web UI *inside Unity* as a texture (UnityWebBrowser and friends) — it needs the game's own Unity project plus a ~150 MB Chromium payload and carries high crash risk. Wave 2 is a **separate top-level Win32 window owned by the game process**, which is a different thing; it does not reverse that rejection.

## Tech stack

- `FusionRpg.Launcher` — net8.0-windows WPF (WPF-UI 3.0.5)
- `Microsoft.Web.WebView2` 1.0.2903.40 (Evergreen Runtime on the player machine; never bundled)
- Win32 via P/Invoke (`user32`): `RegisterHotKey`, `GetWindowRect`, `SetWindowPos`, `SetForegroundWindow`, `ShowWindow`, `IsIconic`
- `System.IO.Pipes` named pipe for the in-game button signal (both sides; no new NuGet package)
- Injector IMGUI for the button itself — the existing host `OnGUI` chain, no new draw surface

## Commands

```powershell
dotnet build src\FusionRpg.Launcher                 # build
dotnet test tests\FusionRpg.Launcher.Tests          # unit tests (includes hotkey parsing)
dotnet test tests\FusionRpg.Core.Tests              # unit tests (pipe command parse, button gate)
.\scripts\publish-player.ps1                        # player zip (self-contained)
python scripts\deploy-play.py                           # dev loop: guards -> build -> server -> game
```

## Project structure (files owned by this feature)

```
gk-fusion/src/FusionRpg.Launcher/
  Services/GameWindowInterop.cs   -> Win32 layer: hotkey register/parse, game window find/rect/focus
  Services/OverlayPipeServer.cs   -> named-pipe accept loop -> same toggle path as WM_HOTKEY  (new, wave 1)
  OverlayWindow.xaml(.cs)         -> borderless topmost WebView2 window (hide-not-destroy)
  MainWindow.xaml(.cs)            -> hotkey hook (WM_HOTKEY), Overlay button, pipe server, shutdown cleanup
  Services/LauncherSettings.cs    -> overlayHotKey setting (WPF Key name, null = F10)
gk-core/src/FusionRpg.Core/
  Overlay/OverlaySwitchState.cs   -> Unity-free: debounce + probe cache + visibility gate       (new, wave 1)
gk-fusion/src/FusionRpg.Injector/
  Hud/OverlaySwitch.cs            -> pipe client + off-thread send; drives OverlaySwitchState   (new, wave 1)
  Hud/OverlaySwitchGui.cs         -> the on-screen button; drawn from both host OnGUI chains    (new, wave 1)
  Hud/OverlaySettings.cs          -> overlayButtonEnabled toggle (persisted beside the plugin)
  Hud/OverlaySettingsGui.cs       -> "Web UI button" row in the F7 panel
  Host/InjectorLoop.cs            -> OverlaySwitch.Tick() beside OverlayInput.Tick()
  GameHooks.cs                    -> OverlaySwitch.OnMatchStart() after board.start
gk-fusion/src/FusionRpg.Injector.BepInEx/Plugin.cs             -> RpgLoop.OnGUI  (in-match button line only)
gk-fusion/src/FusionRpg.Injector.MelonLoader/MelonFusionRpgMod.cs -> OnGUI       (in-match button line only)
gk-core/src/FusionRpg.Core/Overlay/OverlayCommandNames.cs -> "overlay.hide", the page-close command name (new, rift-gate)
gk-core/src/FusionRpg.Core/Overlay/OverlayEmbedMarker.cs  -> the ?embed=1 marker both hosts append   (new, rift-gate)
gk-core/src/FusionRpg.Server/OverlayEndpoints.cs          -> POST /api/overlay/leave -> the command seam (new, rift-gate)
gk-core/src/FusionRpg.Server/InjectorCommandSender.cs     -> the one enqueue-and-send implementation      (new, rift-gate)
gk-web/web/fusion-rpg-web/src/shell/OverlayLeave.tsx     -> the page's Leave control (embedded visits only) (new, rift-gate)
gk-web/web/fusion-rpg-web/src/shell/overlayEmbed.ts      -> reads the marker; Leave is absent without it (new, rift-gate)
gk-core/tests/FusionRpg.Server.Tests/
  OverlayLeaveEndpointsTests.cs   -> leave sends overlay.hide and never acks the story       (new, rift-gate)
gk-core/tests/FusionRpg.Guard.Tests/
  RiftGateEmbedLeaveGuardTests.cs -> the three marker copies agree; leaving is not an ack     (new, rift-gate)
gk-fusion/tests/FusionRpg.Launcher.Tests/
  GameWindowInteropTests.cs       -> ParseOverlayKey table tests
  OverlayPipeServerTests.cs       -> command-line parse table tests                            (new, wave 1)
gk-core/tests/FusionRpg.Core.Tests/
  Overlay/OverlaySwitchStateTests.cs -> debounce, probe caching, button-visibility gate        (new, wave 1)
gk-fusion/docs/launcher/spec.md             -> player-facing behavior summary (§Game / web overlay)
```

## Code style

Match the existing Launcher services: sealed/static service classes, `Try*` methods returning bool, swallow-and-log around process/window races, XML doc-comments only where the code can't say it. P/Invoke signatures stay private inside `GameWindowInterop`; the rest of the launcher never sees a raw HWND except via that class.

On the injector side, match the VFX overlay code: static class, cached `GUIStyle`, no per-frame allocation, no throw escapes into `OnGUI`.

## Behavior contract

1. **Toggle** — hotkey (default `F10`, `MOD_NOREPEAT`, registered on the main window), the **Overlay** button in the launcher, or the **in-game button** (§In-game switch button):
   - Overlay hidden → position over the game window rect (`GetWindowRect` → `SetWindowPos`, topmost), show, activate, focus WebView2. No game window found → maximized fallback.
   - Overlay visible → hide + `SetForegroundWindow(game)`.

   All three entry points converge on **one** toggle method. No entry point gets its own behavior.
2. **Session preservation** — toggling *hides*, never destroys, the WebView2 window: the SPA keeps its SignalR connection and page state. Alt+F4 on the overlay also hides. Real destruction only on launcher shutdown (`ForceClose`).
3. **URL** — active server URL from the play session; falls back to `LastPort` from settings; no URL → log "press Play first", no window.
4. **Hotkey config** — `overlayHotKey` in `%AppData%\FusionRpg\launcher.json` (any WPF `Key` name; unknown/modifier-only names fall back to F10). Registration failure (key owned by another app) logs and leaves the button as the path.
5. **WebView2 Runtime missing** — overlay shows install instructions (developer.microsoft.com link) instead of a blank window; "Open RPG UI" in a normal browser remains the fallback. User data dir: `%LocalAppData%\FusionRpg\webview2` (never next to the exe).
6. **Esc** and the in-overlay "Back to game" button behave exactly like the hotkey's hide path.
6b. **Page-initiated close (rift-gate `overlay-hide`).** The web FE's **Leave** control asks the host to
    close the window over the *server→injector command seam* — the same one every other host command
    uses — and the injector then hides locally (injector host) or relays one pipe verb (launcher host).
    So this is **one more route to the same hide**, not a second behaviour: it converges on the same
    hide as Esc, exactly like the three toggle entry points converge on one toggle method (rule 1).
    Two contracts ride with it:
    - **Hiding is not acknowledging the story.** Closing the window must never write the onboarding /
      story ledger — the prologue dialog's own dismiss path is what records `completed|skipped`, and a
      close that reached it would let one Esc burn the prologue.
    - **The embed marker.** Both hosts append `?embed=1` to the URL they navigate to, so the page knows
      it is embedded and that Leave can work. It is a **query flag, not a hash fragment** (the SPA uses a
      HashRouter, so the hash is the router's domain). A plain browser visit carries no marker, and the
      page must behave correctly without one — the Leave control is simply not offered.
7. **Exclusive fullscreen fallback** — if the game is the foreground window in exclusive-fullscreen D3D mode (`SHQueryUserNotificationState` = `QUNS_RUNNING_D3D_FULL_SCREEN`), topmost windows can't cover it: the launcher minimizes the game and the overlay opens maximized as a normal window switch. Toggling back restores + refocuses the game (minimized-window rects are ignored by the positioner). Logged with a hint that borderless mode gives the seamless toggle.

## In-game switch button

The hotkey is invisible; players who never read the launcher won't find it. The button makes the feature discoverable from inside the match.

- **Draw surface** — the existing host `OnGUI` chain, drawn after `VfxDirector.Draw()` and `OverlaySettingsGui.Draw()`. **No new draw surface, no new host hook** — the two hosts (`RpgLoop.OnGUI` in the BepInEx plugin, `OnGUI` in the Melon mod) each gain exactly one call line, keeping the single-IMGUI-entry rule both hosts already document.
- **IMGUI event passes** — the Repaint-only gate in vfx-ssot §8.3 covers **non-interactive** draws. An interactive control cannot be Repaint-gated: IMGUI needs it drawn on the Layout and mouse passes too, or it never receives input and the Layout/Repaint control counts stop matching. `OverlaySettingsGui` already solves this by admitting exactly `Repaint`, `Layout`, `MouseDown`, `MouseUp` and dropping every other event type — the button uses the same filter. It pays for the extra passes by being one `GUI.Button` with a cached `GUIStyle` and a cached `Rect`: no allocation, no scan, no `FindObjectsOfType`.
- **Placement** — small corner button, screen-space, bottom-right: outside the card tray and the shovel slot so it never eats a gameplay click. Geometry lives in Core (`OverlaySwitchLayout`) and **scales with display height** (1× at 1080p, capped at 3×, never below 1×) — IMGUI works in device pixels, so a fixed-pixel button is half the physical size on a 4K screen.
- **Visibility** — needs all three of: a **live board**, a reachable host, and the player's preference. The preference toggle lives in `OverlaySettings` alongside the existing F7/F9 ones, default on. No reachable host means no dead button in a game started outside the Launcher; **no live board means no button over menus** — the corner was chosen against the in-match HUD, not against menu screens, and the global hotkey still works everywhere, so gating costs nothing.
- **Not a cheats surface.** The button carries one action: show/hide the web UI. Any gameplay control belongs in the web document, per the cheats SSOT row in [decisions.md](../../../docs/architecture/decisions.md). Adding a second action to this button is ask-first.
- **Hotkey unchanged.** F10 stays a launcher-registered global hotkey and keeps working while the game has focus, with or without the button.

## Transport — named pipe

The Launcher is not a hub client, and the button must not depend on the server being healthy. The injector signals the Launcher directly.

| Property | Value |
|---|---|
| Pipe | `\\.\pipe\FusionRpg.Overlay`, local machine only (`NamedPipeClientStream(".", …)`) |
| Direction | One-way, injector → launcher |
| Server | Launcher: async accept loop, one message per connection |
| Protocol | One ASCII line: `toggle`, `ping` or `hide`. Unknown line → log + ignore, never throw. Only what the injector actually sends — unreachable verbs are untested surface, so wave 2 adds its own when something calls them. `hide` is rift-gate `overlay-hide`'s page→host close (rule 6b) and routes to the same hide Esc uses |
| ACL | Windows default for named pipes: full control to the creator, LocalSystem and administrators; **read** to Everyone. The pipe is `PipeDirection.In`, so read access alone cannot send a command — only a same-user process can write. No remote clients (`NamedPipeClientStream(".", ...)`), no impersonation |

Rules:

- **Never on the Unity main thread.** A click sets an intent flag; a background worker connects (250 ms timeout), writes one line, disposes. A pipe connect that blocks a frame is exactly the class of main-thread stall the perf work removed — do not reintroduce it. Note this is the **first background work in the injector** (no `Task.Run` / `Thread` existed there before), so the worker must swallow every exception and must never touch a Unity API.
- **Abandoned sends recover.** A pipe write has no timeout of its own, so a stalled reader on the launcher side would leave the in-flight gate shut and the button dead for the session. Two bounds: the write itself is cancellable and capped at 1 s (async pipe), and the state treats a send older than 3 s as abandoned so a click always eventually gets through.
- **Debounce 300 ms.** A held or double click sends one message. The **in-flight gate comes first**: a connect can take 250 ms, so a send may outlive the window, and a click landing during one is refused *without* being recorded as a send — otherwise a click that never reached the pipe would silently push the window forward and lock the player out for another 300 ms.
- **Availability probe** — `ping` on match start and every 30 s, off-thread. The cached result drives button visibility. Never probe per frame.
- **Log on transition only** — one line when the host becomes reachable or unreachable, never per attempt.
- **One code path on the launcher side** — the pipe handler marshals to the UI thread and calls the same toggle method as `WM_HOTKEY`. The pipe must not grow its own show/hide logic. The `hide` verb (rule 6b) obeys the same rule: it calls the same hide path Esc uses, and is not a third behaviour.
- **Client timeout.** A connected client gets 2 s to send its line. With a single server instance, a client that connects and never writes would otherwise park the listener for the rest of the session and silently kill the button.
- **One owner per machine.** The pipe allows a single server instance, so a second launcher cannot claim it. Claiming the name and serving a connection therefore fail for different reasons and are handled differently: a launcher that cannot claim it says so **once** and re-checks every 5 s quietly, then logs again only when it takes over. Treating that as an ordinary connection error logs twice a second for as long as both launchers run.

Rejected transports:

| Rejected | Why |
|---|---|
| Server relay (`POST /api/overlay/toggle` → launcher subscribes) | Makes the Launcher a hub/poll client for one boolean; adds RTT and a failure mode where the server is down but game and launcher are fine |
| Synthetic keystroke (`keybd_event` F10) | Injects global input, breaks when the hotkey is rebound or its registration failed, and fires into whatever app is focused |
| `PostMessage` to a launcher window found by title | Fragile title/class matching, no delivery confirmation, no room for a protocol |

## Wave 2 — injector-hosted view (gated)

Wave 2 moves the view into the game process so the overlay exists however the game was started.

- Selected by `overlayHost` = `launcher` or `injector` (host config + `FUSIONRPG_OVERLAY_HOST` env override, env wins — matches the `FUSIONRPG_SERVER_URL` convention). An unusable value falls through rather than overriding, so an env typo cannot discard a deliberate config choice; the final fallback is `launcher`. **Default stays `launcher`** — guard-pinned, and flipping it is ask-first.
- **Threading (locked by the spike).** `OverlayViewHost` owns one STA thread and pumps it; the WebView2 environment, controller and CoreWebView2 are created *and* only ever touched there. Never the Unity thread, never the pool — crossing apartments fails every call with a `NotImplementedException` that misleadingly blames version skew. The Unity thread only enqueues commands.
- **Window.** Borderless `WS_POPUP` + `WS_EX_TOOLWINDOW` (kept out of alt-tab), positioned over the largest visible top-level window this process owns, topmost. No game window found → primary display rect rather than a 0×0 window. All P/Invoke stays in `Hud/Win32.cs`, mirroring the Launcher’s `GameWindowInterop` rule.
- **Native loading.** The SDK imports `WebView2Loader.dll` by bare name and the process search path is rooted at the game exe, not our plugin folder, so the host pre-loads it by absolute path first; the later bare-name import then binds to the already-loaded module.
- **Payload.** Only `Microsoft.Web.WebView2.Core.dll` + the x64 loader reach the player (~752 KB). The package’s WPF/WinForms wrappers, XML docs and arm64/x86 natives are trimmed by MSBuild targets and guard-pinned — everything referenced lands in the player’s game folder.
- **Own user-data folder.** The in-game view uses `%LocalAppData%\FusionRpg\webview2-game`, **not** the Launcher’s `\webview2`. A WebView2 user-data folder cannot be shared by two processes, and both hosts can be running at once (a player who launched through the Launcher but set `overlayHost=injector`) — sharing it fails init.
- **Stays on our origin.** `NavigationStarting` cancels anything that is not the server’s own scheme+host+port, and `NewWindowRequested` never opens a second in-process window. Off-origin links are handed to the player’s real browser instead — and only `http(s)` ever reaches the shell, since `Process.Start` with `UseShellExecute` would otherwise invoke whatever handler is registered for `file:`, `javascript:` or an app scheme. Both rules are pure and unit-tested in `OverlayViewPolicy`.
- **Started only when wanted.** In the launcher host the view is created on first toggle. If that
  Launcher is **confirmed absent** by the first bounded pipe probe (the direct-game-launch case), the
  injector preloads its own view **hidden**: it registers F10 without requiring a Rift-menu click, but
  does not show a window until F10 or an in-game button toggles it. A reachable Launcher remains the
  sole host, so normal Launcher sessions never create a second view. In the explicit **injector** host
  the view is started on init rather than waiting for the button preference: the menu tombstone
  (rift-gate) is an independent entry to the overlay. The button preference still suppresses only the
  **in-match button** — it never gates the view's existence.
- **Getting out.** The view covers the game, so the button that opened it is underneath — wave 1’s three exits (Esc, WPF chrome, launcher hotkey) do not exist here. **Esc and F10 close it** via `AcceleratorKeyPressed`, **the page's own Leave control closes it** (rule 6b), and the window **auto-hides when the game stops being the foreground process**: it is topmost and `WS_EX_TOOLWINDOW`, so left up over another application it would be unreachable by alt-tab. Both rules live in `OverlayViewPolicy` (unit-tested) and are guard-pinned.
- **Loading.** Init runs at game start, which can beat the server to listening, so a failed load is retried on the next open; a page that loaded is never re-navigated (that would throw away SPA state, which hide-not-destroy exists to preserve).
- **Teardown.** Both hosts call `OverlaySwitch.Shutdown()` from `OnApplicationQuit`, which enqueues *and joins* the host thread for up to 2 s — the thread is a background thread, so without the join the process can exit before teardown runs, which is exactly how an orphan happens. An orphaned `msedgewebview2.exe` is the spike’s stated no-go — check it by command line (`*FusionRpg*`), never by process name: a normal desktop runs a dozen unrelated WebView2 processes from Edge.
- **Degradation.** No Evergreen runtime, no loader, or any init failure → logged once, `Available` stays false, the button hides. Never a crash, never a hang.
- With `injector`, the button calls the local host directly and the pipe client is bypassed. Behavior contract rules 1–7 are unchanged from the player's side.
- **Spike must answer, before any build:** does the WebView2 loader initialize in an IL2CPP process; can a borderless window owned by the game HWND stay correctly z-ordered and focused; does input routing survive alt-tab; does teardown run cleanly when the game exits or crashes; what happens with no Evergreen runtime.
- **Spike status (2026-08-22)** — [../research/2026-08-22-overlay-injector-host.md](../../../docs/research/2026-08-22-overlay-injector-host.md). Loader init and the missing-runtime path are answered; z-order, focus-across-alt-tab and the crash teardown still need the game. Nothing found blocks wave 2, but it is **not** a go yet.
- **If wave 2 proceeds:** the WebView2 objects are apartment-bound — environment, controller and CoreWebView2 must be created *and* consumed on one thread the injector owns and pumps (a second pump beside Unity's), never the Unity thread and never the pool. Standing the view up costs ~270–315 ms, so it is created once in the background and thereafter only shown/hidden. What ships is `Microsoft.Web.WebView2.Core.dll` + native `WebView2Loader.dll` (~166 KB) — **not** Chromium, so the no-embedded-browser boundary holds; the NuGet dependency in the game process is still ask-first.
- If the spike fails on any of those, wave 1 stands and this section records why.

## Testing strategy

- **Unit (CI):** hotkey-name parsing (`GameWindowInteropTests`, table-driven), pipe command parsing (`OverlayPipeServerTests`, table-driven), and the injector switch state machine (`OverlaySwitchStateTests`: debounce window, probe cache expiry, visibility gate, transition-only logging). Real pipe I/O **is** covered (`OverlayPipeServerRoundTripTests`: a client's line reaches the handler, junk does not dispatch, `Dispose` stops the listener) — each test takes a unique pipe name so it never collides with a launcher running on the same machine. Win32, WebView2, and IMGUI stay untested (no game window / runtime in CI).
- **Contract guard:** the pipe name and verbs are duplicated as literals on both sides (the launcher and injector share no assembly), so nothing at compile time links them. `OverlayPipeContractGuardTests` reads both source files and the spec and fails if the names diverge or the injector sends a verb `ParseCommand` does not map — the failure mode otherwise is a button that silently hides itself.
- **Guards:** `guard-secondary-no-unity` does **not** cover this code — it scans `Core/Effects/Plugins` and `IEffectGrantPlugin` implementers only. What keeps the state machine honest is its home: `FusionRpg.Core` has no UnityEngine reference (the same guard asserts that on the csproj), so `OverlaySwitchState` cannot drift into Unity. Inside the injector only `OverlaySwitchGui` touches Unity; `OverlaySwitch` touches only the pipe.
- **Live checklist (dev machine, once per release):**
  1. `deploy-play.py` → Play → in-game press F10 → overlay covers the game window exactly; UI is the lawn control room.
  2. F10 / Esc → back in game, game has focus and input.
  3. Toggle twice more → SPA did not reload (Log page ring buffer keeps its entries).
  4. Close overlay with Alt+F4 → hides, game focused; F10 brings it back instantly.
  5. Stop all in launcher → no orphaned overlay window, hotkey unregistered (F10 does nothing).
  6. Negative: rename the WebView2 runtime check (or test on a machine without it) → instruction text shows, no crash.
  7. In-game button visible during a match → click shows the overlay; click "Back to game" → button still there, match intact.
  7b. Leave the match → button is gone from the menus; start another → it is back.
  7c. Button is legible and hittable at the display's real resolution (check on 4K if available).
  8. Start the game **without** the launcher → after the first bounded probe, the hidden in-process
     view is ready and F10 opens it without clicking Rift; no window appears before F10 and no probe log spam occurs.
  9. Kill the launcher mid-match → button disappears within one probe interval; a click during the gap logs once and does nothing.
  10. Hold / double-click the button → exactly one toggle (debounce).
  11. Frame time with the button drawn is indistinguishable from the button hidden (perf probe, scenario of choice).

  **Wave 2 only** (`FUSIONRPG_OVERLAY_HOST=injector`, launcher not needed):

  12. The in-game view opens on the button and **covers the game window** — if the game paints over it, z-order lost and wave 2 is dead as designed.
  13. Click into the view, type, alt-tab away and back: input still lands in the view, the game is not receiving it underneath, and the view stays on the right monitor.
  14. Quit the game normally, **then** repeat and kill it from Task Manager. After each, check for a leak with:

      ```powershell
      Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
        Where-Object { $_.CommandLine -like "*FusionRpg*" }
      ```

      Filter by **our user-data folder, never by process name** — a normal desktop runs a dozen unrelated `msedgewebview2.exe` from Edge, so a bare name check reports a leak every time. Expect no rows. A real leak reverts wave 2.
  15. With no Evergreen runtime (or rename the loader), the game still starts, the button stays hidden, and the log says why once.

  **Pause while away:**

  18. Mid-wave with zombies advancing, press F10 — come back and nothing moved. Repeat with the in-game button.
  19. Alt-tab to another app mid-wave — the lawn holds; return and it resumes.
  20. Set a timescale cheat (G-TIMESCALE 2), then toggle the overlay — resumes at 2×, not 1×.
  21. Turn "Pause while away" off in F7 — the lawn keeps running while the overlay is up.
  22. Lose or win a board while the overlay is up — the game is not left frozen afterwards.
  16. **Esc closes the view** (and F10). Then open it and alt-tab away — it hides itself rather than covering the other app.
  17. Start the game with the server stopped, open the view (error page), start the server, close and reopen — it loads.

## Pause while away

Opening the control room mid-wave must not cost a run. **Decided 2026-08-22** (this was the spec's open question; the boundary said ask first).

- **Rule:** hold the lawn still while a **live board** is running and the player is **not looking at it**. Outside a run nothing is at stake and freezing time would just look broken.
- **"Not looking" is deliberately broad** — the game window is not the foreground process, or (wave 2) the in-game view is visible. The launcher's F10 never reaches the injector, so foreground is the *only* signal that covers every way in: hotkey, in-game button, and the launcher's own Overlay button. It also covers a plain alt-tab, which is the same situation: nobody is defending the lawn.
- **One writer.** `CheatActions.TickContinuous` already asserts `Time.timeScale` every frame (G-TIMEFREEZE / G-TIMESCALE). `OverlayPause` therefore **decides and remembers but never writes** — a second writer would be silently overwritten on the next frame whenever a speed setting is active, which is exactly how "pause doesn't work with a speed cheat on" bugs appear. Guard-pinned: `Time.timeScale` may only be assigned in `CheatActions.cs`.
- **Resume restores what was running**, not a hardcoded `1.0`, so a player's own timescale survives the round trip; a captured `0` or NaN (something else had already frozen time) resumes at normal speed rather than leaving the game stuck.
- **Cleared** on board end and on shutdown — never leave a finished board frozen.
- **Player toggle:** "Pause while away" in the F7 panel, default on.

## Shipping the in-game browser

The launcher carries its own WebView2 (that is how F10 works), and the **injector** needs its own
copy for the in-game view. Those two can drift apart silently, and did:

- **Cloud releases build the injector from a committed fallback.** `release.yml` cannot build it
  without game interop, so it falls back to `FUSIONRPG_USE_CI_DROP=1` and copies the checked-in
  `artifacts/ci-drop-into-game/`. That drop predates the in-game overlay and contains no WebView2,
  so a release would ship a launcher that can open the web UI and an injector that cannot.
- **The MelonLoader drop is only built when `FUSIONRPG_ML_GAMEDIR` is set**, and the release
  workflow does not set it — so no release has ever carried a MelonLoader injector.
- **Nothing in the install path recurses.** `PluginInstaller` uses `Directory.EnumerateFiles` on the
  drop folder, top level only, and the CI fallback copy is equally shallow. A loader under
  `runtimes\win-x64
ative\` reaches nobody, which is why the build flattens it to the plugin root.

**Guard:** `PlayerPackProbe.ProbeOverlayPayload` runs inside `smoke-player-pack.ps1`, which the
release workflow already invokes before zipping. It fails the release when the launcher lacks its
own WebView2, when any injector drop is missing WebView2 beside it, when an injector carries none of
the overlay types (a stale drop), or when no MelonLoader drop exists. Shipping without MelonLoader
on purpose requires `FUSIONRPG_ALLOW_NO_MELON=1` — deliberate, not silent. Covered by
`PlayerPackOverlayProbeTests`, including a fixture reproducing the stale-CI-drop release.

## Boundaries

- **Always:** run launcher unit tests before handing back; keep all Win32 inside `GameWindowInterop`; keep the overlay hide-not-destroy invariant; keep every toggle entry point on one code path; keep pipe I/O off the Unity main thread; log every failure path (no silent no-ops).
- **Ask first:** changing the default hotkey; adding a settings UI; bundling/auto-installing the WebView2 Runtime; making the overlay pause or send input to the game; adding any second action to the in-game button; flipping `overlayHost` to `injector` by default; new NuGet dependencies.
- **Never:** touch the game process/window beyond focus + rect reads; route gameplay input or gameplay state through the overlay pipe; make the in-game button a second cheats surface; render the web UI inside Unity as a texture; ship an embedded browser inside the game folder in wave 1; auto-close the game; **let a page-initiated close write the story/onboarding ledger** (rule 6b).

## Success criteria

| # | Criterion | Status |
|---|---|---|
| 1 | Build clean; all launcher tests pass (126, 11 new) | ✅ verified |
| 2 | Hotkey parse: F-keys accepted, junk/modifiers → F10 | ✅ verified (unit) |
| 3 | F10 in-game shows overlay over the game window ≤ ~1 s (after first init) | ⏳ live run |
| 4 | Toggle back restores game focus, game input works immediately | ⏳ live run |
| 5 | SPA session (SignalR, page state) survives ≥ 3 toggles | ⏳ live run |
| 6 | Missing WebView2 Runtime → instructions shown, no crash | ⏳ live run |
| 7 | Launcher shutdown leaves no overlay window or registered hotkey | ⏳ live run |
| 8 | Works with the game in borderless-fullscreen (Unity 2022 default) | ⏳ live run |
| 9 | Exclusive fullscreen → game minimized, overlay opens maximized, toggle-back restores the game | ⏳ live run |
| 10 | Pipe command parse: known verbs dispatch, junk ignored without throw | ✅ verified (unit + real pipe round-trip) |
| 11 | Debounce: repeated clicks inside 300 ms produce one toggle | ✅ verified (unit) |
| 12 | No launcher running → hidden in-process fallback preloads after one failed probe; F10 opens it without Rift, no log spam | ⏳ live run — fallback policy + wiring guard verified |
| 13 | Button adds no measurable frame time vs button hidden | ⏳ live run |
| 14 | Button hidden outside a live board; back on the next match | ✅ verified (unit) · ⏳ live confirm |
| 15 | Button scales with display height, always on screen | ✅ verified (unit) · ⏳ live confirm |
| 16 | Wave 2 spike answered all five questions | ⚠ 2 of 5 answered; 3 need the game |
| 17 | `overlayHost` resolution: env wins, junk falls through, default launcher | ✅ verified (unit) |
| 18 | Only Core.dll + x64 loader ship to the player | ✅ verified (build + guard) |
| 19 | In-game view covers the game window at correct z-order | ✅ **verified live 2026-08-22** (3.9 MelonLoader, no launcher) |
| 20 | Focus and input survive alt-tab with the view up | ⏹ live run |
| 21 | No orphaned `msedgewebview2.exe` **whose command line names our user-data folder** after a normal quit **or a crash** | ⏹ live run — **a leak is a no-go** |

## Resolved decisions

**2026-08-20**

1. **Exclusive fullscreen** → fallback to window switch (behavior rule 7), not just a documented limitation.
2. **Default hotkey** → stays `F10` (JSON-configurable; F8 reserved by injector cheat conventions).
3. **WebView2 Runtime missing** → install link + instructions only; a one-click bootstrapper waits for a real player report.

**2026-08-22**

4. **View host** → launcher now, injector later behind `overlayHost`; in-Unity texture embedding stays rejected.
5. **Switch surface** → on-screen button **and** hotkey, button visibility toggleable in `OverlaySettings`.
6. **Transport** → local named pipe, injector → launcher; server relay and synthetic keystrokes rejected.
7. **Button is in-match chrome** → hidden outside a live board. Its corner was picked against the seed bank and shovel, not menu screens, and the global hotkey covers the menus.
8. **Button scales with display height** → 1× at 1080p, capped 3×, never below 1×. Geometry is pure and lives in Core.
9. **Wire protocol is `toggle` + `ping` + `hide`** → the original `show` was dropped (nothing sent it; hide-not-destroy means a separate show is a toggle). `hide` was added later by rift-gate `overlay-hide` so the page's Leave control can close the window over the same pipe, routing to the same hide Esc uses (rule 6b) — it is not a second behaviour.

## Open questions

- **Remember last overlay page** across toggles — still out of scope, but the button makes short visits common enough that it may be worth revisiting.

## Out of scope v1

Hotkey settings UI, overlay opacity/click-through mode, remembering last overlay page, per-monitor DPI edge cases beyond `SetWindowPos` device pixels, any button action beyond show/hide.
