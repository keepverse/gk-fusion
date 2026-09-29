# Debug MCP — owner walkthrough

Thin adapter MCP over the repo's real debug surface (spec:
`docs/architecture/debug-mcp/spec-debug-mcp.md`). Nineteen tools, stdio default,
local HTTP behind a flag. No domain logic lives here.

## Install

```powershell
cd gk-fusion/tools/debug-mcp
python -m pip install -r requirements.lock
```

## Run (stdio — agent sessions)

```powershell
python gk-fusion/tools/debug-mcp/server.py
```

## Run (local HTTP — dashboards, Inspector remote)

```powershell
python gk-fusion/tools/debug-mcp/server.py --transport http --port 8899
```

Localhost bind only; any other `--host` is refused before serving.

## CLI (agents without MCP support, e.g. pi)

Same 19 adapters, no MCP needed — one subprocess per call, JSON envelope on stdout:

```powershell
python gk-fusion/tools/debug-mcp/cli.py --list
python gk-fusion/tools/debug-mcp/cli.py debug_preflight
python gk-fusion/tools/debug-mcp/cli.py debug_game_state
python gk-fusion/tools/debug-mcp/cli.py debug_call --json '{"method":"GET","route":"/effects/contract"}'
python gk-fusion/tools/debug-mcp/cli.py debug_act --json '{"verb":"shovel","col":2,"row":2}'
python gk-fusion/tools/debug-mcp/cli.py debug_click --param snapshotId=snap1 --param ref=c0
```

`--json` takes the whole argument object; `--param key=value` (repeatable, JSON-typed when
possible) covers the same ground for shells that quote poorly. Exit 0 = the adapter answered
(even `ok:false` — read its `fix`); 2 = usage error; 1 = unexpected failure (a JSON error
envelope is still printed, so callers can always parse stdout). Stateless across calls except
`snapshotId`/ref pairs, which die with their inspect snapshot by design — re-inspect.

## Inspector walkthrough (do all 19, in order)

```powershell
npx @modelcontextprotocol/inspector python gk-fusion/tools/debug-mcp/server.py
```

1. `debug_call` — `{"method":"GET","route":"/effects/contract"}` → 200 plus
   scope label. Then `{"method":"GET","route":"/api/nope"}` → refused.
2. `debug_events` — `{"kind":"board.start","limit":5}` → budgeted envelope
   (`truncated`, `next_cursor`, never a silent dump).
3. `debug_match` — `{}` on an idle server → explicit not-live shape.
4. `debug_actor` — `{"instanceId":"00000000-0000-0000-0000-000000000000"}`
   → typed miss with evidence rung (fabricated id fails at the subject rung).
5. `debug_verify` — `{"feature":"actor","subject":"<same bogus id>"}` →
   `ok:false`, `broken_rung:"subject"`.
6. `debug_preflight` — `{}` → deploy checks plus `live`: server reachability,
   game process fact, injector heartbeat, and a fresh board snapshot. Treat
   `live.ready:true` as the only green light for a lawn probe. A connected
   injector with `board.state:"idle"` is deliberately **not** ready.
7. `debug_lawn_setup` — `{}` **only with a live game you own the board of**:
   enters level 1, freezes waves, runs lab-overlay, returns ptrs. It touches
   the live lawn — never run it against someone else's prove session.
8. `debug_ui_nav` — single-menu hops only (`enter-main-menu`, `back-to-game`, …). NOT the menu
   recovery: `back-to-menu` lands on stale layers (buggy) — use `debug_menu_home` instead.
   Requires `/ui-nav` to exist in the checkout this server runs against
   (`gk-core/src/FusionRpg.Server/DebugEndpoints.cs`) — added 2026-09-14, check it landed before
   relying on this tool.
8b. `debug_menu_home` — **default menu recovery**: walks the real UI to the true main menu in
   one call (pause menu → its own 主菜单 button → quit-confirm 确定), verified-only success
   (confirm observed gone + main-menu marker present). Proven live 2026-09-20, operator-guided.
9. `debug_restart_game` — allowed by default on the session's OWN install only (pass
   `game_dir`+`base_url`+`session` for a clone; the script closes just that install's game and
   refuses another session's locked install). Adapter over
   `scripts/restart-game.ps1` — refuses cleanly if that script is missing
   from the checkout this server runs against.
