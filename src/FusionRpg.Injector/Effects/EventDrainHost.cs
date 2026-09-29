using FusionRpg.Contracts;
using FusionRpg.Core.Events;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// Injector side of the v2 record-then-drain pipeline — event-pipeline-v2 plan Tasks 9–10.
/// Hot-kind hooks call the TryRecord* helpers instead of running the effect pipeline inline;
/// the drain executes from <c>InjectorLoop.Tick</c> under the frame budget.
///
/// ON by default since Task 10 wired the tick (<c>InjectorLoop.ApplyEventPipelineMode</c> sets
/// <see cref="Enabled"/> unless <c>FUSIONRPG_EVENT_V2=0</c>). When off, every hook falls through
/// to the legacy Emit path, so behavior is byte-identical. During a debug session the legacy path
/// is also used — prove packs need full payload fidelity (spec test group 6).
/// </summary>
public static class EventDrainHost
{
    /// <summary>Master switch — set true by InjectorLoop startup (T10 shipped), i.e. ON by default. Env kill: FUSIONRPG_EVENT_V2=0.</summary>
    public static bool Enabled { get; set; }

    static EventDrain? _drain;
    static int _pairSeq;
    // I1 (2026-08-21 review): per-TARGET pending melee pairs — a single slot leaked N-1
    // spurious OnDamageTaken per multi-bite frame. Cleared when the frame advances.
    static readonly Dictionary<IntPtr, int> _meleePairsByTarget = new();
    // lawn-combat-wire L-N3: who bit each target this frame — the observer's vanilla row cannot learn it from damageFrom.
    // Same frame stamp as _meleePairsByTarget, but never consumed by TryRecordTaken.
    static readonly Dictionary<IntPtr, IntPtr> _meleeAttackerByTarget = new();
    static int _meleePairsFrame = -1;

    // lawn-hit-attribution (T6): ambient melee attacker for a multi-target attack whose own Harmony
    // hook has no target parameter (QingZombie.AttackPlants()/EternalZombie_a.AttackPlants() — the
    // plural methods take no args, unlike the singular AttackPlant(Plant)). TakeDamage's own hook
    // knows the target but not the true attacker (damageFrom is unreliable for melee — see the class
    // comment on ZombieAttackPlant in GameHooks.cs), so the outer attack method's own Prefix/Postfix
    // brackets who is currently swinging. A single slot is safe: PVZ Fusion drives all combat from the
    // Unity main thread only, exactly like _meleePairsByTarget/_meleePairsFrame above. Frame-stamped so
    // a Postfix skipped by an exception in the original method (Harmony does not guarantee a postfix
    // runs after the patched method throws) cannot leak a stale attacker past its own frame.
    static IntPtr _ambientMeleeAttackerPtr = IntPtr.Zero;
    static int _ambientMeleeAttackerTypeId;

    /// <summary>2026-09-14 fix (lawn-combat-wire T10/T12, fourth defect): <c>Bullet.from</c>/
    /// <c>from_zombie</c> is only valid AT SPAWN — live trace proved it reads back
    /// <c>IntPtr.Zero</c> 100% of the time by the moment <c>TryRecordDealtFromBullet</c> runs
    /// (`Bullet.HitZombie`/`HitPlant`), for every one of 15 real hits in a clean session-ended
    /// window. The engine clears/does not retain the owner reference across the bullet's own
    /// flight lifetime (same pooling-shaped hazard as <c>InjectorEntityRegistry</c>'s Start()-once
    /// defect, different object). Fix: capture the shooter ptr/typeId once, in
    /// <c>GameHooks.BulletInit.Postfix</c> — the one point <c>from</c>/<c>from_zombie</c> is proven
    /// live — and read it back here by bullet ptr instead of re-reading the stale field. Cleared on
    /// read (a bullet fires its hit hook at most once) so this never grows across a match; a ptr
    /// that never hits (lands on ground, despawns) is simply never removed — one wasted 12-byte
    /// entry per whiffed bullet in a Dictionary keyed by ptr that Bullet.InitData will overwrite the
    /// next time that same freed address is reused, never a real leak. Main-thread only, exactly
    /// like <see cref="_ambientMeleeAttackerPtr"/> above.</summary>
    static readonly Dictionary<IntPtr, (IntPtr ShooterPtr, int ShooterTypeId)> _bulletShooterCache = new();

