# Dual-host roadmap (BepInEx + MelonLoader)

Ship **two injector DLLs** so a player can use FusionRpg on either loader. Server, web, Core, and SQLite stay one.

Comparison / why not dual-load: [research/mod-loaders.md](../../../docs/research/mod-loaders.md).  
Locked row: [architecture/decisions.md](../../../docs/architecture/decisions.md) **Injector host**.

This is a **port plan**, not code. Do not start EffectBag on the MelonLoader branch.

## Goal

| Player pack | They already have | They install |
|---|---|---|
| FULL MOD TOOL / other BepInEx 6 IL2CPP | `winhttp.dll` + `BepInEx\` | `BepInEx\plugins\FusionRpg\` |
| Blooms 3.8.1 multi-lang | `version.dll` + `MelonLoader\` | `Mods\` (FusionRpg MelonMod) |

Same `FusionRpg.Server.exe` on `http://127.0.0.1:5088`. Same event kinds, Writer, Intent.

## Non-goals

- **Dual-load** both loaders in one `PlantsVsZombiesRH.exe`
- Rewriting Harmony patches, StatSystem, or EffectBag for MelonLoader
- NativeHook as the default hit path
- One DLL that runs under both hosts
- Changing the BepInEx LIVE proofs already on FULL MOD TOOL

## Shape (lock this)

Two **host** projects compile the **same** injector sources. Only entry, log, config, output path, and interop HintPaths differ.

```text
gk-fusion/src/FusionRpg.Injector/                 shared: GameHooks, RpgClient, Writer, Debug*
gk-fusion/src/FusionRpg.Injector.BepInEx/         BasePlugin + BepInEx refs  → BepInEx\plugins\FusionRpg\
gk-fusion/src/FusionRpg.Injector.MelonLoader/     MelonMod  + ML refs        → <Blooms>\Mods\
```

Until the split existed, `FusionRpg.Injector` **was** the BepInEx host. Phase 1 extracted a facade so `Plugin.RpgLog` / `Paths` / `Config.Bind` stopped leaking into hooks.

**Status (implemented):** `RpgHost` / `InjectorBootstrap` / `InjectorLoop` live under `gk-fusion/src/FusionRpg.Injector/Host/`. Hosts: `FusionRpg.Injector.BepInEx` + `FusionRpg.Injector.MelonLoader` (optional `FUSIONRPG_ML_GAMEDIR`). Launcher uses `IModLoaderHost`. P0 Melon type dump still required before LIVE Melon Play — [melonloader-assembly-csharp-p0.md](../../../docs/research/melonloader-assembly-csharp-p0.md).

```text
Game (one loader only)
        │
        ├─ FusionRpg.Injector.dll (BepInEx host)     OR
        └─ FusionRpg.Injector.MelonLoader.dll
                    │  same Harmony id com.fusionrpg.injector
                    ▼
            FusionRpg.Core / Contracts / CheatCore
                    │  HTTP + SignalR
                    ▼
            FusionRpg.Server  (one process)
```

Harmony id stays `com.fusionrpg.injector` on both so patch identity is stable. Assembly names differ so logs show which host booted.

### Facade (shared)

Hooks and Writer must not `using BepInEx` or `using MelonLoader`.

| Today | After Phase 1 |
|---|---|
| `Plugin.RpgLog.LogInfo` | `RpgHost.Log.Info` |
| `Config.Bind(...)` | `RpgHost.Config.ServerUrl` / `PersistCheats` |
| `Paths.PluginPath` | `RpgHost.PluginDir` |
| `AddComponent<RpgLoop>()` | `RpgHost.Tick(dt)` from BepInEx `MonoBehaviour` **or** Melon `OnUpdate` |
| `new Harmony("com.fusionrpg.injector")` | same, owned by host entry |

`RpgLoop.Update` body moves to a host-agnostic `InjectorLoop.Tick()`; each host calls it once per frame.

## Local game dirs

| Host | Default `GameDir` |
|---|---|
| BepInEx | *(author machine — repo parent with BepInEx)* |
| MelonLoader | *(author machine — Blooms-style MelonLoader pack)* |

