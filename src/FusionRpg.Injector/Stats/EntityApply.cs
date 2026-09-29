using System;
using System.Collections.Generic;
using System.Linq;
using FusionRpg.Core.Lawn;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Injector.Bridges;
using FusionRpg.Injector.Lawn;
using FusionRpg.Injector.Stats;

using FusionRpg.Injector.Host;

namespace FusionRpg.Injector;

/// <summary>
/// Single entry for Resolve → EntityStatWriter. Spawn, PushScales, reapply, and Tab B Apply all call Run*.
/// </summary>
public static class EntityApply
{
    public static void RunPlant(Plant p, string source, bool includeAbsolute = true)
    {
        if (p == null) return;
        try
        {
            var s = CheatState.EffectiveStats();
            var ptr = p.Pointer;
            var key = ptr.ToString("X");
            // Living membership SSOT = MatchRuntime BoardProjection via Emit Apply (W2-D).
            // Do not steal selection during bulk PushScales / reapply.
            if (!source.Contains("pushScales", StringComparison.Ordinal)
                && !source.Contains("reapply", StringComparison.Ordinal)
                && !source.Contains("absolute", StringComparison.Ordinal))
                CheatState.Select(ptr, "plant");

            try
            {
                SpawnCatalog.Note("plant", (int)p.thePlantType, GameDumps.EnumName(p.thePlantType), "spawn:" + source,
                    GameDumps.PlantName(p.thePlantType));
            }
            catch { /* catalog must not block apply */ }

            var baseline = CheatState.Stats.CaptureOrGet(key, () => new EntityBaseline
            {
                Hp = p.thePlantHealth,
                MaxHp = p.thePlantMaxHealth,
                Atk = p.attackDamage,
                // E16: the intervals become real composed channels, so their game values have to be
                // captured as a baseline like every other channel. Captured ONCE, on first sight —
                // capturing later would bake an already-modified value in as if it were the original.
                AttackInterval = p.thePlantAttackInterval,
                ProduceInterval = p.thePlantProduceInterval,
                // E38 (spec-entity-fields-12plus.md): eight more plant fields, captured the same way
                // and for the same reason — a live field on the plant's own side, so a baseline of
                // exactly zero here is an ordinary value, never "this plant lacks the stat".
                PlantShield = p.theShieldHealth,
                AttackCountdown = p.thePlantAttackCountDown,
                AttackSpeedAdder = p.attackSpeedAdder,
                ProduceCountdown = p.thePlantProduceCountDown,
                PlantSpeed = p.thePlantSpeed,
                PlantMoveSpeed = p.moveSpeed,
                PlantLevel = p.theLevel,
                ShootingLevel = p.shootingLevel
            });

            if (!GameHooks.Applied.Add(ptr)) return;

            var prevHp = p.thePlantHealth;
            var prevMax = p.thePlantMaxHealth;
            var preserveRatio = StatSystem.PreserveLiveCurrentHp(source);
            // L-N20: a debug-spawn max-HP pin rides into the Hub as an absolute input on EVERY resolve
            // (includeAbsolute or not), so Hub max-HP bonuses still compose on top of it.
            var abs = InjectorSpawnHpPin.Store.ApplyTo(key, includeAbsolute ? CheatState.BuildPlantAbsolute() : null);
            var applyScales = s.ApplyStats && !CheatState.On("D-PROBE-BULLET");
            var hasScaleMods = applyScales && CheatState.HasPlantScaleMods();
            var hasPvz = CheatState.HasPvzStatsMods();
            var hasEffectMods = CheatState.Stats.HasSessionModsBySourceKind("effect");
            var hasExtras = includeAbsolute && CheatState.HasPlantExtrasSet();

            var ctx = CheatState.Stats.Contexts.ForPlant(
                key, baseline, (int)p.thePlantType, GameHooks.MatchKey,
                // CheatState.CurrentPlayerId, not PvzStatsPlayerId (an unrelated, often-unset field) --
                // must match the key HydratedPowerIndexProvider.Hydrate uses, or every Θ-scaled
                // aptitude contribution silently resolves to 0 regardless of allocation share
                // (CheatState.CurrentPlayerId's own doc comment has the full trace).
                playerId: CheatState.CurrentPlayerId > 0 ? CheatState.CurrentPlayerId : null,
                cheatScale: s, cheatAbsolute: abs,
                applyStats: PvzStatsApplyGate.ShouldComposeScales(hasScaleMods, hasPvz, hasEffectMods),
                cheatAbsoluteReal: includeAbsolute ? CheatState.BuildPlantAbsoluteReal() : null,
                pvzStatsMods: CheatState.PvzStatsMods);
            var resolved = CheatState.ActorHub.Resolve(ctx);
            InjectorDerivedOverride.Pin(key, resolved.Derived);
            try { Hud.ActorHudCache.MarkDirty(key); } catch { }
            var final = resolved.AppliedCombat;
            EmitAptitudeTrace("plant", key, ctx, resolved);

            // The write decision is a VALUE question, never a contributor question -- the whole rule,
            // with its full trace, lives in EntityWriteGate (Core) so it is stated once, shared with
            // RunZombie below, and reachable by a test CI actually runs.
            var shouldWrite = EntityWriteGate.ShouldWrite(final, baseline, source);

            if (shouldWrite)
                EntityStatWriter.WritePlant(p, final, prevHp, prevMax, preserveRatio, source);

            if (hasExtras)
                EntityStatWriter.WritePlantExtras(p);

            if (shouldWrite && RpgHost.Client != null)
            {
                try
                {
                    GameHooks.Emit("stat.applied", Tag(new Dictionary<string, object>
                    {
                        ["side"] = "plant",
                        ["type"] = (int)p.thePlantType,
                        ["typeName"] = GameDumps.EnumName(p.thePlantType),
                        ["ptr"] = key,
                        ["source"] = source,
                        ["includeAbsolute"] = includeAbsolute,
                        ["hpBefore"] = baseline.Hp,
                        ["hpAfter"] = p.thePlantHealth,
                        ["maxAfter"] = p.thePlantMaxHealth,
                        ["attackBefore"] = baseline.Atk,
                        ["attackAfter"] = p.attackDamage,
                        ["hpPercent"] = s.Plants.HpPercent,
                        ["hpFlat"] = s.Plants.HpFlat
                    }));
                }
                catch { }
            }

            if (RpgHost.Client != null
                && !source.Contains("pushScales", StringComparison.Ordinal)
                && !source.Contains("absolute", StringComparison.Ordinal))
            {
                try
                {
                    var spawnSource = CheatState.ConsumeSpawnSourceTag(ptr) ?? source;
                    var payload = GameDumps.Plant(p, spawnSource, baseline.Hp, baseline.MaxHp, baseline.Atk);
                    AddGeneralProgressionSource(payload, "plant", (int)p.thePlantType, spawnSource);
                    GameHooks.Emit("plant.spawn",
                        Tag(payload));
                }
                catch { }
            }
        }
        catch (Exception ex) { CheatState.Error("EntityApply.plant: " + ex.Message); }
    }