    /// <summary>Called from <c>GameHooks.BulletInit.Postfix</c> right after a bullet spawns, while
    /// its owner reference is still live. No-op for a zero ptr on either side.</summary>
    public static void CacheBulletShooter(IntPtr bulletPtr, IntPtr shooterPtr, int shooterTypeId)
    {
        if (bulletPtr == IntPtr.Zero || shooterPtr == IntPtr.Zero) return;
        _bulletShooterCache[bulletPtr] = (shooterPtr, shooterTypeId);
    }
    /// <summary>lawn-combat-wire L-N35: the shooter cached for <paramref name="bulletPtr"/>, without consuming it — the
    /// observer records a hit before <see cref="TryRecordDealtFromBullet"/> takes the entry, and must name the same shooter
    /// the RPG record will carry.</summary>
    public static bool TryPeekBulletShooter(IntPtr bulletPtr, out IntPtr shooterPtr)
    {
        shooterPtr = IntPtr.Zero;
        if (bulletPtr == IntPtr.Zero || !_bulletShooterCache.TryGetValue(bulletPtr, out var cached)) return false;
        shooterPtr = cached.ShooterPtr;
        return shooterPtr != IntPtr.Zero;
    }

    static int _ambientMeleeAttackerFrame = -1;

    public static EventDrain Drain => _drain ??= new EventDrain(EffectRuntime.OnDrained);

    /// <summary>Record path active — off in debug sessions (legacy fidelity path instead).</summary>
    static bool Active => Enabled && !DebugRuntime.SessionActive;

    // lawn-hit-entry (T9c): ptrs known dead — a Unity object can linger "dying but not destroyed"
    // for a frame or two (spec-lawn-hit-entry.md "Lifecycle correctness"), long enough for a SECOND
    // physical hit already in flight (e.g. two projectiles landing on the same target one frame
    // apart) to reach TryRecord*/TryRecordTaken against an already-dead ptr. Refusing those at
    // RECORD time (below) is simpler and cheaper than filtering at drain time, and needs no change
    // to DamagePacketBuilder/EntityStatWriter. Marked from GameHooks.ForgetEntity — the one shared
    // point both PlantDie and NoteZombieDead already funnel through, so both sides get this for
    // free. Cleared at match end (FlushAllAndReset) and per-ptr on a genuine spawn edge
    // (MarkSpawned, from the Plant.Start / Zombie.Start / Zombie.InitHealth postfixes) — L-N16:
    // IL2CPP reuses native addresses inside a match, and a mark that outlived its entity used to make
    // the next entity at that ptr refuse every RPG hit for the rest of the match. The same set also
    // gates the APPLY side (Liveness.AdmitsDelta in InjectorEffectActionSink): a record queued before
    // death that drains after it absorbs no delta and so cannot ForceKill — a second Die() — again.
    public static readonly EntityLiveness Liveness = new();

    /// <summary>See <see cref="Liveness"/>.</summary>
    public static void MarkDead(IntPtr ptr) => Liveness.MarkDead(ptr);

    /// <summary>See <see cref="Liveness"/>. Call only from a real spawn hook — never from a resync
    /// re-add, which also sees dying-but-not-destroyed objects.</summary>
    public static void MarkSpawned(IntPtr ptr) => Liveness.MarkSpawned(ptr);

    static bool IsDead(IntPtr ptr) => Liveness.IsDead(ptr);

    static int SafeFrame()
    {
        try { return Time.frameCount; }
        catch { return 0; }
    }

