using UnityEngine;
using FusionRpg.Contracts;
using FusionRpg.Core.Stats;
using FusionRpg.Injector.Bridges;
using FusionRpg.Injector.Lawn;
using FusionRpg.Bridge.Stats;

using FusionRpg.Injector.Host;

namespace FusionRpg.Injector;

public static class CheatActions
{
    public static void TickContinuous()
    {
        try
        {
            // Overlay pause wins: the player is looking at the web UI and must not lose the run.
            // This is the only writer of Time.timeScale, so Hud.OverlayPause decides and this
            // applies -- a second writer would be overwritten here on the very next frame.
            if (Hud.OverlayPause.Active)
                Time.timeScale = Core.Overlay.OverlayPausePolicy.PausedTimeScale;
            else if (Hud.OverlayPause.ConsumeRestore(out var resume))
                Time.timeScale = resume;
            else if (CheatState.On("G-TIMEFREEZE"))
                Time.timeScale = 0f;
            else if (CheatState.IsUserSet("G-TIMESCALE"))
                Time.timeScale = Mathf.Clamp(CheatState.FVal("G-TIMESCALE"), 0f, 10f);
            // Unset timescale: leave Unity's current scale alone.
            CheatState.TimeScale = Time.timeScale;

            var board = GameHooks.Board;
            if (board != null && CheatState.On("F-WAVE-FREEZE"))
            {
                // E36 (spec-wave-control.md §2.2): the floor value itself is gk-core/data/tuning/match.v1.json's
                // waveHoldFloorSeconds, not a bare literal (tunables-ssot.md T1 -- a balance pass would
                // tune this). This still FLOORS the timer every tick, it does not stop it -- the
                // "hold, not freeze" naming distinction wave.control's own op vocabulary enforces.
                try
                {
                    board.timeUntilNextWave = Mathf.Max(
                        board.timeUntilNextWave, (float)FusionRpg.Core.Match.MatchTuningPolicy.WaveHoldFloorSeconds);
                }
                catch { }
            }
            if (board != null)
            {
                try
                {
                    if (CheatState.On("H-NOCD-CARD") || CheatState.On("H-NOCD-GLOVE")
                        || CheatState.On("H-NOCD-HAMMER") || CheatState.On("H-NOCD-WHEEL")
                        || CheatState.On("H-ANYWHERE"))
                        board.freeCD = true;
                }
                catch { }
            }
        }
        catch (Exception ex) { CheatState.Error("tick: " + ex.Message); }
    }

    public static void ReapplyAllLiving()
    {
        ReapplyLivingForOwner(EffectOwnerKeys.Match);
    }

    /// <summary>
    /// Re-run EntityApply for living units matching grant/mod <paramref name="ownerKey"/>
    /// (<c>match</c>, <c>plant:N</c>, <c>zombie:N</c>, <c>entity:HEX</c>).
    /// </summary>
    public static void ReapplyLivingForOwner(string? ownerKey)
    {
        var key = StatApplyScope.Normalize(ownerKey);
        var n = 0;

        if (!StatApplyScope.IsKnownOwnerKey(key))
        {
            CheatState.Note($"reapply living owner={key} skipped unknown");
            return;
        }

        if (key.StartsWith("entity:", StringComparison.Ordinal))
        {
            var ptrHex = key[7..];
            foreach (var p in UnityEngine.Object.FindObjectsOfType<Plant>())
            {
                try
                {
                    if (p == null || p.thePlantType == PlantType.Nothing) continue;
                    if (!string.Equals(p.Pointer.ToString("X"), ptrHex, StringComparison.OrdinalIgnoreCase))
                        continue;
                    GameHooks.Applied.Remove(p.Pointer);
                    EntityApply.RunPlant(p, "cheat.reapply", includeAbsolute: true);
                    n++;
                    break;
                }
                catch { }
            }

            if (n == 0)
            {
                foreach (var z in UnityEngine.Object.FindObjectsOfType<Zombie>())
                {
                    try
                    {
                        if (z == null || z.theZombieType == ZombieType.Nothing) continue;
                        if (!string.Equals(z.Pointer.ToString("X"), ptrHex, StringComparison.OrdinalIgnoreCase))
                            continue;
                        GameHooks.Applied.Remove(z.Pointer);
                        EntityApply.RunZombie(z, "cheat.reapply", includeAbsolute: true);
                        n++;
                        break;
                    }
                    catch { }
                }
            }

            CheatState.Note($"reapply living owner={key} n={n}");
            return;
        }

        int? plantType = null;
        int? zombieType = null;
        if (key.StartsWith("plant:", StringComparison.Ordinal) &&
            int.TryParse(key.AsSpan(6), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var pt))
            plantType = pt;
        else if (key.StartsWith("zombie:", StringComparison.Ordinal) &&
                 int.TryParse(key.AsSpan(7), System.Globalization.NumberStyles.Integer,
                     System.Globalization.CultureInfo.InvariantCulture, out var zt))
            zombieType = zt;
        // else match / player: → all living

        var plantsOnly = plantType.HasValue;
        var zombiesOnly = zombieType.HasValue;
        var all = !plantsOnly && !zombiesOnly;

        if (all || plantsOnly)
        {
            foreach (var p in UnityEngine.Object.FindObjectsOfType<Plant>())
            {
                try
                {
                    if (p == null || p.thePlantType == PlantType.Nothing) continue;
                    if (plantType.HasValue && (int)p.thePlantType != plantType.Value) continue;
                    GameHooks.Applied.Remove(p.Pointer);
                    EntityApply.RunPlant(p, "cheat.reapply", includeAbsolute: true);
                    n++;
                }
                catch { }
            }
        }

        if (all || zombiesOnly)
        {
            foreach (var z in UnityEngine.Object.FindObjectsOfType<Zombie>())
            {
                try
                {
                    if (z == null || z.theZombieType == ZombieType.Nothing) continue;
                    if (zombieType.HasValue && (int)z.theZombieType != zombieType.Value) continue;
                    GameHooks.Applied.Remove(z.Pointer);
                    EntityApply.RunZombie(z, "cheat.reapply", includeAbsolute: true);
                    n++;
                }
                catch { }
            }
        }

        CheatState.Note($"reapply living owner={key} n={n}");
    }

