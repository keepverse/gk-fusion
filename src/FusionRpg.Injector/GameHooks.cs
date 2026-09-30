using System.Collections.Generic;
using FusionRpg.Contracts;
using FusionRpg.Core;
using FusionRpg.Core.Diagnostics;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats;
using FusionRpg.Injector.Stats;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

using FusionRpg.Injector.Host;
// The plant component, not the GameLevel type of the same name that both hosts also import (L-N39).
#if FUSIONRPG_MELON
using PlantBambooDragon = Il2Cpp.BambooDragon;
#else
using PlantBambooDragon = global::BambooDragon;
using FusionRpg.Bridge.Hud;
#endif

namespace FusionRpg.Injector;

public static class GameHooks
{
    public static Board? Board;
    public static string? MatchKey;

    /// <summary>lawn-combat-wire L-N31: the server run's key for the whole life of the board. <see cref="MatchKey"/> is the
    /// RPG match's key and <c>MatchHost</c> clears it on <c>match.result</c>; events after that (end-of-level deaths, the
    /// board's own <c>board.end</c>) still belong to this board's run, so the wire falls back to this key. Set in
    /// <c>Board.Awake</c>, cleared after <c>board.end</c> is emitted.</summary>
    static string? _runKey;

    public static int LastWave = -1;
    public static int CatalogPlantCount;
    public static readonly HashSet<IntPtr> Applied = new();
    public static readonly HashSet<IntPtr> DeadZombies = new();
    // Main-thread-only causal tokens. A token is recorded only by a Die hook reached synchronously
    // from the target's active TakeDamage interaction. This is not a last-attacker cache: no HP
    // scan or post-hit HP read is needed, and deferred/indirect deaths fail closed.
    static readonly Dictionary<IntPtr, string> FatalKillers = new();
    // The game may call Die from inside TakeDamage, before the postfix runs. Keep the current
    // interaction source only for that synchronous call frame so the Die prefix can prove the same
    // fatal interaction; it is cleared as soon as TakeDamage returns.
    static readonly Dictionary<IntPtr, IDamageMaker?> ActiveDamageSources = new();
    static readonly HashSet<IntPtr> MowerStarted = new();
    static int _lastSun = int.MinValue;
    static int _lastMoney = int.MinValue;
    static float _lastPoints = float.MinValue;
    static string? _lastLevelName;
    static bool _recipesDumped;
    static bool _almanacTextDumped;
    static bool _baseStatsDumped;
    static bool _conveyDumped;
    static bool _catalogRetried;
    static int _catalogPending;
    // Per-match lifecycle identity. Pointer values are reused by the game, so death facts need a
    // monotonic occurrence in addition to ptr. The payload is persisted with the event and therefore
    // remains stable when the same capture is retried.
    static long _lifecycleOccurrence;

    public static void ClearMatch()
    {
        // Backstop: any records still pending at a match edge drain before state clears.
        try { Effects.EventDrainHost.FlushAllAndReset(); } catch { }
        // lawn-combat-wire T10: a ptr queued for a basic-attack grant bind in a match that ended
        // before its next drain must not bind against the NEXT match's board.
        try { Effects.LawnBasicAttackGrantBinder.ClearPending(); } catch { }
        // lawn-combat-wire T12b: drop this match's regen-telemetry baseline -- the pools themselves
        // are already dropped by InjectorEntityRegistry.Clear() below.
        try { Effects.LawnBasicAttackCostCharger.ClearMatchState(); } catch { }
        // Pins on entities still alive at match end would otherwise leak onto next match's reused ptrs.
        try { Stats.InjectorSpawnHpPin.Clear(); } catch { }
        try { Match.SpawnOriginTags.Clear(); } catch { }
        Effects.InjectorEntityRegistry.Clear();
        Effects.InjectorBoardSnapshot.Invalidate();
        Applied.Clear();
        EntityStatWriter.Clear();
        CheatState.Stats.ClearBaselines();
        // Effect ClearAll is owned by MatchHost after board.start/end Apply (W2-C).
        DeadZombies.Clear();
        FatalKillers.Clear();
        ActiveDamageSources.Clear();
        MowerStarted.Clear();
        LastWave = -1;
        _lastSun = int.MinValue;
        _lastMoney = int.MinValue;
        _lastPoints = float.MinValue;
        _lastLevelName = null;
        _conveyDumped = false;
        _catalogRetried = false;
        _lifecycleOccurrence = 0;
    }

    internal static bool Ready() => Board != null && RpgHost.Client != null;