    public static void RunZombie(Zombie z, string source, bool includeAbsolute = true)
    {
        if (z == null) return;
        try
        {
            var s = CheatState.EffectiveStats();
            var ptr = z.Pointer;
            var key = ptr.ToString("X");
            // Living membership SSOT = MatchRuntime BoardProjection via Emit Apply (W2-D).
            if (!source.Contains("pushScales", StringComparison.Ordinal)
                && !source.Contains("reapply", StringComparison.Ordinal)
                && !source.Contains("absolute", StringComparison.Ordinal))
                CheatState.Select(ptr, "zombie");

            try
            {
                SpawnCatalog.Note("zombie", (int)z.theZombieType, GameDumps.EnumName(z.theZombieType), "spawn:" + source,
                    GameDumps.ZombieName(z.theZombieType));
            }
            catch { }

            var baseline = CheatState.Stats.CaptureOrGet(key, () => new EntityBaseline
            {
                Hp = ZombieCombatFields.GetHp(z),
                MaxHp = ZombieCombatFields.GetMaxHp(z),
                Atk = z.theAttackDamage,
                Arm1 = z.theFirstArmorHealth,
                Arm1Max = z.theFirstArmorMaxHealth,
                Arm2 = z.theSecondArmorHealth,
                Arm2Max = z.theSecondArmorMaxHealth,
                ZombieSpeed = z.uniqueSpeed,
                // E38 (spec-entity-fields-12plus.md): four more zombie fields, captured the same way
                // as ZombieSpeed immediately above.
                ArmorFlat = z.theArmor,
                TakeDmgMultiplier = z.takeDmgMultiplier,
                ZombieSpeedCurrent = z.theSpeed,
                ZombieOriginSpeed = z.theOriginSpeed
            });

            if (!GameHooks.Applied.Add(ptr))
            {
                if (CheatState.On("Z-REAPPLY-RC") &&
                    (source.Contains("reinforce", StringComparison.Ordinal)
                     || source.Contains("setZombie", StringComparison.Ordinal)
                     || source.Contains("setHealth", StringComparison.Ordinal)))
                {
                    GameHooks.Applied.Remove(ptr);
                    if (!GameHooks.Applied.Add(ptr)) return;
                }
                else return;
            }

            var prevHp = ZombieCombatFields.GetHp(z);
            var prevMax = ZombieCombatFields.GetMaxHp(z);
            var preserveRatio = StatSystem.PreserveLiveCurrentHp(source);
            // L-N20: same pin-as-Hub-input as RunPlant.
            var abs = InjectorSpawnHpPin.Store.ApplyTo(key, includeAbsolute ? CheatState.BuildZombieAbsolute() : null);
            var applyScales = s.ApplyStats;
            var hasScaleMods = applyScales && CheatState.HasZombieScaleMods();
            var hasPvz = CheatState.HasPvzStatsMods();
            var hasEffectMods = CheatState.Stats.HasSessionModsBySourceKind("effect");
            var hasExtras = includeAbsolute && CheatState.HasZombieExtrasSet();

            var ctx = CheatState.Stats.Contexts.ForZombie(
                key, baseline, (int)z.theZombieType, GameHooks.MatchKey,
                // CheatState.CurrentPlayerId, not PvzStatsPlayerId (an unrelated, often-unset field) --
                // must match the key HydratedPowerIndexProvider.Hydrate uses, or every Θ-scaled
                // aptitude contribution silently resolves to 0 regardless of allocation share
                // (CheatState.CurrentPlayerId's own doc comment has the full trace).
                playerId: CheatState.CurrentPlayerId > 0 ? CheatState.CurrentPlayerId : null,
                cheatScale: s, cheatAbsolute: abs,
                applyStats: PvzStatsApplyGate.ShouldComposeScales(hasScaleMods, hasPvz, hasEffectMods),
                cheatAbsoluteReal: includeAbsolute ? CheatState.BuildZombieAbsoluteReal() : null,
                pvzStatsMods: CheatState.PvzStatsMods);
            var resolvedZ = CheatState.ActorHub.Resolve(ctx);
            InjectorDerivedOverride.Pin(key, resolvedZ.Derived);
            try { Hud.ActorHudCache.MarkDirty(key); } catch { }
            var final = resolvedZ.AppliedCombat;
            EmitAptitudeTrace("zombie", key, ctx, resolvedZ);

            // Same value-based rule as RunPlant -- the one shared EntityWriteGate, not a second copy.
            var shouldWrite = EntityWriteGate.ShouldWrite(final, baseline, source);

            if (shouldWrite)
                EntityStatWriter.WriteZombie(z, final, prevHp, prevMax, preserveRatio, source);

            if (hasExtras)
                EntityStatWriter.WriteZombieExtras(z);

            if (shouldWrite && RpgHost.Client != null)
            {
                try
                {
                    GameHooks.Emit("stat.applied", Tag(new Dictionary<string, object>
                    {
                        ["side"] = "zombie",
                        ["type"] = (int)z.theZombieType,
                        ["typeName"] = GameDumps.EnumName(z.theZombieType),
                        ["ptr"] = key,
                        ["source"] = source,
                        ["includeAbsolute"] = includeAbsolute,
                        ["hpBefore"] = baseline.Hp,
                        ["hpAfter"] = ZombieCombatFields.GetHp(z),
                        ["attackBefore"] = baseline.Atk,
                        ["attackAfter"] = z.theAttackDamage
                    }));
                }
                catch { }
            }

            if (RpgHost.Client != null
                && !source.Contains("pushScales", StringComparison.Ordinal)
                && !source.Contains("absolute", StringComparison.Ordinal))
            {
                try
                {
                    var spawnSource = CheatState.ConsumeSpawnSourceTag(ptr) ?? source;
                    var payload = GameDumps.Zombie(z, spawnSource, baseline.Hp, baseline.MaxHp, baseline.Atk, baseline.Arm1,
                        baseline.Arm1Max);
                    AddGeneralProgressionSource(payload, "zombie", (int)z.theZombieType, spawnSource);
                    GameHooks.Emit("zombie.spawn",
                        Tag(payload));
                }
                catch { }
            }
        }
        catch (Exception ex) { CheatState.Error("EntityApply.zombie: " + ex.Message); }
    }

