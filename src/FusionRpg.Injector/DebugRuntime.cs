using System.Text.Json;
using FusionRpg.Injector.Host;
using FusionRpg.Injector.Lawn;
using FusionRpg.Injector.Stats;
using FusionRpg.Core.Commanders;
using UnityEngine;

namespace FusionRpg.Injector;
/// <summary>Debug session + one-shot arms for controllable effect-pipeline tests.</summary>
public static class DebugRuntime
{
    public static bool SessionActive;
    public static string ScenarioId = "";
    public static bool HitCapture = true;

    public static bool ArmOnKillExtra;
    public static int OnKillExtraTypeId = 0;
    public static int? OnKillExtraRow;
    public static bool ArmOnKillStatus;
    public static string OnKillStatus = "butter";

    public static bool ArmOnKillGrave;
    public static int OnKillGraveCol = 4;
    public static bool ArmOnKillClearGrave;

    public static bool ArmOnHitExtra;
    public static int OnHitExtraTypeId;
    public static int OnHitExtraRemaining;
    public static bool ArmOnHitStatus;
    public static string OnHitStatus = "butter";
    public static int OnHitStatusRemaining;

    // 2026-09-15 (live, lawn-combat-wire L-N22): a lab scenario sets wave-freeze, plant attack x0, zombie count
    // x0 and silenced vanilla damage through ordinary cheat entries, and nothing undid them when the session
    // ended -- the next "shipped configuration" board spawned no zombies and dealt no vanilla damage. The
    // user-set cheat entries are now snapshotted when a session starts and restored when it ends.
    static string? _preSessionCheats;
    static bool _preSessionLocalStatsOverride;

    public static void StartSession(string? scenarioId)
    {
        if (!SessionActive)
        {
            try
            {
                _preSessionCheats = JsonSerializer.Serialize(CheatState.Snapshot());
                _preSessionLocalStatsOverride = CheatState.LocalStatsOverride;
            }
            catch (Exception ex)
            {
                _preSessionCheats = null;
                CheatState.Error("debug session: cheat snapshot failed, lab state will not be restored: " + ex.Message);
            }
        }
        SessionActive = true;
        ScenarioId = string.IsNullOrWhiteSpace(scenarioId) ? Guid.NewGuid().ToString("N")[..12] : scenarioId!;
        HitCapture = true;
        CheatState.LocalStats.LogDamage = true;
        CheatState.SetToggle("G-FREE-SET", true, "debug", emitInject: false);
        Emit("debug.session.start", new Dictionary<string, object>
        {
            ["scenarioId"] = ScenarioId,
            ["hitCapture"] = HitCapture
        });
        CheatState.Note("debug session " + ScenarioId);
    }

    public static void EndSession()
    {
        DisarmAll();
        // StartSession enabled per-hit telemetry; leaving it on after the session silently
        // re-enables the per-hit emit path until game restart (lag with no visible cause).
        CheatState.LocalStats.LogDamage = false;
        var id = ScenarioId;
        SessionActive = false;
        InjectorDerivedOverride.Clear();
        InjectorElementOverride.Clear();
        Hud.ActorHudMeterOverride.Clear(); // E41: per-match state, same reset as the two lines above
        try { Hud.ActorHudCache.Clear(); } catch { }
        var restored = RestorePreSessionCheats();
        Emit("debug.session.end", new Dictionary<string, object> { ["scenarioId"] = id, ["cheatsRestored"] = restored });
        ScenarioId = "";
    }

    /// <summary>Restores the cheat entries captured by <see cref="StartSession"/> and re-resolves every living
    /// entity so a lab's stat writes (e.g. plant attack 0) are undone on the board too. Returns the number of
    /// user-set entries restored, or -1 when there was no snapshot.</summary>
    static int RestorePreSessionCheats()
    {
        var json = _preSessionCheats;
        _preSessionCheats = null;
        if (json == null) return -1;
        try
        {
            var revision = CheatState.DocumentRevision;
            using var doc = JsonDocument.Parse(json);
            CheatState.ApplySnapshot(doc.RootElement);
            CheatState.LocalStatsOverride = _preSessionLocalStatsOverride;
            CheatState.DocumentRevision = revision + 1;
            CheatState.MarkAppliedRevision();
            CheatActions.ReapplyAllLiving();
            var count = doc.RootElement.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array
                ? entries.GetArrayLength()
                : 0;
            CheatState.Note($"debug session: restored {count} pre-session cheat entries");
            return count;
        }
        catch (Exception ex)
        {
            CheatState.Error("debug session: cheat restore failed: " + ex.Message);
            return -1;
        }
    }