    /// <summary>
    /// Dirty / PvzStats path: reapply living entities without Tab-A-only gate
    /// (empty bag restores baseline after clear/reset).
    /// </summary>
    public static void ReapplyLivingFromStats()
    {
        CheatState.SyncLocalStatsFromEntries();
        var n = 0;
        var err = 0;
        foreach (var p in UnityEngine.Object.FindObjectsOfType<Plant>())
        {
            try
            {
                if (p == null || p.thePlantType == PlantType.Nothing) continue;
                GameHooks.Applied.Remove(p.Pointer);
                EntityApply.RunPlant(p, "cheat.pushScales", includeAbsolute: false);
                n++;
            }
            catch (Exception ex)
            {
                err++;
                CheatState.Error("reapply plant: " + ex.Message);
            }
        }
        foreach (var z in UnityEngine.Object.FindObjectsOfType<Zombie>())
        {
            try
            {
                if (z == null || z.theZombieType == ZombieType.Nothing) continue;
                GameHooks.Applied.Remove(z.Pointer);
                EntityApply.RunZombie(z, "cheat.pushScales", includeAbsolute: false);
                n++;
            }
            catch (Exception ex)
            {
                err++;
                CheatState.Error("reapply zombie: " + ex.Message);
            }
        }
        CheatState.MarkAppliedPvzStatsRevision();
        CheatState.Note($"reapplyLivingFromStats n={n} err={err} pvzRev={CheatState.PvzStatsRevision}");
    }

    /// <summary>A-PUSH-NOW: reapply Tab A EffectiveStats only (skip absolute Tab B extras).</summary>
    public static void PushScalesNow()
    {
        CheatState.SyncLocalStatsFromEntries();
        var hasScale = CheatState.HasPlantScaleMods() || CheatState.HasZombieScaleMods();
        var hasPvz = CheatState.HasPvzStatsMods()
                     || PvzStatsApplyGate.ShouldReapplyPvz(
                         CheatState.AppliedPvzStatsRevision, CheatState.PvzStatsRevision);
        if (!hasScale && !hasPvz)
        {
            CheatState.Note("push scales: no Tab A / PvzStats work");
            return;
        }
        if (hasScale && !CheatState.On("A-APPLY"))
        {
            if (!hasPvz)
            {
                CheatState.Error("Tab A blocked: enable A-APPLY (Apply stats)");
                return;
            }
            // PvzStats-only: proceed without A-APPLY.
        }
        ReapplyLivingFromStats();
        var s = CheatState.EffectiveStats();
        CheatState.Note(
            $"A-PUSH-NOW n done pHP%={s.Plants.HpPercent} pHP+={s.Plants.HpFlat} hasPvz={hasPvz}");
    }

