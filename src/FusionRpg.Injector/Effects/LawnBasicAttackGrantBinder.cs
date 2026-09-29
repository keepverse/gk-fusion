using FusionRpg.Core.Combat;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// lawn-combat-wire T10 (spec-basic-attack-grant.md): binds the per-actor basic-attack grant at spawn
/// so `EffectRuntime.HasOnDamageDealtGrant()` is true for every lawn actor, plant and zombie, specimen
/// and general creature alike.
///
/// <para><b>Record-then-drain, the SAME shape <c>EventDrainHost</c>/<c>MoveDrainHost</c> already use
/// (`InjectorLoop.Tick`), for the identical reason.</b> <c>LawnElementResolverHost.Resolve</c> caches
/// per ptr per match, but the board scan behind a cache MISS goes through
/// <c>InjectorBoardSnapshot.Capture()</c>, which is cached only <i>per rendered frame</i> and is
/// invalidated by every spawn (`PlantStart`/zombie-spawn postfixes call `Invalidate()` before this
/// class ever runs). Resolving inline from the spawn hook itself would mean a wave of N zombies
/// spawning in the same frame forces N fresh board captures — each spawn's own invalidate stomping the
/// previous one's freshly-warmed snapshot. Queuing the ptr here and draining once per frame (AFTER every
/// spawn hook for that frame has already fired) means the batch's first resolve pays for one capture and
/// every other ptr in the same frame hits the frame cache, not the board scan.</para>
///
/// <para><b>2026-09-14 fix (lawn-combat-wire T10/T12 live-inert investigation, second defect —
/// grant-bind/registry-registration race): a spawn hook's own `Emit("plant.spawn"/"zombie.spawn", ...)`
/// is NOT guaranteed to run after `InjectorEntityRegistry.Add` for that same ptr.</b> For an ORGANIC
/// spawn (`PlantStart`/`ZombieStart`/`ZombieInitHealth` Harmony postfixes) `Add` always runs first, in
/// the same call stack, before `ApplyPlant`/`ApplyZombie` ever emits — safe by construction. But
/// `EntityApply.RunPlant`/`RunZombie` also has callers OUTSIDE that call stack — most visibly
/// `DebugActions.SpawnPlant`/`SpawnZombie`, which call `RunPlant`/`RunZombie` directly right after
/// creating the entity — and there is no engine guarantee that the entity's OWN `Start`/`InitHealth`
/// (which is what actually calls `InjectorEntityRegistry.Add`) has fired yet at that point: Unity may
/// defer a freshly-instantiated object's own lifecycle callbacks to a later point in the same frame or
/// the next one. `Bind` below used to resolve once and give up for good on a board-miss — so a ptr
/// resolved one frame too early lost its grant forever, with no error anywhere (confirmed live: 2 of 4
/// otherwise-identical `debug.spawn-plant`/`debug.spawn-zombie` calls lost this exact race, verified via
/// `POST /api/debug/effect/list` showing `grants:0` for the missed ptr, persisting for 60+ seconds with
/// no retry). A ptr that misses now requeues for a further <see cref="MaxRetryFrames"/> frames before
/// being dropped, so a same-frame-or-next-frame registration (the overwhelmingly common shape of this
/// race) still gets its grant. A ptr that is STILL unresolvable after that many frames really is gone
/// (died before ever being registered) and is dropped exactly as before.</para>
///
/// <para><b>2026-09-14, third defect, the actual root cause of the live symptom surviving the retry fix
/// above:</b> the "board-miss" check this class relied on used to be <c>typeId == 0</c> — but species
/// index 0 (Peashooter on the plant side, NormalZombie on the zombie side — the two most common default
/// spawns in every test this whole program ran) is a real creature, not a sentinel. Retrying a wrong
/// check just repeats the same wrong answer, which is why <see cref="LawnCombatObserver"/> kept reading
/// <c>actionTriggers=0</c> after the retry fix shipped. <see cref="LawnElementResolverHost.Resolve"/> now
/// returns a real <c>Found</c> flag, decoupled from the numeric typeId — see its own doc for the live
/// evidence (`gk-data/packs/fusion/data/generated/creatures/Peashooter.json` / `NormalZombie.json`, both
/// <c>gameTypeId: 0</c>).</para>
/// </summary>
public static class LawnBasicAttackGrantBinder
{
    /// <summary>How many EXTRA frames a ptr that missed its board-fact resolve gets requeued for before
    /// being dropped for good. Structural retry bound, not a balance tunable (tunables-ssot.md) — the
    /// race this exists for resolves within one or two frames or not at all; a bigger number only delays
    /// discovering a genuinely-dead ptr, it never binds one that was never going to resolve.</summary>
    const int MaxRetryFrames = 8;

    static readonly object Gate = new();
    static readonly List<string> Pending = new();
    static readonly List<(string Ptr, int RetriesLeft)> PendingRetry = new();
    static readonly FusionRpg.Core.Combat.FeatureSwitchEdge SwitchEdge = new();

    /// <summary>Set by <see cref="MarkThetaDirty"/> on the snapshot edge, cleared by <see cref="Tick"/>
    /// on the main thread. Guarded by <see cref="Gate"/> like every other field here.</summary>
    static bool _thetaDirty;

