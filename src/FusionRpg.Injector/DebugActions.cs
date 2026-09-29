using System.Text.Json;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats;
using FusionRpg.Injector.Bridges;
using FusionRpg.Injector.Lawn;
using FusionRpg.Injector.Stats;
using Il2CppInterop.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FusionRpg.Injector;

/// <summary>Deterministic debug spawn / status / mods for effect-pipeline tests.</summary>
public static class DebugActions
{
    /// <returns>False if Admit rejected, Create null, or exception; true if spawn completed.</returns>
    public static bool SpawnPlant(JsonElement p)
    {
        try
        {
            if (!Match.SpawnAdmit.TryAdmit("plant", out _))
                return false;

            var typeId = Int(p, "typeId", CheatState.ManualTypeId);
            if (p.TryGetProperty("col", out var c) && c.TryGetInt32(out var col)) CheatState.SpawnCol = LawnCoords.ClampCol(col);
            if (p.TryGetProperty("row", out var r) && r.TryGetInt32(out var row)) CheatState.SpawnRow = LawnCoords.ClampRow(row);
            ApplyScaleProps(p, plant: true);
            ApplyAbsoluteProps(p, plant: true);
            CheatState.SetToggle("G-FREE-SET", true, "debug", emitInject: false);
            CheatState.SetToggle("A-APPLY", true, "debug", emitInject: false);

            var plant = CreatePlant.Instance.SetPlant(CheatState.SpawnCol, CheatState.SpawnRow, (PlantType)typeId,
                null, default, true, true, null);
            if (plant == null)
            {
                CheatState.Error("debug.spawn-plant null");
                return false;
            }

            Match.SpawnOriginTags.Mark(plant.Pointer, Match.SpawnOriginTags.Debug);
            // Force re-apply with absolutes if Start already ran without them.
            GameHooks.Applied.Remove(plant.Pointer);
            EntityApply.RunPlant(plant, "debug.spawn", includeAbsolute: true);
            CheatState.Select(plant.Pointer, "plant");
            var dump = GameDumps.Plant(plant, "debug.spawn", plant.thePlantHealth, plant.thePlantMaxHealth, plant.attackDamage);
            dump["side"] = "plant";
            dump["typeId"] = typeId;
            dump["col"] = CheatState.SpawnCol;
            dump["row"] = CheatState.SpawnRow;
            try { dump["thePlantAttackInterval"] = plant.thePlantAttackInterval; } catch { }
            try { dump["thePlantProduceInterval"] = plant.thePlantProduceInterval; } catch { }
            DebugRuntime.Emit("debug.spawn.plant", dump);
            CheatState.Note($"debug spawn plant {typeId} @{CheatState.SpawnCol},{CheatState.SpawnRow}");
            MaybePinDerived(p, plant.Pointer.ToString("X"));
            MaybePinElement(p, plant.Pointer.ToString("X"));
            MaybePinSpawnHp(p, plant.Pointer.ToString("X"));
            return true;
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.spawn-plant: " + ex.Message);
            return false;
        }
    }

    /// <returns>False if Admit rejected, Create null, or exception; true if spawn completed.</returns>
    public static bool SpawnZombie(JsonElement p)
    {
        try
        {
            if (!Match.SpawnAdmit.TryAdmit("zombie", out _))
                return false;

            var typeId = Int(p, "typeId", 0);
            if (p.TryGetProperty("row", out var r) && r.TryGetInt32(out var row)) CheatState.SpawnRow = LawnCoords.ClampRow(row);
            var x = p.TryGetProperty("x", out var xEl) && xEl.TryGetSingle(out var xv) ? xv : 9.9f;
            var mc = p.TryGetProperty("mindControl", out var m) && m.ValueKind == JsonValueKind.True;
            var asExtra = string.Equals(Str(p, "source"), "extra", StringComparison.OrdinalIgnoreCase);
            ApplyScaleProps(p, plant: false);
            ApplyAbsoluteProps(p, plant: false);
            CheatState.SetToggle("A-APPLY", true, "debug", emitInject: false);

            Zombie? z = null;
            if (asExtra) CheatState.PendingSpawnSourceTag = "extra";
            try
            {
                z = mc
                    ? CreateZombieSpawn.SetMindControl(CheatState.SpawnRow, (ZombieType)typeId, x, true)
                    : CreateZombieSpawn.Set(CheatState.SpawnRow, (ZombieType)typeId, x, false);
            }
            finally
            {
                if (asExtra && z != null)
                    CheatState.RegisterSpawnSourceTag(z.Pointer, "extra");
                CheatState.ClearPendingSpawnSourceTag();
            }

            if (z == null)
            {
                CheatState.Error("debug.spawn-zombie null");
                return false;
            }

            Match.SpawnOriginTags.Mark(z.Pointer, Match.SpawnOriginTags.Debug);
            GameHooks.Applied.Remove(z.Pointer);
            EntityApply.RunZombie(z, "debug.spawn", includeAbsolute: true);
            CheatState.Select(z.Pointer, "zombie");
            var dump = GameDumps.Zombie(z, "debug.spawn", Bridges.ZombieCombatFields.GetHp(z), Bridges.ZombieCombatFields.GetMaxHp(z), z.theAttackDamage,
                z.theFirstArmorHealth, z.theFirstArmorMaxHealth);
            dump["side"] = "zombie";
            dump["typeId"] = typeId;
            dump["row"] = CheatState.SpawnRow;
            dump["x"] = x;
            if (asExtra) dump["source"] = "extra";
            if (mc) dump["mindControlled"] = true;
            try { dump["uniqueSpeed"] = z.uniqueSpeed; } catch { }
            try { dump["theSpeed"] = z.theSpeed; } catch { }
            DebugRuntime.Emit("debug.spawn.zombie", dump);
            CheatState.Note($"debug spawn zombie {typeId} row={CheatState.SpawnRow}");
            MaybePinDerived(p, z.Pointer.ToString("X"));
            MaybePinElement(p, z.Pointer.ToString("X"));
            MaybePinSpawnHp(p, z.Pointer.ToString("X"));
            return true;
        }
        catch (Exception ex)
        {
            CheatState.ClearPendingSpawnSourceTag();
            CheatState.Error("debug.spawn-zombie: " + ex.Message);
            return false;
        }
    }