Override MelonLoader path with env `FUSIONRPG_ML_GAMEDIR` so the csproj does not hard-code only this PC.

Interop HintPaths:

```text
BepInEx:     $(BepGameDir)\BepInEx\interop\*.dll
             $(BepGameDir)\BepInEx\core\0Harmony.dll, Il2CppInterop.*, BepInEx.*
MelonLoader: $(MlGameDir)\MelonLoader\Il2CppAssemblies\*.dll
             $(MlGameDir)\MelonLoader\net6\0Harmony.dll, MelonLoader.dll, Il2CppInterop.*
```

**M0 gate:** confirm MelonLoader `Assembly-CSharp.dll` still exposes **global** `Plant` / `Zombie` / `Board` (same as BepInEx 3.8.1). If types land under `Il2Cpp.*`, add aliases or a small type-forward file — do not `#if` every hook.

## Phases

### P0 — Probe (no product code)

| # | Task | Done when |
|---|---|---|
| P0.1 | Cecil/dnSpy MelonLoader `Il2CppAssemblies\Assembly-CSharp.dll`: `Plant`, `Zombie`, `TakeDamage` arity, `CreateZombie.SetZombie` | Same names as [game-types-381.md](../../../docs/research/game-types-381.md) or a short delta list |
| P0.2 | Note translator / Blooms_QOL Harmony targets (boot log or public types only — do not copy their source) | Collision list: `Plant.Start`, `Board.Awake`, … |
| P0.3 | Confirm MelonLoader **0.7.3** + net6 on the Blooms exe | Already observed; re-check after any pack update |

If P0.1 shows a different TakeDamage shape, **stop** and dump before writing a MelonMod.

### P1 — Host facade on BepInEx (no ML yet)

| # | Task | Done when |
|---|---|---|
| P1.1 | `RpgHost` log / config / plugin dir | `GameHooks` compiles with no `BepInEx*` usings |
| P1.2 | `InjectorLoop.Tick` extracted from `RpgLoop` | BepInEx still `AddComponent`; behavior unchanged |
| P1.3 | `deploy-play.py` still boots FULL MOD TOOL | Hello + existing LIVE smoke (board.start, Writer) |

Keep this phase on the **dev pack**. Regression here blocks the port.

### P2 — MelonMod stub

| # | Task | Done when |
|---|---|---|
| P2.1 | `FusionRpg.Injector.MelonLoader.csproj` + `MelonMod` entry (`OnInitializeMelon`) | Builds against Blooms refs |
| P2.2 | MelonInfo: name `FusionRpg`, version `1.0.0`, game `LanPiaoPiao` / `PlantsVsZombiesRH` | Shows in MelonLoader console |
| P2.3 | Log one line; **no Harmony yet** | Game reaches main menu with translator still loaded |
| P2.4 | `MelonPreferences` (or a tiny `fusionrpg.cfg` beside the DLL) for `ServerUrl` | Default `http://127.0.0.1:5088` |

### P3 — Harmony + server Hello

| # | Task | Done when |
|---|---|---|
| P3.1 | Same `SafePatchAll` as BepInEx (still skip `Bullet.Hit*`) | Melon log: `ok=` / `fail=` counts |
| P3.2 | `RpgClient.StartAsync` + HTTP fallback | Server `/health` shows injector heartbeat |
| P3.3 | `InjectorLoop.Tick` from `OnUpdate` | Flush / command pull / PollBoard |

Compare `patch.failed` kinds to BepInEx. New failures → collision with translator, not “ML is weaker”.

### P4 — Capture parity

Prove on **one** Blooms match (debug pipeline, not a new protocol):

| Event | Gate |
|---|---|
| `board.start` / `match.result` / `board.end` | SQLite run opens and closes |
| `plant.place` / `plant.spawn` / `plant.die` | ptr + type |
| `zombie.place` / `zombie.spawn` / `zombie.die` | ptr + type |
| `catalog.*` | plant/zombie counts non-zero after retry |
| Almanac icon / text dump | optional this phase; required before player zip |

### P5 — Write parity