    /// <summary>Bullet dealt (pea→zombie, projectile→plant). No pair — the taken side is
    /// suppressed at source for bullets (CombatHitEmitPolicy), matching v1.
    /// <paramref name="wasBullet"/> distinguishes "not a bullet → caller records taken" from
    /// "bullet but ring overflow → drop counted, caller must NOT record taken" (I2).
    ///
    /// lawn-hit-attribution (T6): the recorded ATTACKER is the firing creature —
    /// <c>bullet.from</c> (Plant) or <c>bullet.from_zombie</c> (Zombie), chosen by
    /// <c>bullet.shootByZombie</c> — never the bullet's own pointer. A bullet ptr has no ActorHub
    /// baseline, so any Hub resolve on it fell to the stub <c>{Hp=100,MaxHp=100,Atk=10}</c>, and an
    /// <c>entity:{ptr}</c> grant bound to the firing plant could never match a projectile hit
    /// (spec-lawn-hit-attribution.md). <c>bullet.fromType</c> is deliberately NOT read here even
    /// though it is a real field — it is a <c>PlantType</c> id (the engine's own naming, not ours)
    /// and stays type-scoped for <c>GrantedBulletModifyAtoms</c>'s existing use; this module exists
    /// precisely because per-INSTANCE attribution was missing. The bullet's own ptr survives as the
    /// record's <see cref="GameEventRec.SwingPtr"/> — the swing identity, not the attacker.</summary>
    public static bool TryRecordDealtFromBullet(
        byte targetSide, Il2CppObjectBase? damageFrom, IntPtr targetPtr, int targetTypeId, long damage, out bool wasBullet,
        bool instakillShaped = false)
    {
        wasBullet = false;
        if (!Active || !EffectRuntime.HasOnDamageDealtGrant()) return false;
        // T9c liveness guard: an already-dead-or-dying target absorbs no further delta.
        if (IsDead(targetPtr)) return false;
        if (damageFrom == null) return false;

        Bullet? bullet = null;
        try { bullet = damageFrom.TryCast<Bullet>(); } catch { }
        if (bullet == null) return false; // melee is the AttackPlant site's job
        wasBullet = true;

        var shooterPtr = IntPtr.Zero;
        var shooterTypeId = 0;
        // Cache first (see _bulletShooterCache doc): bullet.from/from_zombie is proven stale by hit
        // time. Direct read stays as a fallback for a bullet whose spawn predates this cache (e.g.
        // one already in flight when the feature turned on mid-match).
        var cacheHit = _bulletShooterCache.TryGetValue(bullet.Pointer, out var cached);
        if (cacheHit)
        {
            shooterPtr = cached.ShooterPtr;
            shooterTypeId = cached.ShooterTypeId;
            _bulletShooterCache.Remove(bullet.Pointer);
        }
        else
        {
            try
            {
                if (bullet.shootByZombie)
                {
                    var z = bullet.from_zombie;
                    if (z != null)
                    {
                        shooterPtr = z.Pointer;
                        try { shooterTypeId = (int)z.theZombieType; } catch { }
                    }
                }
                else
                {
                    var p = bullet.from;
                    if (p != null)
                    {
                        shooterPtr = p.Pointer;
                        try { shooterTypeId = (int)p.thePlantType; } catch { }
                    }
                }
            }
            catch { shooterPtr = IntPtr.Zero; }
        }

        // A null shooter is ordinary — the firer already died, or an engine-spawned projectile with
        // no owner — never an error, and NEVER a fallback to the bullet's own ptr: that would
        // silently reintroduce the stub-resolve bug this module removes. No RPG contribution; the
        // caller still treats this as "was a bullet" (wasBullet stays true above) so the vanilla
        // taken-side record stays suppressed, matching existing bullet policy.
        if (shooterPtr == IntPtr.Zero)
        {
            if (FsmTrace.Enabled)
                CheatState.Note($"fsm-trace EventDrainHost.TryRecordDealtFromBullet DROPPED bulletPtr={bullet.Pointer:X} shooterPtr=0 (no shooter resolved)");
            return false;
        }

        var d = Drain;
        var recorded = d.Record(new GameEventRec(
            GameEventKind.CombatHit, SafeFrame(), d.NextSeq(),
            actorPtr: shooterPtr, targetPtr: targetPtr,
            typeId: shooterTypeId, targetTypeId: targetTypeId, side: targetSide,
            amount: -Math.Abs(damage), hitCount: 1,
            chainDepth: d.RecordDepth, sourceGrantIdx: -1,
            matchKeyIdx: d.InternMatchKey(GameHooks.MatchKey), pairId: 0,
            swingPtr: bullet.Pointer, instakillShaped: instakillShaped));
        if (FsmTrace.Enabled)
            CheatState.Note($"fsm-trace EventDrainHost.TryRecordDealtFromBullet recorded={recorded} shooterPtr={shooterPtr:X} targetPtr={targetPtr:X} damage={damage}");
        return recorded;
    }

