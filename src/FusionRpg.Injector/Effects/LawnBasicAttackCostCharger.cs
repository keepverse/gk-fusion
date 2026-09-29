using System.IO;
using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Corpus;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Observability;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Injector.Host;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// `lawn-combat-wire` T12 (spec-basic-attack-cost.md): the lawn's own charge site — the SAME
/// <see cref="CostLedger"/>/<see cref="LawnBasicAttackCostGate"/> types battle uses, wired for the
/// injector's own actor-resolution and clock. Never a second cost authority (`guard-actor-hub.py`).
///
/// <para><b>Where <see cref="CostLedger"/> is called on the lawn, and its position relative to the
/// swing dedupe (spec's own explicit ask):</b> from <c>EffectRuntime.OnDrained</c>, via
/// <see cref="ShouldApplyRider"/> — called on EVERY drained <c>OnDamageDealt</c> record, strictly
/// AFTER `lawn-hit-entry`'s (T9) swing dedupe has already run (that dedupe lives in
/// <c>EventDrain.Drain</c>/<c>ConsumeSwingTriggerAndRelease</c>, which stamps
/// <see cref="EffectEventDto.IsFirstOfSwing"/> before the record ever reaches the drain callback this
/// class hooks into). This class never recomputes swing identity — it only reads the already-resolved
/// flag, so a multi-victim swing cannot be double-charged (only the first record's call reaches
/// <see cref="CostLedger.TryPay"/> at all — see <see cref="LawnBasicAttackCostGate"/>) and a genuinely
/// new swing is never skipped (a fresh <c>swingId</c> always charges on its own first-of-swing
/// record).</para>
///
/// <para><b>The lawn clock:</b> <see cref="NowTick"/> is <c>KernelDriveHost.NowTicks / 100</c> — one
/// lawn "tick" is 100 milliseconds of SIMULATED (scaled, pause-respecting) time, the SAME unit
/// <see cref="FusionRpg.Core.Battle.BattleModels.BaseResourceRegen"/> already assumes
/// (<c>TicksPerSecond = 10</c>, that method's own doc). <c>KernelDriveHost.NowTicks</c> freezes at
/// <c>Time.timeScale == 0</c> (`KernelDriveHost.Tick`'s own pause guard: `if (!(scaledDeltaTime &gt;
/// 0f)) return;`), so regen accrues on this grid and does NOT run while paused — inherited for free
/// from that already-shipped clock, with no separate pause check needed here.</para>
///
/// <para><b>Lawn pool lifecycle (spec's own "state it, do not inherit it"):</b> match-scoped and full
/// at spawn — <see cref="InjectorEntityRegistry.ResourcePools"/> creates each ptr's pool the first
/// time it is touched (<see cref="LawnActorResourcePools.GetOrCreate"/>, always via
/// <c>ActorResourcePools.CreateFull</c>) and drops it on death/board-clear
/// (<c>InjectorEntityRegistry.Remove</c>/<c>Clear</c>). This matches <c>resource-hub-ssot.md</c>'s
/// "persist across a run, refill at rest" ONLY in the sense that a run here IS one match — there is no
/// cross-match persistence and no mid-match "rest" refill, which is the same reading
/// `spec-basic-attack-cost.md` already recorded and the ideal doc already reconciled.</para>
/// </summary>
public static class LawnBasicAttackCostCharger
{
    static LawnBasicAttackCostGate? _gate;
    static bool _diagnosed;
    static long _costAmount;

    /// <summary>T12b (regen-accrued telemetry only — never load-bearing for the ledger itself, which
    /// is lazy-pull correct from <see cref="NowTick"/> alone): last stamina value observed per ptr at
    /// the previous kernel tick, so <see cref="KernelTick"/> can report the POSITIVE delta as regen
    /// accrued without needing access to <see cref="ActorResourcePools"/>'s own private pre-settle
    /// state (that type is `resource-subtick`/T5-owned and this module must not edit it).</summary>
    static readonly Dictionary<string, long> LastObservedStamina = new(StringComparer.Ordinal);

    /// <summary>One lawn tick == 100 ms of simulated time — see this class's own doc comment.</summary>
    static long NowTick() => KernelDriveHost.NowTicks / 100;

    static ActorDerivedSnapshot DerivedFor(string ptr) => InjectorStatusBridge.ResolveDerived(ptr, attackerLess: false);