    /// <summary>Tests only (the <c>HeldActionIdsForTest</c> precedent): how many <see cref="Bind"/>
    /// calls the dirty drain has made. <see cref="Bind"/> cannot succeed without a live board, so a
    /// headless test cannot observe the drain through the grant bag — it observes it here instead.</summary>
    public static int RebindAttemptsForTest { get; private set; }

    /// <summary>Tests only: clears both test-visible counters, so a test never reads a value a previous
    /// one left behind.</summary>
    public static void ResetRebindAttemptsForTest()
    {
        RebindAttemptsForTest = 0;
        LastBakedAmountForTest = 0;
    }

    /// <summary>Tests only: the magnitude <see cref="Bind"/> last computed. The three refresh orderings
    /// the spec lists all have to end on the SAME amount, and this is the value they are compared on —
    /// it is written where the real bake happens, so it cannot drift from it.</summary>
    public static long LastBakedAmountForTest { get; private set; }

    /// <summary>action-enrich AE2.3 (spec-lawn-action-base.md §Refresh triggers): the RECORD half of
    /// record-then-drain. The baked amount depends on the owner's Θ, so an edge that moves Θ must
    /// re-bake — but the snapshot arrives on the socket's thread, and a grant call there would touch
    /// Unity. This is the whole snapshot-side cost: one lock-protected O(1) flag, no grant call, no
    /// board read. <see cref="Tick"/> drains it on the main thread.</summary>
    public static void MarkThetaDirty()
    {
        lock (Gate) _thetaDirty = true;
    }