    public static void DisarmAll()
    {
        ArmOnKillExtra = false;
        ArmOnKillStatus = false;
        ArmOnKillGrave = false;
        ArmOnKillClearGrave = false;
        ArmOnHitExtra = false;
        ArmOnHitStatus = false;
        OnHitExtraRemaining = 0;
        OnHitStatusRemaining = 0;
    }

    public static bool ShouldEmitHit() =>
        (SessionActive && HitCapture) || CheatState.LocalStats.LogDamage || (RpgHost.Client?.Stats.LogDamage ?? false);

    public static void Stamp(Dictionary<string, object> payload)
    {
        if (!string.IsNullOrEmpty(ScenarioId))
            payload["scenarioId"] = ScenarioId;
    }

    public static void Emit(string kind, Dictionary<string, object> payload)
    {
        Stamp(payload);
        GameHooks.Emit(kind, payload);
    }

    public static Dictionary<string, object> Snapshot()
    {
        var matchSnap = Match.MatchHost.Runtime.ToSnapshot();
        var matchDict = new Dictionary<string, object>
            {
                ["contractVersion"] = matchSnap.ContractVersion,
                ["phase"] = matchSnap.Phase.ToString(),
                ["matchKey"] = matchSnap.MatchKey ?? "",
                ["revision"] = matchSnap.Revision,
                ["plantCount"] = matchSnap.PlantCount,
                ["zombieCount"] = matchSnap.ZombieCount,
                ["bulletCount"] = matchSnap.BulletCount,
                ["debugActive"] = matchSnap.DebugActive,
                ["scenarioId"] = matchSnap.ScenarioId ?? "",
                ["effectSessionActive"] = matchSnap.EffectSessionActive,
                ["caps"] = new Dictionary<string, object>
                {
                    ["maxLivingPlants"] = matchSnap.Caps.MaxLivingPlants,
                    ["maxLivingZombies"] = matchSnap.Caps.MaxLivingZombies,
                    ["maxLivingBullets"] = matchSnap.Caps.MaxLivingBullets
                },
                ["bindings"] = matchSnap.Bindings.Select(b => new Dictionary<string, object>
                {
                    ["instanceId"] = b.InstanceId,
                    ["ptr"] = b.Ptr ?? "",
                    ["side"] = b.Side,
                    ["typeId"] = b.TypeId,
                    ["phase"] = b.Phase.ToString(),
                    ["correlationId"] = b.CorrelationId ?? ""
                }).ToList()
            };
        var commanderFold = MatchCommanderSnapshotHolder.ObserveCommanderFold();
        if (commanderFold != null)
            matchDict["commander"] = commanderFold;
        var lawnDeployRosterFold = FusionRpg.Core.Match.LawnDeployRosterSnapshotHolder.ObserveRosterFold();
        if (lawnDeployRosterFold != null)
            matchDict["lawnDeployRoster"] = lawnDeployRosterFold;

        var dump = new Dictionary<string, object>
        {
            ["sessionActive"] = SessionActive,
            ["scenarioId"] = ScenarioId,
            ["hitCapture"] = HitCapture,
            ["spawnCol"] = CheatState.SpawnCol,
            ["spawnRow"] = CheatState.SpawnRow,
            ["selectedPtr"] = CheatState.SelectedPtr == IntPtr.Zero ? "" : CheatState.SelectedPtr.ToString("X"),
            ["selectedSide"] = CheatState.SelectedSide ?? "",
            ["armOnKillExtra"] = ArmOnKillExtra,
            ["armOnKillStatus"] = ArmOnKillStatus,
            ["armOnKillGrave"] = ArmOnKillGrave,
            ["armOnKillClearGrave"] = ArmOnKillClearGrave,
            ["armOnHitExtra"] = ArmOnHitExtra,
            ["onHitExtraRemaining"] = OnHitExtraRemaining,
            ["armOnHitStatus"] = ArmOnHitStatus,
            ["onHitStatusRemaining"] = OnHitStatusRemaining,
            ["probePlant"] = CheatState.On("D-PROBE-PLANT"),
            ["probeBullet"] = CheatState.On("D-PROBE-BULLET"),
            ["dmgSet"] = CheatState.IVal("D-DMG-SET"),
            // Flat fields kept for back-compat; nested match is W3-B MatchSnapshot observe.
            ["livingPlants"] = matchSnap.PlantCount,
            ["livingZombies"] = matchSnap.ZombieCount,
            ["matchPhase"] = matchSnap.Phase.ToString(),
            ["matchRevision"] = matchSnap.Revision,
            ["match"] = matchDict,
            // L-N22 observability: per-match stores that must be empty after a match edge.
            ["spawnHpPins"] = Stats.InjectorSpawnHpPin.Store.Count,
            ["spawnOriginMarks"] = Match.SpawnOriginTags.Count
        };
        try
        {
            dump["theBoardType"] = (int)GameAPP.theBoardType;
            dump["theBoardLevel"] = GameAPP.theBoardLevel;
        }
        catch { }
        try
        {
            var board = GameHooks.Board ?? Board.Instance;
            if (board != null)
                dump["sceneType"] = (int)board.sceneType;
        }
        catch { }
        return dump;
    }