    /// <summary>Tab B/C Apply — same EntityApply.Run path. Respects requested side only.</summary>
    public static void ApplyAbsolutesToSelectedOrAll(string side)
    {
        try
        {
            var hasAbs = side == "plant"
                ? CheatState.BuildPlantAbsolute().Count > 0 || CheatState.HasPlantExtrasSet()
                : CheatState.BuildZombieAbsolute().Count > 0 || CheatState.HasZombieExtrasSet();
            var hasScales = side == "plant" ? CheatState.HasPlantScaleMods() : CheatState.HasZombieScaleMods();
            if (!hasAbs && !hasScales)
            {
                CheatState.Note($"apply {side}: nothing set (unset fields are not applied)");
                return;
            }

            if (CheatState.SelectedPtr != IntPtr.Zero
                && string.Equals(CheatState.SelectedSide, side, StringComparison.Ordinal))
            {
                if (side == "plant")
                {
                    foreach (var p in UnityEngine.Object.FindObjectsOfType<Plant>())
                    {
                        if (p == null || p.Pointer != CheatState.SelectedPtr) continue;
                        GameHooks.Applied.Remove(p.Pointer);
                        EntityApply.RunPlant(p, "cheat.absolute", includeAbsolute: true);
                        CheatState.Note("applied plant selected");
                        return;
                    }
                }
                else if (side == "zombie")
                {
                    foreach (var z in UnityEngine.Object.FindObjectsOfType<Zombie>())
                    {
                        if (z == null || z.Pointer != CheatState.SelectedPtr) continue;
                        GameHooks.Applied.Remove(z.Pointer);
                        EntityApply.RunZombie(z, "cheat.absolute", includeAbsolute: true);
                        CheatState.Note("applied zombie selected");
                        return;
                    }
                }
            }

            var n = 0;
            if (side == "plant")
            {
                foreach (var p in UnityEngine.Object.FindObjectsOfType<Plant>())
                {
                    try
                    {
                        if (p == null || p.thePlantType == PlantType.Nothing) continue;
                        GameHooks.Applied.Remove(p.Pointer);
                        EntityApply.RunPlant(p, "cheat.absolute", includeAbsolute: true);
                        n++;
                    }
                    catch (Exception ex) { CheatState.Error("abs plant: " + ex.Message); }
                }
            }
            else
            {
                foreach (var z in UnityEngine.Object.FindObjectsOfType<Zombie>())
                {
                    try
                    {
                        if (z == null || z.theZombieType == ZombieType.Nothing) continue;
                        GameHooks.Applied.Remove(z.Pointer);
                        EntityApply.RunZombie(z, "cheat.absolute", includeAbsolute: true);
                        n++;
                    }
                    catch (Exception ex) { CheatState.Error("abs zombie: " + ex.Message); }
                }
            }
            CheatState.Note($"applied absolutes all {side} n={n}");
        }
        catch (Exception ex) { CheatState.Error("apply abs: " + ex.Message); }
    }

    public static void SpawnPlant(int type)
    {
        try
        {
            if (!Match.SpawnAdmit.TryAdmit("plant", out _))
                return;

            var free = CheatState.On("G-FREE-SET");
            var plant = CreatePlant.Instance.SetPlant(CheatState.SpawnCol, CheatState.SpawnRow, (PlantType)type,
                null, default, free, true, null);
            if (plant == null)
            {
                SpawnCatalog.MarkSpawn("plant", type, false, "null result");
                CheatState.Error("SetPlant returned null");
                return;
            }
            SpawnCatalog.MarkSpawn("plant", type, true);
            Match.SpawnOriginTags.Mark(plant.Pointer, Match.SpawnOriginTags.Cheat);
            CheatState.Select(plant.Pointer, "plant");
            CheatState.Note($"spawn plant {type}");
        }
        catch (Exception ex)
        {
            SpawnCatalog.MarkSpawn("plant", type, false, ex.Message);
            CheatState.Error("spawn plant: " + ex.Message);
        }
    }

    public static void SpawnZombie(int type, bool mindControl)
    {
        try
        {
            if (!Match.SpawnAdmit.TryAdmit("zombie", out _))
                return;

            Zombie? z;
            if (mindControl)
                z = CreateZombieSpawn.SetMindControl(CheatState.SpawnRow, (ZombieType)type, 9.9f, true);
            else
                z = CreateZombieSpawn.Set(CheatState.SpawnRow, (ZombieType)type, 9.9f, false);
            if (z == null)
            {
                SpawnCatalog.MarkSpawn("zombie", type, false, "null result");
                CheatState.Error("SetZombie returned null");
                return;
            }
            SpawnCatalog.MarkSpawn("zombie", type, true);
            Match.SpawnOriginTags.Mark(z.Pointer, Match.SpawnOriginTags.Cheat);
            CheatState.Select(z.Pointer, "zombie");
            CheatState.Note($"spawn zombie {type} mc={mindControl}");
        }
        catch (Exception ex)
        {
            SpawnCatalog.MarkSpawn("zombie", type, false, ex.Message);
            CheatState.Error("spawn zombie: " + ex.Message);
        }
    }

    /// <summary>PvzIntent: spawn independent of Board waves; dump source=extra.</summary>
    public static void SpawnExtraZombie(int typeId, int? row, string? reason, string? correlationId)
    {
        SpawnExtra("zombie", typeId, col: null, row, reason, correlationId);
    }

