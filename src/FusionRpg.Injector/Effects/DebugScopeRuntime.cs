using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Scope;
using FusionRpg.Injector.Match;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// T11 test harness — makes the buff-debuff-scope program's own-side mechanism reachable from a real,
/// live match, which nothing before this did (Core-only through T13, no Server/Injector wiring).
/// Deliberately NOT the real specimen-ownership bridge (that Cold-plane `player_id` read does not
/// exist yet, per buff-debuff-scope-ideal.md §4.1) — this oracle always answers the same relation for
/// every ptr, so it tests exactly what this program built (the event-driven grant/withdraw mechanism
/// reacting to real spawn/death/hypno-toggle events), not the ownership-resolution work that was never
/// in scope here.
/// </summary>
sealed class AlwaysRelationOracle : IOwnSideOracle
{
    readonly RelationKind _relation;
    public AlwaysRelationOracle(RelationKind relation) => _relation = relation;
    public RelationKind? RelationOf(string ptr) => _relation;
}

public static class DebugScopeRuntime
{
    static BattlefieldOwnSideReactor? _active;

    /// <summary>
    /// Wires a `BattlefieldOwnSideReactor` to the live match's real `EffectBag` (`EffectRuntime.Bag`)
    /// and real `MembershipChanged` event (`MatchHost.Runtime`) — every spawn from this point on gets
    /// the grant (or, for a G8-shaped kind on `Live`, this call itself throws before anything
    /// subscribes, per `ScopeCompatibility` — proving the same refusal T10 already proved in tests,
    /// now reachable live).
    /// </summary>
    public static void StartOwnSide(string effectId, string pluginId, string atomKindId, ScopeHost host, string? channel, RelationKind relation)
    {
        StopOwnSide();

        var reactor = new BattlefieldOwnSideReactor(
            EffectRuntime.Bag, new AlwaysRelationOracle(relation), relation,
            effectId, pluginId, grantIdPrefix: "debugscope:" + pluginId,
            atomKindId, host, channel);

        MatchHost.Runtime.MembershipChanged += reactor.OnMembershipChanged;
        _active = reactor;
    }

    /// <summary>Unsubscribes and withdraws every grant this debug reactor issued.</summary>
    public static void StopOwnSide()
    {
        if (_active == null) return;
        MatchHost.Runtime.MembershipChanged -= _active.OnMembershipChanged;
        _active.WithdrawAll();
        _active = null;
    }
}