    /// <summary>Melee dealt (zombie bite, or a plant-side area melee like Shulkflower's
    /// AttackEffect). Stamps a pair id that the target's TakeDamage taken record consumes in the
    /// same frame — the causal dealt/taken link (SSOT §D3). <paramref name="targetSide"/> is the
    /// SIDE OF THE TARGET (Plant for a zombie bite, Zombie for a plant-side area attack) — widened
    /// from a hardcoded Plant so this one function serves both directions rather than forking a
    /// second copy for the reverse attacker/target shape.</summary>
    public static bool TryRecordMeleeDealt(
        byte targetSide, IntPtr attackerPtr, int attackerTypeId, IntPtr targetPtr, int targetTypeId, long damage,
        bool instakillShaped = false)
    {
        if (!Active || !EffectRuntime.HasOnDamageDealtGrant()) return false;
        // T9c liveness guard: an already-dead-or-dying target absorbs no further delta.
        if (IsDead(targetPtr)) return false;
        var d = Drain;
        var frame = SafeFrame();
        var pair = ++_pairSeq;
        // swingPtr omitted (defaults to Zero): melee's swing identity is (ActorPtr, Frame),
        // already on the record — no bullet-shaped identity to carry.
        var ok = d.Record(new GameEventRec(
            GameEventKind.CombatHit, frame, d.NextSeq(),
            actorPtr: attackerPtr, targetPtr: targetPtr,
            typeId: attackerTypeId, targetTypeId: targetTypeId, side: targetSide,
            amount: -Math.Abs(damage), hitCount: 1,
            chainDepth: d.RecordDepth, sourceGrantIdx: -1,
            matchKeyIdx: d.InternMatchKey(GameHooks.MatchKey), pairId: pair,
            instakillShaped: instakillShaped));
        if (ok)
        {
            if (frame != _meleePairsFrame)
            {
                _meleePairsByTarget.Clear();
                _meleeAttackerByTarget.Clear();
                _meleePairsFrame = frame;
            }
            _meleePairsByTarget[targetPtr] = pair;
            _meleeAttackerByTarget[targetPtr] = attackerPtr;
        }
        return ok;
    }

    /// <summary>lawn-combat-wire L-N3: the attacker whose melee record for <paramref name="targetPtr"/> was taken this frame,
    /// without consuming it. The observer names a bite's attacker from this, so the vanilla row joins the RPG record, which
    /// carries the same attacker.</summary>
    public static bool TryPeekMeleeAttacker(IntPtr targetPtr, out IntPtr attackerPtr)
    {
        attackerPtr = IntPtr.Zero;
        if (targetPtr == IntPtr.Zero) return false;
        return _meleePairsFrame == SafeFrame()
            && _meleeAttackerByTarget.TryGetValue(targetPtr, out attackerPtr)
            && attackerPtr != IntPtr.Zero;
    }

    /// <summary>lawn-combat-wire L-N39: the attacker currently bracketed as ambient for this frame, if any — the observer reads
    /// it for a hit whose damageFrom names nobody, before the drain records that hit.</summary>
    public static bool TryPeekAmbientMeleeAttacker(out IntPtr attackerPtr)
    {
        attackerPtr = _ambientMeleeAttackerFrame == SafeFrame() ? _ambientMeleeAttackerPtr : IntPtr.Zero;
        return attackerPtr != IntPtr.Zero;
    }

