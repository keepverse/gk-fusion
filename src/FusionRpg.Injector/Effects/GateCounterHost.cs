using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.PassiveTree.GateCounters;
using FusionRpg.Core.Status;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// passive-tree-todo.md G6 — the injector's composition root for the two gate counters
/// (spec-gate-counters.md §2, §4.3, §7). Everything G1-G5 built (<see cref="StatusAppliedCounter"/>,
/// <see cref="ElementMasteryCounter"/>, <see cref="GateCounterAccumulator"/>) takes its dependencies as
/// delegates specifically so this file — the one place allowed to know about the live game — can supply
/// them without either counter ever referencing Unity or the DAL boundary
/// (<see cref="StatusAppliedSource"/>'s own doc comment: "the composition root wires the real closure").
///
/// <para><b>Ownership resolution — spawn-time roster, never current allegiance (§2.1's closing rule).</b>
/// <see cref="LawnElementResolverHost.Resolve"/> already answers "is this ptr a plant or a zombie" from
/// the actor's own OBJECT KIND (the shared board-scan cache both combat bridges already read), which is
/// invariant under a charm/hypno status — those flip which side a ZOMBIE currently fights for; they do
/// not turn it into a Plant. That is exactly the property the spec's charm/hypno closing rule needs:
/// crediting reads spawn kind, never "which team is this unit helping right now." A ptr this host cannot
/// resolve (torn-down, or off the tracked board) answers no owner, never a guessed one.</para>
///
/// <para><b>Single player, by construction.</b> Every plant-side actor is credited to
/// <see cref="CheatState.CurrentPlayerId"/> — the one player this lawn overlay ever tracks (there is no
/// PvP in this game). A zombie-side actor (charmed or not) is never credited, matching §2.1(a)/(d) and
/// §2.2(a): only an actor this player owns earns anything.</para>
/// </summary>
public static class GateCounterHost
{
    static readonly object Gate = new();
    static GateCounterAccumulator? _accumulator;
    static StatusAppliedCounter? _statusCounter;
    static ElementMasteryCounter? _elementCounter;

    public static void Ensure()
    {
        if (_accumulator != null) return;
        lock (Gate)
        {
            if (_accumulator != null) return;
            _accumulator = new GateCounterAccumulator();
            _statusCounter = new StatusAppliedCounter(_accumulator, ResolveStatusOwner);
            _elementCounter = new ElementMasteryCounter(_accumulator, ResolveElementOwner);
        }
    }

    /// <summary>Wire directly: <c>runtime.OnFreshApplication += GateCounterHost.StatusCounter.Handle;</c></summary>
    public static StatusAppliedCounter StatusCounter
    {
        get { Ensure(); return _statusCounter!; }
    }

    /// <summary>
    /// Adapter for <see cref="Core.Effects.EffectBag.OnDamageApplied"/>'s four-argument shape into
    /// <see cref="ElementMasteryCounter.Handle"/>'s single <see cref="ElementMasteryCreditInput"/> —
    /// wire directly: <c>bag.OnDamageApplied = GateCounterHost.HandleDamageApplied;</c>
    /// </summary>
    public static void HandleDamageApplied(
        DamageApplyResult result, DamageOrigin origin, IReadOnlyList<ElementPayloadComponent> components, string? attackerPtr)
    {
        if (FsmTrace.Enabled)
            CheatState.Note($"fsm-trace CombatDamageDispatcher.OnDamageApplied outcome={result.Outcome} appliedAmount={result.AppliedAmount} absorbedAmount={result.AbsorbedAmount} origin={origin} attackerPtr={attackerPtr}");
        Ensure();
        _elementCounter!.Handle(new ElementMasteryCreditInput(result, origin, components, attackerPtr));
    }

    /// <summary>
    /// §4.3's timer half — call on the same cadence <c>PerfReporter.Flush</c> already runs on
    /// (<c>gateCounters.flushIntervalMs</c> defaults to 5000ms, explicitly "the window PerfProbe already
    /// uses" per spec §4.3, and <c>PerfReporter.IntervalSeconds</c> is that exact window already
    /// configured and running in this process — <c>PassiveTreeTuningHub</c> is never configured here,
    /// only server-side, so this reads the shared cadence rather than adding a second tuning load to the
    /// injector for one number). Also call unconditionally at match end (§4.3's other trigger).
    ///
    /// <para>Best-effort and silent on failure, matching every other periodic reporter on this loop
    /// (<c>PerfReporter.Flush</c>): a lost window costs a little progress, never correctness (§4.3).</para>
    /// </summary>
    public static void Flush(RpgClient? client)
    {
        Ensure();
        var drained = _accumulator!.DrainAndClear();
        if (drained.Count == 0) return;

        var credits = drained.Select(kv => new
        {
            ownerKind = kv.Key.OwnerKind,
            ownerKey = kv.Key.OwnerKey,
            quantity = kv.Key.Quantity,
            subjectId = kv.Key.SubjectId,
            delta = kv.Value
        }).ToList();

        _ = client?.PostGateCounterCreditAsync(new { credits });
    }

    static GateOwnerKey? ResolveStatusOwner(StatusInstance instance) => ResolveOwnerFromPtr(instance.AttackerPtr);

    static GateOwnerKey? ResolveElementOwner(string attackerPtr) => ResolveOwnerFromPtr(attackerPtr);

    static GateOwnerKey? ResolveOwnerFromPtr(string? ptr)
    {
        if (string.IsNullOrWhiteSpace(ptr)) return null;

        var playerId = CheatState.CurrentPlayerId;
        if (playerId <= 0) return null;

        string side;
        try { (side, _, _, _) = LawnElementResolverHost.Resolve(CombatPtr.Normalize(ptr)); }
        catch { return null; } // an unresolvable ptr credits nobody, never a guess (§2.1's closing rule).

        if (!string.Equals(side, "plant", StringComparison.OrdinalIgnoreCase))
            return null; // zombie-side, charmed or not -- spawn kind, never current allegiance.

        // AptitudeEndpoints.ScopeKey(playerId)'s exact "player:{id}" shape (GateCounterKey.cs's own doc
        // comment) -- reconstructed here rather than shared because FusionRpg.Injector does not, and
        // must not, reference FusionRpg.Server (separate deployable, AGENTS.md's module table).
        return new GateOwnerKey("player", "player:" + playerId);
    }
}