10. `debug_game_state` — `{}` reads Board/InitBoard, real Unity Plant/Zombie
    counts, and `liveState` directly — no event-log history, no polling.
    Prefer this over `debug_preflight`'s `matchPhase` reading whenever you
    need to know "is a match really running right now"; `debug_preflight`
    now cross-checks against it automatically when the route is present and
    flags a `gameStateCrossCheck`/corrected `fix` on disagreement. Same
    checkout caveat as `debug_ui_nav` — requires `/game-state` to exist in
    `DebugEndpoints.cs` in this server's checkout.
11. `debug_screenshot` — `{"tag":"probe"}` captures the live Unity frame and
    returns it as base64 PNG (Playwright `browser_screenshot` analogue) plus
    an optional `save_to` path. Shows what the engine renders — proves nothing
    about the server. Same checkout caveat as `debug_ui_nav` — requires the
    `/screenshot` routes to exist in `DebugEndpoints.cs` in this server's
    checkout (live-probe `lawn-screenshot` module).
12. `debug_inspect` — `{"scope":"menu"}` returns the budgeted control tree with
    snapshot-scoped refs. Same checkout caveat — requires `/inspect`.
13. `debug_click` — `{"snapshotId":"snap1","ref":"c0"}` acts on a ref; stale refs
    refuse with the fresh-snapshot instruction. Same checkout caveat — `/click`.
14. `debug_act` — `{"verb":"shovel","col":2,"row":2}` runs a lawn verb with a
    named receipt. Same checkout caveat — `/act`.
15. `debug_cursor` — **DISRUPTIVE**: `{"x":100,"y":100,"click":false,
    "confirmed":true}` moves the real mouse. Opt-in, foreground-checked,
    throttled. Same checkout caveat — `/cursor`.
16. `debug_evaluate_search` — `{"nameContains":"Almanac"}` finds controls with
    ptrs. Same checkout caveat — `/evaluate-search`.
17. `debug_evaluate_methods` — `{"ptr":"..."}` lists method tables. Same checkout
    caveat — `/evaluate-methods`.
18. `debug_evaluate_call` — `{"ptr":"...","method":"OnMouseUp"}` invokes one
    method, reporting `via`. Same checkout caveat — `/evaluate-call`.
19. `debug_evaluate_text` — `{"text":"确定"}` finds visible text and resolves the
    clickable behind it (no OCR). Same checkout caveat — `/evaluate-text`.
    Items 12–19 are the game-control program: one distinguished tool per verb,
    adapter-only, scope-stamped.

## Scope labels (every response carries one)

- `game-injector-debug` — engine state only; proves Unity reflects it, nothing
  about server correctness.
- `rpg-server-debug` — real domain/persistence path for a real record.
- `local-machine` — filesystem/process preflight facts. `debug_preflight.live`
  also carries server and injector scopes because it reads existing routes and
  requests one bounded snapshot; it never launches, deploys, or restarts.
- `null` — unclassifiable static read (served, flagged, never trusted blindly).

## Troubleshooting

| Symptom | Fix |
|---|---|
| Tool returns NOT-READY naming a start command | Run the named command from your own terminal (agent-spawned servers die with the tool call) |
| `debug_lawn_setup` 409 injector-not-connected | Launch the game with the injector loaded, then retry |
| `debug_preflight.live.ready` is false but injector is connected | Follow its `fix`: wait for loading, or open a lawn/use `debug_lawn_setup` only on a board you own |
| Live tests skip | Start the SIM server first; unit tests never need it |
| `debug_call`/`debug_ui_nav` says "route not in debug allowlist" for a route you know exists | The allowlist is generated from `DebugEndpoints.cs` in whatever checkout this server's cwd resolves to — a route added in a worktree is invisible here until it's merged/present in this checkout. Confirmed real 2026-09-14: `/ui-nav`/`/game-state` existed only in a worktree branch while this server ran against main. Check `git branch --show-current` in the server's own working directory. |
| A route you just added to `DebugEndpoints.cs` still isn't found | The allowlist cache is keyed by every `*Endpoints.cs` file's mtime and reloads automatically on the next call within the same checkout — no server restart needed for that case (fixed 2026-09-14; previously required a restart). |