    /// <summary>Drain half of <see cref="MarkThetaDirty"/>: re-run <see cref="Bind"/> for every live
    /// grant this binder owns, enumerated exactly as <see cref="WithdrawAllBound"/> does (same grant
    /// list, same id filter). The deterministic <c>GrantId</c> makes each call an idempotent upsert, so
    /// two dirty drains in a row leave one grant per ptr with one amount.</summary>
    static void RebindAllBound()
    {
        const string entityPrefix = "entity:";
        foreach (var grant in EffectRuntime.Bag.Grants.All().ToList())
        {
            if (!FusionRpg.Core.Combat.BasicAttackGrantBuilder.IsBasicAttackGrantId(grant.GrantId)) continue;
            if (!grant.OwnerKey.StartsWith(entityPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            RebindAttemptsForTest++;
            try { Bind(grant.OwnerKey.Substring(entityPrefix.Length)); }
            catch (Exception ex) { CheatState.Error("lawn-basic-attack rebind: " + ex.Message); }
        }
    }

    static void WithdrawAllBound()
    {
        ClearPending();
        var withdrawn = 0;
        foreach (var grant in EffectRuntime.Bag.Grants.All().ToList())
        {
            if (!FusionRpg.Core.Combat.BasicAttackGrantBuilder.IsBasicAttackGrantId(grant.GrantId)) continue;
            try { if (EffectRuntime.Withdraw(grant.GrantId)) withdrawn++; }
            catch (Exception ex) { CheatState.Error("lawn-basic-attack withdraw: " + ex.Message); }
        }
        CheatState.Note($"lawn-basic-attack switched off: withdrew {withdrawn} bound grants");
    }

    /// <summary>Record a spawn — called from `MatchHost.Apply` on `plant.spawn`/`zombie.spawn`, inside
    /// the SAME board-fold apply the spec asks this to key on. No board read, no grant call: O(1).</summary>
    public static void QueueSpawn(string? ptr)
    {
        if (string.IsNullOrWhiteSpace(ptr)) return;
        lock (Gate) Pending.Add(ptr!);
    }

    /// <summary>Drain the batch — called once per frame from `InjectorLoop.Tick`, the same slot
    /// `EventDrainHost.Tick`/`MoveDrainHost.Tick` already occupy. A no-op when nothing spawned this
    /// frame (the common case) or the feature is off (kill switch — queue is drained-and-discarded so
    /// it never grows unbounded while disabled).</summary>
    public static void Tick()
    {
        // L-N8: the switch turning off mid-match must take the already-bound grants with it, or their riders keep
        // applying with no cost (ShouldApplyRider passes OnDamageDealt through while the switch is off).
        if (SwitchEdge.TurnedOff(LawnBasicAttackFeature.Enabled))
            WithdrawAllBound();

        List<string> batch;
        List<(string Ptr, int RetriesLeft)> retryBatch;
        bool thetaDirty;
        lock (Gate)
        {
            thetaDirty = _thetaDirty;
            _thetaDirty = false;
            if (Pending.Count == 0 && PendingRetry.Count == 0 && !thetaDirty) return;
            batch = new List<string>(Pending);
            Pending.Clear();
            retryBatch = new List<(string, int)>(PendingRetry);
            PendingRetry.Clear();
        }

        if (!LawnBasicAttackFeature.Enabled) return;

        // The snapshot edge only recorded; this is where Θ actually moves, on the main thread.
        if (thetaDirty) RebindAllBound();

        foreach (var ptr in batch)
            TryBindOrRequeue(ptr, MaxRetryFrames);

        foreach (var (ptr, retriesLeft) in retryBatch)
            TryBindOrRequeue(ptr, retriesLeft);
    }

    static void TryBindOrRequeue(string ptr, int retriesLeft)
    {
        bool bound;
        try { bound = Bind(ptr); }
        catch (Exception ex)
        {
            CheatState.Error("lawn-basic-attack bind: " + ex.Message);
            return; // an exception is a real failure, not a "not registered yet" race -- never requeue it
        }

        if (bound || retriesLeft <= 0) return;
        lock (Gate) PendingRetry.Add((ptr, retriesLeft - 1));
    }

    /// <summary>Match-edge reset — a ptr queued in a match that ended before its drain must not bind
    /// against the NEXT match's board. Called from `GameHooks.ClearMatch`.
    ///
    /// <para>AE2.3: the Θ-dirty mark is dropped here for exactly the same reason. A snapshot that
    /// arrived at the end of a match leaves its record behind, and draining it in the next one would
    /// rebind the previous match's grants — the stale-edge hazard this method already exists to stop,
    /// one field along.</para></summary>
    public static void ClearPending()
    {
        lock (Gate)
        {
            Pending.Clear();
            PendingRetry.Clear();
            _thetaDirty = false;
        }
    }

    /// <returns><c>true</c> when a grant was actually bound this call; <c>false</c> when the board does
    /// not know this ptr YET (or anymore) — the caller decides whether that is worth a retry.</returns>
    static bool Bind(string ptr)
    {
        // action-enrich AE2.2/AE2.3 (spec-lawn-action-base.md §Design, §Refresh triggers): the baked
        // magnitude is resolved FIRST. It depends only on Θ and the tuning — never on the board — and it
        // is THE value the record-then-drain contract is about, so a spawn the board does not know YET
        // still resolves its amount, and an unconfigured hub is reported on the first bind attempt
        // rather than only for an actor the board happens to know (the stronger reading of "never a
        // silent zero"). Θ is read ONCE per bind, never per hit, from the provider the Hub's
        // `progression.power` is written from — `CheatState.PowerIndex` is keyed by player id ALONE, so
        // both lawn sides read the commander's Θ (lawn-tuning-profile, 2026-09-16). The base is the
        // basic attack's own tuning value (rung 0, so no rung row is read on the lawn), multiplied by
        // P(Θ) through the SAME `BattleRuleset.PowerValue` the battle hit uses — parity by construction.
        var thetaCtx = new FusionRpg.Core.Stats.StatContext
        {
            PlayerId = CheatState.CurrentPlayerId > 0 ? CheatState.CurrentPlayerId : null,
        };
        var amount = -FusionRpg.Core.Actions.ActionBaseMath.BasePerHit(
            FusionRpg.Core.Actions.ActionBaseDerivation.BasePowerMilli(
                FusionRpg.Core.Actions.ActionKind.Basic, "act.attack", effectiveRung: 0,
                FusionRpg.Core.Actions.Rungs.RungPolicy.Table,
                FusionRpg.Core.Actions.ActionBaseTuningHub.Tuning),
            FusionRpg.Core.Battle.BattleRuleset.PowerValue(CheatState.PowerIndex.ActorIndex(thetaCtx)));
        LastBakedAmountForTest = amount;

        // The same board-fact resolve InjectorCombatBridge/InjectorStatusBridge already share (E27) —
        // never a second board scan. `Found` is a real flag (LawnElementResolverHost's own 2026-09-14
        // "corrected same day" doc) — typeId 0 is NOT a sentinel here, it is Peashooter's/NormalZombie's
        // real species index, so this must never gate on the numeric typeId. A ptr the board genuinely
        // does not (yet, or any longer) know about has nothing to bind THIS frame; the caller requeues a
        // fresh spawn's ptr for a few frames (see this class's own 2026-09-14 doc) rather than assuming a
        // later real spawn will re-queue it, since a genuinely dead ptr never spawns again to do so. This
        // never lets a stale element survive: the resolve here reads whatever the resolver's cache holds
        // for THIS ptr right now, not what it held at queue time, so a same-frame death+reuse at the same
        // address still resolves the CURRENT occupant's own species, never the dead one's.
        var (_, _, elements, found) = LawnElementResolverHost.Resolve(ptr);
        if (!found) return false;

        // L-N6: an element pin wins over the species element here exactly as it does when this actor defends
        // (InjectorCombatBridge, E27) — otherwise a pinned actor defends as the pin and attacks as its species.
        var attack = FusionRpg.Injector.Stats.InjectorElementOverride.TryGet(ptr, out var pinned) ? pinned : elements;

        // Inert-at-default dual-typing (HybridPayload's own doc: 0 weight collapses to the single
        // full-weight primary component) — raising it for lawn actors is a balance decision this
        // module does not make; spec-basic-attack-grant.md names only the primary/secondary source.
        var dto = BasicAttackGrantBuilder.Build(ptr, attack.Primary, attack.Secondary, secondaryWeightMilli: 0, amount: amount);
        EffectRuntime.GrantQuiet(dto);
        return true;
    }
}