    /// <summary>
    /// Living plant/zombie combat fields + effect session mods for LIVE FA1 scope asserts.
    /// Emitted as <c>debug.board-stats</c>.
    /// </summary>
    static void AddBoardStatsActorHud(Dictionary<string, object> row, string ptrHex) =>
        Hud.ActorHudObserve.AttachRow(row, ptrHex);

    /// <summary>
    /// Living plant/zombie combat fields + effect session mods for LIVE FA1 scope asserts.
    /// Emitted as <c>debug.board-stats</c>.
    /// </summary>
    public static Dictionary<string, object> BoardEntityStats()
    {
        var plants = new List<Dictionary<string, object>>();
        var zombies = new List<Dictionary<string, object>>();
        try
        {
            foreach (var p in UnityEngine.Object.FindObjectsOfType<Plant>())
            {
                try
                {
                    if (p == null || p.thePlantType == PlantType.Nothing) continue;
                    var armor = 0;
                    try { armor = p.theShieldHealth; } catch { }
                    float interval = 0;
                    try { interval = p.thePlantAttackInterval; } catch { }
                    plants.Add(new Dictionary<string, object>
                    {
                        ["ptr"] = GameDumps.Ptr(p),
                        ["typeId"] = (int)p.thePlantType,
                        ["col"] = p.thePlantColumn,
                        ["row"] = p.thePlantRow,
                        ["attack"] = p.attackDamage,
                        ["attackDamage"] = p.attackDamage,
                        ["hp"] = p.thePlantHealth,
                        ["maxHp"] = p.thePlantMaxHealth,
                        ["armor"] = armor,
                        ["thePlantAttackInterval"] = interval
                    });
                    AddBoardStatsActorHud(plants[^1], GameDumps.Ptr(p));
                }
                catch { }
            }
        }
        catch { }

        try
        {
            foreach (var z in UnityEngine.Object.FindObjectsOfType<Zombie>())
            {
                try
                {
                    if (z == null || z.theZombieType == ZombieType.Nothing) continue;
                    var armor = 0;
                    var armorMax = 0;
                    var armor2 = 0;
                    var armor2Max = 0;
                    try { armor = z.theFirstArmorHealth; } catch { }
                    try { armorMax = z.theFirstArmorMaxHealth; } catch { }
                    try { armor2 = z.theSecondArmorHealth; } catch { }
                    try { armor2Max = z.theSecondArmorMaxHealth; } catch { }
                    float speed = 0;
                    try { speed = z.theSpeed; } catch { }
                    var item = new Dictionary<string, object>
                    {
                        ["ptr"] = GameDumps.Ptr(z),
                        ["typeId"] = (int)z.theZombieType,
                        ["attack"] = z.theAttackDamage,
                        ["attackDamage"] = z.theAttackDamage,
                        ["hp"] = Bridges.ZombieCombatFields.GetHp(z),
                        ["maxHp"] = Bridges.ZombieCombatFields.GetMaxHp(z),
                        ["armor"] = armor,
                        ["armorMax"] = armorMax,
                        ["theSecondArmorHealth"] = armor2,
                        ["theSecondArmorMaxHealth"] = armor2Max,
                        ["theSpeed"] = speed
                    };
                    GameDumps.AddZombiePos(item, z);
                    AddBoardStatsActorHud(item, GameDumps.Ptr(z));
                    zombies.Add(item);
                }
                catch { }
            }
        }
        catch { }

        var mowers = new List<Dictionary<string, object>>();
        var seenMowers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var board = GameHooks.Board ?? Board.Instance;
            var arr = board?.mowerArray;
            if (arr != null)
            {
                var row = 0;
                foreach (var mw in arr)
                {
                    try
                    {
                        if (mw != null)
                        {
                            var ptr = GameDumps.Ptr(mw);
                            seenMowers.Add(ptr);
                            mowers.Add(new Dictionary<string, object>
                            {
                                ["ptr"] = ptr,
                                ["typeId"] = (int)mw.theMowerType,
                                ["type"] = (int)mw.theMowerType,
                                ["row"] = row,
                                ["col"] = 0,
                                ["started"] = mw.started
                            });
                        }
                    }
                    catch { }
                    row++;
                }
            }
        }
        catch { }
        try
        {
            foreach (var mw in UnityEngine.Object.FindObjectsOfType<Mower>())
            {
                try
                {
                    if (mw == null) continue;
                    var ptr = GameDumps.Ptr(mw);
                    if (!seenMowers.Add(ptr)) continue;
                    mowers.Add(new Dictionary<string, object>
                    {
                        ["ptr"] = ptr,
                        ["typeId"] = (int)mw.theMowerType,
                        ["type"] = (int)mw.theMowerType,
                        ["started"] = mw.started
                    });
                }
                catch { }
            }
        }
        catch { }

