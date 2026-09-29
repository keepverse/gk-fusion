# Lawn coordinates (injector SSOT)

**Status:** Shipped. Cherry / freeze / doom `pos` and IMGUI damage floaters share this path.  
**Not** Phaser `#/lawn` — that projector uses [`gk-web/web/fusion-rpg-web/src/game/gridMath.ts`](../../../gk-web/web/fusion-rpg-web/src/game/gridMath.ts) only.

Unity `Board` / `Mouse` remain physics SSOT. Overlay code must not invent a second grid from entity `transform.position` (roots sit at feet → VFX one lawn row low) or from a walking zombie’s X.

Pure index/GUI math lives in Core [`LawnCoordMath`](../../../gk-core/src/FusionRpg.Core/Lawn/LawnCoordMath.cs). Injector [`LawnCoords`](../../src/FusionRpg.Injector/Lawn/LawnCoords.cs) is the Mouse/Board adapter.

## Mapping

| Need | Call | Source |
|---|---|---|
| Cell center `(col, row)` | `LawnCoords.CellCenter` | `Mouse.GetBoxXFromColumn` / `GetBoxYFromRow` |
| Clamp | `CheatState.SpawnCol` / `SpawnRow` setters + `ClampCol` / `ClampRow` | `LawnCoordMath.ClampIndex`; last col **9**, last row **4** unless `gridSystem` says otherwise |
| World X → col | `LawnCoords.ColFromX` | `Mouse.GetColumnFromX` |
| Floater follow | `LawnCoords.BodyWorld` | **Y** = `GetBoxYFromRow` of `theZombieRow` / `thePlantRow`; **X** = transform (walker). Else renderer center / feet + half-cell |
| IMGUI | `LawnCoords.TryWorldToGui` only | `WorldToScreenPoint` + `LawnCoordMath.GuiPoint` (`pixelRect` + rise). Do not invent `(0,0)` on miss |
| MiniPet / bucket | `LawnCoords.CellCenter(SpawnCol, SpawnRow)` | Same cell center as cherry — never `(col, row)` as world units |

`SetPlant` / `SetZombie(row)` / `SetGridItem` stay integer col/row game APIs.

## Do not

- Prefer any living `Zombie`/`Plant` transform for BoardAction world XY (cherry X used to track a walker).
- Copy Phaser `CELL_W` / `ORIGIN_Y` into the injector.
- Clamp row to **5** (that is a phantom sixth row on a 0–4 lawn).
- Assign `CheatState.SpawnRow` without going through the property setter.