    /// <summary>PvzIntent: plant or zombie extra spawn; default side zombie for legacy callers.
    /// <paramref name="empireId"/>/<paramref name="controller"/> (aura-skill T21b, widened by
    /// save-identity SE4.28): the specimen's owning empire and its controller, threaded straight
    /// through from `UniqueActorService.DeployAsync`'s own `pvz.spawn.extra` payload — either missing
    /// for spawns with no real owner (manual/debug spawns), matching
    /// `CheatState.RegisterSpecimenOwner`'s own no-op guard for an unparseable/absent pair.</summary>
    public static void SpawnExtra(string? side, int typeId, int? col, int? row, string? reason, string? correlationId,
        string? instanceId = null, string? loadoutJson = null, string? empireId = null, string? controller = null)
    {
        var s = (side ?? "zombie").Trim().ToLowerInvariant();
        if (s == "plant")
            SpawnExtraPlant(typeId, col, row, reason, correlationId, instanceId, loadoutJson, empireId, controller);
        else
            SpawnExtraZombieCore(typeId, row, reason, correlationId, instanceId, loadoutJson, empireId, controller);
    }

    static void SpawnExtraPlant(int typeId, int? col, int? row, string? reason, string? correlationId,
        string? instanceId = null, string? loadoutJson = null, string? empireId = null, string? controller = null)
    {
        try
        {
            if (!Match.SpawnAdmit.TryAdmit("plant", out _))
                return;

            var unique = !string.IsNullOrWhiteSpace(instanceId) && !string.IsNullOrWhiteSpace(correlationId);
            if (unique)
            {
                if (!Match.MatchHost.TryBeginUniquePending(instanceId!, correlationId!, "plant", typeId, loadoutJson))
                {
                    CheatState.Error("pvz.spawn.extra: unique Pending rejected; abort plant spawn");
                    return;
                }
            }

            if (col is { } c)
                CheatState.SpawnCol = LawnCoords.ClampCol(c);
            if (row is { } r)
                CheatState.SpawnRow = LawnCoords.ClampRow(r);
            CheatState.PendingSpawnSourceTag = "extra";
            Plant? plant = null;
            try
            {
                var free = CheatState.On("G-FREE-SET");
                plant = CreatePlant.Instance.SetPlant(CheatState.SpawnCol, CheatState.SpawnRow, (PlantType)typeId,
                    null, default, free, true, null);
            }
            finally
            {
                if (plant != null && !string.IsNullOrEmpty(CheatState.PendingSpawnSourceTag))
                    CheatState.RegisterSpawnSourceTag(plant.Pointer, "extra");
                CheatState.ClearPendingSpawnSourceTag();
            }
            if (plant == null)
            {
                if (unique)
                {
                    Match.MatchHost.TryClearUniquePending(instanceId, correlationId);
                    RpgHost.Client?.ReportFailedDeploy(instanceId!, "SetPlant returned null");
                }
                CheatState.Error("pvz.spawn.extra: SetPlant null");
                return;
            }
            SpawnCatalog.MarkSpawn("plant", typeId, true);
            CheatState.Select(plant.Pointer, "plant");
            var plantPtr = GameDumps.Ptr(plant);
            CheatState.RegisterSpecimenOwner(plantPtr, empireId, controller); // aura-skill T21b / SE4.28
            var ackPlant = new Dictionary<string, object>
            {
                ["typeId"] = typeId,
                ["col"] = CheatState.SpawnCol,
                ["row"] = CheatState.SpawnRow,
                ["reason"] = reason ?? "extra",
                ["correlationId"] = correlationId ?? "",
                ["ptr"] = plantPtr,
                ["side"] = "plant",
                ["source"] = "extra"
            };
            if (!string.IsNullOrWhiteSpace(instanceId))
                ackPlant["instanceId"] = instanceId!;
            GameHooks.Emit("pvz.spawn.extra.ack", ackPlant);
            CheatState.Note($"pvz.spawn.extra plant type={typeId} reason={reason} corr={correlationId}");
        }
        catch (Exception ex)
        {
            CheatState.ClearPendingSpawnSourceTag();
            if (!string.IsNullOrWhiteSpace(instanceId) || !string.IsNullOrWhiteSpace(correlationId))
                Match.MatchHost.TryClearUniquePending(instanceId, correlationId);
            if (!string.IsNullOrWhiteSpace(instanceId))
                RpgHost.Client?.ReportFailedDeploy(instanceId!, "plant spawn threw: " + ex.Message);
            CheatState.Error("pvz.spawn.extra plant: " + ex.Message);
        }
    }

