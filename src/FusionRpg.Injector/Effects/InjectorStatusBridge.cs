using FusionRpg.Core.Status;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Injector.Stats;

namespace FusionRpg.Injector.Effects;

/// <summary>Bridge ActorHub derived resolve for StatusRuntime L2b.</summary>
public static class InjectorStatusBridge
{
    public static StatusRuntime CreateRuntime()
    {
        return new StatusRuntime(
            StatusCatalogHub.Current,
            ResolveDerived);
    }

    public static ActorDerivedSnapshot ResolveDerived(string? entityPtr, bool attackerLess)
    {
        if (!TryBuildContext(entityPtr, attackerLess, out var ctx, out var pinned))
            return pinned ?? ActorDerivedSnapshot.AttackerLess();
        if (pinned is not null) return pinned;
        return CheatState.ActorHub.ResolveDerived(ctx!);
    }

    /// <summary>Debug / proof path — same compose as <see cref="ResolveDerived"/> with GG-49 bags.</summary>
    public static (ActorDerivedSnapshot Snapshot, DerivedContributionBag Contributions)
        ResolveDerivedWithContributions(string? entityPtr, bool attackerLess)
    {
        if (!TryBuildContext(entityPtr, attackerLess, out var ctx, out var pinned))
            return (pinned ?? ActorDerivedSnapshot.AttackerLess(), DerivedContributionBag.From(Array.Empty<DerivedModifier>()));
        if (pinned is not null)
            return (pinned, DerivedContributionBag.From(Array.Empty<DerivedModifier>()));
        return CheatState.ActorHub.ResolveDerivedWithContributions(ctx!);
    }

    static bool TryBuildContext(
        string? entityPtr,
        bool attackerLess,
        out StatContext? ctx,
        out ActorDerivedSnapshot? pinnedOverride)
    {
        ctx = null;
        pinnedOverride = null;
        if (attackerLess || string.IsNullOrWhiteSpace(entityPtr))
            return false;

        var key = entityPtr.Trim();
        if (InjectorDerivedOverride.TryGet(key, out var pinned))
        {
            pinnedOverride = pinned;
            return true;
        }

        var hub = CheatState.ActorHub;
        if (!hub.Stats.TryGetBaseline(key, out var baseline)
            && !hub.Stats.TryGetBaseline(key.ToUpperInvariant(), out baseline))
            baseline = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 };

        // E27 (spec-lawn-element-bind.md §2.4): the shared LawnElementResolverHost replaces this
        // bridge's own board scan — a cache hit here is free when InjectorCombatBridge already
        // resolved the same ptr for the same match, and either bridge's first call for a ptr warms it
        // for the other.
        var (side, typeId, elementTypes, _) = LawnElementResolverHost.Resolve(key);

        ctx = string.Equals(side, "zombie", StringComparison.OrdinalIgnoreCase)
            ? hub.Stats.Contexts.ForZombie(
                key,
                baseline,
                typeId,
                matchKey: GameHooks.MatchKey,
                playerId: CheatState.PvzStatsPlayerId > 0 ? CheatState.PvzStatsPlayerId : null,
                cheatScale: CheatState.EffectiveStats(),
                pvzStatsMods: CheatState.PvzStatsMods,
                elementTypes: elementTypes)
            : hub.Stats.Contexts.ForPlant(
                key,
                baseline,
                typeId,
                matchKey: GameHooks.MatchKey,
                playerId: CheatState.PvzStatsPlayerId > 0 ? CheatState.PvzStatsPlayerId : null,
                cheatScale: CheatState.EffectiveStats(),
                pvzStatsMods: CheatState.PvzStatsMods,
                elementTypes: elementTypes);
        return true;
    }
}
