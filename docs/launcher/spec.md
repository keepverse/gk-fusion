# Launcher (player entry)

WPF dashboard + installer for non-tech players. Self-contained `win-x64` publish — **no .NET SDK, Desktop Runtime, or Node** on the player PC.

First-run is the dashboard itself (Browse game folder → Install BepInEx if needed → Play). There is no separate multi-page wizard.

## Role

| Does | Does not |
|---|---|
| Pick game folder (Browse + `%AppData%\FusionRpg\launcher.json`) | Hardcode machine-local game paths |
| Install BepInEx / MelonLoader from **official GitHub** (pinned in `loader-manifest.json`) | Download or patch the PVZ Fusion game binary |
| Validate loader; refuse dual-load | Dual-load BepInEx + MelonLoader |
| Copy plugin from `DropIntoGame\BepInEx` or `DropIntoGame\MelonLoader` | Mixing both loaders in one game folder |
| Start/stop/restart `FusionRpg.Server` + game | Host Kestrel / reimplement Cheats UI |
| Auto-pick free port; sync injector URL | |
| Disk / DB size warnings | Auto-delete saves |
| Download & install **FusionRpg** update (`FusionRpg-win-x64.zip`), preserve `Server\data\`; **Cancel** during long downloads | Velopack / delta patches (later) |
| Open BepInEx log / server folder in Explorer | |

Players open the RPG control UI in the browser at the live server URL (default `http://127.0.0.1:5088`).

## Layout (player zip)

```text
FusionRpg/
  FusionRpg.Launcher.exe     ← only double-click
  loader-manifest.json       ← BepInEx / MelonLoader / FusionRpg release pins
  Server/
    FusionRpg.Server.exe     ← launcher starts this (ASP.NET runtime bundled)
    wwwroot/
    data/                    ← created at runtime (preserved across FusionRpg updates)
  DropIntoGame/
    BepInEx/                 ← → BepInEx\plugins\FusionRpg\
    MelonLoader/             ← → Mods\ (when Melon host was built)
    (legacy flat DLLs also accepted for BepInEx)
  PLAYERS.txt
  LICENSE
  NOTICE
```

Launcher and Server each ship their **own** self-contained runtime (Desktop vs ASP.NET). Do not merge those folders.

## Loader probe

| Detected | Result |
|---|---|
| `winhttp.dll` **and** `BepInEx\core\` (no Melon markers) | OK — Play installs to `BepInEx\plugins\FusionRpg\` |
| Incomplete Bep (only one of those markers) | Not Play-ready; **Reinstall BepInEx**; refuse Melon install |
| `version.dll` **and** `MelonLoader\` (no Bep markers) | Loader OK — Play also needs Melon drop payload (below) |
| Incomplete Melon | Not Play-ready; refuse Bep install |
| Any Bep marker + any Melon marker | Block — never dual-load |
| Neither | Offer **Install BepInEx** or **Install MelonLoader** (user picks one) |

Install/update downloads show **Cancel**. Post-install requires complete markers (`winhttp`+`core`, or `version.dll`+`MelonLoader\`).

### Drop payload + uninstall

| Host | Drop ready when | Uninstall |
|---|---|---|
| BepInEx | `DropIntoGame\BepInEx\FusionRpg.Injector.dll` (or legacy flat `DropIntoGame\`) | Wipes dedicated `BepInEx\plugins\FusionRpg\` |
| MelonLoader | `DropIntoGame\MelonLoader\FusionRpg.Injector.MelonLoader.dll` present | Deletes only owned files in shared `Mods\` (`FusionRpg.*`, `fusionrpg.cfg`) — never wipes other mods |

Empty `DropIntoGame\MelonLoader\` does **not** count as payload. Install Plugin / Play fail with a clear “Melon drop missing” message (`FUSIONRPG_ML_GAMEDIR` + re-run `publish-player.ps1`). Probe `OkForV1` stays folder-only (loader markers); drop readiness is a separate host check.

Restart Game writes `ServerUrl` only when probe resolves a single host — dual-load / none refuses (no Bep cfg invent).

Loader strategy: `IModLoaderHost` (`BepInExHost` / `MelonLoaderHost`) — new loader = new class.

### Official sources (pins in `loader-manifest.json`)

| Loader | Repo | Default pin |
|---|---|---|
| BepInEx | [BepInEx/BepInEx](https://github.com/BepInEx/BepInEx/releases) | `v6.0.0-pre.2`, asset `BepInEx-Unity.IL2CPP-win-x64-*.zip` |
| MelonLoader | [LavaGang/MelonLoader](https://github.com/LavaGang/MelonLoader/releases) | `latest`, Windows x64 zip |

Confirm dialog always names the **target game folder**. Suggest paths only relative to the launcher zip / parent that already contains `PlantsVsZombiesRH.exe`.

## FusionRpg self-update

1. Check latest GitHub Release for this repo (owner/repo from `loader-manifest.json`); parse `assets[]` for `FusionRpg-win-x64.zip`.
2. If tag newer than local version (metadata like `+git` stripped) **and** zip asset present → show **Download & install FusionRpg update**. Newer tag without zip → banner only (no button).
3. Download to `%LocalAppData%\FusionRpg\updates\` (atomic `.partial` then rename), stage extract, **preserve `Server\data\`**, stop server/game, bootstrap `apply-update.cmd` (wait for launcher exit → robocopy → relaunch). Cancel supported during download. If update fails after stop, launcher tries to restart the server.

Never updates the game exe.

## Port picker

1. Prefer last good port from `%AppData%\FusionRpg\launcher.json`, then **5088**.
2. If that port answers `GET /health` with JSON `ok: true` → **reuse** (ownership is health-based, not “any FusionRpg.Server process”).
3. Else scan **5089–5188** on `127.0.0.1` (skip **5173** Vite).
4. Start server with `FUSIONRPG_URLS=http://127.0.0.1:{port}` and `FUSIONRPG_NO_BROWSER=1`.
5. Start game with `FUSIONRPG_SERVER_URL=...` (wins over BepInEx/Melon cfg). Restart the game only when the URL changed.
6. Write `ServerUrl` into the active host config (`BepInEx\config\com.fusionrpg.injector.cfg` or `Mods\fusionrpg.cfg`) for starts outside the launcher.