    /// <summary>Begins the ambient-attacker bracket for a multi-target attack method with no target
    /// parameter of its own (see the class-level comment on the static fields above). Called from the
    /// attack method's own Harmony Prefix.</summary>
    public static void BeginAmbientMeleeAttacker(IntPtr attackerPtr, int attackerTypeId)
    {
        _ambientMeleeAttackerPtr = attackerPtr;
        _ambientMeleeAttackerTypeId = attackerTypeId;
        _ambientMeleeAttackerFrame = SafeFrame();
    }

    /// <summary>Ends the ambient-attacker bracket. Called from the attack method's own Harmony
    /// Postfix; safe to call even when no bracket is active.</summary>
    public static void EndAmbientMeleeAttacker()
    {
        _ambientMeleeAttackerPtr = IntPtr.Zero;
        _ambientMeleeAttackerTypeId = 0;
        _ambientMeleeAttackerFrame = -1;
    }

    /// <summary>
    /// Fallback dealt-record for a multi-target melee attack whose own Harmony hook has no target
    /// parameter to record from directly (QingZombie.AttackPlants / EternalZombie_a.AttackPlants) —
    /// TakeDamage's own hook knows the target but, per the class comment on ZombieAttackPlant in
    /// GameHooks.cs, cannot trust <c>damageFrom</c> to name the true attacker for a melee hit. No-ops
    /// (no exception, no fallback to the target's own ptr) when no ambient attacker is active, when
    /// the ambient is stale past its own frame (its Postfix never ran — e.g. the patched method
    /// threw), or when a single-target hook (AttackPlant's own Prefix) already claimed this exact
    /// target this frame — the ambient path exists only to cover targets that hook never sees, and
    /// must never double-record one it already did.
    /// </summary>
    public static bool TryRecordAmbientMeleeDealt(
        byte targetSide, IntPtr targetPtr, int targetTypeId, long damage, bool instakillShaped = false)
    {
        if (_ambientMeleeAttackerPtr == IntPtr.Zero) return false;
        var frame = SafeFrame();
        if (_ambientMeleeAttackerFrame != frame) return false;
        if (_meleePairsFrame == frame && _meleePairsByTarget.ContainsKey(targetPtr)) return false;
        return TryRecordMeleeDealt(
            targetSide, _ambientMeleeAttackerPtr, _ambientMeleeAttackerTypeId, targetPtr, targetTypeId, damage,
            instakillShaped);
    }

    /// <summary>
    /// lawn-combat-wire L-N36: a zombie hit whose <paramref name="damageFrom"/> is a Plant, not a Bullet — FumeShroom's fumes
    /// arrive this way (live 2026-09-15: 23 hits, no dealt record, no rider). Records the plant as the melee attacker, so the
    /// swing is (plant, frame) and a fume that reaches N zombies in one frame fires one trigger. Skips a target that already
    /// has a melee record this frame, the same check <see cref="TryRecordAmbientMeleeDealt"/> uses: Shulkflower/WaterShulk
    /// record their victims in <c>AttackEffect</c> before this hit reaches <c>TakeDamage</c>.
    /// </summary>
    public static bool TryRecordDirectPlantDealt(
        byte targetSide, Il2CppObjectBase? damageFrom, IntPtr targetPtr, int targetTypeId, long damage, bool instakillShaped = false)
    {
        if (damageFrom == null || targetSide != GameEventSide.Zombie) return false;
        Plant? plant = null;
        try { plant = damageFrom.TryCast<Plant>(); } catch { }
        if (plant == null || plant.Pointer == IntPtr.Zero || plant.Pointer == targetPtr) return false;
        var frame = SafeFrame();
        if (_meleePairsFrame == frame && _meleePairsByTarget.ContainsKey(targetPtr))
        {
            if (FsmTrace.Enabled)
                CheatState.Note($"fsm-trace EventDrainHost.TryRecordDirectPlantDealt skipped=already-recorded-this-frame plantPtr={plant.Pointer:X} targetPtr={targetPtr:X} frame={frame}");
            return false;
        }
        var plantTypeId = 0;
        try { plantTypeId = (int)plant.thePlantType; } catch { }
        var recorded = TryRecordMeleeDealt(targetSide, plant.Pointer, plantTypeId, targetPtr, targetTypeId, damage, instakillShaped);
        if (FsmTrace.Enabled)
            CheatState.Note($"fsm-trace EventDrainHost.TryRecordDirectPlantDealt recorded={recorded} plantPtr={plant.Pointer:X} targetPtr={targetPtr:X} frame={frame} dead={IsDead(targetPtr)} active={Active}");
        return recorded;
    }