    static void SpawnExtraZombieCore(int typeId, int? row, string? reason, string? correlationId,
        string? instanceId = null, string? loadoutJson = null, string? empireId = null, string? controller = null)
    {
        try
        {
            if (!Match.SpawnAdmit.TryAdmit("zombie", out _))
                return;

            var unique = !string.IsNullOrWhiteSpace(instanceId) && !string.IsNullOrWhiteSpace(correlationId);
            if (unique)
            {
                if (!Match.MatchHost.TryBeginUniquePending(instanceId!, correlationId!, "zombie", typeId, loadoutJson))
                {
                    CheatState.Error("pvz.spawn.extra: unique Pending rejected; abort zombie spawn");
                    return;
                }
            }

            if (row is { } r)
                CheatState.SpawnRow = LawnCoords.ClampRow(r);
            CheatState.PendingSpawnSourceTag = "extra";
            Zombie? z = null;
            try
            {
                z = CreateZombieSpawn.Set(CheatState.SpawnRow, (ZombieType)typeId, 9.9f, false);
            }
            finally
            {
                if (z != null && !string.IsNullOrEmpty(CheatState.PendingSpawnSourceTag))
                    CheatState.RegisterSpawnSourceTag(z.Pointer, "extra");
                CheatState.ClearPendingSpawnSourceTag();
            }
            if (z == null)
            {
                if (unique)
                {
                    Match.MatchHost.TryClearUniquePending(instanceId, correlationId);
                    RpgHost.Client?.ReportFailedDeploy(instanceId!, "SetZombie returned null");
                }
                CheatState.Error("pvz.spawn.extra: SetZombie null");
                return;
            }
            SpawnCatalog.MarkSpawn("zombie", typeId, true);
            CheatState.Select(z.Pointer, "zombie");
            var zombiePtr = GameDumps.Ptr(z);
            CheatState.RegisterSpecimenOwner(zombiePtr, empireId, controller); // aura-skill T21b / SE4.28
            var ackZombie = new Dictionary<string, object>
            {
                ["typeId"] = typeId,
                ["row"] = CheatState.SpawnRow,
                ["reason"] = reason ?? "extra",
                ["correlationId"] = correlationId ?? "",
                ["ptr"] = zombiePtr,
                ["side"] = "zombie",
                ["source"] = "extra"
            };
            if (!string.IsNullOrWhiteSpace(instanceId))
                ackZombie["instanceId"] = instanceId!;
            GameHooks.Emit("pvz.spawn.extra.ack", ackZombie);
            CheatState.Note($"pvz.spawn.extra zombie type={typeId} reason={reason} corr={correlationId}");
        }
        catch (Exception ex)
        {
            CheatState.ClearPendingSpawnSourceTag();
            if (!string.IsNullOrWhiteSpace(instanceId) || !string.IsNullOrWhiteSpace(correlationId))
                Match.MatchHost.TryClearUniquePending(instanceId, correlationId);
            if (!string.IsNullOrWhiteSpace(instanceId))
                RpgHost.Client?.ReportFailedDeploy(instanceId!, "zombie spawn threw: " + ex.Message);
            CheatState.Error("pvz.spawn.extra: " + ex.Message);
        }
    }

    public static void DeleteAllPlants()
    {
        var n = 0;
        foreach (var p in UnityEngine.Object.FindObjectsOfType<Plant>())
        {
            try
            {
                if (p == null) continue;
                p.Die(Plant.DieReason.BySelf);
                n++;
            }
            catch { }
        }
        CheatState.Note($"delete plants n={n}");
    }

    public static void DeleteAllZombies()
    {
        var n = 0;
        var err = 0;
        try
        {
            foreach (var z in UnityEngine.Object.FindObjectsOfType<Zombie>())
            {
                try
                {
                    if (z == null) continue;
                    EntityStatWriter.ForceKillZombie(z, "cheat.killAll");
                    n++;
                }
                catch { err++; }
            }
        }
        catch (Exception ex) { CheatState.Error("killAll find: " + ex.Message); }
        // IL2CPP reuses zombie IntPtrs; NoteZombieDead uses DeadZombies as once-per-ptr.
        // Clear after mass-delete so the next spawn at a recycled ptr still emits die/onkill.
        GameHooks.DeadZombies.Clear();
        CheatState.Note($"delete zombies n={n} err={err}");
    }

    public static void KillAllZombies() => DeleteAllZombies();

    public static void HypnoAll()
    {
        var n = 0;
        foreach (var z in UnityEngine.Object.FindObjectsOfType<Zombie>())
        {
            try { z.SetMindControl(1); n++; } catch { }
        }
        CheatState.Note($"hypno n={n}");
    }