On launcher startup, restore `LastPort` into the session if `/health` still answers; otherwise clear the active URL but keep `LastPort` as the next Pick preference. Restart Server uses `ActivePort` → `LastPort` → Pick (never a blind hop that forgets the game’s URL).

## Status lights

| Light | Meaning |
|---|---|
| Server | Process and/or `GET /health` `ok` |
| Game | `PlantsVsZombiesRH` process |
| Injector | `/health.injectorConnected` |

## Logs

| Link | Opens |
|---|---|
| Open loader log | `{game}\BepInEx\LogOutput.log` or Melon `Latest.log` → `MelonLoader\Logs\Latest.log` → MelonLoader folder |
| Open server folder | `Server\` next to the launcher (exe + `data\`) |
| Log pane | Launcher messages + captured server stdout/stderr |

## Game / web overlay (F10 or the in-game button)

The launcher hosts the RPG web UI in an embedded browser so players can flip between the game and the UI without alt-tabbing:

- **OverlayWindow** — borderless topmost WPF window with **WebView2** pointed at the active server URL; positioned exactly over the game window (`GetWindowRect` + `SetWindowPos`), maximized fallback when the game window is not found.
- **Global hotkey** — `RegisterHotKey` on the main window (default **F10**, override via `overlayHotKey` in `%AppData%\FusionRpg\launcher.json`, WPF `Key` names). Works while the game has focus; the **Overlay** button in the actions row does the same. `Esc` or the in-overlay button returns to the game (`SetForegroundWindow`).
- Toggling **hides** the overlay (never destroys it) so the SPA keeps its SignalR session; Alt+F4 on the overlay also just hides it. The window is destroyed only on launcher shutdown.
- **WebView2 Runtime** (Evergreen, preinstalled on Win 10/11) — if missing, the overlay shows install instructions (`https://developer.microsoft.com/microsoft-edge/webview2/`) and players can still use **Open RPG UI** in a normal browser. User data dir: `%LocalAppData%\FusionRpg\webview2`.
- **In-game button** — the injector draws a small **RPG** button in the bottom-right corner of the game and signals the launcher over a local named pipe (`\\.\pipe\FusionRpg.Overlay`). Same toggle path as the hotkey. The button hides itself when no launcher is listening, and players can turn it off in the F7 overlay settings panel.
- If the hotkey is taken by another app, the launcher logs it and the button remains the fallback.
- Seamless covering toggle needs the game in **windowed / borderless-fullscreen** mode (Unity 2022 default). **Exclusive fullscreen** is detected (`SHQueryUserNotificationState`) and falls back to a window switch: the game is minimized, the overlay opens maximized, and toggling back restores the game.

Code: `OverlayWindow.xaml(.cs)`, `Services/GameWindowInterop.cs` (Win32 P/Invoke: hotkey, window rect, foreground, fullscreen probe), `Services/OverlayPipeServer.cs` (in-game button listener). Contract + live checklist: [overlay-spec.md](overlay-spec.md).

## Disk thresholds

- Warn free space &lt; **2 GB** on the server data drive.
- Warn DB total (`rpg-hot` + `rpg-media` + legacy `rpg.sqlite`) &gt; **500 MB**.
- No auto-delete.

## Build

Developer PC (needs SDK + Node + game interop refs):

```powershell
$env:FUSIONRPG_GAME_DIR = "<your Bep game folder>"
# Optional Melon drop (Blooms 3.8.1 Melon pack — not 3.9):
# $env:FUSIONRPG_ML_GAMEDIR = "<Blooms Game Files>"
.\scripts\publish-player.ps1
```

See [docs/contributing/dev-setup.md](../../../docs/contributing/dev-setup.md). Dev play without the launcher GUI: `python scripts\deploy-play.py` (`--loader-host MelonLoader` writes `Mods\fusionrpg.cfg` + `FUSIONRPG_SERVER_URL`).

Launcher unit tests: `dotnet test tests\FusionRpg.Launcher.Tests` (includes Melon uninstall/drop/Play/LogPath + `FileRpgConfig` parse).