    /// <summary>Damage taken (OnDamageTaken trigger). Consumes a same-frame melee pair when the
    /// target matches, so the coalescer suppresses the double-fire.</summary>
    public static bool TryRecordTaken(byte side, IntPtr targetPtr, int targetTypeId, IntPtr actorPtr, long damage)
    {
        if (!Active || !EffectRuntime.HasOnDamageTakenGrant()) return false;
        // T9c liveness guard: an already-dead-or-dying target absorbs no further delta.
        if (IsDead(targetPtr)) return false;
        var d = Drain;

        var pair = 0;
        if (_meleePairsFrame == SafeFrame() && _meleePairsByTarget.TryGetValue(targetPtr, out var mp))
        {
            pair = mp;
            _meleePairsByTarget.Remove(targetPtr);
        }

        return d.Record(new GameEventRec(
            side == GameEventSide.Plant ? GameEventKind.PlantDamage : GameEventKind.ZombieDamage,
            SafeFrame(), d.NextSeq(),
            actorPtr: actorPtr, targetPtr: targetPtr,
            typeId: targetTypeId, targetTypeId: targetTypeId, side: side,
            amount: -Math.Abs(damage), hitCount: 1,
            chainDepth: d.RecordDepth, sourceGrantIdx: -1,
            matchKeyIdx: d.InternMatchKey(GameHooks.MatchKey), pairId: pair));
    }

    /// <summary>bullet.init → OnSpawn, only while an OnSpawn grant is live.</summary>
    public static bool TryRecordBulletSpawn(IntPtr bulletPtr, int plantType)
    {
        if (!Active || !EffectRuntime.HasOnSpawnGrant()) return false;
        var d = Drain;
        return d.Record(new GameEventRec(
            GameEventKind.BulletInit, SafeFrame(), d.NextSeq(),
            actorPtr: bulletPtr, targetPtr: IntPtr.Zero,
            typeId: plantType, targetTypeId: 0, side: GameEventSide.Bullet,
            amount: 0, hitCount: 1,
            chainDepth: d.RecordDepth, sourceGrantIdx: -1,
            matchKeyIdx: d.InternMatchKey(GameHooks.MatchKey), pairId: 0));
    }

    // Rolling stats for the perf window (read + reset by PerfReporter).
    static long _processedWindow;
    static long _carriedWindow;
    static long _expensiveDeferredWindow;
    static int _maxLatencyFramesWindow;
    static long _maxRecordTicksWindow;
    // v4 item 1: per-frame death-flush allowance pool (one drain-budget's worth per frame).
    static long _lastBudgetTicks;
    static int _deathFlushFrame = -1;
    static long _deathFlushSpent;