    /// <summary>
    /// Lazily builds the lawn's <see cref="LawnBasicAttackCostGate"/> from the SAME shipped
    /// `action-corpus-cost-templates.v{n}.json` `basic-attack-cost` (T11/T12) already reads —
    /// loud-once-on-failure, never retried, mirroring <c>LawnBasicAttackRow.TryGet</c>'s (T8) own
    /// established shape exactly.
    /// </summary>
    static LawnBasicAttackCostGate? TryGetGate()
    {
        if (_gate is not null) return _gate;
        if (_diagnosed) return null;

        try
        {
            var tuningDir = Path.Combine(RpgHost.PluginDir, "data", "tuning");
            var template = ActionCorpusCostTemplateLoader.Parse(
                File.ReadAllText(Path.Combine(tuningDir, "action-corpus-cost-templates.v2.json")));
            var row = template.ResolveFor(ActionKind.Basic, ActionCategory.Attack);
            _costAmount = row.BaseAmountAtRung1;

            // The exact failure class named in this task's own restored acceptance criterion: this
            // charge site calls CostLedger.TryPay with ActionCostTiming.OnCommit (below) -- if a
            // future balance pass ever re-authors kinds.basic.timing to "perTick" without updating
            // this call site too, CostLedger.RowsFor would find ZERO rows for OnCommit and TryPay
            // would silently report Success/Paid with NOTHING spent (rows.Count == 0 short-circuit).
            // Fail loudly at construction instead of shipping that silent-vacuous gate.
            if (row.Timing != ActionCostTiming.OnCommit)
                throw new InvalidOperationException(
                    $"lawn-basic-attack-cost: kinds.basic.timing is '{row.Timing}', not OnCommit -- the " +
                    "lawn charge site (LawnBasicAttackCostGate) only ever calls CostLedger.TryPay with " +
                    "OnCommit. Update THIS charge site (and its onCommit-only read) before retuning the " +
                    "timing, or the cost gate silently stops charging anything.");

            var costs = LawnBasicAttackCostRow.Build(template);
            var ledger = new CostLedger(
                costsByActionId: costs,
                poolsFor: ptr => InjectorEntityRegistry.ResourcePools.GetOrCreate(ptr, DerivedFor(ptr), NowTick()),
                derivedFor: DerivedFor,
                // The lawn has no rung concept for the basic attack (no unlock ladder gates it) --
                // rung 1 is RungPolicy's own shipped inert row (CostMulti=1000), the same "byte-
                // identical until content opts in" reading CostLedgerTests.cs already documents.
                rungOf: (_, _) => 1,
                nowTick: NowTick);
            _gate = new LawnBasicAttackCostGate(ledger);
            return _gate;
        }
        catch (Exception ex)
        {
            _diagnosed = true;
            RpgHost.Log.Error(
                "lawn-basic-attack-cost: construction failed and will NOT be retried -- " +
                $"{ex.GetType().Name}: {ex.Message}. Every lawn basic-attack cost charge fails CLOSED " +
                "(no elemental rider, matching D2/D6) until this is fixed -- most likely " +
                "action-corpus-cost-templates.v2.json is missing from the plugin's data/tuning folder.");
            return null;
        }
    }

    /// <summary>
    /// Called from <c>EffectRuntime.OnDrained</c> for EVERY drained record — untouched passthrough
    /// (returns <c>true</c>) for every trigger other than <c>OnDamageDealt</c>, and for
    /// <c>OnDamageDealt</c> when the feature's kill switch is off (byte-identical to before this
    /// feature existed: with the switch off, `basic-attack-grant`/T10 never binds the grant in the
    /// first place, so this method existing at all changes nothing observable).
    ///
    /// <para>For a real lawn OnDamageDealt record: charges the swing's cost exactly once (on
    /// <see cref="EffectEventDto.IsFirstOfSwing"/>) and returns whether the elemental rider may apply
    /// — <c>false</c> means "no resource, no [RPG] trigger" (D6): the caller must skip
    /// <c>Bag.OnEvent</c>/<c>AtomPushReceiver.OnEvent</c> for this record entirely. The vanilla shot
    /// itself is untouched either way — it already landed before this event pipeline ever runs.</para>
    /// </summary>
    public static bool ShouldApplyRider(EffectEventDto ev)
    {
        if (!string.Equals(ev.Trigger, EffectTriggers.OnDamageDealt, StringComparison.OrdinalIgnoreCase))
            return true; // every other trigger (OnSpawn, OnActivate, OnDeath, ...) is untouched by this gate

        if (!LawnBasicAttackFeature.Enabled) return true; // kill switch: byte-identical to today

        var actorPtr = ev.ActorPtr;
        if (string.IsNullOrWhiteSpace(actorPtr)) return false; // no attacker identity to charge -- fail closed

        var gate = TryGetGate();
        if (gate is null) return false; // construction failed once, already logged loudly -- fail closed

        var key = CombatPtr.Normalize(actorPtr);
        var swingId = string.IsNullOrWhiteSpace(ev.SwingId) ? key : ev.SwingId!;

        bool afforded;
        try { afforded = gate.TryChargeForSwing(key, swingId, ev.IsFirstOfSwing); }
        catch (Exception ex)
        {
            RpgHost.Log.Warning("lawn-basic-attack-cost: charge failed: " + ex.Message);
            return false; // fail closed -- a charge that could not run is not a free rider
        }

        // Observer (T0) hooks -- once per swing only, alongside the ledger's own once-per-swing charge.
        if (ev.IsFirstOfSwing)
        {
            LawnCombatObserver.RecordActionTrigger(swingId); // D8: one attack, one trigger
            if (afforded) LawnCombatObserver.RecordStaminaSpent(_costAmount);
            else LawnCombatObserver.RecordExhaustion();

            // lawn LW1.3 (spec-exhaustion-event.md): the transition the counter above cannot see. The
            // charge just moved this pool, so its live value is read fresh and the lifecycle answers the
            // EDGE -- one actor.exhausted per window, never one per refused swing (defect E1) -- and
            // syncs the status on the same edge (defect E3: the policy was built and inert).
            ObserveExhaustion(key, ev.Side, ev.MatchKey);
        }

        return afforded;
    }

