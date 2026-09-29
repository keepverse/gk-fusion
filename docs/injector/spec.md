# Injector spec (play-scene ingest)

Dual-host: **BepInEx 6 Unity IL2CPP** plugin and **MelonLoader MelonMod** compile the same shared sources. Harmony id: `com.fusionrpg.injector`. Never load both into one exe.

| Host | Assembly | Entry | Install path |
|---|---|---|---|
| BepInEx | `FusionRpg.Injector.dll` | `BasePlugin.Load` | `BepInEx\plugins\FusionRpg\` |
| MelonLoader | `FusionRpg.Injector.MelonLoader.dll` | `MelonMod.OnInitializeMelon` | `Mods\` |

Shared facade: `RpgHost` / `InjectorBootstrap` / `InjectorLoop` under `gk-fusion/src/FusionRpg.Injector/Host/` — hooks must not `using BepInEx` or `using MelonLoader`. Port plan: [dual-host-roadmap.md](dual-host-roadmap.md). Melon P0 type dump: [melonloader-assembly-csharp-p0.md](../../../docs/research/melonloader-assembly-csharp-p0.md).

References: Bep game `BepInEx/core` + `BepInEx/interop`, or Melon `MelonLoader/net6` + `Il2CppAssemblies` (3.8.1). Game types are **global** (not `Il2Cpp.*`).

The injector **does not know players**. It mints `matchKey` and sends events. The server stamps `player_id`.

Dump helpers: `GameDumps.Plant` / `Zombie` / `BoardStats` / `BoardConfig`. Field lists: [game-types-381.md](../../../docs/research/game-types-381.md).

## Entry

- Host wires `IRpgLog` + `IRpgConfig` + plugin dir → `RpgHost.Initialize` → `InjectorBootstrap.Start` (SafePatchAll, `RpgClient`).
- `serverUrl` from env `FUSIONRPG_SERVER_URL` (wins) else host config (`BepInEx/config/com.fusionrpg.injector.cfg` or `Mods/fusionrpg.cfg`) `ServerUrl` (default `http://127.0.0.1:5088`).
- Per-frame: BepInEx `AddComponent<RpgLoop>` or Melon `OnUpdate` → `InjectorLoop.Tick`. Do not Harmony-patch `Update`.

## SafePatchAll

Per-type `CreateClassProcessor`. Log and emit `patch.failed`. Do not abort Load.

## Hooks

Match: `Board.Awake` (matchKey, `board.start` + modifiers + catalog retry), `OnDestroy`, `Die` (`board.end`), `BoardStatistics.GameOver` (`match.result` + full snapshot), `HandleGameLose`, `BoardVictory.Win`, `ProcessZombieEnter`, `PauseMenu_Btn.Restart`, `UIMgr.EnterPauseMenu` / `InGameUI.PauseGame` → `MatchHost.NotifyPaused(true)`, `UIMgr.BackToGame` / `BackToMenu` → `NotifyPaused(false)`.

Factories: `CreatePlant.SetPlant` → `plant.place`; `MixEvent`; `UniqueEvent`; `SetPlantAttributes` (spawn if ptr not Applied); `CreateZombie.SetZombie` / `SetZombieWithMindControl` → `zombie.place`.

Lifecycle: `Plant.Start` / `Die` / `TakeDamage` / `RealTakeDamage` / `Crashed`; `Zombie.Start` / `InitHealth` / `Die` / `DestoryZombie` / `TakeDamage` / `BodyTakeDamage` / `ApplyDamage` / `SetMindControl`; `Board.OnPlantCreate` / `OnPlantDie`.

Recapture (emit `entity.stats`, append spawn_stats): `Board.SetHealthInTravel`, `Lawnf.SetZombieHealth`, `TravelMgr.ReinforceZombie` / `ReinforcePlant`.

Economy: `UseSun` / `GetSun` / `UseMoney` / `GetMoney` / `GetPoint`; `CreateItem.SetCoin`.

Board: `InGameUI.SetLevelName`; `BoardSpawner.SummonZombies`; `Board.HugeWaveEvent`. Poll `theWave`/`theSun`/`theMoney`/`thePoints`/`plantedCount`/wave health/`mowerArray.started`.

Mower / bullet: `CreateMower.SetMower`, `Mower.StartMove`/`Die`, `Bullet.InitData`, `CreateBullet.SetBullet`.

Player: `Mouse.ClickOnCard` / `TryToSetPlantByCard` / `TryToSetZombieByCard` / `DisassemblePlant` / `TryToSetPlantByGlove`; `CardUI.UseOnce`; `InitBoard.CreateCard` (3 overloads); Almanac `SelectCard`; `Shovel.Use` / `PayBackSun`; `Glove.Use`; `Fertilize.Use(int,int)`; `Hammer.Use`; `Wheel.Use`; `MiniPet.SetPet` / `GetExperience`; `GridItem.SetGridItem` / `Die`; `Present.RandomPlant`; `Lawnf.SetDroppedCard` / `SetAward`; `PrizeMgr.Click`; `ItemManager.SetBucket`; `ConveyManager.GetCardPool`.

Travel: `TravelMgr.OnBoardStart` / buff getters / `UnlockPlant`; `MultipleChoiceMenu.OnSelect`.

Catalog: after Hello, Il2Cpp `Enum.GetValues` or scan `-1..6000`; `Lawnf.GetName` as `displayName`; `PlantMixTreeManager.ChildToParents` → `catalog.recipes`; `InitZombieList.InitZombie` → `catalog.zombies`. Retry dump on `Board.Awake` if plant count was 0.

Skip `PlantType.Nothing` / `ZombieType.Nothing`. Null-check `__result` / `gameObject`.

**3.8.1 TakeDamage** is five args (`Plant`, damage, …). Do not patch a two-arg shape from older mods.

Scale DEF only on existing `TakeDamage` prefixes. Apply HP/ATK once per `IntPtr` (`Applied` HashSet, clear on `board.start`).

## Network

Same as before: GET stats, SignalR `Events` batch or POST `/api/events`, queue cap 50k drop noisy first.

Noisy: `plant.damage`, `zombie.damage`, `bullet.init`, `bullet.place`, `item.drop`, `pet.xp`.

## Must not

SQLite, player ids, copying third-party plugin source, patch `GameAPP.Start` / `Update` / EventNodes, GodMode / NoLose / writing sun/money, `CheckMix` / `TryGetMix`.
