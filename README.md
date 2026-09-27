# gk-fusion

The Plants vs. Zombies Fusion integration: the Harmony injector, its three
loader hosts, the launcher, and the tooling that drives a live game.

- **Working rules:** [AGENTS.md](AGENTS.md)

## What belongs here

| Path | What it is |
|---|---|
| `src/FusionRpg.Injector` | The injector. |
| `src/FusionRpg.Injector.*Hosts` | BepInEx, MelonLoader, MelonLoader.39. |
| `src/FusionRpg.Launcher` | The WPF player entry point. |
| `game-profiles.json` | The supported game × loader matrix. |
| `tools/debug-mcp/`, live-probe | Live-game inspection. |

## The boundaries that are not negotiable

1. **Never download or patch the game binary.** Fusion is a mod, and the host
   game is read-only.
2. **Dual-load BepInEx + MelonLoader is forbidden.** One DLL per matrix cell.
3. **No Unity above the injector.** Everything below is plain .NET.
4. **Combat writes go through `EntityStatWriter` / the effect Funnel.** No ad-hoc
   Unity stat patches, ever.

## The original-IP test

This repo is the **one** exception: it is meaningless without Plants vs. Zombies
and it always will be. Every other repo must build, run and make sense with the
host game absent.

## Status

Empty. Not staged yet. Note that this repo's projects need a real game pack to
compile — a cell is only buildable against the pack it fingerprints.
