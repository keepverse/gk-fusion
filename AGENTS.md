# gk-fusion — agent guide

The Plants vs. Zombies Fusion integration: the injector, its loader hosts, the
launcher, and the live-game tooling. **Public.**

The binding rules for every Keepverse repository are in the workspace root:
`../AGENTS.md` (loaded automatically for any agent working inside this folder).
Docs live in `../docs/`. This file is emitted by kvsplit; change its template in
`tools/kvsplit/rules/templates/`, not here.

## Rules specific to this repo

- **Never download or patch the game binary.** The host game is read-only. This
  is a mod.
- **Dual-load BepInEx + MelonLoader is forbidden.** One DLL per matrix cell,
  selected at compile time; a cell is only buildable against the pack it
  fingerprints.
- **The RPG never reads the game's current state and never guesses it.** It works
  from past events and contributes a signed delta later.
- **Record, then drain.** Hooks record and return; effects are decided in a later
  budgeted drain that carries to the next frame. Delayed effects are the designed
  degradation mode, not a bug to engineer away.
- **Deltas, never absolutes.** An overlay mutation is a signed delta through the
  Funnel. Absolute HP or attack from an overlay snapshot is rejected by contract.
- **One Unity writer.** All combat writes go through the single writer. No ad-hoc
  Unity stat patches.
- **A cell needs its pack.** These projects do not compile without a real game
  directory; the fingerprint check refuses a mismatched pack rather than building
  against the wrong one.

## Build and test

```powershell
# requires FUSIONRPG_GAME_DIR, or the matching per-cell variable
dotnet build src/FusionRpg.Injector.MelonLoader
python tools/debug-mcp/cli.py <tool> --json '{}'   # live inspection without MCP
```

## Status

Empty; not staged yet.