    /// <summary>
    /// T12b — the kernel's third kind (<c>KernelDriveHost</c>'s own 100 ms grid, alongside DoT pulse
    /// and shield upkeep). NOT required for the ledger's own correctness — every
    /// <see cref="ActorResourcePools.Resolve"/>/<see cref="ActorResourcePools.TrySpend"/> call already
    /// reads live off <see cref="NowTick"/> with no periodic tick needed (S10.1's whole point). This
    /// exists to produce the observer's (T0) own "regen accrued" reading — a real, bounded settle
    /// point, never a fabricated number.
    /// </summary>
    public static void KernelTick()
    {
        if (!LawnBasicAttackFeature.Enabled) return;

        var pools = InjectorEntityRegistry.ResourcePools.Snapshot();
        if (pools.Count == 0) return;

        var nowTick = NowTick();
        foreach (var (ptr, pool) in pools)
        {
            long now;
            try { now = pool.Resolve("stamina", nowTick, DerivedFor(ptr)); }
            catch { continue; } // a ptr the board no longer knows about -- next tick heals or drops it

            // A spend between two ticks reads as a negative delta here and is floored to zero rather
            // than reported as negative regen -- RecordStaminaSpent (above) already counts spends, so
            // this stays scoped to what the pool gained on its own, never double-counting a charge.
            if (LastObservedStamina.TryGetValue(ptr, out var prev) && now > prev)
                LawnCombatObserver.RecordRegenAccrued(now - prev);
            LastObservedStamina[ptr] = now;

            // lawn LW1.3: the same read is the RECOVERING edge's observation point. An actor that regens
            // back above zero between two swings emits actor.recovered here, rather than only at its next
            // swing -- which for an idle actor would be never.
            ObserveExhaustion(ptr, side: null, matchKey: GameHooks.MatchKey);
        }
    }

    /// <summary>
    /// LW1.3 — hand one ptr's live pool value to the exhaustion lifecycle. Silent when this ptr has no
    /// pool yet (nothing was observed, so nothing is emitted), and loud-but-non-throwing otherwise: this
    /// sits on the per-swing and per-kernel-tick paths, and neither an observation nor a status sync may
    /// ever change what a swing costs.
    /// </summary>
    static void ObserveExhaustion(string ptr, string? side, string? matchKey)
    {
        try
        {
            if (!InjectorEntityRegistry.ResourcePools.TryGet(ptr, out var pools)) return;
            var nowTick = NowTick();
            var value = pools.Resolve(LawnExhaustionLifecycle.ResourceId, nowTick, DerivedFor(ptr));
            LawnExhaustionLifecycle.ObserveAndEmit(ptr, value, nowTick, side, matchKey);
        }
        catch (Exception ex)
        {
            RpgHost.Log.Warning("lawn-exhaustion: pool read failed: " + ex.Message);
        }
    }

    /// <summary>Match-edge reset — mirrors <c>LawnBasicAttackGrantBinder.ClearPending</c>'s own
    /// convention. Called from <c>GameHooks.ClearMatch</c>. Only clears THIS class's own bookkeeping
    /// (the regen-telemetry baseline); <see cref="InjectorEntityRegistry.Clear"/> already owns
    /// dropping the pools themselves.</summary>
    public static void ClearMatchState() => LastObservedStamina.Clear();
}