    public static void OneShotSelected()
    {
        if (CheatState.SelectedPtr == IntPtr.Zero) { CheatState.Error("no selection"); return; }
        foreach (var z in UnityEngine.Object.FindObjectsOfType<Zombie>())
        {
            if (z == null || z.Pointer != CheatState.SelectedPtr) continue;
            EntityStatWriter.ForceKillZombie(z, "cheat.oneshot");
            CheatState.Note("oneshot zombie");
            return;
        }
        foreach (var p in UnityEngine.Object.FindObjectsOfType<Plant>())
        {
            if (p == null || p.Pointer != CheatState.SelectedPtr) continue;
            EntityStatWriter.ForceKillPlant(p, "cheat.oneshot");
            CheatState.Note("oneshot plant");
            return;
        }
    }

    public static void SetEconomy(string which, float value, bool add)
    {
        var board = GameHooks.Board;
        if (board == null) { CheatState.Error("no board"); return; }
        try
        {
            switch (which)
            {
                case "sun":
                    board.theSun = add ? board.theSun + (int)value : (int)value;
                    break;
                case "money":
                    board.theMoney = add ? board.theMoney + (int)value : (int)value;
                    break;
                case "points":
                    board.thePoints = add ? board.thePoints + value : value;
                    break;
                case "maxSun":
                    board.maxSun = (int)value;
                    break;
                case "maxMoney":
                    board.maxMoney = (int)value;
                    break;
            }
            CheatState.Note($"{which}={(add ? "add" : "set")} {value}");
            try
            {
                var eco = GameDumps.LiveBoard(board);
                CheatState.TagProbe(eco);
                GameHooks.Emit("board.economy", eco);
            }
            catch { /* LiveBoard optional */ }
        }
        catch (Exception ex) { CheatState.Error("economy: " + ex.Message); }
    }

    public static void ApplyBoardConfig()
    {
        var board = GameHooks.Board;
        if (board?.config == null) { CheatState.Error("no board.config"); return; }
        try
        {
            var ids = new[]
            {
                "E-ZH", "E-ZD", "E-ZS", "E-ZC", "E-ZARM",
                "E-PMIN", "E-PMAX", "E-ZMIN", "E-ZMAX", "E-WAVE-I", "E-CONV-I"
            };
            if (!ids.Any(CheatState.IsUserSet))
            {
                CheatState.Note("board.config: nothing set (unset E-* not applied)");
                return;
            }

            var c = board.config;
            if (CheatState.IsUserSet("E-ZH")) c.zombieHealthMultiplier = CheatState.FVal("E-ZH");
            if (CheatState.IsUserSet("E-ZD")) c.zombieDamageMultiplier = CheatState.FVal("E-ZD");
            if (CheatState.IsUserSet("E-ZS")) c.zombieSpeedMultiplier = CheatState.FVal("E-ZS");
            if (CheatState.IsUserSet("E-ZC")) c.zombieCountMultiplier = CheatState.FVal("E-ZC");
            // E35 (spec-match-modify.md §2.3): the one `long` channel on this kind. `checked` so an
            // authored value above int.MaxValue THROWS at this boundary rather than wrapping or
            // silently clamping — the same overflow discipline docs/architecture/numeric-types.md requires everywhere else. No
            // `float` hop: this reads CheatState.LVal (long), never FVal/IVal (which round through a
            // float that stops being integer-exact at 16,777,216, well under a cursed armour value).
            if (CheatState.IsUserSet("E-ZARM")) c.zombieStartAmmor = checked((int)CheatState.LVal("E-ZARM"));
            if (CheatState.IsUserSet("E-PMIN")) c.plantModifyMin = CheatState.FVal("E-PMIN");
            if (CheatState.IsUserSet("E-PMAX")) c.plantModifyMax = CheatState.FVal("E-PMAX");
            if (CheatState.IsUserSet("E-ZMIN")) c.zombieModifyMin = CheatState.FVal("E-ZMIN");
            if (CheatState.IsUserSet("E-ZMAX")) c.zombieModifyMax = CheatState.FVal("E-ZMAX");
            if (CheatState.IsUserSet("E-WAVE-I")) c.waveInterval = CheatState.FVal("E-WAVE-I");
            if (CheatState.IsUserSet("E-CONV-I")) c.conveyInterval = CheatState.FVal("E-CONV-I");
            var mods = GameDumps.BoardConfig(c);
            CheatState.TagProbe(mods);
            GameHooks.Emit("board.modifiers", mods);
            CheatState.BoardConfigLocked = true;
            CheatState.Note("board.config applied");
        }
        catch (Exception ex) { CheatState.Error("board.config: " + ex.Message); }
    }