    internal static void Emit(string kind, object payload)
    {
        PerfProbe.CountEmit(kind);
        Dictionary<string, object>? dict = null;
        try
        {
            dict = CoercePayloadDict(payload);
            if (dict != null &&
                string.Equals(kind, "board.start", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(MatchKey) &&
                !dict.ContainsKey("matchKey"))
            {
                dict["matchKey"] = MatchKey!;
            }
            if (dict != null && IsProgressionLifecycle(kind) &&
                !dict.ContainsKey("activeMatchMs"))
            {
                // KernelDriveHost is the one scaled active-match clock. Missing/zero is valid at
                // board.start; terminal settlement fails closed if a lifecycle event lacks a real
                // active timestamp instead of falling back to server wall time.
                dict["activeMatchMs"] = string.Equals(kind, "board.start", StringComparison.OrdinalIgnoreCase)
                    ? 0L
                    : Effects.KernelDriveHost.NowMilliseconds;
            }
            if (dict != null && IsProgressionLifecycle(kind) &&
                !dict.ContainsKey("lifecycleOccurrence"))
            {
                // Assign once at capture time so the same payload keeps its identity across
                // transport retries. Death hooks stamp their id before Emit because they need
                // the occurrence while constructing the terminal payload.
                dict["lifecycleOccurrence"] = Interlocked.Increment(ref _lifecycleOccurrence);
            }
        }
        catch (Exception ex)
        {
            try { CheatState.Error("emit stamp: " + ex.Message); } catch { }
        }

        // lawn-combat-wire L-N31: enqueue before the capture side effects. MatchHost.Apply and EffectRuntime.OnCapture
        // emit their own events (cheat.apply, debug.effect.cleared on board.start) and clear MatchKey on match.result and
        // board.end; enqueuing afterwards put effects on the wire ahead of their cause and sent match.result/board.end
        // without a key, so no live run was ever closed or given a result.
        RpgHost.Client?.Enqueue(kind, payload, MatchKey ?? _runKey);

        try
        {
            using var _perf = PerfProbe.Measure(PerfSection.MatchApply);
            Match.MatchHost.Apply(kind, dict);
        }
        catch (Exception ex)
        {
            try { CheatState.Error("match apply: " + ex.Message); } catch { }
        }

        try
        {
            if (dict != null)
                Effects.EffectRuntime.OnCapture(kind, dict);
        }
        catch (Exception ex)
        {
            try { CheatState.Error("effect capture: " + ex.Message); } catch { }
        }
    }

    static bool IsProgressionLifecycle(string kind) =>
        string.Equals(kind, "board.start", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, "board.end", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, "match.result", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, "plant.die", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, "zombie.die", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, "pvz.spawn.extra.ack", StringComparison.OrdinalIgnoreCase);

    static void CaptureFatalKiller(IntPtr targetPtr, IDamageMaker? damageFrom)
    {
        if (targetPtr == IntPtr.Zero || damageFrom is not Il2CppObjectBase source)
            return;
        try
        {
            if (source.Pointer == IntPtr.Zero || source.Pointer == targetPtr) return;
            if (source.TryCast<Bullet>() != null) return;
            if (source.TryCast<Plant>() == null && source.TryCast<Zombie>() == null) return;
            if (!FatalKillers.ContainsKey(targetPtr))
                FatalKillers[targetPtr] = GameDumps.Ptr(source);
        }
        catch { }
    }

    static string? TakeFatalKiller(IntPtr targetPtr)
    {
        if (!FatalKillers.Remove(targetPtr, out var killer)) return null;
        return killer;
    }

    static void BeginDamageSource(IntPtr targetPtr, IDamageMaker? damageFrom)
    {
        if (targetPtr != IntPtr.Zero) ActiveDamageSources[targetPtr] = damageFrom;
    }

    static bool TryGetDamageSource(IntPtr targetPtr, out IDamageMaker? damageFrom) =>
        ActiveDamageSources.TryGetValue(targetPtr, out damageFrom);

    static void EndDamageSource(IntPtr targetPtr)
    {
        if (targetPtr != IntPtr.Zero) ActiveDamageSources.Remove(targetPtr);
    }

    static void AddFatalKiller(Dictionary<string, object> payload, string? killer)
    {
        if (!string.IsNullOrWhiteSpace(killer)) payload["killerPtr"] = killer;
    }

    static Dictionary<string, object>? CoercePayloadDict(object payload)
    {
        if (payload is Dictionary<string, object> d) return d;
        if (payload is IReadOnlyDictionary<string, object> ro)
            return new Dictionary<string, object>(ro);
        if (payload is IDictionary<string, object> id)
            return new Dictionary<string, object>(id);
        return null;
    }

    public static void RequestTypeCatalog() => Interlocked.Exchange(ref _catalogPending, 1);

    public static void PumpMainThread()
    {
        if (Interlocked.CompareExchange(ref _catalogPending, 0, 1) != 1) return;
        try { EnqueueTypeCatalog(); }
        catch (Exception ex) { RpgHost.Log.Warning("type catalog: " + ex.Message); }
    }

    public static void EnqueueTypeCatalog()
    {
        CatalogPlantCount = 0;
        try { CatalogPlantCount += EnqueueEnumSide("plant", typeof(PlantType)); }
        catch (Exception ex) { RpgHost.Log.Warning("catalog plants: " + ex.Message); }
        try { EnqueueEnumSide("zombie", typeof(ZombieType)); }
        catch (Exception ex) { RpgHost.Log.Warning("catalog zombies: " + ex.Message); }
        try { EnqueueEnumSide("pet", typeof(PetType)); }
        catch (Exception ex) { RpgHost.Log.Warning("catalog pets: " + ex.Message); }
        try { EnqueueEnumSide("grid", typeof(GridItemType)); }
        catch (Exception ex) { RpgHost.Log.Warning("catalog grid: " + ex.Message); }
        try { EnqueueEnumSide("mower", typeof(MowerType)); }
        catch (Exception ex) { RpgHost.Log.Warning("catalog mowers: " + ex.Message); }
    }

    static int EnqueueEnumSide(string side, Type enumType)
    {
        // Structural (tunables-ssot.md T2) — network payload batch size, not balance.
        const int chunk = 400;
        var entries = new List<Dictionary<string, object>>(chunk);
        var n = 0;
        foreach (var (id, name) in EnumerateEnum(enumType))
        {
            if (string.Equals(name, "Nothing", StringComparison.Ordinal)) continue;
            entries.Add(new Dictionary<string, object>
            {
                ["type"] = id,
                ["typeName"] = name
            });
            n++;
            if (entries.Count >= chunk)
            {
                EmitCatalogChunk(side, entries);
                entries = new List<Dictionary<string, object>>(chunk);
            }
        }
        if (entries.Count > 0)
            EmitCatalogChunk(side, entries);
        return n;
    }

    static IEnumerable<(int id, string name)> EnumerateEnum(Type enumType)
    {
        var seen = new HashSet<int>();
        List<(int id, string name)>? il2 = null;
        try { il2 = Il2CppEnumValues(enumType); }
        catch { il2 = null; }
        if (il2 != null)
        {
            foreach (var v in il2)
            {
                if (!seen.Add(v.id)) continue;
                yield return v;
            }
        }

        if (seen.Count > 0) yield break;

        for (var i = -1; i <= 6000; i++)
        {
            object boxed;
            try { boxed = Enum.ToObject(enumType, i); }
            catch { continue; }
            string name;
            try { name = boxed.ToString() ?? ""; }
            catch { continue; }
            if (string.IsNullOrEmpty(name) || name == i.ToString()) continue;
            if (!seen.Add(i)) continue;
            yield return (i, name);
        }
    }

    static List<(int id, string name)> Il2CppEnumValues(Type enumType)
    {
        var list = new List<(int id, string name)>();
        var native = NativeClassPtr(enumType);
        if (native == IntPtr.Zero) return list;
        var il2Type = Il2CppSystem.Type.internal_from_handle(IL2CPP.il2cpp_class_get_type(native));
        var values = Il2CppSystem.Enum.GetValues(il2Type);
        if (values == null || values.Length == 0) return list;
        for (var i = 0; i < values.Length; i++)
        {
            object? raw;
            try { raw = values.GetValue(i); }
            catch { continue; }
            int id;
            try { id = Convert.ToInt32(raw); }
            catch
            {
                try { id = Convert.ToInt32(raw?.ToString()); }
                catch { continue; }
            }
            string name;
            try { name = Enum.ToObject(enumType, id).ToString() ?? id.ToString(); }
            catch { name = id.ToString(); }
            list.Add((id, name));
        }
        return list;
    }

    static IntPtr NativeClassPtr(Type enumType)
    {
        if (enumType == typeof(PlantType)) return Il2CppClassPointerStore<PlantType>.NativeClassPtr;
        if (enumType == typeof(ZombieType)) return Il2CppClassPointerStore<ZombieType>.NativeClassPtr;
        if (enumType == typeof(PetType)) return Il2CppClassPointerStore<PetType>.NativeClassPtr;
        if (enumType == typeof(GridItemType)) return Il2CppClassPointerStore<GridItemType>.NativeClassPtr;
        if (enumType == typeof(MowerType)) return Il2CppClassPointerStore<MowerType>.NativeClassPtr;
        return IntPtr.Zero;
    }

    public static void EnqueueRecipes()
    {
        if (_recipesDumped) return;
        try
        {
            PlantMixTreeManager.Init();
            var dict = PlantMixTreeManager.ChildToParents;
            if (dict == null)
            {
                RpgHost.Log.Warning("catalog recipes: ChildToParents is null (PlantMixTreeManager not initialized?)");
                return;
            }
            if (RpgHost.Client == null)
                RpgHost.Log.Warning("catalog recipes: RpgHost.Client is null — entries will be computed but never sent");
            // Structural (tunables-ssot.md T2) — network payload batch size, not balance.
            const int chunk = 200;
            var entries = new List<Dictionary<string, object>>(chunk);
            var pairCount = 0;
            var castFailures = 0;
            foreach (var pair in dict)
            {
                pairCount++;
                var list = pair.Value;
                if (list == null) continue;
                foreach (var info in list)
                {
                    int a = 0, b = 0, r = 0;
                    try { a = (int)info.ParentA; b = (int)info.ParentB; r = (int)info.Result; }
                    catch (Exception castEx)
                    {
                        castFailures++;
                        RpgHost.Log.Warning("catalog recipes: entry cast failed: " + castEx.Message);
                        continue;
                    }
                    entries.Add(new Dictionary<string, object>
                    {
                        ["parentA"] = a,
                        ["parentAName"] = GameDumps.EnumName(info.ParentA),
                        ["parentB"] = b,
                        ["parentBName"] = GameDumps.EnumName(info.ParentB),
                        ["result"] = r,
                        ["resultName"] = GameDumps.EnumName(info.Result)
                    });
                    if (entries.Count >= chunk)
                    {
                        RpgHost.Client?.Enqueue("catalog.recipes", new Dictionary<string, object> { ["entries"] = entries });
                        entries = new List<Dictionary<string, object>>(chunk);
                    }
                }
            }
            if (entries.Count > 0)
                RpgHost.Client?.Enqueue("catalog.recipes", new Dictionary<string, object> { ["entries"] = entries });
            RpgHost.Log.Info($"[catalog] recipes: {pairCount} parent groups, {castFailures} cast failures, client={(RpgHost.Client != null)}");
            _recipesDumped = true;
        }
        catch (Exception ex)
        {
            RpgHost.Log.Warning("catalog recipes: " + ex.Message);
        }
    }

    /// <summary>
    /// Sweep every PlantType/ZombieType and queue its almanac pedia text (name/info/cost/
    /// introduce) straight from AlmanacDataLoader's already-loaded dictionaries — no need to
    /// open the almanac UI or click each card. Window-only TMP fields (uiName/uiCost/...) are
    /// skipped since no AlmanacPlantWindow/AlmanacZombieWindow is open during the sweep.
    /// </summary>
    public static void EnqueueFullAlmanacText()
    {
        if (_almanacTextDumped) return;
        try
        {
            var n = 0;
            foreach (var (id, name) in EnumerateEnum(typeof(PlantType)))
            {
                if (string.Equals(name, "Nothing", StringComparison.Ordinal)) continue;
                AlmanacTextCapture.TryCapture("plant", id, (PlantType)id, null, includeWindowText: false);
                n++;
            }
            foreach (var (id, name) in EnumerateEnum(typeof(ZombieType)))
            {
                if (string.Equals(name, "Nothing", StringComparison.Ordinal)) continue;
                AlmanacTextCapture.TryCapture("zombie", id, null, (ZombieType)id, includeWindowText: false);
                n++;
            }
            _almanacTextDumped = true;
            RpgHost.Log.Info($"[almanac-text] full sweep queued {n} entries");
        }
        catch (Exception ex)
        {
            RpgHost.Log.Warning("almanac text sweep: " + ex.Message);
        }
    }

    /// <summary>
    /// Sweep every PlantType/ZombieType and queue its BASE stat row straight from the game's own static
    /// tables — <c>PlantDataManager.GetPlantOriginalData</c> and <c>ZombieDataManager.GetZombieData</c>.
    ///
    /// <para><b>Why this exists.</b> Every other stat path in this injector reads fields off a LIVE
    /// spawned entity (<see cref="EmitZombieDump"/> → <c>GameDumps.Zombie</c>, reading
    /// <c>z.theAttackDamage</c> / <c>z.theFirstArmorHealth</c>), so a type that was never spawned has no
    /// stats at all. Measured on the committed corpus dump: 64 of 677 plants and 18 of 227 zombies had
    /// observed stats, and plant armour had been captured zero times. The game has held the numbers
    /// statically the whole time; nothing was bound to them.</para>
    ///
    /// <para><b>Why `GetPlantOriginalData` and not `GetPlantData`.</b> The original is the UNMODIFIED
    /// base. `PlantDataManager` also carries a `PlantData_Modified` table that `ModifyPlant`/`ApplyModify`
    /// write into for the current run, so `GetPlantData` would hand back whatever this save has done to
    /// the plant. A corpus baseline must be the same on every machine, so it reads the original.
    /// `ZombieDataManager` exposes no such split — `GetZombieData` is the only accessor.</para>
    ///
    /// <para>Runs the same enum sweep as <see cref="EnqueueFullAlmanacText"/>, so it needs no board, no
    /// spawn and no almanac window. Every field read is individually guarded: this is a wide reflection
    /// surface over game types, and one unexpected field must cost that field, never the sweep.</para>
    /// </summary>
    public static void EnqueueBaseStats()
    {
        if (_baseStatsDumped) return;
        try
        {
            if (RpgHost.Client == null)
                RpgHost.Log.Warning("catalog basestats: RpgHost.Client is null — entries will be computed but never sent");

            // Structural (tunables-ssot.md T2) — network payload batch size, not balance. Matches
            // EnqueueRecipes' own chunk so both sweeps put the same pressure on the ingest queue.
            const int chunk = 200;

            var entries = new List<Dictionary<string, object>>(chunk);
            var plants = 0;
            var zombies = 0;
            var failures = 0;

            void Flush(bool force)
            {
                if (entries.Count == 0) return;
                if (!force && entries.Count < chunk) return;
                RpgHost.Client?.Enqueue("catalog.basestats", new Dictionary<string, object> { ["entries"] = entries });
                entries = new List<Dictionary<string, object>>(chunk);
            }

            foreach (var (id, name) in EnumerateEnum(typeof(PlantType)))
            {
                if (string.Equals(name, "Nothing", StringComparison.Ordinal)) continue;
                try
                {
                    var data = PlantDataManager.GetPlantOriginalData((PlantType)id);
                    if (data == null) continue;

                    var row = new Dictionary<string, object>
                    {
                        ["side"] = "plant",
                        ["typeId"] = id,
                        ["typeName"] = name
                    };
                    // `*Base` key names deliberately match what `ResolveCombatBaseline` already reads out
                    // of a spawn sample, so the two sources stay interchangeable downstream.
                    GameDumps.TryInto(row, "hpBase", () => data.maxHealth);
                    GameDumps.TryInto(row, "attackBase", () => data.attackDamage);
                    GameDumps.TryInto(row, "attackIntervalMs", () => data.attackInterval);
                    GameDumps.TryInto(row, "produceIntervalMs", () => data.produceInterval);
                    GameDumps.TryInto(row, "cost", () => data.cost);
                    GameDumps.TryInto(row, "cd", () => data.cd);

                    entries.Add(row);
                    plants++;
                    Flush(force: false);
                }
                catch (Exception ex)
                {
                    failures++;
                    if (failures <= 3) RpgHost.Log.Warning($"catalog basestats: plant {id} ({name}): {ex.Message}");
                }
            }

            foreach (var (id, name) in EnumerateEnum(typeof(ZombieType)))
            {
                if (string.Equals(name, "Nothing", StringComparison.Ordinal)) continue;
                try
                {
                    var data = ZombieDataManager.GetZombieData((ZombieType)id);
                    if (data == null) continue;

                    var row = new Dictionary<string, object>
                    {
                        ["side"] = "zombie",
                        ["typeId"] = id,
                        ["typeName"] = name
                    };
                    GameDumps.TryInto(row, "hpBase", () => data.theMaxHealth);
                    GameDumps.TryInto(row, "attackBase", () => data.theAttackDamage);
                    GameDumps.TryInto(row, "armorBase", () => data.armor);
                    GameDumps.TryInto(row, "armorMaxBase", () => data.theFirstArmorMaxHealth);
                    GameDumps.TryInto(row, "armor2MaxBase", () => data.theSecondArmorMaxHealth);
                    GameDumps.TryInto(row, "cost", () => data.cost);
                    GameDumps.TryInto(row, "cd", () => data.cd);
                    // The game's own tier signal: which wave tier may summon this zombie, and how often.
                    // Authored by the game's designers, so it needs no derivation on our side.
                    GameDumps.TryInto(row, "summonLevel", () => data.summonLevel);
                    GameDumps.TryInto(row, "summonWeight", () => data.summonWeight);

                    entries.Add(row);
                    zombies++;
                    Flush(force: false);
                }
                catch (Exception ex)
                {
                    failures++;
                    if (failures <= 6) RpgHost.Log.Warning($"catalog basestats: zombie {id} ({name}): {ex.Message}");
                }
            }

            Flush(force: true);
            _baseStatsDumped = true;
            RpgHost.Log.Info(
                $"[basestats] swept {plants} plants, {zombies} zombies, {failures} failures, client={(RpgHost.Client != null)}");
        }
        catch (Exception ex)
        {
            RpgHost.Log.Warning("catalog basestats sweep: " + ex.Message);
        }
    }

    static void EmitCatalogChunk(string side, List<Dictionary<string, object>> entries)
    {
        RpgHost.Client?.Enqueue("catalog.types", new Dictionary<string, object>
        {
            ["side"] = side,
            ["entries"] = entries
        });
    }

    public static void PollBoard()
    {
        var board = Board;
        if (board == null) return;
        try
        {
            var wave = board.theWave;
            if (wave != LastWave)
            {
                LastWave = wave;
                var payload = GameDumps.LiveBoard(board);
                payload["wave"] = wave;
                payload["maxWave"] = board.theMaxWave;
                Emit("wave.change", payload);
            }
        }
        catch { }

        try
        {
            var sun = board.theSun;
            var money = board.theMoney;
            var points = board.thePoints;
            if (sun != _lastSun || money != _lastMoney || !Mathf.Approximately(points, _lastPoints))
            {
                _lastSun = sun;
                _lastMoney = money;
                _lastPoints = points;
                var eco = GameDumps.LiveBoard(board);
                CheatState.TagProbe(eco);
                Emit("board.economy", eco);
            }
        }
        catch { }

        try
        {
            var name = LevelName();
            if (!string.IsNullOrWhiteSpace(name) && name != "Unknown" && name != _lastLevelName)
            {
                _lastLevelName = name;
                Emit("level.name", new Dictionary<string, object> { ["levelName"] = name });
            }
        }
        catch { }

        try
        {
            var arr = board.mowerArray;
            if (arr != null)
            {
                foreach (var m in arr)
                {
                    if (m == null || !m.started) continue;
                    if (!MowerStarted.Add(m.Pointer)) continue;
                    Emit("mower.start", new Dictionary<string, object>
                    {
                        ["ptr"] = GameDumps.Ptr(m),
                        ["type"] = (int)m.theMowerType,
                        ["typeName"] = m.theMowerType.ToString(),
                        ["polled"] = true
                    });
                }
            }
        }
        catch { }

        if (!_conveyDumped)
        {
            try
            {
                var cm = ConveyManager.Instance;
                if (cm != null)
                {
                    var pool = cm.GetCardPool();
                    var types = new List<Dictionary<string, object>>();
                    if (pool != null)
                    {
                        foreach (var item in pool)
                        {
                            try
                            {
                                var tid = Convert.ToInt32(item);
                                var tname = item.ToString() ?? "";
                                SpawnCatalog.Note("plant", tid, tname, "convey.pool");
                                types.Add(new Dictionary<string, object>
                                {
                                    ["type"] = tid,
                                    ["typeName"] = tname
                                });
                            }
                            catch { }
                        }
                    }
                    Emit("convey.pool", new Dictionary<string, object> { ["types"] = types });
                    _conveyDumped = true;
                }
            }
            catch { _conveyDumped = true; }
        }
    }

    internal static string LevelName()
    {
        try
        {
            if (InGameUI.Instance != null)
            {
                var ui = InGameUI.Instance;
                if (ui.LevelName1 != null && !string.IsNullOrWhiteSpace(ui.LevelName1.text)) return ui.LevelName1.text;
                if (ui.LevelName2 != null && !string.IsNullOrWhiteSpace(ui.LevelName2.text)) return ui.LevelName2.text;
                if (ui.LevelName3 != null && !string.IsNullOrWhiteSpace(ui.LevelName3.text)) return ui.LevelName3.text;
            }
        }
        catch { }
        return "Unknown";
    }

    [HarmonyPatch(typeof(Board), nameof(Board.Awake))]
    public static class BoardAwake
    {
        public static void Postfix(Board __instance)
        {
            Board = __instance;
            ClearMatch();
            MatchKey = Guid.NewGuid().ToString();
            _runKey = MatchKey;
            try { LastWave = __instance.theWave; }
            catch { LastWave = -1; }
            Dictionary<string, object> modifiers;
            try { modifiers = GameDumps.BoardConfig(__instance.config); }
            catch { modifiers = new Dictionary<string, object>(); }
            var start = new Dictionary<string, object>
            {
                ["matchKey"] = MatchKey!,
                ["levelName"] = LevelName(),
                ["modifiers"] = modifiers
            };
            // commander-roster EP3.4 (spec-lawn-commander-seat.md): the frozen leader's commander id,
            // ADDITIVE on MatchStarted. Read from the same session cache MatchHost freezes at this very
            // moment (so the value cannot disagree with the holder), and written only when there is a
            // leader: an ABSENT field means "no seat", which is why an empty string is never sent.
            try
            {
                var leading = Commanders.MatchCommanderSnapshotSource.BuildFromSessionCache();
                if (!string.IsNullOrWhiteSpace(leading.LeadingCommanderId))
                    start["leadingCommanderId"] = leading.LeadingCommanderId;
            }
            catch { /* a capture-side failure never blocks the match */ }
            try { start["boardLevel"] = GameAPP.theBoardLevel; } catch { }
            try { start["levelType"] = GameAPP.theBoardType.ToString(); } catch { }
            try { start["theTotalNumOfZombie"] = __instance.theTotalNumOfZombie; } catch { }
            Emit("board.start", start);
            try { Hud.OverlaySwitch.OnMatchStart(); } catch { }
            Emit("board.modifiers", modifiers);
            try { InjectorBootstrap.EnsureCombatHitPatches(); } catch (Exception ex) { RpgHost.Log.Warning("EnsureCombatHitPatches: " + ex.Message); }
            try
            {
                if (CheatState.BoardConfigLocked)
                    CheatActions.ApplyBoardConfig();
                else
                    CheatActions.LoadBoardConfigIntoCheats();
            }
            catch { }
            if (CatalogPlantCount == 0 && !_catalogRetried)
            {
                _catalogRetried = true;
                RequestTypeCatalog();
            }
        }
    }

    [HarmonyPatch(typeof(Board), nameof(Board.OnDestroy))]
    public static class BoardDestroy
    {
        public static void Postfix(Board __instance)
        {
            if (Board != null && Board.Pointer == __instance.Pointer)
                Board = null;
        }
    }

    [HarmonyPatch(typeof(Board), nameof(Board.Die))]
    public static class BoardDie
    {
        public static void Postfix()
        {
            // v2 lifecycle barrier: drain everything before board.end triggers ClearAll (SSOT §A1).
            try { Effects.EventDrainHost.FlushAllAndReset(); } catch { }
            Dictionary<string, object> summary;
            try { summary = GameDumps.BoardStats(Board, Board?.boardStatistics); }
            catch { summary = new Dictionary<string, object>(); }
            Emit("board.end", new Dictionary<string, object>
            {
                ["levelName"] = LevelName(),
                ["summary"] = summary
            });
            try { Hud.OverlaySwitch.OnMatchEnd(); } catch { }
            MatchKey = null;
            _runKey = null;
            ClearMatch();
            try { Fx.VfxDirector.ClearAll(); } catch { }
        }
    }

    [HarmonyPatch(typeof(BoardStatistics), nameof(BoardStatistics.GameOver))]
    public static class StatsGameOver
    {
        public static void Postfix(BoardStatistics __instance, GameResult result)
        {
            if (result == GameResult.None) return;
            var name = result.ToString().ToLowerInvariant();
            Emit("match.result", new Dictionary<string, object> { ["result"] = name });
            Emit("board.snapshot", GameDumps.BoardStats(Board, __instance));
        }
    }

    [HarmonyPatch(typeof(GameLose), nameof(GameLose.HandleGameLose))]
    public static class GameLoseHook
    {
        public static void Postfix()
        {
            Emit("match.lose", new Dictionary<string, object>());
        }
    }

    [HarmonyPatch(typeof(BoardVictory), nameof(BoardVictory.Win))]
    public static class BoardVictoryWin
    {
        public static void Postfix()
        {
            Emit("match.win", new Dictionary<string, object>());
        }
    }

    [HarmonyPatch(typeof(Plant), nameof(Plant.Start))]
    public static class PlantStart
    {
        public static void Postfix(Plant __instance)
        {
            if (__instance == null) return;
            // L-N16: a real spawn clears a dead mark left by an earlier entity at this native address.
            Effects.EventDrainHost.MarkSpawned(__instance.Pointer);
            Effects.InjectorEntityRegistry.Add(__instance);
            Effects.InjectorBoardSnapshot.Invalidate();
            if (!Ready()) return;
            if (__instance.thePlantType == PlantType.Nothing) return;
            ApplyPlant(__instance, "start");
        }
    }

    [HarmonyPatch(typeof(Plant), nameof(Plant.Die))]
    public static class PlantDie
    {
        public static void Prefix(Plant __instance)
        {
            if (__instance == null) return;
            try
            {
                if (TryGetDamageSource(__instance.Pointer, out var source))
                    CaptureFatalKiller(__instance.Pointer, source);
            }
            catch { }
        }

        public static void Postfix(Plant __instance, Plant.DieReason reason)
        {
            if (__instance == null) return;
            var ptr = __instance.Pointer;
            // lawn-hit-entry (T9c): mark dead BEFORE the flush — a hit already in flight for this
            // ptr (e.g. a second projectile landing the same frame) must be refused at record
            // time, not merely left to drain late. Marking early never blocks the flush below from
            // delivering this ptr's own already-pending records (the liveness guard only gates
            // NEW records, never drain-time processing of existing ones).
            Effects.EventDrainHost.MarkDead(ptr);
            // v2 barrier: pending hit records for this plant drain before its grants withdraw —
            // same position as before this fix. `flushedNow` is false only when this death itself
            // fired from inside an active drain pass (T9c); in that case ForgetEntity below must
            // wait, or this ptr's still-pending records could drain AFTER the grant withdraws.
            var flushedNow = true;
            try { flushedNow = Effects.EventDrainHost.FlushForPtr(ptr); } catch { }
            try { Effects.InjectorEntityRegistry.Remove(ptr); } catch { }
            Effects.InjectorBoardSnapshot.Invalidate();
            // OnDeath must see entity grants; ForgetEntity withdraws after Emit.
            var diePayload = new Dictionary<string, object>
            {
                ["type"] = (int)__instance.thePlantType,
                ["typeName"] = GameDumps.EnumName(__instance.thePlantType),
                ["ptr"] = GameDumps.Ptr(__instance),
                ["reason"] = (int)reason,
                ["reasonName"] = GameDumps.EnumName(reason),
                ["lifecycleOccurrence"] = Interlocked.Increment(ref _lifecycleOccurrence),
                // live-probe Task 18: game | debug | cheat — rides onto the PlantLost fact.
                ["spawnOrigin"] = Match.SpawnOriginTags.TakeOnDeath(ptr)
            };
            AddFatalKiller(diePayload, TakeFatalKiller(ptr));
            Emit("plant.die", diePayload);
            if (flushedNow) ForgetEntity(ptr);
            else Effects.EventDrainHost.DeferForget(ptr, () => ForgetEntity(ptr));
        }
    }

    // v5 hotfix (v5-600z-war finding): a full StatSystem.Resolve ran per TakeDamage —
    // 2,100+/s under war load, a top allocator. The defense scale only changes with the
    // cheat document or pvz-mod revisions, so resolve once per revision pair per side.
    static long _dmgScaleDocRev = long.MinValue;
    static long _dmgScalePvzRev = long.MinValue;
    static bool _dmgScaleActive;
    static float _plantDefPct = 1f;
    static long _plantDefFlat;
    static float _zombieDefPct = 1f;
    static long _zombieDefFlat;

    static void EnsureDamageScaleCache(StatsConfig s)
    {
        if (_dmgScaleDocRev == CheatState.DocumentRevision && _dmgScalePvzRev == CheatState.PvzStatsRevision)
            return;
        _dmgScaleDocRev = CheatState.DocumentRevision;
        _dmgScalePvzRev = CheatState.PvzStatsRevision;
        var hasPvz = CheatState.HasPvzStatsMods();
        _dmgScaleActive = s.ApplyStats || hasPvz;
        if (!_dmgScaleActive) return;
        var baseline = new EntityBaseline { Hp = 1, MaxHp = 1, Atk = 1 };
        // Sole Hot gate (ADR 2026-09-07): AppliedCombat from ActorHub, never StatSystem-only.
        var pf = CheatState.ActorHub.Resolve(CheatState.Stats.Contexts.ForPlant(
            "dmg", baseline, cheatScale: s, applyStats: true, pvzStatsMods: CheatState.PvzStatsMods)).AppliedCombat;
        _plantDefPct = pf.DefensePercent;
        _plantDefFlat = pf.DefenseFlat;
        var zf = CheatState.ActorHub.Resolve(CheatState.Stats.Contexts.ForZombie(
            "dmg", baseline, cheatScale: s, applyStats: true, pvzStatsMods: CheatState.PvzStatsMods)).AppliedCombat;
        _zombieDefPct = zf.DefensePercent;
        _zombieDefFlat = zf.DefenseFlat;
    }

    [HarmonyPatch(typeof(Plant), nameof(Plant.TakeDamage))]
    public static class PlantTakeDamage
    {
        // Plant signature: (int damage, IDamageMaker damageFrom, DamageType damageType, PlantType reportType, bool fix)
        public static void Prefix(Plant __instance, ref int damage, IDamageMaker damageFrom, DamageType damageType, PlantType reportType, bool fix)  // overflow-bounded: PvZ native signature - Harmony binds this int; widened to long on entry to the RPG layer
        {
            BeginDamageSource(__instance?.Pointer ?? IntPtr.Zero, damageFrom);
            using var _perf = PerfProbe.Measure(PerfSection.TakeDamagePrefix);
            if (CheatState.On("P-GOD")) { damage = 0; return; }
            if (OverlayApplyGuard.IsActive) return;
            var s = CheatState.EffectiveStats();
            var before = damage;
            EnsureDamageScaleCache(s);
            if (_dmgScaleActive)
                // ScaleIncoming now works in long (defense flat rides the same RPG-scaled channel
                // as Atk/Hp); damage is Harmony's own int parameter on Plant.TakeDamage and cannot
                // widen, so the result is clamped back at this boundary — never a silent narrow.
                // Routed through the reporting wrapper so a saturation is a proof event, not a
                // silent clamp (injector write-path honesty, BCU8.2).
                damage = EntityStatWriter.ClampToInt32Reporting(
                    StatMath.ScaleIncoming(damage, _plantDefPct, _plantDefFlat),
                    "plant.damage", "GameHooks.PlantTakeDamage");
            // lawn-combat-observer (Task 0, lawn-combat-wire): unconditional vanilla-hit capture, no
            // flag, no session gate — runs regardless of what the telemetry/record branches below do,
            // so it never perturbs them. See LawnCombatObserverBridge's own doc comment.
            Effects.LawnCombatObserverBridge.RecordVanillaHit("plant", __instance, damageFrom, damage);
            // v2 record path (Task 9) — mirrors ZombieTakeDamage; melee bites consume the
            // AttackPlant prefix's pending pair id inside TryRecordTaken.
            //
            // lawn-hit-entry (T9a) — caller-side ordering audit: `v2Active` (Enabled && not a debug
            // session) now decides FIRST, matching the already-correct RecordOrEmitMeleeAttackPlant
            // site below. Previously `telemetry` (which folds in LogDamage, a normal non-session
            // toggle, not just SessionActive) won the else-if BEFORE the recorder was even
            // consulted — so leaving LogDamage on outside a debug session silently routed every
            // hit down the legacy path forever, bypassing D8/D9's dedupe/never-drop rules entirely
            // (see LawnCombatObserver's own class doc, which already flagged this exact defect).
            // SessionActive alone still forces legacy fidelity (gate 2 — "must not be fixed").
            var sessionActive = DebugRuntime.SessionActive;
            var logDamage = s.LogDamage || (RpgHost.Client?.Stats.LogDamage ?? false);
            var v2Active = Effects.EventDrainHost.Enabled && !sessionActive;
            var instakillShaped = IsInstakillShapedDamageType(damageType);
            if (v2Active)
            {
                // L-N27: a living plant at a dead-marked ptr means a stale mark is refusing its RPG hits.
                try { if (__instance.thePlantHealth > 0) Effects.EventDrainHost.Liveness.NoteHitOnLivingEntity(__instance.Pointer); } catch { }
                var pType = 0;
                try { pType = (int)__instance.thePlantType; } catch { }
                var actorPtr = IntPtr.Zero;
                try { if (damageFrom is Il2CppObjectBase dfObj) actorPtr = dfObj.Pointer; } catch { }
                Effects.EventDrainHost.TryRecordDealtFromBullet(
                    Core.Events.GameEventSide.Plant, damageFrom as Il2CppObjectBase, __instance.Pointer, pType, damage,
                    out var wasBullet, instakillShaped);
                if (!wasBullet)
                {
                    // Multi-target melee with no target param of its own (AttackPlants-shaped) —
                    // the ambient attacker bracketed around that call fills in what damageFrom can't.
                    Effects.EventDrainHost.TryRecordAmbientMeleeDealt(
                        Core.Events.GameEventSide.Plant, __instance.Pointer, pType, damage, instakillShaped);
                    Effects.EventDrainHost.TryRecordTaken(
                        Core.Events.GameEventSide.Plant, __instance.Pointer, pType, actorPtr, damage);
                }
            }
            else if (sessionActive || logDamage)
            {
                var payload = new Dictionary<string, object>
                {
                    ["ptr"] = GameDumps.Ptr(__instance),
                    ["damage"] = damage,
                    ["before"] = before,
                    ["after"] = damage,
                    ["path"] = "take",
                    ["reportType"] = (int)reportType,
                    ["damageType"] = (int)damageType
                };
                TryStampDamageFrom(payload, damageFrom);
                DebugRuntime.Stamp(payload);
                Emit("plant.damage", payload);
            }
            else if (Effects.EffectRuntime.HasOnDamageTakenGrant())
            {
                var payload = new Dictionary<string, object>
                {
                    ["ptr"] = GameDumps.Ptr(__instance),
                    ["damage"] = damage,
                    ["path"] = "take"
                };
                TryStampDamageFrom(payload, damageFrom);
                Emit("plant.damage", payload);
            }
            // Hit* Harmony skipped — drive on-hit arms + combat.hit identity from TakeDamage.
            try { DebugRuntime.OnCombatHit("plant", null, __instance); } catch (Exception ex) { CheatState.Error("debug onhit plant: " + ex.Message); }
            if (!v2Active)
                TryEmitCombatHitFromBullet("plant", damageFrom, plantTarget: __instance, zombieTarget: null, fallbackDamage: damage);
        }

        public static void Postfix(Plant __instance, IDamageMaker damageFrom)
        {
            EndDamageSource(__instance?.Pointer ?? IntPtr.Zero);
        }
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.Start))]
    public static class ZombieStart
    {
        public static void Postfix(Zombie __instance)
        {
            if (__instance == null) return;
            NoteZombieSpawned(__instance.Pointer);
            Effects.InjectorEntityRegistry.Add(__instance);
            Effects.InjectorBoardSnapshot.Invalidate();
            if (!Ready() || __instance.theZombieType == ZombieType.Nothing) return;
            ApplyZombie(__instance, "start");
        }
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.InitHealth))]
    public static class ZombieInitHealth
    {
        public static void Postfix(Zombie __instance)
        {
            if (__instance != null) NoteZombieSpawned(__instance.Pointer);
            Effects.InjectorEntityRegistry.Add(__instance);
            Effects.InjectorBoardSnapshot.Invalidate();
            if (!Ready() || __instance == null || __instance.theZombieType == ZombieType.Nothing) return;
            ApplyZombie(__instance, "initHealth");
        }
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.Die))]
    public static class ZombieDie
    {
        public static void Prefix(Zombie __instance, int reason)
        {
            try
            {
                if (__instance != null && TryGetDamageSource(__instance.Pointer, out var source))
                    CaptureFatalKiller(__instance.Pointer, source);
            }
            catch { }
            NoteZombieDead(__instance, reason);
        }
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.DestoryZombie))]
    public static class ZombieDestroy
    {
        public static void Prefix(Zombie __instance)
        {
            NoteZombieDead(__instance, -1);
        }
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.TakeDamage))]
    public static class ZombieTakeDamage
    {
        public static void Prefix(Zombie __instance, ref int theDamage, IDamageMaker damageFrom, DamageType theDamageType, PlantType reportType, bool fix)  // overflow-bounded: PvZ native signature - Harmony binds this int; widened to long on entry to the RPG layer
        {
            if (Effects.FsmTrace.Enabled)
                CheatState.Note($"fsm-trace ZombieTakeDamage.Prefix RAW theDamage={theDamage} zombiePtr={__instance?.Pointer:X} zGod={CheatState.On("Z-GOD")} frame={UnityEngine.Time.frameCount} hp={(__instance == null ? 0 : Bridges.ZombieCombatFields.GetHp(__instance))} from={DamageFromTrace(damageFrom)}");
            BeginDamageSource(__instance?.Pointer ?? IntPtr.Zero, damageFrom);
            using var _perf = PerfProbe.Measure(PerfSection.TakeDamagePrefix);
            if (CheatState.On("Z-GOD")) { theDamage = 0; return; }
            if (OverlayApplyGuard.IsActive) return;
            var s = CheatState.EffectiveStats();
            var before = theDamage;
            EnsureDamageScaleCache(s);
            if (_dmgScaleActive)
                // Same boundary as PlantTakeDamage — theDamage is Harmony's own int on
                // Zombie.TakeDamage; clamp back rather than narrow silently, through the reporting
                // wrapper (injector write-path honesty, BCU8.2).
                theDamage = EntityStatWriter.ClampToInt32Reporting(
                    StatMath.ScaleIncoming(theDamage, _zombieDefPct, _zombieDefFlat),
                    "zombie.damage", "GameHooks.ZombieTakeDamage");
            // lawn-combat-observer (Task 0, lawn-combat-wire): unconditional vanilla-hit capture, no
            // flag, no session gate — runs regardless of what the telemetry/record branches below do,
            // so it never perturbs them. See LawnCombatObserverBridge's own doc comment.
            Effects.LawnCombatObserverBridge.RecordVanillaHit("zombie", __instance, damageFrom, theDamage);
            // v2 record path (Task 9): telemetry/session keeps the legacy dict path for
            // fidelity; otherwise grants get compact records via the drain host.
            //
            // lawn-hit-entry (T9a) — caller-side ordering audit: see the identical comment on
            // PlantTakeDamage.Prefix. `v2Active` decides first; SessionActive alone still forces
            // legacy fidelity, but a plain LogDamage toggle no longer preempts the recorder.
            var sessionActive = DebugRuntime.SessionActive;
            var logDamage = s.LogDamage || (RpgHost.Client?.Stats.LogDamage ?? false);
            var v2Active = Effects.EventDrainHost.Enabled && !sessionActive;
            var instakillShaped = IsInstakillShapedDamageType(theDamageType);
            if (v2Active)
            {
                // L-N27: a living zombie at a dead-marked ptr means a stale mark is refusing its RPG hits.
                try { if (Bridges.ZombieCombatFields.GetHp(__instance) > 0) Effects.EventDrainHost.Liveness.NoteHitOnLivingEntity(__instance.Pointer); } catch { }
                var zType = 0;
                try { zType = (int)__instance.theZombieType; } catch { }
                var actorPtr = IntPtr.Zero;
                try { if (damageFrom is Il2CppObjectBase dfObj) actorPtr = dfObj.Pointer; } catch { }
                // Bullet damage suppresses the taken record at source, matching v1 policy —
                // including on ring overflow (I2: a dropped bullet-dealt must not turn into
                // a spurious taken).
                Effects.EventDrainHost.TryRecordDealtFromBullet(
                    Core.Events.GameEventSide.Zombie, damageFrom as Il2CppObjectBase, __instance.Pointer, zType, theDamage,
                    out var wasBullet, instakillShaped);
                if (!wasBullet)
                {
                    // L-N36: a plant that damages with itself as damageFrom (FumeShroom) gets its dealt record here;
                    // a plant area attack that already recorded this victim this frame is skipped inside.
                    if (!Effects.EventDrainHost.TryRecordDirectPlantDealt(
                            Core.Events.GameEventSide.Zombie, damageFrom as Il2CppObjectBase, __instance.Pointer, zType, theDamage,
                            instakillShaped))
                        // Symmetric with the plant-side branch above. Shulkflower/WaterShulk.AttackEffect records its
                        // exact victims directly; BambooDragon's breath (damageFrom null) is attributed here through the
                        // ambient bracket around BambooDragon.FixedUpdate (L-N39).
                        Effects.EventDrainHost.TryRecordAmbientMeleeDealt(
                            Core.Events.GameEventSide.Zombie, __instance.Pointer, zType, theDamage, instakillShaped);
                    Effects.EventDrainHost.TryRecordTaken(
                        Core.Events.GameEventSide.Zombie, __instance.Pointer, zType, actorPtr, theDamage);
                }
            }
            else if (sessionActive || logDamage)
            {
                var payload = new Dictionary<string, object>
                {
                    ["ptr"] = GameDumps.Ptr(__instance),
                    ["damage"] = theDamage,
                    ["before"] = before,
                    ["after"] = theDamage,
                    ["path"] = "take",
                    ["reportType"] = (int)reportType,
                    ["damageType"] = (int)theDamageType
                };
                TryStampDamageFrom(payload, damageFrom);
                DebugRuntime.Stamp(payload);
                Emit("zombie.damage", payload);
            }
            else if (Effects.EffectRuntime.HasOnDamageTakenGrant())
            {
                // v2 host off: preserve the audit-4c.2 fix via the legacy path.
                var payload = new Dictionary<string, object>
                {
                    ["ptr"] = GameDumps.Ptr(__instance),
                    ["damage"] = theDamage,
                    ["path"] = "take"
                };
                TryStampDamageFrom(payload, damageFrom);
                Emit("zombie.damage", payload);
            }
            // Hit* Harmony skipped — drive on-hit arms + combat.hit identity from TakeDamage.
            try { DebugRuntime.OnCombatHit("zombie", __instance, null); } catch (Exception ex) { CheatState.Error("debug onhit zombie: " + ex.Message); }
            if (!v2Active)
                TryEmitCombatHitFromBullet("zombie", damageFrom, plantTarget: null, zombieTarget: __instance, fallbackDamage: theDamage);
        }

        public static void Postfix(Zombie __instance, IDamageMaker damageFrom)
        {
            EndDamageSource(__instance?.Pointer ?? IntPtr.Zero);
        }
    }

    /// <summary>
    /// lawn-hit-entry (T9c): "instakill-shaped" is DEFINED via the engine's own
    /// <c>DamageType</c> enum, never a magnitude threshold (spec-lawn-hit-entry.md "Lifecycle
    /// correctness" — a threshold is a magic number a balance pass will eventually cross
    /// legitimately; a 1,000,000-damage lawnmower event was observed live). These four are the
    /// lawnmower/board-wipe family (<c>DamageType</c> has 19 members total; the rest are ordinary
    /// combat damage). Defensive try/catch matches this file's own convention for every other
    /// game-enum read — an unrecognised or inaccessible value reads as "not instakill-shaped"
    /// rather than throwing across a Harmony hook.
    /// </summary>
    static bool IsInstakillShapedDamageType(DamageType t)
    {
        try
        {
            return t is DamageType.Squash or DamageType.MaxDamage or DamageType.Crash or DamageType.RealDamage;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>lawn-combat-wire L-N39: the damage source's Il2Cpp class and ptr for the env-gated FSM trace, so a hit whose
    /// attacker the drain cannot name (neither a Bullet nor a Plant) shows what actually dealt it.</summary>
    static string DamageFromTrace(IDamageMaker? damageFrom)
    {
        if (damageFrom is not Il2CppObjectBase obj) return "null";
        var cls = "?";
        try { cls = obj.TryCast<Il2CppSystem.Object>()?.GetIl2CppType().Name ?? "?"; } catch { }
        return $"{cls}:{obj.Pointer:X}";
    }

    static void TryStampDamageFrom(Dictionary<string, object> payload, IDamageMaker? damageFrom)
    {
        if (damageFrom == null)
        {
            payload["damageFromNull"] = true;
            return;
        }
        try
        {
            if (damageFrom is not Il2CppObjectBase obj) return;
            payload["damageFrom"] = GameDumps.Ptr(obj);
            try
            {
                var sysObj = obj.TryCast<Il2CppSystem.Object>();
                if (sysObj != null) payload["damageFromClass"] = sysObj.GetIl2CppType().Name;
            }
            catch { }
            try { payload["damageFromIsBullet"] = obj.TryCast<Bullet>() != null; } catch { payload["damageFromIsBullet"] = false; }
            try { payload["damageFromIsZombie"] = obj.TryCast<Zombie>() != null; } catch { payload["damageFromIsZombie"] = false; }
            try { payload["damageFromIsPlant"] = obj.TryCast<Plant>() != null; } catch { payload["damageFromIsPlant"] = false; }
        }
        catch
        {
            /* optional enrich */
        }
    }

    /// <summary>
    /// Safe <c>combat.hit</c> identity without base <c>Bullet.Hit*</c> Harmony.
    /// Bullet attackers (pea→zombie, projectile→plant): cast <see cref="Bullet"/>.
    /// Melee bite (zombie→plant): cast <see cref="Zombie"/> — no fake bullet fields.
    /// </summary>
    static void TryEmitCombatHitFromBullet(
        string side,
        IDamageMaker? damageFrom,
        Plant? plantTarget,
        Zombie? zombieTarget,
        long fallbackDamage)
    {
        if (!Effects.EffectRuntime.ShouldEmitCombatHit()) return;
        if (damageFrom == null) return;
        try
        {
            var obj = damageFrom as Il2CppObjectBase;
            if (obj == null) return;

            Bullet? bullet = null;
            try { bullet = obj.TryCast<Bullet>(); } catch { }
            if (bullet != null)
            {
                var payload = new Dictionary<string, object>
                {
                    ["side"] = side,
                    ["source"] = "takeDamage",
                    ["attackerKind"] = "bullet",
                    ["bulletPtr"] = GameDumps.Ptr(bullet),
                    // TakeDamage amount is SSOT (pea follows plant ATK); Bullet.Damage may differ.
                    ["damage"] = fallbackDamage
                };
                try { payload["bulletType"] = (int)bullet.theBulletType; } catch { }
                try { payload["bulletDamage"] = bullet.Damage; } catch { }
                try
                {
                    payload["fromType"] = (int)bullet.fromType;
                    payload["fromTypeName"] = GameDumps.EnumName(bullet.fromType);
                }
                catch { }
                StampCombatHitTarget(payload, side, plantTarget, zombieTarget);
                DebugRuntime.Stamp(payload);
                Emit("combat.hit", payload);
                return;
            }

            // Plant-side melee combat.hit is AttackPlant-only (avoid double with TakeDamage).
        }
        catch (Exception ex) { CheatState.Error("combat.hit take: " + ex.Message); }
    }

    static void StampCombatHitTarget(
        Dictionary<string, object> payload,
        string side,
        Plant? plantTarget,
        Zombie? zombieTarget)
    {
        if (side == "zombie" && zombieTarget != null)
        {
            payload["targetPtr"] = GameDumps.Ptr(zombieTarget);
            try { payload["targetType"] = (int)zombieTarget.theZombieType; } catch { }
        }
        else if (side == "plant" && plantTarget != null)
        {
            payload["targetPtr"] = GameDumps.Ptr(plantTarget);
            try { payload["targetType"] = (int)plantTarget.thePlantType; } catch { }
        }
    }

    [HarmonyPatch(typeof(CreateMower), nameof(CreateMower.SetMower))]
    public static class MowerPlace
    {
        public static void Postfix(MowerType mowerType, float x, int row, Mower __result)
        {
            if (__result == null) return;
            Emit("mower.place", new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(__result),
                ["type"] = (int)mowerType,
                ["typeName"] = mowerType.ToString(),
                ["row"] = row
            });
        }
    }

    /// <summary>
    /// Melee bite identity: plant TakeDamage often passes the plant itself as <c>damageFrom</c>,
    /// so Bullet/Zombie casts miss. <see cref="Zombie.AttackPlant"/> has the real attacker (the
    /// receiving instance itself — reliable regardless of what <c>damageFrom</c> carries).
    /// Applied deferred with HitLand — not at chainloader.
    ///
    /// Shared by every override of this method (<see cref="ZombieAttackPlant"/>,
    /// <see cref="QingZombieAttackPlant"/>): Harmony patches the EXACT method it is given, and an
    /// override has its own native body, so a patch on the base <c>Zombie.AttackPlant</c> never runs
    /// for a type that overrides it (verified against the shipped Il2CppInterop proxy —
    /// <c>QingZombie.AttackPlant(Plant)</c> is virtual, newslot=false: a real, separate override, not
    /// a variant of the base method). Each override type needs its own Harmony patch class; this
    /// function is what they share so the recording/emit logic itself is not duplicated per type.
    /// </summary>
    static void RecordOrEmitMeleeAttackPlant(Zombie attacker, Plant plant, string logTag)
    {
        if (attacker == null || plant == null) return;
        try
        {
            if (attacker.Pointer == IntPtr.Zero || plant.Pointer == IntPtr.Zero) return;
        }
        catch { return; }
        // v2 record path (Task 9): melee dealt as a compact record with a pair id that the
        // plant's TakeDamage taken record consumes.
        if (Effects.EventDrainHost.Enabled && !DebugRuntime.SessionActive)
        {
            var dmgRec = 0;
            try { dmgRec = attacker.theAttackDamage; } catch { }
            var zTypeRec = 0;
            try { zTypeRec = (int)attacker.theZombieType; } catch { }
            var pTypeRec = 0;
            try { pTypeRec = (int)plant.thePlantType; } catch { }
            Effects.EventDrainHost.TryRecordMeleeDealt(
                Core.Events.GameEventSide.Plant, attacker.Pointer, zTypeRec, plant.Pointer, pTypeRec, dmgRec);
            return;
        }
        if (!Effects.EffectRuntime.ShouldEmitCombatHit()) return;
        try
        {
            var damage = 0;
            try { damage = attacker.theAttackDamage; } catch { }
            var payload = new Dictionary<string, object>
            {
                ["side"] = "plant",
                ["source"] = "attackPlant",
                ["attackerKind"] = "zombie",
                ["attackerPtr"] = GameDumps.Ptr(attacker),
                ["damage"] = damage
            };
            try
            {
                payload["fromType"] = (int)attacker.theZombieType;
                payload["fromTypeName"] = GameDumps.EnumName(attacker.theZombieType);
            }
            catch { }
            StampCombatHitTarget(payload, "plant", plant, null);
            DebugRuntime.Stamp(payload);
            Emit("combat.hit", payload);
        }
        catch (Exception ex) { CheatState.Error("combat.hit " + logTag + ": " + ex.Message); }
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.AttackPlant))]
    public static class ZombieAttackPlant
    {
        public static void Prefix(Zombie __instance, Plant plant) =>
            RecordOrEmitMeleeAttackPlant(__instance, plant, "attackPlant");
    }

    /// <summary>QingZombie's own override of AttackPlant — see the class comment on
    /// <see cref="RecordOrEmitMeleeAttackPlant"/> for why the base patch above does not cover it.</summary>
    [HarmonyPatch(typeof(QingZombie), nameof(QingZombie.AttackPlant))]
    public static class QingZombieAttackPlant
    {
        public static void Prefix(QingZombie __instance, Plant plant) =>
            RecordOrEmitMeleeAttackPlant(__instance, plant, "attackPlant(QingZombie)");
    }

    /// <summary>
    /// Multi-target melee whose own method takes no target parameter (unlike
    /// <see cref="Zombie.AttackPlant"/>'s single <c>Plant plant</c>) — <c>AttackPlants()</c> is a
    /// genuinely different method, not a variant of the singular one (verified against the shipped
    /// proxy: both are real, separate, non-virtual methods on <c>QingZombie</c>/
    /// <c>EternalZombie_a</c>). With no target to record from directly, this brackets an ambient
    /// attacker around the call so the plant's own TakeDamage hook — which DOES know the target —
    /// can attribute the hit correctly (<see cref="Effects.EventDrainHost.TryRecordAmbientMeleeDealt"/>).
    /// </summary>
    static void BeginMultiMeleeAttack(Zombie attacker)
    {
        if (attacker == null) return;
        try { if (attacker.Pointer == IntPtr.Zero) return; }
        catch { return; }
        if (!Effects.EventDrainHost.Enabled || DebugRuntime.SessionActive) return;
        var typeId = 0;
        try { typeId = (int)attacker.theZombieType; } catch { }
        Effects.EventDrainHost.BeginAmbientMeleeAttacker(attacker.Pointer, typeId);
    }

    static void EndMultiMeleeAttack() => Effects.EventDrainHost.EndAmbientMeleeAttacker();

    [HarmonyPatch(typeof(QingZombie), nameof(QingZombie.AttackPlants))]
    public static class QingZombieAttackPlants
    {
        public static void Prefix(QingZombie __instance) => BeginMultiMeleeAttack(__instance);
        public static void Postfix() => EndMultiMeleeAttack();
    }

    [HarmonyPatch(typeof(EternalZombie_a), nameof(EternalZombie_a.AttackPlants))]
    public static class EternalZombieAAttackPlants
    {
        public static void Prefix(EternalZombie_a __instance) => BeginMultiMeleeAttack(__instance);
        public static void Postfix() => EndMultiMeleeAttack();
    }

    /// <summary>
    /// lawn-combat-wire L-N39: BambooDragon's breath reaches <c>Zombie.TakeDamage</c> with <c>damageFrom</c> null (live
    /// 2026-09-16 FSM trace: 762 of its hits, no Bullet, no Plant), so the drain could not name the attacker and the rider
    /// never fired. Its damage runs inside its own <c>FixedUpdate</c> (the class declares only StartShooting, SearchZombie,
    /// FixedUpdate, EndShooting and SummonFlying), so the dragon is bracketed as the ambient attacker there, the same
    /// mechanism <see cref="QingZombieAttackPlants"/> uses for a multi-target attack with no target parameter.
    /// </summary>
    static void BeginPlantAmbientAttack(Plant attacker)
    {
        if (attacker == null) return;
        try { if (attacker.Pointer == IntPtr.Zero) return; }
        catch { return; }
        if (!Effects.EventDrainHost.Enabled || DebugRuntime.SessionActive) return;
        var typeId = 0;
        try { typeId = (int)attacker.thePlantType; } catch { }
        Effects.EventDrainHost.BeginAmbientMeleeAttacker(attacker.Pointer, typeId);
    }

    [HarmonyPatch(typeof(PlantBambooDragon), nameof(PlantBambooDragon.FixedUpdate))]
    public static class BambooDragonFixedUpdate
    {
        public static void Prefix(PlantBambooDragon __instance) => BeginPlantAmbientAttack(__instance);
        public static void Postfix() => EndMultiMeleeAttack();
    }

    /// <summary>
    /// Plant-side area melee — Shulkflower/WaterShulk hit a row/column of zombies in one swing.
    /// Unlike <c>AttackPlants()</c> above, this method's own parameter names the exact victims, so
    /// each gets its own compact dealt record directly (no ambient bracket needed): verified against
    /// the shipped proxy as <c>AttackEffect(Il2CppSystem.Collections.Generic.List&lt;Zombie&gt;
    /// zombies)</c> — an IL2CPP-bridged List, not <see cref="System.Collections.Generic.List{T}"/>.
    /// WaterShulk overrides Shulkflower's own declaration, so — same reasoning as
    /// <see cref="RecordOrEmitMeleeAttackPlant"/> — it needs its own separate Harmony patch.
    /// </summary>
    static void RecordPlantAreaMeleeDealt(Plant attacker, Il2CppSystem.Collections.Generic.List<Zombie> zombies)
    {
        if (attacker == null || zombies == null || zombies.Count == 0) return;
        try { if (attacker.Pointer == IntPtr.Zero) return; }
        catch { return; }
        if (!Effects.EventDrainHost.Enabled || DebugRuntime.SessionActive) return;
        var dmgRec = 0;
        try { dmgRec = attacker.attackDamage; } catch { }
        var pTypeRec = 0;
        try { pTypeRec = (int)attacker.thePlantType; } catch { }
        foreach (var zombie in zombies)
        {
            if (zombie == null) continue;
            IntPtr zPtr;
            try
            {
                if (zombie.Pointer == IntPtr.Zero) continue;
                zPtr = zombie.Pointer;
            }
            catch { continue; }
            var zTypeRec = 0;
            try { zTypeRec = (int)zombie.theZombieType; } catch { }
            Effects.EventDrainHost.TryRecordMeleeDealt(
                Core.Events.GameEventSide.Zombie, attacker.Pointer, pTypeRec, zPtr, zTypeRec, dmgRec);
        }
    }

    [HarmonyPatch(typeof(Shulkflower), nameof(Shulkflower.AttackEffect))]
    public static class ShulkflowerAttackEffect
    {
        public static void Prefix(Shulkflower __instance, Il2CppSystem.Collections.Generic.List<Zombie> zombies) =>
            RecordPlantAreaMeleeDealt(__instance, zombies);
    }

    [HarmonyPatch(typeof(WaterShulk), nameof(WaterShulk.AttackEffect))]
    public static class WaterShulkAttackEffect
    {
        public static void Prefix(WaterShulk __instance, Il2CppSystem.Collections.Generic.List<Zombie> zombies) =>
            RecordPlantAreaMeleeDealt(__instance, zombies);
    }

    [HarmonyPatch(typeof(Mower), nameof(Mower.StartMove))]
    public static class MowerStart
    {
        public static void Postfix(Mower __instance)
        {
            if (__instance == null) return;
            MowerStarted.Add(__instance.Pointer);
            Emit("mower.start", new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(__instance),
                ["type"] = (int)__instance.theMowerType,
                ["typeName"] = __instance.theMowerType.ToString()
            });
        }
    }

    [HarmonyPatch(typeof(Mower), nameof(Mower.Die))]
    public static class MowerDie
    {
        public static void Postfix(Mower __instance)
        {
            if (__instance == null) return;
            Emit("mower.die", new Dictionary<string, object> { ["ptr"] = GameDumps.Ptr(__instance) });
        }
    }

    [HarmonyPatch(typeof(Bullet), nameof(Bullet.InitData))]
    public static class BulletInit
    {
        public static void Postfix(Bullet __instance)
        {
            // BumpBullet is the authoritative bullets_spawned source; always count.
            RpgHost.Client?.BumpBullet();
            if (__instance == null) return;
            // lawn-combat-wire T10/T12 fourth defect: bullet.from/from_zombie is only live HERE, at
            // spawn — proven stale (always IntPtr.Zero) by the time Bullet.HitZombie/HitPlant reads
            // it. Cache the shooter now so EventDrainHost.TryRecordDealtFromBullet can look it up by
            // bullet ptr instead of re-reading the dead field. Unconditional — cheap, and every other
            // gate below this line may return before the record path ever runs.
            try
            {
                var shooterPtr = IntPtr.Zero;
                var shooterTypeId = 0;
                if (__instance.shootByZombie)
                {
                    var z = __instance.from_zombie;
                    if (z != null) { shooterPtr = z.Pointer; try { shooterTypeId = (int)z.theZombieType; } catch { } }
                }
                else
                {
                    var p = __instance.from;
                    if (p != null) { shooterPtr = p.Pointer; try { shooterTypeId = (int)p.thePlantType; } catch { } }
                }
                // 2026-09-15 fifth defect (live-proven): `from`/`from_zombie` is proven UNSET even at
                // this exact spawn instant, not merely stale by hit time -- confirmed live via a
                // dedicated trace, every fire, for a lab-overlay debug-spawned Peashooter (a
                // debug.spawn-plant creature never goes through whatever vanilla firing-code path
                // assigns the real field; the direct read above is a correct idea for a REAL
                // player-placed plant, just not sufficient alone). Position fallback: `theBulletRow`
                // is a real field (confirmed via metadata dump) and always set regardless of spawn
                // path, so resolve the firing side's living occupant of that row from the SAME board
                // snapshot InjectorCombatBridge/InjectorStatusBridge already share (E27) -- no second
                // scan. A row holds many same-side entities (Sunflower, Wall-nut, shooter), so row alone
                // is not an identity: InitData runs at the bullet's spawn point, i.e. the shooter's own
                // cell, so match the nearest same-side entity by column (<= 1 away). No column, no
                // candidate, or a tie ⇒ leave shooterPtr zero (no RPG record) rather than credit a guess.
                // Known residual: a multi-lane shot (Threepeater side peas) can still match an adjacent-
                // lane occupant at the same column -- tracked in lawn-combat-wire-todo next-run tasks.
                // L-N22 observability: which source named the shooter (the direct field, the position fallback, or none).
                var shooterVia = shooterPtr != IntPtr.Zero ? "from" : "none";
                if (shooterPtr == IntPtr.Zero)
                {
                    try
                    {
                        var side = __instance.shootByZombie ? "zombie" : "plant";
                        var row = __instance.theBulletRow;
                        var bulletCol = FusionRpg.Injector.Lawn.LawnCoords.ColFromX(__instance.transform.position.x);
                        var best = FusionRpg.Core.Combat.BulletShooterMatch.Resolve(
                            Effects.InjectorBoardSnapshot.Capture().Entities, side, row, bulletCol);
                        if (best != null &&
                            ulong.TryParse(best.Ptr, System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out var raw))
                        {
                            shooterPtr = unchecked((IntPtr)raw);
                            shooterTypeId = best.TypeId;
                            shooterVia = "fallback";
                        }
                    }
                    catch { }
                }
                Effects.EventDrainHost.CacheBulletShooter(__instance.Pointer, shooterPtr, shooterTypeId);
                if (Effects.FsmTrace.Enabled)
                    CheatState.Note($"fsm-trace BulletInit.Postfix bulletPtr={__instance.Pointer:X} shooterPtr={shooterPtr:X} shooterTypeId={shooterTypeId} via={shooterVia}");
            }
            catch { }
            // Highest-rate kind (~per pea). Emit only when something consumes it: an OnSpawn
            // grant or a debug session. MatchHost ignores it; the server metric is redundant;
            // the web strips it (v2 audit §4c.2).
            if (!Effects.EffectRuntime.HasOnSpawnGrant() && !DebugRuntime.SessionActive) return;
            // v2 record path (Task 9): compact record instead of dict when the host is live.
            if (Effects.EventDrainHost.Enabled && !DebugRuntime.SessionActive)
            {
                var fromTypeRec = 0;
                try { fromTypeRec = (int)__instance.fromType; } catch { }
                Effects.EventDrainHost.TryRecordBulletSpawn(__instance.Pointer, fromTypeRec);
                return;
            }
            var payload = new Dictionary<string, object> { ["ptr"] = GameDumps.Ptr(__instance) };
            try { payload["damage"] = __instance.Damage; } catch { }
            try
            {
                payload["plantType"] = (int)__instance.fromType;
                payload["plantTypeName"] = GameDumps.EnumName(__instance.fromType);
            }
            catch { }
            Emit("bullet.init", payload);
        }
    }

    /// <summary>L-N16: a real zombie spawn edge (Start / InitHealth postfix) — clears the once-per-ptr
    /// death latch and the drain liveness mark left by an earlier zombie at this native address, so
    /// the new zombie takes RPG hits and its own death is flushed and forgotten. Never called from a
    /// registry resync, which also re-adds dying-but-not-destroyed objects.</summary>
    static void NoteZombieSpawned(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        DeadZombies.Remove(p);
        Effects.EventDrainHost.MarkSpawned(p);
    }

    static void NoteZombieDead(Zombie z, int reason)
    {
        if (z == null) return;
        var p = z.Pointer;
        // Every call: a board resync can re-add a dying zombie to the registry, so each death-animation frame removes it again.
        Effects.InjectorEntityRegistry.Remove(p);
        var firstForPtr = DeadZombies.Add(p);
        // lawn-combat-wire L-N5: which death hook arrived, and whether the once-per-death latch let it through.
        if (Effects.FsmTrace.Enabled)
            CheatState.Note($"fsm-trace NoteZombieDead hook={(reason == -1 ? "DestoryZombie" : "Die")} zombiePtr={p:X} frame={UnityEngine.Time.frameCount} firstForPtr={firstForPtr}");
        if (!firstForPtr) return;
        // lawn-combat-wire L-N37: once per death. The game calls Die on every frame of the death animation (up to 158 times
        // live), and invalidating here forced a board snapshot rebuild on each of those frames.
        Effects.InjectorBoardSnapshot.Invalidate();
        // lawn-hit-entry (T9c): mark dead before the flush — see PlantDie.Postfix's own comment.
        Effects.EventDrainHost.MarkDead(p);
        // v2 barrier: pending hit records for this zombie drain before its grants withdraw.
        // `flushedNow` false means this death fired from inside an active drain pass (T9c) — the
        // ForgetEntity below must then wait (see the deferred branch at the end of this method).
        var flushedNow = true;
        try { flushedNow = Effects.EventDrainHost.FlushForPtr(p); } catch { }
        var diePayload = new Dictionary<string, object>
        {
            ["type"] = (int)z.theZombieType,
            ["typeName"] = GameDumps.EnumName(z.theZombieType),
            ["ptr"] = p.ToString("X"),
            ["reason"] = reason,
            ["lifecycleOccurrence"] = Interlocked.Increment(ref _lifecycleOccurrence),
            // live-probe Task 18: game | debug | cheat — rides onto the ZombieKilled fact, which the
            // soul ledger's kill rows reference, so a debug-funded balance is attributable.
            ["spawnOrigin"] = Match.SpawnOriginTags.TakeOnDeath(p)
        };
        AddFatalKiller(diePayload, TakeFatalKiller(p));
        DebugRuntime.Stamp(diePayload);
        // OnDeath must see entity grants; ForgetEntity withdraws after Emit (or later still, if
        // the flush above was nested).
        Emit("zombie.die", diePayload);
        if (flushedNow) ForgetEntity(p);
        else Effects.EventDrainHost.DeferForget(p, () => ForgetEntity(p));
        try { DebugRuntime.OnZombieDying(z); } catch (Exception ex) { CheatState.Error("debug onkill: " + ex.Message); }
    }

    [HarmonyPatch(typeof(Bullet), nameof(Bullet.HitZombie))]
    public static class BulletHitZombie
    {
        public static void Prefix(Bullet __instance, Zombie zombie)
        {
            if (__instance == null || zombie == null) return;
            try
            {
                if (__instance.Pointer == IntPtr.Zero || zombie.Pointer == IntPtr.Zero) return;
            }
            catch { return; }
            if (!DebugRuntime.ShouldEmitHit()) return;
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["side"] = "zombie",
                    ["source"] = "hitZombie",
                    ["bulletPtr"] = GameDumps.Ptr(__instance),
                    ["bulletType"] = (int)__instance.theBulletType,
                    ["damage"] = __instance.Damage,
                    ["targetPtr"] = GameDumps.Ptr(zombie),
                    ["targetType"] = (int)zombie.theZombieType
                };
                try
                {
                    payload["fromType"] = (int)__instance.fromType;
                    payload["fromTypeName"] = GameDumps.EnumName(__instance.fromType);
                }
                catch { }
                DebugRuntime.Stamp(payload);
                Emit("combat.hit", payload);
                // Arms stay on TakeDamage only — avoid double-fire if this patch is enabled.
            }
            catch (Exception ex) { CheatState.Error("combat.hit zombie: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(Bullet), nameof(Bullet.HitPlant))]
    public static class BulletHitPlant
    {
        public static void Prefix(Bullet __instance, Plant plant)
        {
            if (__instance == null || plant == null) return;
            try
            {
                if (__instance.Pointer == IntPtr.Zero || plant.Pointer == IntPtr.Zero) return;
            }
            catch { return; }
            if (!DebugRuntime.ShouldEmitHit()) return;
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["side"] = "plant",
                    ["source"] = "hitPlant",
                    ["bulletPtr"] = GameDumps.Ptr(__instance),
                    ["bulletType"] = (int)__instance.theBulletType,
                    ["damage"] = __instance.Damage,
                    ["targetPtr"] = GameDumps.Ptr(plant),
                    ["targetType"] = (int)plant.thePlantType
                };
                try
                {
                    payload["fromType"] = (int)__instance.fromType;
                    payload["fromTypeName"] = GameDumps.EnumName(__instance.fromType);
                }
                catch { }
                DebugRuntime.Stamp(payload);
                Emit("combat.hit", payload);
            }
            catch (Exception ex) { CheatState.Error("combat.hit plant: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(Bullet), nameof(Bullet.HitLand))]
    public static class BulletHitLand
    {
        public static void Prefix(Bullet __instance)
        {
            if (__instance == null) return;
            try
            {
                if (__instance.Pointer == IntPtr.Zero) return;
            }
            catch { return; }
            if (!DebugRuntime.ShouldEmitHit()) return;
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["source"] = "hitLand",
                    ["bulletPtr"] = GameDumps.Ptr(__instance)
                };
                try { payload["bulletType"] = (int)__instance.theBulletType; } catch { }
                try { payload["row"] = __instance.theBulletRow; } catch { }
                try { payload["damage"] = __instance.Damage; } catch { }
                try { payload["fromType"] = (int)__instance.fromType; } catch { }
                DebugRuntime.Stamp(payload);
                Emit("combat.hitland", payload);
            }
            catch (Exception ex) { CheatState.Error("combat.hitland: " + ex.Message); }
        }
    }

    /// <summary>Delegates to EntityApply — sole Resolve→Writer path.</summary>
    internal static void ApplyPlant(Plant p, string source, bool includeAbsolute = true)
    {
        using var _perf = PerfProbe.Measure(PerfSection.EntityApply);
        EntityApply.RunPlant(p, source, includeAbsolute);
    }

    /// <summary>Delegates to EntityApply — sole Resolve→Writer path.</summary>
    internal static void ApplyZombie(Zombie z, string source, bool includeAbsolute = true)
    {
        using var _perf = PerfProbe.Measure(PerfSection.EntityApply);
        EntityApply.RunZombie(z, source, includeAbsolute);
    }

    /// <summary>
    /// Leave-board cleanup: withdraw <c>entity:{ptr}</c> grants (OnRemoved) then clear baselines.
    /// Call after die Emit so OnDeath still sees entity grants.
    /// </summary>
    public static void ForgetEntity(IntPtr ptr)
    {
        try { Effects.EffectRuntime.WithdrawEntity(ptr.ToString("X")); } catch { }
        Applied.Remove(ptr);
        EntityStatWriter.Forget(ptr);
        CheatState.Stats.ForgetEntity(ptr.ToString("X"));
        // IL2CPP can hand this exact address to a NEW entity later in the same match, and the element
        // cache is keyed by ptr and cleared only on a match change — so without this the next creature
        // at this address would inherit the dead one's species element
        // (LawnElementResolver's trigger set, item 3).
        try { Effects.LawnElementResolverHost.Invalidate(ptr.ToString("X")); } catch { }
        try { FusionRpg.Bridge.Hud.ActorHudCache.Remove(ptr.ToString("X")); } catch { }
        try { Hud.ActorHudPool.ReleaseOwner(ptr.ToString("X")); } catch { }
        // Same ptr-reuse hazard as LawnElementResolverHost.Invalidate above, same fix shape —
        // see InjectorSpawnHpPin.Remove's own doc comment (confirmed live, not theoretical).
        try { Stats.InjectorSpawnHpPin.Remove(ptr.ToString("X")); } catch { }
        try { Match.SpawnOriginTags.Forget(ptr); } catch { }
    }

    internal static void RecapturePlant(Plant p, string source)
    {
        if (p == null) return;
        var dump = GameDumps.Plant(p, source, p.thePlantHealth, p.thePlantMaxHealth, p.attackDamage);
        dump["side"] = "plant";
        Emit("entity.stats", dump);
        if (CheatState.On("Z-REAPPLY-RC"))
        {
            Applied.Remove(p.Pointer);
            EntityApply.RunPlant(p, "recapture:" + source);
        }
    }

    internal static void RecaptureZombie(Zombie z, string source)
    {
        if (z == null) return;
        var dump = GameDumps.Zombie(z, source, Bridges.ZombieCombatFields.GetHp(z), Bridges.ZombieCombatFields.GetMaxHp(z), z.theAttackDamage, z.theFirstArmorHealth, z.theFirstArmorMaxHealth);
        dump["side"] = "zombie";
        Emit("entity.stats", dump);
        if (CheatState.On("Z-REAPPLY-RC"))
        {
            Applied.Remove(z.Pointer);
            EntityApply.RunZombie(z, "recapture:" + source);
        }
    }
}