    /// <summary>
    /// Per-frame drain (plan Task 10): budget = 10% of the measured frame time (spec decision
    /// #3), clamped to [0.2ms, 2ms]. Session mode drains unbudgeted and uncoalesced.
    /// CAP RATIONALE (ssot-power-scale.md §11 / tunables-ssot.md): the clamp below is a STRUCTURAL
    /// per-frame cap, not a progression ceiling and not a balance tunable — it bounds how long the
    /// drain may hold the Unity main thread inside one frame, is derived from that frame's own
    /// measured time, and exhausting it carries records to the next frame (G5: delayed effects,
    /// never frame drops). Hardcoded on purpose; it has no `gk-core/data/tuning` row.
    /// Shares one board freeze with the kernel's DoT pulse, which runs later in the same frame.
    /// </summary>
    public static void Tick(float unscaledDeltaTime)
    {
        if (!Enabled || _drain == null || _drain.PendingCount == 0) return;
        using var _perf = FusionRpg.Core.Diagnostics.PerfProbe.Measure(FusionRpg.Core.Diagnostics.PerfSection.DrainTick);

        var frameSec = unscaledDeltaTime > 0f ? unscaledDeltaTime : 1f / 60f;
        var budgetSec = Math.Clamp(frameSec * 0.10, 0.0002, 0.002);
        var budgetTicks = (long)(budgetSec * System.Diagnostics.Stopwatch.Frequency);
        _lastBudgetTicks = budgetTicks;

        _drain.SessionMode = DebugRuntime.SessionActive;
        EffectRuntime.FreezeBoard();
        var stats = _drain.Drain(budgetTicks, SafeFrame());
        _processedWindow += stats.Processed;
        _carriedWindow += stats.Carried;
        _expensiveDeferredWindow += stats.ExpensiveDeferred;
        if (stats.MaxLatencyFrames > _maxLatencyFramesWindow) _maxLatencyFramesWindow = stats.MaxLatencyFrames;
        if (stats.MaxRecordTicks > _maxRecordTicksWindow) _maxRecordTicksWindow = stats.MaxRecordTicks;

        // T9c: Tick is the one call site guaranteed never to be nested itself — by the time
        // _drain.Drain(...) above returns, EventDrain's own nested-flush queue has already forced
        // through every ptr any nested FlushForPtr call queued during this pass, so it is now safe
        // to run the grant-withdraw callbacks that were deferred alongside them.
        // Each ptr is flushed once more (mop up anything that arrived since it was queued) before
        // its forget runs — the ordering contract DeferredForgetQueueTests pins.
        _pendingForgets.RunDue(ptr => FlushForPtr(ptr));
    }

    /// <summary>Perf-window stats for PerfReporter — returns and resets the rolling counters.</summary>
    public static Dictionary<string, object> SnapshotStats()
    {
        var d = new Dictionary<string, object>
        {
            ["enabled"] = Enabled,
            ["pending"] = _drain?.PendingCount ?? 0,
            ["droppedOverflow"] = _drain?.DroppedByOverflow ?? 0,
            ["droppedDepth"] = _drain?.DroppedByDepth ?? 0,
            ["droppedDeathBudget"] = _drain?.DroppedByDeathBudget ?? 0,
            ["droppedDeathBudgetLivingTarget"] = _drain?.DroppedByDeathBudgetLivingTarget ?? 0,
            ["maxLatencyFrames"] = _maxLatencyFramesWindow,
            ["maxRecordUs"] = _maxRecordTicksWindow > 0
                ? Math.Round(_maxRecordTicksWindow * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency, 0)
                : 0,
            ["processed"] = _processedWindow,
            ["carried"] = _carriedWindow,
            ["expensiveDeferred"] = _expensiveDeferredWindow
        };
        // lawn-combat-wire L-N27: dead marks held, and hits this window on entities that still had health but were marked dead.
        var (staleHits, stalePtr) = Liveness.TakeStaleMarkHits();
        d["livenessDeadMarks"] = Liveness.DeadCount;
        d["staleDeadMarkHits"] = staleHits;
        d["staleDeadMarkLastPtr"] = stalePtr == IntPtr.Zero ? "" : stalePtr.ToString("X");
        _processedWindow = 0;
        _carriedWindow = 0;
        _expensiveDeferredWindow = 0;
        _maxLatencyFramesWindow = 0;
        _maxRecordTicksWindow = 0;
        return d;
    }