    static Dictionary<string, object> Tag(Dictionary<string, object> payload)
    {
        CheatState.TagProbe(payload);
        return payload;
    }

    /// <summary>
    /// First-session progression T4: ordinary PvZ spawns carry an explicit empire-general claim.
    /// Dedicated extra/unique/commander mechanisms are intentionally left untouched; the server
    /// rejects missing or contradictory claims rather than inferring a source from the type id.
    /// </summary>
    static void AddGeneralProgressionSource(Dictionary<string, object> payload, string side, int typeId, string source)
    {
        // Only the vanilla lifecycle entry points identify an ordinary lawn spawn. Debug,
        // recapture, extra, patron, and other opaque operations are deliberately not evidence;
        // a deny-list would make a newly added dedicated mechanism fall through by accident.
        if (!string.Equals(source, "start", StringComparison.Ordinal)
            && !string.Equals(source, "initHealth", StringComparison.Ordinal))
            return;
        if (!FusionRpg.Core.Creatures.CreatureSpeciesCatalog.IsConfigured) return;
        var index = new FusionRpg.Core.Creatures.LawnElementIndex(FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All);
        if (!index.TryGet(side, typeId, out var species)) return;
        payload["sourceKind"] = FusionRpg.Core.Creatures.CreatureProgressionSource.EmpireGeneralKind;
        payload["sourceId"] = "general:" + species.SpeciesId;
    }