    public static void LoadBoardConfigIntoCheats()
    {
        var board = GameHooks.Board;
        if (board?.config == null) return;
        try
        {
            var c = board.config;
            CheatState.SetFloatQuiet("E-ZH", c.zombieHealthMultiplier);
            CheatState.SetFloatQuiet("E-ZD", c.zombieDamageMultiplier);
            CheatState.SetFloatQuiet("E-ZS", c.zombieSpeedMultiplier);
            CheatState.SetFloatQuiet("E-ZC", c.zombieCountMultiplier);
            CheatState.SetFloatQuiet("E-ZARM", c.zombieStartAmmor);
            CheatState.SetFloatQuiet("E-PMIN", c.plantModifyMin);
            CheatState.SetFloatQuiet("E-PMAX", c.plantModifyMax);
            CheatState.SetFloatQuiet("E-ZMIN", c.zombieModifyMin);
            CheatState.SetFloatQuiet("E-ZMAX", c.zombieModifyMax);
            CheatState.SetFloatQuiet("E-WAVE-I", c.waveInterval);
            CheatState.SetFloatQuiet("E-CONV-I", c.conveyInterval);
            CheatState.BoardConfigLocked = false;
        }
        catch { }
    }

    public static void SummonWave(int wave)
    {
        try
        {
            var board = GameHooks.Board ?? Board.Instance;
            var spawner = board?.boardSpawner;
            if (spawner == null) { CheatState.Error("no boardSpawner"); return; }
            spawner.SummonZombies(wave);
            CheatState.Note("summon wave " + wave);
        }
        catch (Exception ex) { CheatState.Error("summon: " + ex.Message); }
    }

    public static void HugeWave()
    {
        var board = GameHooks.Board;
        if (board == null) { CheatState.Error("no board"); return; }
        try
        {
            board.HugeWaveEvent(board.theWave);
            CheatState.Note("huge wave");
        }
        catch (Exception ex) { CheatState.Error("huge: " + ex.Message); }
    }

    public static void SetWaveTimer(float t)
    {
        var board = GameHooks.Board;
        if (board == null) return;
        try { board.timeUntilNextWave = t; CheatState.Note("wave timer=" + t); }
        catch (Exception ex) { CheatState.Error(ex.Message); }
    }

    public static void DumpRecipes()
    {
        try
        {
            GameHooks.EnqueueRecipes();
            CheatState.Note("recipes enqueued");
        }
        catch (Exception ex) { CheatState.Error(ex.Message); }
    }

    public static void DumpAlmanacAll()
    {
        try
        {
            GameHooks.EnqueueFullAlmanacText();
            CheatState.Note("almanac text sweep enqueued");
        }
        catch (Exception ex) { CheatState.Error(ex.Message); }
    }

    public static void DumpBaseStats()
    {
        try
        {
            GameHooks.EnqueueBaseStats();
            CheatState.Note("base stat sweep enqueued");
        }
        catch (Exception ex) { CheatState.Error(ex.Message); }
    }

    public static void ReinforceSelected()
    {
        if (CheatState.SelectedPtr == IntPtr.Zero) { CheatState.Error("no selection"); return; }
        var mgr = TravelMgr.Instance ?? UnityEngine.Object.FindObjectOfType<TravelMgr>();
        var board = GameHooks.Board ?? Board.Instance;
        foreach (var z in UnityEngine.Object.FindObjectsOfType<Zombie>())
        {
            if (z == null || z.Pointer != CheatState.SelectedPtr) continue;
            try
            {
                if (mgr != null) mgr.ReinforceZombie(z);
                else EntityStatWriter.ScaleZombieHp(z, 2, "cheat.reinforce");
                GameHooks.RecaptureZombie(z, "cheat.reinforce");
                CheatState.Note("reinforce zombie");
            }
            catch (Exception ex) { CheatState.Error(ex.Message); }
            return;
        }
        foreach (var p in UnityEngine.Object.FindObjectsOfType<Plant>())
        {
            if (p == null || p.Pointer != CheatState.SelectedPtr) continue;
            try
            {
                if (mgr != null && board != null) mgr.ReinforcePlant(board, p);
                else EntityStatWriter.ScalePlantHp(p, 2, "cheat.reinforce");
                GameHooks.RecapturePlant(p, "cheat.reinforce");
                CheatState.Note("reinforce plant");
            }
            catch (Exception ex) { CheatState.Error(ex.Message); }
            return;
        }
    }

    public static void SetSelectedZombieHealth(long hp)
    {
        foreach (var z in UnityEngine.Object.FindObjectsOfType<Zombie>())
        {
            if (z == null || z.Pointer != CheatState.SelectedPtr) continue;
            try
            {
                EntityStatWriter.ForceSetZombieHp(z, hp, "cheat.setZombieHealth");
                try { Lawnf.SetZombieHealth(z, 1f); } catch { }
                GameHooks.RecaptureZombie(z, "cheat.setZombieHealth");
                CheatState.Note("set zombie hp " + hp);
            }
            catch (Exception ex) { CheatState.Error(ex.Message); }
            return;
        }
        CheatState.Error("select a zombie");
    }