    /// <returns>False if Admit rejected, Create null, or exception; true if spawn completed.</returns>
    public static bool SpawnBullet(JsonElement p)
    {
        try
        {
            if (!Match.SpawnAdmit.TryAdmit("bullet", out _))
                return false;

            var board = GameHooks.Board;
            if (board == null) { CheatState.Error("debug.spawn-bullet: no board"); return false; }
            var bulletType = Int(p, "bulletType", Int(p, "typeId", 0));
            var row = LawnCoords.ClampRow(Int(p, "row", CheatState.SpawnRow));
            var x = p.TryGetProperty("x", out var xEl) && xEl.TryGetSingle(out var xv) ? xv : 3f;
            var y = p.TryGetProperty("y", out var yEl) && yEl.TryGetSingle(out var yv) ? yv : 0f;
            var moveWay = p.TryGetProperty("moveWay", out var mw) && mw.TryGetInt32(out var mwi)
                ? (BulletMoveWay)mwi
                : BulletMoveWay.MoveRight;
            var b = CreateBullet.Instance.SetBullet(x, y, row, (BulletType)bulletType, moveWay, false);
            if (b == null) { CheatState.Error("debug.spawn-bullet null"); return false; }
            if (p.TryGetProperty("damage", out var d) && d.TryGetInt32(out var dmg) && dmg >= 0)
                b.Damage = dmg;
            if (p.TryGetProperty("fromType", out var ft) && ft.TryGetInt32(out var fti))
            {
                try { b.fromType = (PlantType)fti; } catch { }
            }
            DebugRuntime.Emit("debug.spawn.bullet", new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(b),
                ["bulletType"] = bulletType,
                ["damage"] = b.Damage,
                ["row"] = row,
                ["x"] = x,
                ["y"] = y
            });
            return true;
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.spawn-bullet: " + ex.Message);
            return false;
        }
    }

    // E40 (spec-spawn-non-grid.md §2a): pet/bucket/mower widen spawn.entity.kind onto three more
    // Unity write paths that were reachable only from CheatActions/the cheat menu (CheatActions.cs's
    // own SpawnPet/SpawnBucket) or from nowhere at all (CreateMower.SetMower -- patched for capture,
    // never called, GameHooks.cs's MowerPlace hook). Same idiom as SpawnPlant/SpawnZombie/SpawnBullet
    // above: parse the payload, clamp col/row through LawnCoords (structural -- an out-of-board cell
    // is not a legal placement, never a balance question, the same reasoning MatchCaps' per-runtime
    // spawn caps like MaxLivingBullets carry), call the real game API, dump a debug emit, return
    // false rather than throw on a null result or a caught exception.

    /// <returns>False if no board, Create null, or exception; true if spawn completed.</returns>
    public static bool SpawnPet(JsonElement p)
    {
        try
        {
            var board = GameHooks.Board;
            if (board == null) { CheatState.Error("debug.spawn-pet: no board"); return false; }

            var typeId = Int(p, "typeId", 0);
            var col = LawnCoords.ClampCol(Int(p, "col", CheatState.SpawnCol));
            var row = LawnCoords.ClampRow(Int(p, "row", CheatState.SpawnRow));

            var pet = MiniPet.SetPet(board, LawnCoords.CellCenter(col, row), (PetType)typeId);
            if (pet == null) { CheatState.Error("debug.spawn-pet null"); return false; }

            DebugRuntime.Emit("debug.spawn.pet", new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(pet),
                ["typeId"] = typeId,
                ["col"] = col,
                ["row"] = row,
            });
            CheatState.Note($"debug spawn pet {typeId} @{col},{row}");
            return true;
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.spawn-pet: " + ex.Message);
            return false;
        }
    }

    /// <returns>False if no board/itemManager, Create null, or exception; true if spawn completed.</returns>
    public static bool SpawnBucket(JsonElement p)
    {
        try
        {
            var board = GameHooks.Board ?? Board.Instance;
            var mgr = GameAPP.itemManager;
            if (mgr == null || board == null) { CheatState.Error("debug.spawn-bucket: no board/itemManager"); return false; }

            var typeId = Int(p, "typeId", 0);
            var col = LawnCoords.ClampCol(Int(p, "col", CheatState.SpawnCol));
            var row = LawnCoords.ClampRow(Int(p, "row", CheatState.SpawnRow));

            var bucket = mgr.SetBucket(board, (BucketType)typeId, LawnCoords.CellCenter(col, row));
            if (bucket == null) { CheatState.Error("debug.spawn-bucket null"); return false; }

            DebugRuntime.Emit("debug.spawn.bucket", new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(bucket),
                ["typeId"] = typeId,
                ["col"] = col,
                ["row"] = row,
            });
            CheatState.Note($"debug spawn bucket {typeId} @{col},{row}");
            return true;
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.spawn-bucket: " + ex.Message);
            return false;
        }
    }

    /// <returns>False if Create null or exception; true if spawn completed.</returns>
    public static bool SpawnMower(JsonElement p)
    {
        try
        {
            var typeId = Int(p, "typeId", 0);
            var row = LawnCoords.ClampRow(Int(p, "row", CheatState.SpawnRow));
            var x = p.TryGetProperty("x", out var xEl) && xEl.TryGetSingle(out var xv) ? xv : 0f;

            // CreateMower.SetMower is an INSTANCE method (confirmed by a real build against the
            // BepInEx interop DLLs -- CS0120 on the static call form; the spec's own citation and
            // GameHooks.cs's Harmony Postfix on it, MowerPlace, both omit __instance, which is not
            // reliable evidence either way for Harmony). Same shape as CreatePlant.Instance.SetPlant /
            // CreateBullet.Instance.SetBullet above.
            var mower = CreateMower.Instance.SetMower((MowerType)typeId, x, row);
            if (mower == null) { CheatState.Error("debug.spawn-mower null"); return false; }

            DebugRuntime.Emit("debug.spawn.mower", new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(mower),
                ["typeId"] = typeId,
                ["row"] = row,
                ["x"] = x,
            });
            CheatState.Note($"debug spawn mower {typeId} row={row}");
            return true;
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.spawn-mower: " + ex.Message);
            return false;
        }
    }

    /// <summary>Clear Tab A scales, P-/Z- absolutes, and D damage/probe knobs to identity defaults.</summary>
    public static void ResetMods()
    {
        try
        {
            CheatState.ResetGroup("A-");
            CheatState.ResetGroup("P-");
            CheatState.ResetGroup("Z-");
            CheatState.ResetGroup("E-");
            CheatState.SetFloat("D-DMG-SET", -1, "debug", emitInject: false);
            CheatState.SetFloat("D-DMG-%", 1, "debug", emitInject: false);
            CheatState.SetToggle("D-PROBE-PLANT", false, "debug", emitInject: false);
            CheatState.SetToggle("D-PROBE-BULLET", false, "debug", emitInject: false);
            CheatState.SetToggle("A-APPLY", true, "debug", emitInject: false);
            CheatActions.PushScalesNow();
            DebugRuntime.Emit("debug.mods.reset", new Dictionary<string, object> { ["ok"] = true });
        }
        catch (Exception ex) { CheatState.Error("debug.reset-mods: " + ex.Message); }
    }

    public static void SetMods(JsonElement p)
    {
        try
        {
            if (p.TryGetProperty("probePlant", out var pp))
                CheatState.SetToggle("D-PROBE-PLANT", pp.ValueKind == JsonValueKind.True, "debug");
            if (p.TryGetProperty("probeBullet", out var pb))
                CheatState.SetToggle("D-PROBE-BULLET", pb.ValueKind == JsonValueKind.True, "debug");
            if (p.TryGetProperty("logDamage", out var ld))
                CheatState.LocalStats.LogDamage = ld.ValueKind != JsonValueKind.False;

            SetFloatIf(p, "plant", "attackPercent", "A-P-ATK%");
            SetFlatIf(p, "plant", "attackFlat", "A-P-ATK+");
            SetFloatIf(p, "plant", "hpPercent", "A-P-HP%");
            SetFlatIf(p, "plant", "hpFlat", "A-P-HP+");
            SetFloatIf(p, "plant", "defensePercent", "A-P-DEF%");
            SetFlatIf(p, "plant", "defenseFlat", "A-P-DEF+");
            SetFloatIf(p, "zombie", "attackPercent", "A-Z-ATK%");
            SetFlatIf(p, "zombie", "attackFlat", "A-Z-ATK+");
            SetFloatIf(p, "zombie", "hpPercent", "A-Z-HP%");
            SetFlatIf(p, "zombie", "hpFlat", "A-Z-HP+");
            SetFloatIf(p, "zombie", "defensePercent", "A-Z-DEF%");
            SetFlatIf(p, "zombie", "defenseFlat", "A-Z-DEF+");

            if (p.TryGetProperty("bullet", out var bullet) && bullet.ValueKind == JsonValueKind.Object)
            {
                if (bullet.TryGetProperty("damageSet", out var ds) && ds.TryGetDouble(out var dsv))
                    CheatState.SetFloat("D-DMG-SET", dsv, "debug");
                if (bullet.TryGetProperty("damagePercent", out var dp) && dp.TryGetDouble(out var dpv))
                    CheatState.SetFloat("D-DMG-%", dpv, "debug");
            }

            if (p.TryGetProperty("plant", out var plant) && plant.ValueKind == JsonValueKind.Object)
            {
                if (plant.TryGetProperty("attackInterval", out var ai) && ai.TryGetDouble(out var aiv))
                    CheatState.SetFloat("P-ATK-INT", aiv, "debug");
                if (plant.TryGetProperty("produceInterval", out var pi) && pi.TryGetDouble(out var piv))
                    CheatState.SetFloat("P-PROD-INT", piv, "debug");
            }
            if (p.TryGetProperty("zombie", out var zom) && zom.ValueKind == JsonValueKind.Object)
            {
                if (zom.TryGetProperty("uniqueSpeed", out var us) && us.TryGetDouble(out var usv))
                    CheatState.SetFloat("Z-SPD-U", usv, "debug");
                if (zom.TryGetProperty("theSpeed", out var ts) && ts.TryGetDouble(out var tsv))
                    CheatState.SetFloat("Z-SPD", tsv, "debug");
                if (zom.TryGetProperty("theOriginSpeed", out var os) && os.TryGetDouble(out var osv))
                    CheatState.SetFloat("Z-SPD-O", osv, "debug");
            }

            var boardTouched = false;
            if (p.TryGetProperty("board", out var board) && board.ValueKind == JsonValueKind.Object)
            {
                if (board.TryGetProperty("zombieHealthMultiplier", out var zh) && zh.TryGetDouble(out var zhv))
                { CheatState.SetFloat("E-ZH", zhv, "debug"); boardTouched = true; }
                if (board.TryGetProperty("zombieDamageMultiplier", out var zd) && zd.TryGetDouble(out var zdv))
                { CheatState.SetFloat("E-ZD", zdv, "debug"); boardTouched = true; }
                if (board.TryGetProperty("zombieSpeedMultiplier", out var zs) && zs.TryGetDouble(out var zsv))
                { CheatState.SetFloat("E-ZS", zsv, "debug"); boardTouched = true; }
                if (board.TryGetProperty("zombieCountMultiplier", out var zc) && zc.TryGetDouble(out var zcv))
                { CheatState.SetFloat("E-ZC", zcv, "debug"); boardTouched = true; }
            }

            CheatState.SetToggle("A-APPLY", true, "debug", emitInject: false);
            CheatActions.PushScalesNow();
            if (boardTouched)
                CheatActions.ApplyBoardConfig();
            DebugRuntime.Emit("debug.mods.set", new Dictionary<string, object> { ["ok"] = true, ["boardTouched"] = boardTouched });
        }
        catch (Exception ex) { CheatState.Error("debug.set-mods: " + ex.Message); }
    }

    public static void Economy(JsonElement p)
    {
        var which = Str(p, "which") ?? "sun";
        var add = p.TryGetProperty("add", out var a) && a.ValueKind == JsonValueKind.True;
        var value = p.TryGetProperty("value", out var v) && v.TryGetDouble(out var dv) ? (float)dv : 0f;
        CheatActions.SetEconomy(which, value, add);
        DebugRuntime.Emit("debug.economy", new Dictionary<string, object>
        {
            ["which"] = which,
            ["value"] = value,
            ["add"] = add
        });
    }

    public static void BoardConfigApply(JsonElement p)
    {
        _ = p;
        CheatActions.ApplyBoardConfig();
        DebugRuntime.Emit("debug.board.config", new Dictionary<string, object> { ["ok"] = true });
    }

    /// <summary>Mid-match BoardAction AOE: freeze / doom / fireline / cherry.</summary>
    public static void BoardAction(JsonElement p)
    {
        try
        {
            var board = GameHooks.Board ?? Board.Instance;
            if (board?.boardAction == null)
            {
                CheatState.Error("debug.board-action: no boardAction");
                return;
            }

            var op = (Str(p, "op") ?? "freeze").ToLowerInvariant();
            var col = Int(p, "col", CheatState.SpawnCol);
            var row = Int(p, "row", CheatState.SpawnRow);
            col = LawnCoords.ClampCol(col);
            row = LawnCoords.ClampRow(row);
            CheatState.SpawnCol = col;
            CheatState.SpawnRow = row;

            var pos = CellPos(col, row);
            var damage = Int(p, "damage", 1800);
            var fromType = (PlantType)Int(p, "fromType", -1);
            Il2CppSystem.Action<Zombie>? zombieAction = null;

            switch (op)
            {
                case "freeze":
                {
                    var timer = p.TryGetProperty("timer", out var t) && t.TryGetSingle(out var tv) ? tv : 3f;
                    board.boardAction.CreateFreeze(pos, timer);
                    EmitBoardAction(op, col, row, damage, new Dictionary<string, object> { ["timer"] = timer, ["x"] = pos.x, ["y"] = pos.y });
                    break;
                }
                case "doom":
                {
                    var setPit = !(p.TryGetProperty("setPit", out var sp) && sp.ValueKind == JsonValueKind.False);
                    var iceDoom = p.TryGetProperty("iceDoom", out var ice) && ice.ValueKind == JsonValueKind.True;
                    var effect = Int(p, "effect", 0);
                    var existParticle = !(p.TryGetProperty("existParticle", out var ep) && ep.ValueKind == JsonValueKind.False);
                    board.boardAction.SetDoom(col, row, setPit, iceDoom, pos, damage, effect, zombieAction, existParticle, fromType);
                    EmitBoardAction(op, col, row, damage, new Dictionary<string, object>
                    {
                        ["setPit"] = setPit,
                        ["iceDoom"] = iceDoom,
                        ["x"] = pos.x,
                        ["y"] = pos.y
                    });
                    break;
                }
                case "fireline":
                case "fire":
                {
                    var fromZombie = p.TryGetProperty("fromZombie", out var fz) && fz.ValueKind == JsonValueKind.True;
                    var fix = p.TryGetProperty("fix", out var fx) && fx.ValueKind == JsonValueKind.True;
                    var shake = !(p.TryGetProperty("shake", out var sh) && sh.ValueKind == JsonValueKind.False);
                    board.boardAction.CreateFireLine(row, damage, fromZombie, fix, shake, zombieAction, fromType);
                    EmitBoardAction(op, col, row, damage, new Dictionary<string, object>
                    {
                        ["fromZombie"] = fromZombie,
                        ["fix"] = fix,
                        ["shake"] = shake
                    });
                    break;
                }
                case "cherry":
                case "cherryexplode":
                {
                    var bombType = (CherryBombType)Int(p, "bombType", (int)CherryBombType.Normal);
                    var immediately = !(p.TryGetProperty("immediately", out var im) && im.ValueKind == JsonValueKind.False);
                    board.boardAction.CreateCherryExplode(pos, row, bombType, damage, fromType, zombieAction, immediately);
                    EmitBoardAction(op, col, row, damage, new Dictionary<string, object>
                    {
                        ["bombType"] = (int)bombType,
                        ["immediately"] = immediately,
                        ["x"] = pos.x,
                        ["y"] = pos.y
                    });
                    break;
                }
                default:
                    CheatState.Error("debug.board-action unknown op: " + op);
                    break;
            }
        }
        catch (Exception ex) { CheatState.Error("debug.board-action: " + ex.Message); }
    }

    public static void SpawnGrid(JsonElement p)
    {
        try
        {
            var typeId = Int(p, "typeId", (int)GridItemType.Grave);
            if (p.TryGetProperty("col", out var c) && c.TryGetInt32(out var col))
                CheatState.SpawnCol = LawnCoords.ClampCol(col);
            if (p.TryGetProperty("row", out var r) && r.TryGetInt32(out var row))
                CheatState.SpawnRow = LawnCoords.ClampRow(row);
            var graveType = (GraveType)Int(p, "graveType", 0);
            PlaceGridItem(CheatState.SpawnCol, CheatState.SpawnRow, typeId, graveType, "debug.spawn");
        }
        catch (Exception ex) { CheatState.Error("debug.spawn-grid: " + ex.Message); }
    }

    /// <summary>Clear a grid item by cell and/or type; <c>random:true</c> picks any matching item.</summary>
    public static void ClearGrid(JsonElement p)
    {
        try
        {
            var typeId = Int(p, "typeId", (int)GridItemType.Grave);
            var random = p.TryGetProperty("random", out var rnd) && rnd.ValueKind == JsonValueKind.True;
            int? col = p.TryGetProperty("col", out var c) && c.TryGetInt32(out var cv) ? cv : null;
            int? row = p.TryGetProperty("row", out var r) && r.TryGetInt32(out var rv) ? rv : null;
            var cleared = ClearGridItem(typeId, col, row, random, emitKind: "debug.grid.clear");
            if (!cleared)
                CheatState.Note($"debug clear-grid miss type={typeId} random={random}");
        }
        catch (Exception ex) { CheatState.Error("debug.clear-grid: " + ex.Message); }
    }

    /// <summary>Paint <see cref="BoxType"/> on a cell via <c>BoardGrid.boxType</c> + <c>UpdateBox</c>.
    /// Nuclear-scorched lawn uses <see cref="BoxType.Dirt"/> (aliases: dirt, nuclear, scorched).</summary>
    public static void SetBox(JsonElement p)
    {
        try
        {
            var board = GameHooks.Board ?? Board.Instance;
            if (board == null)
            {
                CheatState.Error("debug.set-box: no board");
                return;
            }

            var col = LawnCoords.ClampCol(Int(p, "col", CheatState.SpawnCol));
            var row = LawnCoords.ClampRow(Int(p, "row", CheatState.SpawnRow));
            CheatState.SpawnCol = col;
            CheatState.SpawnRow = row;
            var boxType = ParseBoxType(p, out var boxName);
            var withPit = p.TryGetProperty("withPit", out var wp) && wp.ValueKind == JsonValueKind.True;

            BoxType before = default;
            try { before = board.GetBoxType(col, row); } catch { }

            var wrote = false;
            var path = "";
            try
            {
                var gs = board.gridSystem;
                var cell = gs?.GetGrid(col, row);
                if (cell != null)
                {
                    cell.boxType = boxType;
                    wrote = true;
                    path = "BoardGrid.set_boxType";
                }
            }
            catch (Exception ex) { CheatState.Error("set BoardGrid.boxType: " + ex.Message); }

            if (!wrote)
            {
                wrote = TryWriteRoadType(board, col, row, boxType, out var index, out var layout);
                path = layout ?? "roadType";
                if (wrote) path = "roadType:" + path + " idx=" + index;
            }

            try { board.UpdateBox(col, row); } catch (Exception ex) { CheatState.Error("UpdateBox: " + ex.Message); }

            if (withPit && board.boardAction != null)
            {
                try
                {
                    board.boardAction.SetPit(col, row);
                    path += "+SetPit";
                }
                catch (Exception ex) { CheatState.Error("SetPit: " + ex.Message); }
            }

            BoxType after = before;
            try { after = board.GetBoxType(col, row); } catch { }

            DebugRuntime.Emit("debug.box.set", new Dictionary<string, object>
            {
                ["ok"] = wrote && (int)after == (int)boxType,
                ["wrote"] = wrote,
                ["col"] = col,
                ["row"] = row,
                ["boxType"] = (int)boxType,
                ["boxName"] = boxName,
                ["before"] = (int)before,
                ["beforeName"] = before.ToString(),
                ["after"] = (int)after,
                ["afterName"] = after.ToString(),
                ["path"] = path,
                ["withPit"] = withPit
            });
            CheatState.Note($"debug set-box {boxName} @{col},{row} {before}->{after} via {path}");
        }
        catch (Exception ex) { CheatState.Error("debug.set-box: " + ex.Message); }
    }

    /// <summary>Zomboni / Sledge-style ice trail via <see cref="DriverZombie.CreateIceRoad"/>.</summary>
    public static void IceRoad(JsonElement p)
    {
        try
        {
            var row = LawnCoords.ClampRow(Int(p, "row", CheatState.SpawnRow));
            CheatState.SpawnRow = row;
            var x = p.TryGetProperty("x", out var xEl) && xEl.TryGetSingle(out var xv) ? xv : 8f;
            var typeId = Int(p, "typeId", 16); // DriverZombie
            var killAfter = !(p.TryGetProperty("keepDriver", out var kd) && kd.ValueKind == JsonValueKind.True);

            CheatState.SetToggle("A-APPLY", true, "debug", emitInject: false);
            var iceBefore = 0;
            try { iceBefore = (GameHooks.Board ?? Board.Instance)?.iceRoads?.Count ?? 0; } catch { }

            if (!Match.SpawnAdmit.TryAdmit("zombie", out _))
                return;

            var z = CreateZombieSpawn.Set(row, (ZombieType)typeId, x, false);
            if (z == null)
            {
                CheatState.Error("debug.ice-road: spawn null");
                return;
            }
            Match.SpawnOriginTags.Mark(z.Pointer, Match.SpawnOriginTags.Debug);

            string typeName = "?";
            try { typeName = z.theZombieType.ToString(); } catch { try { typeName = z.GetIl2CppType().Name; } catch { } }

            var drove = false;
            var castPath = "";
            try
            {
                var driver = z.TryCast<DriverZombie>();
                if (driver != null)
                {
                    castPath = "TryCast<DriverZombie>";
                    driver.CreateIceRoad();
                    drove = true;
                }
                else
                {
                    var super = z.TryCast<SuperDriverZombie>();
                    if (super != null)
                    {
                        castPath = "TryCast<SuperDriverZombie>";
                        super.CreateIceRoad();
                        drove = true;
                    }
                }
            }
            catch (Exception ex) { CheatState.Error("CreateIceRoad: " + ex.Message); }

            // Fallback: call on any existing driver already on the lawn.
            if (!drove)
            {
                try
                {
                    foreach (var other in UObject.FindObjectsOfType<DriverZombie>())
                    {
                        if (other == null) continue;
                        other.CreateIceRoad();
                        drove = true;
                        castPath = "FindObjectsOfType<DriverZombie>";
                        break;
                    }
                }
                catch (Exception ex) { CheatState.Error("ice-road fallback: " + ex.Message); }
            }

            var iceAfter = iceBefore;
            try { iceAfter = (GameHooks.Board ?? Board.Instance)?.iceRoads?.Count ?? iceBefore; } catch { }

            if (killAfter)
            {
                try { z.Die(0); } catch { }
            }

            DebugRuntime.Emit("debug.ice.road", new Dictionary<string, object>
            {
                ["ok"] = drove,
                ["row"] = row,
                ["x"] = x,
                ["typeId"] = typeId,
                ["typeName"] = typeName,
                ["castPath"] = castPath,
                ["iceRoadCountBefore"] = iceBefore,
                ["iceRoadCount"] = iceAfter,
                ["keepDriver"] = !killAfter
            });
            CheatState.Note($"debug ice-road row={row} drove={drove} type={typeName} ice {iceBefore}->{iceAfter}");
        }
        catch (Exception ex) { CheatState.Error("debug.ice-road: " + ex.Message); }
    }

    public static void GridQuery(JsonElement p)
    {
        try
        {
            var board = GameHooks.Board ?? Board.Instance;
            if (board == null)
            {
                CheatState.Error("debug.grid-query: no board");
                return;
            }

            var col = LawnCoords.ClampCol(Int(p, "col", CheatState.SpawnCol));
            var row = LawnCoords.ClampRow(Int(p, "row", CheatState.SpawnRow));
            var dump = new Dictionary<string, object> { ["col"] = col, ["row"] = row };
            try
            {
                var bt = board.GetBoxType(col, row);
                dump["boxType"] = (int)bt;
                dump["boxName"] = bt.ToString();
            }
            catch (Exception ex) { dump["boxError"] = ex.Message; }

            var gs = board.gridSystem;
            if (gs != null)
            {
                try { dump["hasGrave"] = gs.HasGrave(col, row); } catch { }
                try { dump["graveNum"] = gs.GetGraveNum(col, row); } catch { }
                try { dump["hasPit"] = gs.HasPit(col, row); } catch { }
                try { dump["hasPot"] = gs.HasPot(col, row); } catch { }
                try { dump["hasLily"] = gs.HasLily(col, row); } catch { }
                try { dump["hasPlant"] = gs.HasPlant(col, row); } catch { }
            }

            DebugRuntime.Emit("debug.grid.query", dump);
        }
        catch (Exception ex) { CheatState.Error("debug.grid-query: " + ex.Message); }
    }

    public static GridItem? PlaceGridItem(int col, int row, int typeId, GraveType graveType, string source)
    {
        col = LawnCoords.ClampCol(col);
        row = LawnCoords.ClampRow(row);
        CheatState.SpawnCol = col;
        CheatState.SpawnRow = row;
        var g = GridItem.SetGridItem(col, row, (GridItemType)typeId, graveType);
        SpawnCatalog.Note("grid", typeId, ((GridItemType)typeId).ToString(), source);
        DebugRuntime.Emit("debug.spawn.grid", new Dictionary<string, object>
        {
            ["ok"] = g != null,
            ["typeId"] = typeId,
            ["typeName"] = ((GridItemType)typeId).ToString(),
            ["graveType"] = (int)graveType,
            ["col"] = col,
            ["row"] = row,
            ["source"] = source
        });
        CheatState.Note(g != null ? $"debug spawn grid {typeId} @{col},{row}" : "debug spawn grid null");
        return g;
    }

    /// <returns>true if an item was cleared.</returns>
    public static bool ClearGridItem(int typeId, int? col, int? row, bool random, string emitKind)
    {
        var matches = new List<GridItem>();
        foreach (var item in UObject.FindObjectsOfType<GridItem>())
        {
            if (item == null) continue;
            try
            {
                if ((int)item.theItemType != typeId) continue;
                if (col.HasValue && item.theItemColumn != col.Value) continue;
                if (row.HasValue && item.theItemRow != row.Value) continue;
                matches.Add(item);
            }
            catch { }
        }

        if (matches.Count == 0) return false;

        GridItem target;
        if (random)
            target = matches[UnityEngine.Random.Range(0, matches.Count)];
        else if (col.HasValue && row.HasValue)
            target = matches[0];
        else if (matches.Count == 1)
            target = matches[0];
        else
        {
            CheatState.Error("debug.clear-grid: multiple matches; pass col/row or random:true");
            return false;
        }

        var tCol = 0;
        var tRow = 0;
        var tType = typeId;
        try { tCol = target.theItemColumn; tRow = target.theItemRow; tType = (int)target.theItemType; } catch { }
        try { target.Die(); } catch (Exception ex) { CheatState.Error("grid Die: " + ex.Message); return false; }

        try
        {
            var board = GameHooks.Board ?? Board.Instance;
            var gs = board?.gridSystem;
            if (gs != null && tType == (int)GridItemType.Grave)
                gs.RemoveGrave(tCol, tRow);
        }
        catch { }

        DebugRuntime.Emit(emitKind, new Dictionary<string, object>
        {
            ["ok"] = true,
            ["typeId"] = tType,
            ["typeName"] = ((GridItemType)tType).ToString(),
            ["col"] = tCol,
            ["row"] = tRow,
            ["random"] = random
        });
        CheatState.Note($"debug clear grid {tType} @{tCol},{tRow}");
        return true;
    }

    static BoxType ParseBoxType(JsonElement p, out string name)
    {
        if (p.TryGetProperty("boxType", out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
            {
                var bt = (BoxType)n;
                name = bt.ToString();
                return bt;
            }
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = (el.GetString() ?? "Grass").Trim();
                // Nuclear boom scorches grass → Dirt (classic doom-shroom / nuclear crater dirt).
                if (s.Equals("nuclear", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("scorched", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("destroyed", StringComparison.OrdinalIgnoreCase))
                {
                    name = nameof(BoxType.Dirt);
                    return BoxType.Dirt;
                }
                if (Enum.TryParse<BoxType>(s, ignoreCase: true, out var parsed))
                {
                    name = parsed.ToString();
                    return parsed;
                }
            }
        }
        name = nameof(BoxType.Grass);
        return BoxType.Grass;
    }

    static bool TryWriteRoadType(Board board, int col, int row, BoxType boxType, out int index, out string? layout)
    {
        index = -1;
        layout = null;
        var roads = board.roadType;
        if (roads == null) return false;

        var len = roads.Length;
        var cols = 9;
        var rows = 5;
        try { cols = Mathf.Max(1, board.columnNum); } catch { }
        try
        {
            var gs = board.gridSystem;
            if (gs != null)
            {
                try { cols = Mathf.Max(1, gs.ColumnCount); } catch { }
                try { rows = Mathf.Max(1, gs.RowCount); } catch { }
            }
        }
        catch { }

        // Prefer row-major (row * cols + col); fall back to col-major if length fits that better.
        var rowMajor = row * cols + col;
        var colMajor = col * rows + row;
        if (rowMajor >= 0 && rowMajor < len)
        {
            index = rowMajor;
            layout = $"rowMajor cols={cols} len={len}";
        }
        else if (colMajor >= 0 && colMajor < len)
        {
            index = colMajor;
            layout = $"colMajor rows={rows} len={len}";
        }
        else
        {
            layout = $"noIndex cols={cols} rows={rows} len={len}";
            return false;
        }

        roads[index] = boxType;
        try { board.roadType = roads; } catch { }
        return true;
    }

    static Vector2 CellPos(int col, int row) => LawnCoords.CellCenter(col, row);

    static void EmitBoardAction(string op, int col, int row, long damage, Dictionary<string, object> extra)
    {
        var dump = new Dictionary<string, object>
        {
            ["op"] = op,
            ["col"] = col,
            ["row"] = row,
            ["damage"] = damage
        };
        foreach (var kv in extra)
            dump[kv.Key] = kv.Value;
        DebugRuntime.Emit("debug.board.action", dump);
        CheatState.Note($"debug board-action {op} @{col},{row}");
    }

    public static void ApplyStatus(JsonElement p, bool method)
    {
        var status = (Str(p, "status") ?? "butter").ToLowerInvariant();
        var duration = p.TryGetProperty("duration", out var d) && d.TryGetSingle(out var dv) ? dv : 4f;
        var level = Int(p, "level", 1);
        var target = Str(p, "target") ?? "selected";
        var n = 0;
        var ptrs = new List<string>();
        foreach (var z in ResolveZombies(target, p))
        {
            ApplyStatusToZombie(z, status, duration, level, method);
            try { ptrs.Add(GameDumps.Ptr(z)); } catch { }
            n++;
        }
        var dump = new Dictionary<string, object>
        {
            ["status"] = status,
            ["method"] = method,
            ["count"] = n,
            ["duration"] = duration,
            ["ptrs"] = ptrs
        };
        if (ptrs.Count == 1)
            dump["ptr"] = ptrs[0];
        DebugRuntime.Emit("debug.status.applied", dump);
    }

    public static void ClearStatus(JsonElement p)
    {
        var target = Str(p, "target") ?? "selected";
        var status = Str(p, "status");
        var n = 0;
        var ptrs = new List<string>();
        foreach (var z in ResolveZombies(target, p))
        {
            try
            {
                try { z.UnButtered(); } catch { }
                try { z.Warm(); } catch { }
                try { z.KillDebuff(); } catch { }
                try { z.freezeLevel = 0; } catch { }
                try { z.freezeSpeed = 1f; } catch { }
                try { z.coldSpeed = 1f; } catch { }
                try { z.butterSpeed = 1f; } catch { }
                try { ptrs.Add(GameDumps.Ptr(z)); } catch { }
                n++;
            }
            catch { }
        }
        var dump = new Dictionary<string, object>
        {
            ["count"] = n,
            ["ptrs"] = ptrs
        };
        if (!string.IsNullOrWhiteSpace(status))
            dump["status"] = status.ToLowerInvariant();
        if (ptrs.Count == 1)
            dump["ptr"] = ptrs[0];
        DebugRuntime.Emit("debug.status.cleared", dump);
    }

    public static void ApplyStatusToZombie(Zombie z, string status, float duration, int level, bool method)
    {
        if (z == null) return;
        if (!method)
        {
            switch (status)
            {
                case "butter": z.butterSpeed = 0f; break;
                case "freeze": z.freezeSpeed = 0f; break;
                case "cold": z.coldSpeed = 0.5f; break;
            }
            return;
        }
        switch (status)
        {
            case "butter":
                z.Buttered(duration, true);
                break;
            case "freeze":
                z.SetFreeze(duration, level);
                break;
            case "cold":
                z.SetCold(duration, level, false);
                break;
            case "poison":
                try { z.SetPoison(duration); }
                catch { CheatState.Error("SetPoison unavailable"); }
                break;
            case "hypno":
                z.SetMindControl(1);
                break;

            // E17: ember / jala / kelp declared UnityCc and had NO branch here. The methods were in
            // the game the whole time — status-ssot.md was right and our code never caught up — so
            // three of the eight "declared and does nothing" statuses were three cases away from
            // working. Verified against Assembly-CSharp metadata before writing: the signatures are
            // NOT uniform (SetJalaed takes nothing, SetKelped takes two), so the obvious
            // copy-paste of SetFreeze's shape would not have compiled.
            //
            // ⚠️ These three are FLAGS, not timed CC, and the compiler is what said so: SetEmbered
            // takes a bool, SetJalaed takes nothing, and only SetKelped carries a duration at all.
            // Copying SetFreeze(duration, level) — the obvious shape — does not compile.
            //
            // The consequence is real and is NOT papered over: for ember and jala the Unity side has
            // no notion of expiry, so the status instance expiring on our clock does not clear the
            // game flag. Clearing them needs a Withdraw path that FA3 does not have for these ids,
            // and inventing one here would be the applier layer wearing a debug action's clothes.
            case "ember":
                try { z.SetEmbered(true); }
                catch { CheatState.Error("SetEmbered unavailable"); }
                break;
            case "jala":
                try { z.SetJalaed(); }
                catch { CheatState.Error("SetJalaed unavailable"); }
                break;
            case "kelp":
                try { z.SetKelped(duration, true); }
                catch { CheatState.Error("SetKelped unavailable"); }
                break;
        }
    }

    /// <summary>
    /// E39 (spec-plant-side-status.md §2c): plant-side write for the 8 <c>UnityCc</c> statuses,
    /// swept against the 3.9 interop (`H:\Games\PVZ-Fusion-3.9_MelonLoader\...\Assembly-CSharp.dll`,
    /// ilspycmd -t Plant, 2026-09-04 — recorded in
    /// <c>docs/research/effect-runtime/03-status-and-spawn-surface.md</c> under "Plant-side status").
    ///
    /// <para><c>butter</c> is the only confirmed, safe write: <c>Plant.butterP</c> is a real IL2CPP
    /// <c>int</c> field with no competing name anywhere else on the class. Its exact unit (a frame
    /// count? a stack level? a bare on/off flag?) is UNVERIFIED — IL2CPP compiles the field's real
    /// consumer to native code the interop assembly never exposes, so no static read can confirm it.
    /// Writing the atom's own <c>level</c> param (already an int, already meant to carry a status's
    /// intensity/tier — <c>ApplyStatusToZombie</c>'s own signature) is the one mapping that invents
    /// no scaling factor; converting <c>duration</c> (float seconds) into whatever unit
    /// <c>butterP</c> actually counts would be exactly the kind of guess this module exists to
    /// avoid. Floored at 1 (never 0/negative), the same "count is structural-floored at 1" idiom
    /// spawn.entity's own count handling uses in the sink (InjectorEffectActionSink.cs, E28 fix
    /// #5).</para>
    ///
    /// <para><c>jala</c>'s own sweep hit, <c>Plant.InfluenceByJalapeno()</c>, is downgraded to
    /// REFUSED here after this module's own required follow-up read (the sweep result names a
    /// candidate; verifying it is this module's job before wiring it, same as E17's own
    /// precedent): the interop dump places it beside <c>UpgradeEvent</c> /
    /// <c>InfluenceByIceShroom</c> / <c>UseItem(BucketType, Bucket)</c> — an item-use / upgrade-
    /// reaction group, not a CC-apply group — and, like every IL2CPP method in this assembly, its
    /// real body is native code the interop wrapper never exposes, so there is no way to confirm
    /// from static analysis alone that calling it sets the same "on fire" state
    /// <c>Zombie.SetJalaed()</c> does rather than something else (an upgrade-catalyst reaction, a
    /// UI hook) entirely. Calling an unverified method here is exactly the E17 failure mode this
    /// module exists to stop shipping, so it refuses by name instead — the same posture
    /// <see cref="FusionRpg.Injector.Effects.InjectorEffectActionSink"/>'s own
    /// <c>UnclearableStatuses</c> already takes for ember/jala/hypno/kelp.</para>
    ///
    /// <para>The other six (<c>freeze</c>, <c>cold</c>, <c>poison</c>, <c>hypno</c>, <c>ember</c>,
    /// <c>kelp</c>) had ZERO hits in the sweep — no field or method on <c>Plant</c> matches any of
    /// them, nor did a broadened grep for freeze/cold/poison/slow/speed/mindcontrol/charm turn up
    /// anything beyond unrelated speed-modifier infrastructure and a type-identity bool
    /// (`tanglekelpPlant`) that is not a kelp-status flag.</para>
    /// </summary>
    public static bool ApplyStatusToPlant(Plant p, string status, float duration, int level, out string? reason)
    {
        reason = null;
        if (p == null) { reason = "status-side-unsupported"; return false; }

        if (status == "butter")
        {
            try { p.butterP = Math.Max(1, level); return true; }
            catch { reason = "status-side-unsupported"; return false; }
        }

        reason = "status-side-unsupported";
        return false;
    }

    public static void Kill(JsonElement p, bool plants)
    {
        // An explicit ptr is exact: kill that entity or fail loudly. Never fall back to the selection —
        // a caller naming ptr X must not have a different entity killed and read it back as X's death.
        var ptrHex = Str(p, "ptr");
        if (!string.IsNullOrWhiteSpace(ptrHex))
        {
            KillByPtr(ptrHex!, plants);
            return;
        }

        var target = Str(p, "target") ?? "selected";
        if (plants)
        {
            if (target is "all" or "all-plants")
                CheatActions.DeleteAllPlants();
            else
            {
                foreach (var pl in ResolvePlants(target, p))
                {
                    try { pl.Die(Plant.DieReason.BySelf); } catch { }
                }
            }
            return;
        }

        if (target is "all" or "all-zombies")
            CheatActions.DeleteAllZombies();
        else
            CheatActions.OneShotSelected();
    }

    static void KillByPtr(string ptrHex, bool plants)
    {
        if (plants)
        {
            foreach (var pl in UObject.FindObjectsOfType<Plant>())
            {
                if (pl == null || !string.Equals(GameDumps.Ptr(pl), ptrHex, StringComparison.OrdinalIgnoreCase)) continue;
                EntityStatWriter.ForceKillPlant(pl, "debug.kill-plant");
                return;
            }
            CheatState.Error("debug.kill-plant: no living plant ptr=" + ptrHex);
            return;
        }

        foreach (var z in UObject.FindObjectsOfType<Zombie>())
        {
            if (z == null || !string.Equals(GameDumps.Ptr(z), ptrHex, StringComparison.OrdinalIgnoreCase)) continue;
            EntityStatWriter.ForceKillZombie(z, "debug.kill");
            return;
        }
        CheatState.Error("debug.kill: no living zombie ptr=" + ptrHex);
    }

    public static void WaveFreeze(bool enabled)
    {
        CheatState.SetToggle("F-WAVE-FREEZE", enabled, "debug");
        DebugRuntime.Emit("debug.wave.freeze", new Dictionary<string, object> { ["enabled"] = enabled });
    }

    public static void EnsureSun(float value = 9999f)
    {
        CheatActions.SetEconomy("sun", value, add: false);
        CheatState.SetToggle("G-FREE-SET", true, "debug", emitInject: false);
    }

    /// <summary>
    /// Stress-test board setup in one command (perf runbook): freeze waves, raise spawn caps,
    /// fill a plant grid, and mass-spawn zombies with staggered x so they walk and fight.
    /// Payload: { plants, zombies, plantType, zombieType, freeze=true, rows=5 }.
    /// Runs synchronously — call BEFORE starting a probe capture; the setup hitch is not the
    /// measurement. Ack: <c>debug.stress.fill</c> with spawned/failed counts and elapsed ms.
    /// </summary>
    // C1 (2026-08-21 review): stress runs must not leave caps/toggles corrupted for normal play.
    static FusionRpg.Core.Match.CapPolicyConfig? _stressPrevCaps;
    static bool _stressPrevWaveFreeze;
    static bool _stressPrevFreeSet;
    static bool _stressActive;

    public static void StressFill(JsonElement p)
    {
        if (GameHooks.Board == null)
        {
            // S4: bail before mutating any state — spawns would all fail anyway.
            DebugRuntime.Emit("debug.stress.fill", new Dictionary<string, object> { ["error"] = "no-board" });
            CheatState.Error("stress-fill: no live board");
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var plants = Int(p, "plants", 40);
        var zombies = Int(p, "zombies", 150);
        var plantType = Int(p, "plantType", 0);
        var zombieType = Int(p, "zombieType", 0);
        var rows = Math.Clamp(Int(p, "rows", 5), 1, 6);
        var freeze = !(p.TryGetProperty("freeze", out var f) && f.ValueKind == JsonValueKind.False);

        if (!_stressActive)
        {
            _stressActive = true;
            _stressPrevCaps = Match.SpawnAdmit.Config; // getter returns a copy
            _stressPrevWaveFreeze = CheatState.On("F-WAVE-FREEZE");
            _stressPrevFreeSet = CheatState.On("G-FREE-SET");
        }

        if (freeze) WaveFreeze(true);
        EnsureSun();
        Match.SpawnAdmit.Config = new FusionRpg.Core.Match.CapPolicyConfig
        {
            MaxLivingPlants = Math.Max(plants + 10, 50),
            MaxLivingZombies = Math.Max(zombies + 20, 80),
            MaxLivingBullets = 4096
        };

        var plantOk = 0;
        var plantFail = 0;
        for (var i = 0; i < plants; i++)
        {
            var col = i % 9;
            var row = (i / 9) % rows;
            using var doc = JsonDocument.Parse(
                $"{{\"typeId\":{plantType},\"col\":{col},\"row\":{row}}}");
            if (SpawnPlant(doc.RootElement)) plantOk++;
            else plantFail++;
        }

        var zombieOk = 0;
        var zombieFail = 0;
        for (var i = 0; i < zombies; i++)
        {
            var row = i % rows;
            // Stagger x 4.0..9.5 so the horde spreads across the lawn instead of stacking.
            var x = (4.0f + (i / rows % 12) * 0.5f).ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var doc = JsonDocument.Parse(
                $"{{\"typeId\":{zombieType},\"row\":{row},\"x\":{x}}}");
            if (SpawnZombie(doc.RootElement)) zombieOk++;
            else zombieFail++;
        }

        sw.Stop();
        DebugRuntime.Emit("debug.stress.fill", new Dictionary<string, object>
        {
            ["plantsRequested"] = plants,
            ["plantsSpawned"] = plantOk,
            ["plantsFailed"] = plantFail,
            ["zombiesRequested"] = zombies,
            ["zombiesSpawned"] = zombieOk,
            ["zombiesFailed"] = zombieFail,
            ["freeze"] = freeze,
            ["elapsedMs"] = sw.ElapsedMilliseconds
        });
        CheatState.Note($"stress fill: {plantOk}/{plants} plants, {zombieOk}/{zombies} zombies in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Restore the session state a stress run mutated: spawn caps, wave freeze, free-set
    /// toggle (C1). Sun is not restorable (EnsureSun overwrote it) — noted in the ack.
    /// Safe to call when no stress run is active (restores defaults).
    /// </summary>
    public static void StressClear(JsonElement p)
    {
        Match.SpawnAdmit.Config = _stressActive && _stressPrevCaps != null
            ? _stressPrevCaps
            : FusionRpg.Core.Match.CapPolicyConfig.Defaults();
        WaveFreeze(_stressActive && _stressPrevWaveFreeze);
        CheatState.SetToggle("G-FREE-SET", _stressActive && _stressPrevFreeSet, "debug", emitInject: false);
        var wasActive = _stressActive;
        _stressActive = false;
        _stressPrevCaps = null;

        DebugRuntime.Emit("debug.stress.clear", new Dictionary<string, object>
        {
            ["restored"] = wasActive,
            ["sunNote"] = "sun value not restored"
        });
        CheatState.Note("stress clear: caps/freeze/free-set restored");
        _ = p;
    }

    /// <summary>
    /// Gated LIVE probe: call <c>UIMgr.EnterGame</c>. Requires <c>FUSIONRPG_LEVEL_ENTRY=1</c>
    /// or toggle <c>DEBUG-LEVEL-ENTRY</c>. Rejects when a Board is already live unless <c>force</c>.
    /// </summary>
    public static void EnterLevel(JsonElement p)
    {
        var envOn = string.Equals(
            Environment.GetEnvironmentVariable("FUSIONRPG_LEVEL_ENTRY"), "1", StringComparison.Ordinal);
        var toggleOn = CheatState.On("DEBUG-LEVEL-ENTRY");
        var force = p.TryGetProperty("force", out var fEl) && fEl.ValueKind == JsonValueKind.True;

        var dump = new Dictionary<string, object>
        {
            ["env"] = envOn,
            ["toggle"] = toggleOn,
            ["force"] = force
        };

        if (!envOn && !toggleOn)
        {
            dump["ok"] = false;
            dump["error"] = "disabled — set FUSIONRPG_LEVEL_ENTRY=1 or enable DEBUG-LEVEL-ENTRY";
            DebugRuntime.Emit("debug.level.enter", dump);
            CheatState.Error("debug.enter-level: disabled");
            return;
        }

        if (GameHooks.Board != null && !force)
        {
            dump["ok"] = false;
            dump["error"] = "board already live — return to main menu, or pass force=true (unsafe)";
            DebugRuntime.Emit("debug.level.enter", dump);
            CheatState.Error("debug.enter-level: board already live");
            return;
        }

        LevelType levelType;
        try
        {
            levelType = ParseLevelType(p);
        }
        catch (Exception ex)
        {
            dump["ok"] = false;
            dump["error"] = ex.Message;
            DebugRuntime.Emit("debug.level.enter", dump);
            CheatState.Error("debug.enter-level: " + ex.Message);
            return;
        }

        var levelNumber = Int(p, "levelNumber", 1);
        var id = Int(p, "id", 0);
        var name = Str(p, "name") ?? "";
        dump["levelType"] = levelType.ToString();
        dump["levelTypeInt"] = (int)levelType;
        dump["levelNumber"] = levelNumber;
        dump["id"] = id;
        dump["name"] = name;

        try
        {
            UIMgr.EnterGame(levelType, levelNumber, id, name);
            dump["ok"] = true;
            DebugRuntime.Emit("debug.level.enter", dump);
            CheatState.Note($"debug.enter-level {levelType}#{levelNumber}");
        }
        catch (Exception ex)
        {
            dump["ok"] = false;
            dump["error"] = ex.Message;
            DebugRuntime.Emit("debug.level.enter", dump);
            CheatState.Error("debug.enter-level: " + ex.Message);
        }
    }

    /// <summary>
    /// Gated LIVE dismissal of the host game's "choose your plants" seed-picker, run as a pending
    /// operation ticked from <see cref="Host.InjectorLoop"/> (<see cref="TickPendingSkipSetup"/>).
    ///
    /// <para><b>2026-09-15 fix (owner-reported, every run):</b> this used to fire
    /// <c>InitBoard.QuickInGame()</c> / <c>OnStartBattleButtonClick()</c> the instant the command
    /// arrived. Right after <c>debug.enter-level</c> the picker is still animating in — the call landed
    /// with <c>InitBoard.ready=false</c>, acked <c>ok:true</c>, and the level stayed blocked behind the
    /// picker with no game time passing. Screenshots proved the working sequence: wait until the
    /// picker's <c>StartGameButton</c> is live, let it settle, press it the same way
    /// <c>ControlClick</c> presses a <c>*Btn</c> (<c>OnMouseDown</c>/<c>OnMouseUp</c>), and only then read
    /// <c>InitBoard.ready</c> flip to true. The ack (<c>debug.setup.skip</c>) is emitted once, at the real
    /// outcome, and names the stage it reached.</para>
    ///
    /// <para><c>waitForBoard=false</c> (quick-start's "is a level already mid-entry?" probe) fails fast
    /// when no picker or InitBoard exists yet instead of waiting. A new request replaces any pending one.</para>
    /// </summary>
    public static void SkipSetup(JsonElement p)
    {
        var envOn = string.Equals(
            Environment.GetEnvironmentVariable("FUSIONRPG_SETUP_SKIP"), "1", StringComparison.Ordinal);
        var toggleOn = CheatState.On("DEBUG-SETUP-SKIP");
        var method = (Str(p, "method") ?? "button").Trim().ToLowerInvariant();
        var waitForBoard = !(p.ValueKind == JsonValueKind.Object && p.TryGetProperty("waitForBoard", out var wf)
                             && wf.ValueKind == JsonValueKind.False);
        // "cards": [plantTypeId, ...] picks those seed packets into the bank before starting; omitted = every
        // packet the picker offers (the seed bank must hold cards, or nothing can be planted); [] = none.
        List<int>? cards = null;
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("cards", out var cardsEl) && cardsEl.ValueKind == JsonValueKind.Array)
        {
            cards = new List<int>();
            foreach (var c in cardsEl.EnumerateArray())
                if (c.TryGetInt32(out var id) && !cards.Contains(id)) cards.Add(id);
        }
        var dump = new Dictionary<string, object>
        {
            ["method"] = method,
            ["env"] = envOn,
            ["toggle"] = toggleOn,
            ["waitForBoard"] = waitForBoard
        };

        if (!envOn && !toggleOn)
        {
            dump["ok"] = false;
            dump["error"] = "disabled — set FUSIONRPG_SETUP_SKIP=1 or enable DEBUG-SETUP-SKIP";
            DebugRuntime.Emit("debug.setup.skip", dump);
            CheatState.Error("debug.skip-setup: disabled");
            return;
        }

        if (method is not ("quick" or "button"))
        {
            dump["ok"] = false;
            dump["error"] = "unknown method — expected quick or button";
            DebugRuntime.Emit("debug.setup.skip", dump);
            CheatState.Error("debug.skip-setup: unknown method " + method);
            return;
        }

        if (!waitForBoard && InitBoard.Instance == null && FindStartGameButton() == null)
        {
            dump["ok"] = false;
            dump["stage"] = "no-picker";
            dump["error"] = "no seed-picker and no InitBoard right now (waitForBoard=false)";
            DebugRuntime.Emit("debug.setup.skip", dump);
            return;
        }

        var now = Environment.TickCount64;
        _pendingSkip = new PendingSkipSetup
        {
            Method = method,
            RequestedCards = cards,
            StartedMs = now,
            DeadlineMs = now + (waitForBoard ? SkipSetupTimeoutMs : SkipSetupProbeTimeoutMs),
            Dump = dump
        };
        CheatState.Note("debug.skip-setup " + method + " pending (waiting for the seed-picker)");
    }

    sealed class PendingSkipSetup
    {
        public string Method = "button";
        public long StartedMs;
        public long DeadlineMs;
        public long PickerSeenMs = -1;
        public long PressedMs = -1;
        public List<int>? RequestedCards;
        public List<int>? CardsToPick;
        public int CardIndex;
        public int CardAttempt;
        public long LastPickMs = -1;
        public Dictionary<string, object> Dump = new();
    }

    static PendingSkipSetup? _pendingSkip;

    // Structural (not tunable): acknowledgement/wait bounds for a debug command, not balance values.
    const long SkipSetupTimeoutMs = 30_000;       // level load + picker slide-in
    const long SkipSetupProbeTimeoutMs = 3_000;   // quick-start's mid-entry probe
    const long PickerSettleMs = 1_500;            // picker animation finishes before the press
    const long ReadyAfterPressTimeoutMs = 10_000; // InitBoard.ready must follow the press
    const long CardPickSettleMs = 400;            // a picked packet animates into the bank
    const int MaxCardPickAttempts = 12;           // ~5s of retries while a cold picker ignores clicks

    /// <summary>Advances a pending <see cref="SkipSetup"/>; called every <c>InjectorLoop.Tick</c>.</summary>
    public static void TickPendingSkipSetup()
    {
        var pending = _pendingSkip;
        if (pending == null) return;
        var now = Environment.TickCount64;
        try
        {
            var init = InitBoard.Instance;
            var ready = init != null && SafeReady(init);

            if (pending.PressedMs < 0)
            {
                if (ready)
                {
                    FinishSkip(pending, true, "already-started", null, now);
                    return;
                }
                var button = FindStartGameButton();
                if (button != null)
                {
                    if (pending.PickerSeenMs < 0) pending.PickerSeenMs = now;
                    if (now - pending.PickerSeenMs >= PickerSettleMs && !PickCardsStep(pending, now))
                        return;
                    if (now - pending.PickerSeenMs >= PickerSettleMs)
                    {
                        if (pending.Method == "quick")
                        {
                            if (init == null) throw new InvalidOperationException("InitBoard.Instance is null with the picker visible");
                            init.QuickInGame();
                        }
                        else if (!PressBtn(button))
                        {
                            FinishSkip(pending, false, "press-failed", "StartGameButton has no *Btn component to press", now);
                            return;
                        }
                        pending.PressedMs = now;
                        pending.Dump["pickerSettledMs"] = now - pending.PickerSeenMs;
                        return;
                    }
                }
                if (now > pending.DeadlineMs)
                    FinishSkip(pending, false, button == null ? "picker-never-appeared" : "picker-not-settled",
                        "seed-picker StartGameButton " + (button == null ? "never appeared" : "never settled")
                        + " within " + ((pending.DeadlineMs - pending.StartedMs) / 1000) + "s", now);
                return;
            }

            if (ready)
            {
                FinishSkip(pending, true, "started", null, now);
                return;
            }
            if (now - pending.PressedMs > ReadyAfterPressTimeoutMs)
                FinishSkip(pending, false, "pressed-not-started",
                    "start pressed but InitBoard.ready stayed false for " + (ReadyAfterPressTimeoutMs / 1000) + "s", now);
        }
        catch (Exception ex)
        {
            FinishSkip(pending, false, "exception", ex.Message, now);
        }
    }

    static void FinishSkip(PendingSkipSetup pending, bool ok, string stage, string? error, long now)
    {
        if (!ReferenceEquals(_pendingSkip, pending)) return;
        _pendingSkip = null;
        var dump = pending.Dump;
        dump["ok"] = ok;
        dump["stage"] = stage;
        dump["waitedMs"] = now - pending.StartedMs;
        var init = InitBoard.Instance;
        dump["ready"] = init != null && SafeReady(init);
        dump["board"] = init != null && init.board != null;
        dump["ui"] = init != null && init.uI != null;
        if (error != null) dump["error"] = error;
        DebugRuntime.Emit("debug.setup.skip", dump);
        if (ok) CheatState.Note("debug.skip-setup " + pending.Method + " " + stage + " after " + dump["waitedMs"] + "ms");
        else CheatState.Error("debug.skip-setup: " + stage + " — " + error);
    }

    /// <summary>One card-selection step per tick. Returns true when every requested packet is in the seed
    /// bank (or none were requested), false while still working. A packet is picked with
    /// <c>CardUI.OnMouseDown</c> — the game's own click (live-verified 2026-09-15: it moves the packet into
    /// <c>SeedBank/SeedGroup/seedN</c>; <c>Mouse.ClickOnCard</c> instead starts a plant drag).</summary>
    static bool PickCardsStep(PendingSkipSetup pending, long now)
    {
        if (pending.CardsToPick == null)
        {
            pending.CardsToPick = pending.RequestedCards != null
                ? new List<int>(pending.RequestedCards)
                : LibraryCards().Select(c => SafeType(c)).Where(t => t >= 0).Distinct().ToList();
            pending.Dump["cardsRequested"] = pending.CardsToPick.ToArray();
        }
        if (pending.LastPickMs >= 0 && now - pending.LastPickMs < CardPickSettleMs) return false;

        while (pending.CardIndex < pending.CardsToPick.Count)
        {
            var type = pending.CardsToPick[pending.CardIndex];
            if (BankHas(type))
            {
                pending.CardIndex++;
                pending.CardAttempt = 0;
                continue;
            }
            var candidates = LibraryCards().Where(c => SafeType(c) == type)
                // The clickable packet is the "(Clone)" one (live-verified 2026-09-15: it moves itself into
                // SeedBank/SeedGroup/seedN); the bare "Packet " is the library slot's placeholder.
                .OrderBy(c => NameOf(c).Contains("(Clone)") ? 0 : 1).ToList();
            if (candidates.Count == 0 || pending.CardAttempt >= MaxCardPickAttempts)
            {
                FinishSkip(pending, false, "card-not-selectable",
                    $"plant type {type} is not offered by the seed-picker, or no packet for it reached the seed bank", now);
                return false;
            }
            // Cycle the candidates: on the first picker after a cold boot the packets ignore clicks for a moment.
            candidates[pending.CardAttempt % candidates.Count].OnMouseDown();
            pending.CardAttempt++;
            pending.LastPickMs = now;
            return false;
        }
        pending.Dump["cardsInBank"] = BankCards().Select(c => SafeType(c)).Where(t => t >= 0).ToArray();
        return true;
    }

    static IEnumerable<CardUI> LibraryCards() => CardsUnder("/SeedLibrary/");
    static IEnumerable<CardUI> BankCards() => CardsUnder("/SeedBank/");
    static bool BankHas(int type) => BankCards().Any(c => SafeType(c) == type);

    static IEnumerable<CardUI> CardsUnder(string pathPart)
    {
        CardUI[] all;
        try { all = UnityEngine.Object.FindObjectsOfType<CardUI>(); } catch { yield break; }
        foreach (var c in all)
        {
            if (c == null) continue;
            string path;
            try { path = PathOf(c.transform); } catch { continue; }
            if (path.Contains(pathPart, StringComparison.Ordinal)) yield return c;
        }
    }

    static string PathOf(Transform t)
    {
        var parts = new List<string>();
        for (var cur = t; cur != null; cur = cur.parent) parts.Add(cur.name);
        parts.Reverse();
        return "/" + string.Join("/", parts) + "/";
    }

    static int SafeType(CardUI c)
    {
        try { return (int)c.thePlantType; } catch { return -1; }
    }

    static string NameOf(CardUI c)
    {
        try { return c.gameObject.name ?? ""; } catch { return ""; }
    }

    static bool SafeReady(InitBoard init)
    {
        try { return init.ready; } catch { return false; }
    }

    /// <summary>The live, active picker start button — the object `debug_inspect` lists as
    /// <c>CanvasUp/InGameUI(Clone)/Bottom/SeedLibrary/StartGameButton</c>. <c>GameObject.Find</c> only
    /// returns active objects, which is exactly the "picker is showing" condition.</summary>
    static GameObject? FindStartGameButton()
    {
        try
        {
            var go = GameObject.Find("CanvasUp/InGameUI(Clone)/Bottom/SeedLibrary/StartGameButton");
            return go != null && go.activeInHierarchy ? go : null;
        }
        catch { return null; }
    }

    /// <summary>Presses a custom <c>*Btn</c> component the way <c>ControlClick.Invoke</c> does.</summary>
    static bool PressBtn(GameObject go)
    {
        foreach (var c in go.GetComponents<Component>())
        {
            if (c == null) continue;
            string real;
            try { real = c.GetIl2CppType().Name; } catch { real = c.GetType().Name; }
            if (real.IndexOf("Btn", StringComparison.OrdinalIgnoreCase) < 0) continue;
            c.SendMessage("OnMouseDown");
            c.SendMessage("OnMouseUp");
            return true;
        }
        return false;
    }

    /// <summary>Answers "where is current game state, right now" by reading the game's own live
    /// objects directly -- never reconstructed from event history. Real problem this replaces
    /// (2026-09-14): every prior attempt at this question read the event log for the most recent
    /// board.economy/match.result/etc., which is fragile by construction (a long enough session, a
    /// clamped query window, or a signal that simply never fires for a given transition all produce a
    /// wrong answer with no way to tell it apart from a right one). <c>Board.Instance</c> and
    /// <c>InitBoard.Instance</c> are the same live accessors <c>CheatActions.cs</c>/
    /// <c>DebugActions.cs</c> already read everywhere else in this file -- this command exists only to
    /// surface them as one direct, synchronous answer instead of requiring a caller to infer the same
    /// thing from a trickle of past events.
    ///
    /// <c>hasInitBoard</c> is a best-effort signal, not a certainty. Live-verified 2026-09-14:
    /// <c>InitBoard.Instance</c> stays non-null once ANY level has been entered this session --
    /// a real probe mid-match returned <c>hasBoard:true, hasInitBoard:true</c> together -- so it
    /// does NOT distinguish "on the seed-picker" from "in a match" after the first entry. Whether
    /// it clears after backing out to the main menu is still unverified; treat that specific
    /// question as open until checked against a real return-to-menu.</summary>
    public static void GameState()
    {
        var dump = new Dictionary<string, object>();
        try
        {
            var board = GameHooks.Board ?? Board.Instance;
            var init = InitBoard.Instance;
            dump["hasBoard"] = board != null;
            dump["hasInitBoard"] = init != null;
            try { dump["theBoardType"] = (int)GameAPP.theBoardType; dump["theBoardTypeName"] = GameAPP.theBoardType.ToString(); } catch { }
            try { dump["theBoardLevel"] = GameAPP.theBoardLevel; } catch { }
            if (board != null)
            {
                try { dump["sceneType"] = (int)board.sceneType; } catch { }
            }
            // 2026-09-15 (owner: "use screenshot … better than trust unreliable gamestate endpoint"): the
            // two facts this answer was missing when a live board looked InMatch while no game time
            // passed — whether the seed-picker/setup has actually finished, and whether time runs.
            bool? initBoardReady = null;
            if (init != null)
            {
                try { initBoardReady = init.ready; dump["initBoardReady"] = init.ready; } catch { }
            }
            try { dump["timeScale"] = UnityEngine.Time.timeScale; } catch { }
            try { dump["unscaledTime"] = UnityEngine.Time.unscaledTime; dump["gameTime"] = UnityEngine.Time.time; } catch { }

            var matchSnap = Match.MatchHost.Runtime.ToSnapshot();
            var phase = matchSnap.Phase.ToString();
            dump["matchPhase"] = phase;
            dump["matchPhaseNote"] = "MatchHost.Runtime's own tracked FSM -- can desync from the real " +
                "game after a server restart (real incident 2026-09-14: reported InMatch with living " +
                "entities while the operator's screen showed a genuine defeat). Cross-checked below " +
                "against real Unity object counts -- trust those over this phase when they disagree.";

            // Real counts from the live Unity objects (same FindObjectsOfType approach
            // DebugRuntime.BoardEntityStats already uses for debug.board-stats), NOT
            // MatchHost.Runtime's tracked counters -- those can desync from the real board (see note
            // above and matchSnap.PlantCount/ZombieCount, deliberately not used here anymore).
            var realPlantCount = 0;
            var realZombieCount = 0;
            try { realPlantCount = UnityEngine.Object.FindObjectsOfType<Plant>().Count(p => p != null && p.thePlantType != PlantType.Nothing); } catch { }
            try { realZombieCount = UnityEngine.Object.FindObjectsOfType<Zombie>().Count(z => z != null && z.theZombieType != ZombieType.Nothing); } catch { }
            dump["plantCount"] = realPlantCount;
            dump["zombieCount"] = realZombieCount;

            // A phase claiming an active match with zero real living entities is itself evidence of
            // the desync named above -- surfaced explicitly rather than silently trusted or silently
            // overridden (zero entities does not ALWAYS mean the match ended -- e.g. between waves --
            // so this is a flag to investigate, not an automatic re-classification).
            var phaseClaimsActive = phase is "InMatch" or "Paused" or "Starting";
            dump["phaseMismatch"] = phaseClaimsActive && realPlantCount == 0 && realZombieCount == 0;

            // Real bug found live 2026-09-14: a defeated board (Board.Instance still alive, the
            // game's own overlay not yet torn down) reported "InMatch" because this only checked
            // hasBoard, never matchPhase -- MatchRuntime.Apply had already driven the phase to Idle
            // (via board.end/match.result) while the Board object itself still existed. board!=null
            // is necessary but not sufficient for "a match is actually running"; the phase FSM is the
            // authority on that -- except when phaseMismatch is true above, in which case neither
            // signal alone is trustworthy and a caller should treat liveState as unconfirmed.
            // lawn-combat-wire L-N26: the Board already exists on the seed picker and MatchHost has applied board.start, so
            // the phase reads InMatch there; InitBoard.ready stays false until the battle starts (the setup-skip ack waits
            // on the same flag).
            dump["liveState"] = board == null
                ? (init != null ? "SeedPickerOrPersistentInitBoard" : "AtMainMenuOrNoBoard")
                : initBoardReady == false
                ? "SeedPicker"
                : phase switch
                {
                    "Paused" => "Paused",
                    "InMatch" => "InMatch",
                    "Starting" => "Starting",
                    "Idle" or "Ending" => "MatchEndedBoardStillAlive",
                    _ => phase
                };
            dump["ok"] = true;
            DebugRuntime.Emit("debug.game-state", dump);
        }
        catch (Exception ex)
        {
            dump["ok"] = false;
            dump["error"] = ex.Message;
            DebugRuntime.Emit("debug.game-state", dump);
            CheatState.Error("debug.game-state: " + ex.Message);
        }
    }

    /// <summary>Calls one real <c>UIMgr</c> static navigation entry point, chosen by name. Real
    /// problem this replaces (2026-09-14): forcing <c>debug.enter-level(force:true)</c> straight over
    /// a defeated-but-still-alive <c>Board</c> never acked, live, twice (automated and manual) --
    /// consistent with the already-known hazard that repeated/rapid forced entry can destabilize the
    /// engine (see force-debug-enter-level memory). The fix attempt was <c>UIMgr.BackToMenu()</c>
    /// (already Harmony-hooked, emits <c>menu.enter</c>) -- proven live to leave the dead board, but it
    /// landed on the game's PREVIOUS menu layer (Challenge Mode select), not the true main menu: this
    /// game's menu stack is not flat, so one guessed method was not enough on its own. Exposing every
    /// real <c>UIMgr</c> static navigation method by name here lets a caller reach any specific menu
    /// directly. Never fabricates -- every action below is the literal real static method call.
    ///
    /// <para><b>2026-09-15 fix:</b> "back-to-menu" itself now chains <c>EnterMainMenu()</c> after
    /// <c>BackToMenu()</c> (see that case) instead of leaving the two-call recovery dance to every
    /// caller -- an action named "back to menu" that silently lands one layer short of the menu is a
    /// defect in the action, not a caller-side workaround to keep re-deriving.</para></summary>
    public static void UiNav(JsonElement p)
    {
        var action = Str(p, "action") ?? "";
        var dump = new Dictionary<string, object> { ["action"] = action };
        try
        {
            switch (action)
            {
                case "back-to-menu":
                    UIMgr.BackToMenu();
                    // 2026-09-15 fix: BackToMenu() alone only pops one menu layer -- proven live,
                    // repeatedly, that it lands on the game's PREVIOUS layer (Challenge Mode
                    // select), never the true main menu (see this method's own class doc). Every
                    // caller this session that wanted the true main menu had to replay the same
                    // two-call dance by hand (back-to-menu, then a SEPARATE enter-main-menu) --
                    // that is not a caller decision, it is this action under-delivering on its own
                    // name. EnterMainMenu() is the same static entry point "enter-main-menu" already
                    // calls standalone, proven safe to call unconditionally from any menu depth
                    // (used successfully as its own action many times this session); chaining it
                    // here makes "back-to-menu" actually mean what it says.
                    UIMgr.EnterMainMenu();
                    break;
                case "enter-main-menu": UIMgr.EnterMainMenu(); break;
                case "back-to-game": UIMgr.BackToGame(); break;
                case "enter-pause-menu": UIMgr.EnterPauseMenu(); break;
                case "enter-lose-menu": UIMgr.EnterLoseMenu(Str(p, "reason") ?? ""); break;
                case "enter-challenge-menu": UIMgr.EnterChallengeMenu(); break;
                case "enter-classic-travel": UIMgr.EnterClassicTravel(); break;
                case "enter-travel-adv": UIMgr.EnterTravelAdv(); break;
                case "enter-travel-game": UIMgr.EnterTravelGame(); break;
                case "enter-travel-challenge": UIMgr.EnterTravelChallenge(); break;
                case "enter-treasure-menu": UIMgr.EnterTreasureMenu(); break;
                case "enter-tower-menu": UIMgr.EnterTowerMenu(); break;
                case "enter-iz-menu": UIMgr.EnterIZMenu(); break;
                case "enter-survival-e-menu": UIMgr.EnterSurvivalEMenu(); break;
                case "menu-normal-settings": UIMgr.MenuNormalSettings(); break;
                case "enter-help-menu": UIMgr.EnterHelpMenu(); break;
                case "enter-other-menu": UIMgr.EnterOtherMenu(); break;
                case "enter-option-menu": UIMgr.EnterOptionMenu(); break;
                case "enter-explore-menu": UIMgr.EnterExploreMenu(); break;
                case "enter-almanac": UIMgr.EnterAlmanac(); break;
                case "enter-garden": UIMgr.EnterGarden(); break;
                case "enter-zuma": UIMgr.EnterZuma(); break;
                default:
                    dump["ok"] = false;
                    dump["error"] = $"unknown action '{action}'";
                    DebugRuntime.Emit("debug.ui-nav", dump);
                    CheatState.Error("debug.ui-nav: unknown action " + action);
                    return;
            }
            dump["ok"] = true;
            DebugRuntime.Emit("debug.ui-nav", dump);
            CheatState.Note("debug.ui-nav: " + action);
        }
        catch (Exception ex)
        {
            dump["ok"] = false;
            dump["error"] = ex.Message;
            DebugRuntime.Emit("debug.ui-nav", dump);
            CheatState.Error("debug.ui-nav: " + ex.Message);
        }
    }

    static LevelType ParseLevelType(JsonElement p)
    {
        if (p.TryGetProperty("levelType", out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
                return (LevelType)n;
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString() ?? "";
                if (int.TryParse(s, out var asInt))
                    return (LevelType)asInt;
                if (Enum.TryParse(s, ignoreCase: true, out LevelType parsed))
                    return parsed;
                // Game spelling Advanture — accept Adventure typo.
                if (string.Equals(s, "Adventure", StringComparison.OrdinalIgnoreCase))
                    return LevelType.Advanture;
                throw new ArgumentException("unknown levelType string: " + s);
            }
        }

        return LevelType.Advanture;
    }

    public static void Select(JsonElement p)
    {
        var side = Str(p, "side") ?? "zombie";
        if (p.TryGetProperty("ptr", out var ptrEl) && ptrEl.ValueKind == JsonValueKind.String)
        {
            var hex = CombatPtr.Normalize(ptrEl.GetString());
            if (long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
                CheatState.Select(new IntPtr(v), side);
            return;
        }

        // Select living plant by col/row (LIVE prove: spawn A+B then grant entity on A).
        if (string.Equals(side, "plant", StringComparison.OrdinalIgnoreCase) &&
            p.TryGetProperty("col", out var colEl) && colEl.TryGetInt32(out var col) &&
            p.TryGetProperty("row", out var rowEl) && rowEl.TryGetInt32(out var row))
        {
            foreach (var plant in UObject.FindObjectsOfType<Plant>())
            {
                try
                {
                    if (plant == null || plant.thePlantType == PlantType.Nothing) continue;
                    if (plant.thePlantColumn != col || plant.thePlantRow != row) continue;
                    CheatState.Select(plant.Pointer, "plant");
                    return;
                }
                catch { }
            }

            CheatState.Error($"debug.select: no plant at col={col} row={row}");
            return;
        }

        if (string.Equals(side, "plant", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var plant in UObject.FindObjectsOfType<Plant>())
            {
                try
                {
                    if (plant == null || plant.thePlantType == PlantType.Nothing) continue;
                    CheatState.Select(plant.Pointer, "plant");
                    return;
                }
                catch { }
            }

            CheatState.Error("debug.select: no living plant");
            return;
        }

        if (p.TryGetProperty("row", out var zRowEl) && zRowEl.TryGetInt32(out var zRow))
        {
            var wantX = p.TryGetProperty("x", out var zxEl) && zxEl.TryGetSingle(out var zxv) ? zxv : (float?)null;
            Zombie? best = null;
            var bestDx = float.MaxValue;
            foreach (var z in UObject.FindObjectsOfType<Zombie>())
            {
                try
                {
                    if (z == null || z.theZombieType == ZombieType.Nothing) continue;
                    if (z.theZombieRow != zRow) continue;
                    if (wantX == null)
                    {
                        CheatState.Select(z.Pointer, "zombie");
                        return;
                    }

                    var dx = Math.Abs(z.transform.position.x - wantX.Value);
                    if (dx >= bestDx) continue;
                    bestDx = dx;
                    best = z;
                }
                catch { }
            }

            if (best != null)
            {
                CheatState.Select(best.Pointer, "zombie");
                return;
            }

            CheatState.Error($"debug.select: no zombie at row={zRow}");
            return;
        }

        foreach (var z in UObject.FindObjectsOfType<Zombie>())
        {
            try
            {
                if (z == null || z.theZombieType == ZombieType.Nothing) continue;
                CheatState.Select(z.Pointer, "zombie");
                return;
            }
            catch { }
        }

        CheatState.Error("debug.select: no living zombie");
    }

    /// <summary>Living plant at col/row → hex ptr for synthetic OnDamageDealt actor.</summary>
    public static string? TryResolvePlantPtr(int col, int row, out int typeId)
    {
        typeId = 0;
        foreach (var plant in UObject.FindObjectsOfType<Plant>())
        {
            try
            {
                if (plant == null || plant.thePlantType == PlantType.Nothing) continue;
                if (plant.thePlantColumn != col || plant.thePlantRow != row) continue;
                typeId = (int)plant.thePlantType;
                return plant.Pointer.ToString("X");
            }
            catch { }
        }

        return null;
    }

    /// <summary>Living zombie on row (optional x pick) → hex ptr for synthetic target.</summary>
    public static string? TryResolveZombiePtr(int row, float? x, out int typeId)
    {
        typeId = 0;
        Zombie? best = null;
        var bestDx = float.MaxValue;
        foreach (var z in UObject.FindObjectsOfType<Zombie>())
        {
            try
            {
                if (z == null || z.theZombieType == ZombieType.Nothing) continue;
                if (!LawnCoords.ZombieInRow(z, row)) continue;
                if (x == null)
                {
                    typeId = (int)z.theZombieType;
                    return z.Pointer.ToString("X");
                }

                var dx = Math.Abs(z.transform.position.x - x.Value);
                if (dx >= bestDx) continue;
                bestDx = dx;
                best = z;
            }
            catch { }
        }

        if (best == null) return null;
        typeId = (int)best.theZombieType;
        return best.Pointer.ToString("X");
    }

    static IEnumerable<Zombie> ResolveZombies(string target, JsonElement p)
    {
        if (target is "all" or "all-zombies")
        {
            foreach (var z in UObject.FindObjectsOfType<Zombie>())
                if (z != null) yield return z;
            yield break;
        }
        if (p.TryGetProperty("ptr", out var ptrEl) && ptrEl.ValueKind == JsonValueKind.String)
        {
            var hex = ptrEl.GetString() ?? "";
            foreach (var z in UObject.FindObjectsOfType<Zombie>())
            {
                if (z != null && string.Equals(GameDumps.Ptr(z), hex, StringComparison.OrdinalIgnoreCase))
                {
                    yield return z;
                    yield break;
                }
            }
        }
        if (CheatState.SelectedSide == "zombie" && CheatState.SelectedPtr != IntPtr.Zero)
        {
            foreach (var z in UObject.FindObjectsOfType<Zombie>())
            {
                if (z != null && z.Pointer == CheatState.SelectedPtr)
                {
                    yield return z;
                    yield break;
                }
            }
        }
        foreach (var z in UObject.FindObjectsOfType<Zombie>())
        {
            if (z != null) { yield return z; yield break; }
        }
    }

    static IEnumerable<Plant> ResolvePlants(string target, JsonElement p)
    {
        if (target is "all" or "all-plants")
        {
            foreach (var pl in UObject.FindObjectsOfType<Plant>())
                if (pl != null) yield return pl;
            yield break;
        }
        if (CheatState.SelectedSide == "plant" && CheatState.SelectedPtr != IntPtr.Zero)
        {
            foreach (var pl in UObject.FindObjectsOfType<Plant>())
            {
                if (pl != null && pl.Pointer == CheatState.SelectedPtr)
                {
                    yield return pl;
                    yield break;
                }
            }
        }
    }

    static void ApplyScaleProps(JsonElement p, bool plant)
    {
        var prefix = plant ? "A-P-" : "A-Z-";
        if (HasNum(p, "hpPercent", out var hpP)) CheatState.SetFloat(prefix + "HP%", hpP, "debug");
        if (HasNum(p, "hpFlat", out var hpF)) CheatState.SetFloat(prefix + "HP+", hpF, "debug");
        if (HasNum(p, "attackPercent", out var atkP)) CheatState.SetFloat(prefix + "ATK%", atkP, "debug");
        if (HasNum(p, "attackFlat", out var atkF)) CheatState.SetFloat(prefix + "ATK+", atkF, "debug");
        if (HasNum(p, "defensePercent", out var defP)) CheatState.SetFloat(prefix + "DEF%", defP, "debug");
        if (HasNum(p, "defenseFlat", out var defF)) CheatState.SetFloat(prefix + "DEF+", defF, "debug");
    }

    static void ApplyAbsoluteProps(JsonElement p, bool plant)
    {
        if (plant)
        {
            if (HasInt(p, "hp", out var hp)) CheatState.SetFloat("P-HP", hp, "debug");
            if (HasInt(p, "maxHp", out var maxHp)) CheatState.SetFloat("P-MAXHP", maxHp, "debug");
            if (HasInt(p, "atk", out var atk)) CheatState.SetFloat("P-ATK", atk, "debug");
        }
        else
        {
            if (HasInt(p, "hp", out var hp)) CheatState.SetFloat("Z-HP", hp, "debug");
            if (HasInt(p, "maxHp", out var maxHp)) CheatState.SetFloat("Z-MAXHP", maxHp, "debug");
            if (HasInt(p, "armor1", out var a1)) CheatState.SetFloat("Z-ARM1", a1, "debug");
            if (HasInt(p, "armor2", out var a2)) CheatState.SetFloat("Z-ARM2", a2, "debug");
            // E28 fix #5: Z-ATK already exists as an absolute cheat id (CheatState.cs:234,605) — this
            // branch just never read atk from the payload, mirroring the plant branch's P-ATK above.
            if (HasInt(p, "atk", out var atk)) CheatState.SetFloat("Z-ATK", atk, "debug");
        }
    }

    static void SetFloatIf(JsonElement root, string obj, string field, string cheatId)
    {
        if (root.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object
            && o.TryGetProperty(field, out var f) && f.TryGetDouble(out var v))
            CheatState.SetFloat(cheatId, v, "debug");
    }

    static void SetFlatIf(JsonElement root, string obj, string field, string cheatId) =>
        SetFloatIf(root, obj, field, cheatId);

    static bool HasNum(JsonElement p, string name, out double v)
    {
        v = 0;
        return p.TryGetProperty(name, out var e) && e.TryGetDouble(out v);
    }

    static bool HasInt(JsonElement p, string name, out int v)
    {
        v = 0;
        return p.TryGetProperty(name, out var e) && e.TryGetInt32(out v);
    }

    static int Int(JsonElement p, string name, int dflt) =>
        p.TryGetProperty(name, out var e) && e.TryGetInt32(out var v) ? v : dflt;

    static string? Str(JsonElement p, string name) =>
        p.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    static void MaybePinDerived(JsonElement p, string ptr)
    {
        var profile = Str(p, "derivedProfile");
        Dictionary<string, double>? overlay = null;
        if (p.TryGetProperty("derived", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            overlay = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var prop in d.EnumerateObject())
            {
                if (prop.Value.TryGetDouble(out var v))
                    overlay[prop.Name] = v;
                else if (prop.Value.TryGetInt64(out var l))
                    overlay[prop.Name] = l;
            }
        }

        if (string.IsNullOrWhiteSpace(profile) && (overlay == null || overlay.Count == 0))
            return;
        InjectorDerivedOverride.PinProfile(ptr, profile, overlay);
    }

    static void MaybePinElement(JsonElement p, string ptr)
    {
        var primary = Str(p, "elementPrimary") ?? Str(p, "element.primary");
        var secondary = Str(p, "elementSecondary") ?? Str(p, "element.secondary");
        if (string.IsNullOrWhiteSpace(primary) && string.IsNullOrWhiteSpace(secondary))
            return;
        try
        {
            InjectorElementOverride.PinParse(ptr, primary, secondary);
        }
        catch (Exception ex)
        {
            CheatState.Error("debug spawn element pin: " + ex.Message);
        }
    }

    /// <summary>
    /// lawn-combat-wire (2026-09-15 finding): `ApplyAbsoluteProps`'s own "hp"/"maxHp" write the
    /// GLOBAL `P-HP`/`P-MAXHP` Tab-B channels, which the very next `includeAbsolute: false` reapply
    /// (e.g. `cheat.pushScales`) silently drops for every plant/zombie on the board — a debug-spawned
    /// entity's own spawn-time HP override does not survive normal cheat-state churn. Pin it
    /// per-ptr too, via <see cref="InjectorSpawnHpPin"/>, so <see cref="EntityApply"/> feeds it to the
    /// Hub as an absolute input on every future resolve for this one ptr regardless of that flag
    /// (L-N20 — Hub max-HP bonuses still compose on top). Additive: the global
    /// channel write in `ApplyAbsoluteProps` is unchanged, so Tab-B's own board-wide behaviour is
    /// untouched — this only makes the SAME spawn-time value durable for the one ptr it was meant for.
    /// </summary>
    static void MaybePinSpawnHp(JsonElement p, string ptr)
    {
        // Prefer "maxHp" (the durable target this pin exists to protect); "hp" alone covers the
        // common full-health spawn shape where the caller only gave one number for both.
        int target;
        if (HasInt(p, "maxHp", out var maxHp)) target = maxHp;
        else if (HasInt(p, "hp", out var hp)) target = hp;
        else return;

        try
        {
            InjectorSpawnHpPin.Pin(ptr, target);
        }
        catch (Exception ex)
        {
            CheatState.Error("debug spawn hp pin: " + ex.Message);
        }
    }
}