| Write | Gate |
|---|---|
| EntityApply → Writer HP/ATK | `stat.writer` + visible HP |
| TakeDamage DEF Prefix | damage log before/after |
| Intent `pvz.spawn.extra` | extra zombie + Activity |
| Debug `/api/debug/apply-status` | butter/freeze on a live zombie |

Single-writer law unchanged. MelonMod must not assign combat fields.

### P6 — Coexistence

Boot order on Blooms pack: translator + Blooms_QOL + AudioImportLib + FusionRpg.

| Check | Expect |
|---|---|
| Main menu language | Translator still works |
| First plant / first zombie | FusionRpg spawn events |
| No AV on board load | Hit* still skipped |
| Harmony fights | Document; Prefix priority or skip their methods — do not patch `Update` |

### P7 — Player zip + scripts

| Artifact | Contents |
|---|---|
| `dist/FusionRpg/FusionRpg.Server.exe` | Unchanged (one server) |
| `DropIntoGame/BepInEx/` | Today’s plugin folder |
| `DropIntoGame/MelonLoader/` | MelonMod + Contracts/Core/CheatCore (+ SignalR deps) |
| `PLAYERS.txt` | Two copy paths; **never both** |

Scripts:

```powershell
python scripts\deploy-play.py                       # MelonLoader 3.9 (default, 2026-08-30 — faster startup)
python scripts\deploy-play.py --loader-host BepInEx   # older FULL MOD TOOL install
.\scripts\publish-player.ps1                    # both DropIntoGame trees when ML refs set
```

`-LoaderHost MelonLoader` must not write into `BepInEx\plugins`.

### P8 — Docs + LIVE sign-off

- [injector/spec.md](spec.md): two entries, shared hooks
- [runbook/players.md](../../../docs/runbook/players.md) + [local-dev.md](../../../docs/runbook/local-dev.md)
- Melon LIVE checklist (separate from Bep): [runbook/melon-live-checklist.md](../../../docs/runbook/melon-live-checklist.md) + post-boot [`scripts/smoke-melon-live.ps1`](../../scripts/smoke-melon-live.ps1). Bep record stays [runbook/debug-live-checklist.md](../../../docs/runbook/debug-live-checklist.md).
- [testing/foundation.md](../../../docs/testing/foundation.md): “out of CI” includes MelonLoader Harmony load

**Prep done** (checklist + smoke script). **LIVE fill** of Melon Pass/Fail is still an author session. **Done** when P4+P5 pass on Blooms **and** BepInEx still passes on FULL MOD TOOL.

## Suggested order vs Effect work

```text
P0 probe  →  P1 facade (BepInEx)  →  P2 stub  →  P3 Hello
                                              ↘ EffectBag design can continue on BepInEx
     P4 capture  →  P5 write  →  P6 coexist  →  P7 zip  →  P8 docs
```

Do **not** wait for EffectBag to start P0–P3. Do **not** implement EffectBag first on MelonLoader.

## Risks

| Risk | Mitigation |
|---|---|
| Interop type prefix / signature drift | P0 dump; abort if TakeDamage arity differs |
| Translator Harmony on `Plant.Start` | Our Postfix must be null-safe; Applied gate already exists |
| SignalR.Client fails in ML CoreCLR | HTTP fallback already exists |
| Copying BepInEx plugin into `Mods\` | Wrong refs; ship a **separate** build |
| Dual-load by accident | Player doc + zip folder names; no `version.dll` in BepInEx drop |
| Hit* still crashes | Same skip as BepInEx; not a port bug |

## Effort (order of magnitude)

| Phase | Size |
|---|---|
| P0 | hours |
| P1 | half day (mechanical usings) |
| P2–P3 | one sitting if P0 is clean |
| P4–P5 | one live session each pack |
| P6–P8 | docs + zip + coexist evening |

## Next concrete step

P0.1: dump MelonLoader `Assembly-CSharp.dll` for `Plant.TakeDamage` / `Zombie.TakeDamage` / `CreateZombie.SetZombie` and write a 10-line delta under `docs/research/` if anything differs. No csproj until that file exists.