    /// <summary>
    /// A-M2 lawn-reposition — the ONLY public way to move an actor (spec-lawn-reposition.md §2).
    /// Called only from <c>MoveDrainHost.Tick</c>'s drain, never from a hook directly — that is
    /// the entire point of record-then-drain, so by the time this method runs, being on
    /// <c>InjectorLoop.Tick</c>'s own call stack IS the proof that no write happens inside a
    /// Harmony hook (spec AC5).
    ///
    /// Deltas-not-absolutes does NOT apply here — restated so a later session does not "fix" it
    /// in: a cell is a destination, not a magnitude, so <see cref="EntityWriteGate.ShouldWrite"/>
    /// (a VALUE comparison over combat fields) is never consulted for a move.
    /// <see cref="MoveDecisionPolicy.Decide"/> is this method's own, cell-shaped equivalent —
    /// Core-side and pure, unlike this method, for the exact reason
    /// <see cref="EntityWriteGate"/>'s own header gives: this assembly needs a real PVZ Fusion
    /// install to build and never runs under CI, so the part that actually needs a regression
    /// test has to live somewhere a test CAN reach.
    /// </summary>
    public static void MoveToCell(Plant? p, int col, int row, string source)
    {
        if (p == null) return;
        try
        {
            var alive = p.thePlantHealth > 0;
            var spawned = GameHooks.Applied.Contains(p.Pointer);
            var decision = MoveDecisionPolicy.Decide(
                alive, spawned,
                p.thePlantColumn, p.thePlantRow,
                col, row,
                LawnCoords.LastCol, LawnCoords.LastRow);

            if (decision.Outcome != MoveOutcome.Apply) return;
            EntityPositionWriter.WritePlantPosition(p, decision.Col, decision.Row, source);
        }
        catch (Exception ex) { CheatState.Error("EntityApply.moveToCell.plant: " + ex.Message); }
    }

    /// <summary>Zombie overload of <see cref="MoveToCell(Plant, int, int, string)"/> — see that
    /// overload's doc for the shared contract.</summary>
    public static void MoveToCell(Zombie? z, int col, int row, string source)
    {
        if (z == null) return;
        try
        {
            var alive = ZombieCombatFields.GetHp(z) > 0;
            var spawned = GameHooks.Applied.Contains(z.Pointer);
            // Zombies have no discrete column field (they walk continuously) — LawnCoords.ColFromX
            // is the same read-only lane-math helper FX/HUD callers already use for the inverse
            // problem. A -1 on a missing Mouse never spuriously matches a clamped 0..LastCol
            // request, so the worst case here is a same-cell skip that fails to fire — it is
            // EntityPositionWriter's own Mouse.Instance guard (spec §2) that actually protects the
            // write, not this comparison.
            var currentCol = LawnCoords.ColFromX(z.transform.position.x);
            var decision = MoveDecisionPolicy.Decide(
                alive, spawned,
                currentCol, z.theZombieRow,
                col, row,
                LawnCoords.LastCol, LawnCoords.LastRow);

            if (decision.Outcome != MoveOutcome.Apply) return;
            EntityPositionWriter.WriteZombiePosition(z, decision.Col, decision.Row, source);
        }
        catch (Exception ex) { CheatState.Error("EntityApply.moveToCell.zombie: " + ex.Message); }
    }