        var sessionMods = new List<Dictionary<string, object>>();
        try
        {
            foreach (var m in CheatState.Stats.ListSessionMods("effect"))
            {
                sessionMods.Add(new Dictionary<string, object>
                {
                    ["sourceId"] = m.SourceId,
                    ["channel"] = m.Channel,
                    ["op"] = (int)m.Op,
                    ["value"] = m.Value,
                    ["applyOwnerKey"] = m.ApplyOwnerKey
                });
            }
        }
        catch { }

        return new Dictionary<string, object>
        {
            ["selectedPtr"] = CheatState.SelectedPtr == IntPtr.Zero ? "" : CheatState.SelectedPtr.ToString("X"),
            ["plantCount"] = plants.Count,
            ["zombieCount"] = zombies.Count,
            ["plants"] = plants,
            ["zombies"] = zombies,
            ["mowers"] = mowers,
            ["sessionMods"] = sessionMods
        };
    }

    public static void OnCombatHit(string side, Zombie? zombieTarget, Plant? plantTarget)
    {
        // A3: skip on-hit arms only when an OnDamageDealt Foundation grant is active.
        var skipOnHitArms = Effects.EffectRuntime.HasOnDamageDealtGrant();

        if (!skipOnHitArms && ArmOnHitExtra && OnHitExtraRemaining > 0)
        {
            OnHitExtraRemaining--;
            if (OnHitExtraRemaining <= 0) ArmOnHitExtra = false;
            var row = zombieTarget != null ? zombieTarget.theZombieRow : CheatState.SpawnRow;
            CheatActions.SpawnExtraZombie(OnHitExtraTypeId, row, "debug.onhit.extra", Guid.NewGuid().ToString("N"));
            Emit("debug.onhit.extra", new Dictionary<string, object>
            {
                ["typeId"] = OnHitExtraTypeId,
                ["row"] = row,
                ["remaining"] = OnHitExtraRemaining
            });
        }

        if (!skipOnHitArms && ArmOnHitStatus && OnHitStatusRemaining > 0 && zombieTarget != null)
        {
            OnHitStatusRemaining--;
            if (OnHitStatusRemaining <= 0) ArmOnHitStatus = false;
            DebugActions.ApplyStatusToZombie(zombieTarget, OnHitStatus, 4f, 1, method: true);
            Emit("debug.onhit.status", new Dictionary<string, object>
            {
                ["status"] = OnHitStatus,
                ["target"] = GameDumps.Ptr(zombieTarget),
                ["remaining"] = OnHitStatusRemaining
            });
        }

        _ = side;
        _ = plantTarget;
    }

    public static void OnZombieDying(Zombie z)
    {
        if (z == null) return;
        var skipOnKillArms = Effects.EffectRuntime.HasOnDeathGrant();
        if (!skipOnKillArms && ArmOnKillExtra)
        {
            ArmOnKillExtra = false;
            var row = OnKillExtraRow ?? z.theZombieRow;
            CheatActions.SpawnExtraZombie(OnKillExtraTypeId, row, "debug.onkill.extra", Guid.NewGuid().ToString("N"));
            Emit("debug.onkill.extra", new Dictionary<string, object>
            {
                ["typeId"] = OnKillExtraTypeId,
                ["row"] = row,
                ["deadPtr"] = GameDumps.Ptr(z)
            });
        }

        if (!skipOnKillArms && ArmOnKillStatus)
        {
            ArmOnKillStatus = false;
            try
            {
                foreach (var other in UnityEngine.Object.FindObjectsOfType<Zombie>())
                {
                    if (other == null || other.Pointer == z.Pointer) continue;
                    DebugActions.ApplyStatusToZombie(other, OnKillStatus, 4f, 1, method: true);
                    Emit("debug.onkill.status", new Dictionary<string, object>
                    {
                        ["status"] = OnKillStatus,
                        ["target"] = GameDumps.Ptr(other)
                    });
                    break;
                }
            }
            catch (Exception ex) { CheatState.Error("onkill.status: " + ex.Message); }
        }

        if (!skipOnKillArms && ArmOnKillGrave)
        {
            ArmOnKillGrave = false;
            try
            {
                var row = z.theZombieRow;
                var col = OnKillGraveCol;
                try
                {
                    var x = z.transform != null ? z.transform.position.x : 0f;
                    var fromX = LawnCoords.ColFromX(x);
                    if (fromX >= 0) col = fromX;
                }
                catch { }
                col = LawnCoords.ClampCol(col);
                DebugActions.PlaceGridItem(col, row, (int)GridItemType.Grave, GraveType.Default, "debug.onkill.grave");
                Emit("debug.onkill.grave", new Dictionary<string, object>
                {
                    ["col"] = col,
                    ["row"] = row,
                    ["deadPtr"] = GameDumps.Ptr(z)
                });
            }
            catch (Exception ex) { CheatState.Error("onkill.grave: " + ex.Message); }
        }

        if (!skipOnKillArms && ArmOnKillClearGrave)
        {
            ArmOnKillClearGrave = false;
            try
            {
                var ok = DebugActions.ClearGridItem((int)GridItemType.Grave, null, null, random: true,
                    emitKind: "debug.onkill.clear-grave");
                if (!ok)
                    Emit("debug.onkill.clear-grave", new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["deadPtr"] = GameDumps.Ptr(z)
                    });
            }
            catch (Exception ex) { CheatState.Error("onkill.clear-grave: " + ex.Message); }
        }
    }

    public static void ApplyPayloadArm(JsonElement p)
    {
        var kind = Str(p, "kind") ?? "";
        switch (kind)
        {
            case "onkill-extra":
                ArmOnKillExtra = true;
                OnKillExtraTypeId = Int(p, "typeId", 0);
                OnKillExtraRow = p.TryGetProperty("row", out var r) && r.TryGetInt32(out var rv) ? rv : null;
                break;
            case "onkill-status":
                ArmOnKillStatus = true;
                OnKillStatus = Str(p, "status") ?? "butter";
                break;
            case "onkill-grave":
                ArmOnKillGrave = true;
                OnKillGraveCol = Int(p, "col", 4);
                break;
            case "onkill-clear-grave":
                ArmOnKillClearGrave = true;
                break;
            case "onhit-extra":
                ArmOnHitExtra = true;
                OnHitExtraTypeId = Int(p, "typeId", 0);
                OnHitExtraRemaining = Int(p, "maxTriggers", 1);
                break;
            case "onhit-status":
                ArmOnHitStatus = true;
                OnHitStatus = Str(p, "status") ?? "butter";
                OnHitStatusRemaining = Int(p, "maxTriggers", 1);
                break;
        }
    }

    static string? Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    static int Int(JsonElement p, string name, int dflt) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var e) && e.TryGetInt32(out var v) ? v : dflt;
}