    // lawn-hit-entry (T9c): grant-withdraw callbacks deferred because the matching EventDrain.
    // FlushForPtr call came back nested (-1) — see FlushForPtr's own doc. Drained by Tick(), the
    // one call site that is NEVER itself nested (it is the top-level per-frame entry point), right
    // after its own Drain() call returns — by which point EventDrain's own nested-flush queue (see
    // EventDrain.DrainPendingNestedFlushes) has already forced every one of these ptrs' records
    // through, so it is finally safe to withdraw their grants.
    static readonly DeferredForgetQueue _pendingForgets = new();

    /// <summary>
    /// Death barrier — pending hits for a dying entity drain before grant-withdraw (SSOT §A2).
    /// v4 item 1: bounded by a per-frame allowance (one drain-budget's worth); over-allowance
    /// records for dying entities are shed with a counter instead of blowing the frame.
    /// Returns true when the flush ran for real (the common, non-nested case) — the caller may
    /// then withdraw this ptr's grants immediately, exactly as before this method returned a
    /// value at all. Returns false when the underlying <see cref="EventDrain.FlushForPtr"/> call
    /// came back nested (T9c) — the caller must NOT withdraw grants yet; pass the withdraw action
    /// to the <paramref name="onSafeToForget"/> overload instead of calling it directly.
    /// </summary>
    public static bool FlushForPtr(IntPtr ptr)
    {
        if (_drain == null || _drain.PendingCount == 0) return true;

        var frame = SafeFrame();
        if (frame != _deathFlushFrame)
        {
            _deathFlushFrame = frame;
            _deathFlushSpent = 0;
        }
        var allowance = _lastBudgetTicks > 0 ? _lastBudgetTicks : System.Diagnostics.Stopwatch.Frequency / 500; // 2ms fallback
        var remaining = allowance - _deathFlushSpent;
        if (remaining <= 0) remaining = 0; // still processes nothing beyond budget; drops counted

        EffectRuntime.FreezeBoard();
        var spent = _drain.FlushForPtr(ptr, remaining);
        if (spent < 0) return false; // nested — EventDrain queued it; do not spend/return yet
        _deathFlushSpent += spent;
        return true;
    }

    /// <summary>
    /// T9c: the caller-friendly form of <see cref="FlushForPtr(IntPtr)"/> — always safe to call
    /// unconditionally in place of the old "flush then forget" two-step. Runs
    /// <paramref name="onSafeToForget"/> (typically <c>() =&gt; ForgetEntity(ptr)</c>) immediately
    /// when the flush was not nested (the overwhelmingly common case, byte-identical timing to
    /// before this fix), or queues it to run from <see cref="Tick"/> once the outer drain pass that
    /// nested this call has fully unwound (still strictly before grants withdraw — just not
    /// synchronously inside the nested caller's own stack frame).
    /// </summary>
    public static void FlushForPtr(IntPtr ptr, Action onSafeToForget)
    {
        if (FlushForPtr(ptr)) onSafeToForget();
        else DeferForget(ptr, onSafeToForget);
    }

    /// <summary>
    /// T9c: queues a grant-withdraw for a ptr whose own <see cref="FlushForPtr(IntPtr)"/> call
    /// already ran and returned false (nested) — for a caller that needs the flush and the
    /// forget-or-defer decision at two DIFFERENT points in its own method (e.g. an Emit in
    /// between), rather than the single combined <see cref="FlushForPtr(IntPtr, Action)"/> call.
    /// Runs from <see cref="Tick"/> exactly like every other deferred forget.
    /// </summary>
    public static void DeferForget(IntPtr ptr, Action onSafeToForget) => _pendingForgets.Defer(ptr, onSafeToForget);

    /// <summary>Match-edge barrier — drain everything, reset interning (board.end / match.result).</summary>
    public static void FlushAllAndReset()
    {
        if (_drain == null) return;
        if (_drain.PendingCount > 0)
            EffectRuntime.FreezeBoard();
        _drain.FlushAllAndReset();
        _meleePairsByTarget.Clear();
        _meleeAttackerByTarget.Clear();
        _meleePairsFrame = -1;
        Liveness.Clear();
        _pendingForgets.Clear(); // match is over — nothing queued for it may fire into the next one
        EndAmbientMeleeAttacker();
    }
}