    /// <summary>Diagnostic kept permanently (aura-skill Checkpoint 2/5, 2026-08-30): this is what
    /// actually closed the "does commander allocation reach a lawn entity's stat" question — earlier
    /// spawn-and-read probes against a phantom/stale board (a `lawn/quick-start` "already live"
    /// false-positive after a redeploy) showed `attackDamage` stuck at 1 regardless of allocation; this
    /// trace, run against a genuinely fresh `debug.level.enter` board, proved the full chain real:
    /// `subsystems="rpg.progression,rpg.aptitude"` (registered), `bonusAtkContribs=
    /// "aptitude.Might:Flat:30990"` (funded and composing), `primaryAtk=20 -&gt; appliedAtk=31010`
    /// (written). Left in (gated behind `SYS-EMIT-PROOF` like every other proof emit) because it is the
    /// only fast way to tell "no live board yet" apart from "allocation genuinely not applying" without
    /// another multi-minute redeploy-and-guess cycle — this exact ambiguity cost real time once
    /// already.</summary>
    static void EmitAptitudeTrace(string side, string ptr, StatContext ctx, ActorResolveResult resolved)
    {
        if (!(CheatState.EmitProof && CheatState.On("SYS-EMIT-PROOF"))) return;
        try
        {
            var (_, contributions) = CheatState.ActorHub.ResolveDerivedWithContributions(ctx);
            string Contribs(string channel) => string.Join(";", contributions.ContributionsFor(channel)
                .Select(c => $"{c.SourceId}:{c.Op}:{c.Value}"));
            GameHooks.Emit("debug.aptitude-trace", new Dictionary<string, object>
            {
                ["side"] = side,
                ["ptr"] = ptr,
                ["ctxPlayerId"] = ctx.PlayerId ?? -1,
                ["currentPlayerId"] = CheatState.CurrentPlayerId,
                ["theta"] = CheatState.PowerIndex.ActorIndex(ctx),
                ["subsystems"] = string.Join(",", CheatState.ActorHub.Subsystems.Select(s => s.SubsystemId)),
                ["primaryHp"] = resolved.RuntimePrimary.Hp,
                ["primaryMaxHp"] = resolved.RuntimePrimary.MaxHp,
                ["primaryAtk"] = resolved.RuntimePrimary.Atk,
                ["primaryArm1"] = resolved.RuntimePrimary.Arm1,
                ["primaryArm2"] = resolved.RuntimePrimary.Arm2,
                ["primaryDefenseFlat"] = resolved.RuntimePrimary.DefenseFlat,
                ["appliedHp"] = resolved.AppliedCombat.Hp,
                ["appliedMaxHp"] = resolved.AppliedCombat.MaxHp,
                // `appliedAtk` and `primaryAtk` are now always equal: `progression.bonus.atk` was retired
                // 2026-09-18 (solid-enforcement `retire-atk` R4), so `ActorHub.MergeAppliedCombat` no
                // longer adds a bonus atk term. Both fields stay — the trace's SHAPE is a contract the
                // probe reads, and `bonusAtk`/`bonusAtkContribs` were the only retired pair.
                ["appliedAtk"] = resolved.AppliedCombat.Atk,
                ["appliedArm1"] = resolved.AppliedCombat.Arm1,
                ["appliedArm2"] = resolved.AppliedCombat.Arm2,
                ["appliedDefenseFlat"] = resolved.AppliedCombat.DefenseFlat,
                ["bonusMaxHp"] = resolved.Derived.Get(DerivedStatChannels.ProgressionBonusMaxHp, -999),
                ["bonusDefense"] = resolved.Derived.Get(DerivedStatChannels.ProgressionBonusDefense, -999),
                ["bonusArm1"] = resolved.Derived.Get(DerivedStatChannels.ProgressionBonusArm1, -999),
                ["bonusArm2"] = resolved.Derived.Get(DerivedStatChannels.ProgressionBonusArm2, -999),
                ["combatPowerOmni"] = resolved.Derived.Get(DerivedStatChannels.CombatPowerOmni, -999),
                ["bonusMaxHpContribs"] = Contribs(DerivedStatChannels.ProgressionBonusMaxHp),
                ["bonusDefenseContribs"] = Contribs(DerivedStatChannels.ProgressionBonusDefense),
                ["bonusArm1Contribs"] = Contribs(DerivedStatChannels.ProgressionBonusArm1),
                ["bonusArm2Contribs"] = Contribs(DerivedStatChannels.ProgressionBonusArm2),
                ["powerOmniContribs"] = Contribs(DerivedStatChannels.CombatPowerOmni)
            });
        }
        catch (Exception ex) { CheatState.Error("aptitude-trace: " + ex.Message); }
    }
}