    public static void SpawnPet(int type)
    {
        var board = GameHooks.Board;
        if (board == null) { CheatState.Error("no board"); return; }
        try
        {
            var pet = MiniPet.SetPet(board, LawnCoords.CellCenter(CheatState.SpawnCol, CheatState.SpawnRow), (PetType)type);
            SpawnCatalog.Note("pet", type, ((PetType)type).ToString(), "cheat.spawn");
            CheatState.Note(pet != null ? "spawn pet " + type : "pet null");
        }
        catch (Exception ex) { CheatState.Error("pet: " + ex.Message); }
    }

    public static void SpawnGrid(int type)
    {
        try
        {
            var g = GridItem.SetGridItem(CheatState.SpawnCol, CheatState.SpawnRow, (GridItemType)type, default);
            SpawnCatalog.Note("grid", type, ((GridItemType)type).ToString(), "cheat.spawn");
            CheatState.Note(g != null ? "spawn grid " + type : "grid null");
        }
        catch (Exception ex) { CheatState.Error("grid: " + ex.Message); }
    }

    public static void SpawnBucket(int type)
    {
        try
        {
            var board = GameHooks.Board ?? Board.Instance;
            var mgr = GameAPP.itemManager;
            if (mgr == null || board == null) { CheatState.Error("no board/itemManager"); return; }
            var b = mgr.SetBucket(board, (BucketType)type, LawnCoords.CellCenter(CheatState.SpawnCol, CheatState.SpawnRow));
            CheatState.Note(b != null ? "bucket " + type : "bucket null");
        }
        catch (Exception ex) { CheatState.Error("bucket: " + ex.Message); }
    }

    public static void TriggerPresent()
    {
        try
        {
            var present = UnityEngine.Object.FindObjectOfType<Present>();
            if (present == null) { CheatState.Error("no Present"); return; }
            present.RandomPlant();
            CheatState.Note("present.open");
        }
        catch (Exception ex) { CheatState.Error("present: " + ex.Message); }
    }

    public static void TravelBuffStub()
    {
        CheatState.Note("I-TRAVEL-BUFF: inject API varies by mode — observe travel.buff events; stub OK");
    }

    // v3 A5: two FindObjectsOfType scans per FRAME made auto-collect the largest loop cost
    // (~9ms/frame on big boards). 150ms cadence is imperceptible for coin pickup and cuts the
    // scans 10–30×. Escalate to a hook-fed coin registry if the stress gate still shows heat.
    static long _nextAutoCollectMs;

    public static void AutoCollectTick()
    {
        if (!CheatState.On("G-AUTOCOLLECT")) return;
        var nowMs = Environment.TickCount64;
        if (nowMs < _nextAutoCollectMs) return;
        _nextAutoCollectMs = nowMs + 150;
        var board = GameHooks.Board ?? Board.Instance;
        try
        {
            foreach (var coin in UnityEngine.Object.FindObjectsOfType<CoinSun>())
            {
                try
                {
                    if (coin == null || board == null) continue;
                    var price = 25;
                    try { price = Math.Max(1, coin.sunPrice); } catch { }
                    board.theSun += price;
                    try { coin.Die(); } catch { }
                }
                catch { }
            }
            foreach (var coin in UnityEngine.Object.FindObjectsOfType<CoinMoney>())
            {
                try
                {
                    if (coin == null || board == null) continue;
                    var price = 50;
                    try { price = Math.Max(1, coin.moneyPrice); } catch { }
                    board.theMoney += price;
                    try { coin.Die(); } catch { }
                }
                catch { }
            }
        }
        catch { }
    }

    public static async void PushStatsToServer()
    {
        try
        {
            CheatState.SyncLocalStatsFromEntries();
            if (RpgHost.Client == null) { CheatState.Error("no client"); return; }
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var json = System.Text.Json.JsonSerializer.Serialize(CheatState.LocalStats);
            var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var baseUrl = RpgHost.ServerUrl.TrimEnd('/');
            var resp = await http.PutAsync(baseUrl + "/api/stats", content);
            await http.PostAsync(baseUrl + "/api/commands/reload-stats", new System.Net.Http.StringContent("{}"));
            CheatState.Note("pushed stats " + resp.StatusCode);
            if (RpgHost.Client != null)
                RpgHost.Client.Stats = CheatState.LocalStats;
        }
        catch (Exception ex) { CheatState.Error("push: " + ex.Message); }
    }

    public static void PullStatsFromServer()
    {
        if (RpgHost.Client?.Stats == null) { CheatState.Error("no stats"); return; }
        CheatState.PullFromServer(RpgHost.Client.Stats);
    }
}
