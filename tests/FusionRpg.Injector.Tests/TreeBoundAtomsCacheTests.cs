using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using FusionRpg.Injector;
using FusionRpg.Injector.Stats;
using Xunit;

namespace FusionRpg.Injector.Tests;

/// <summary>
/// lawn-tree-hydrate (T13): <see cref="TreeBoundAtomsCache"/> is the injector-side half of the
/// "Injector has no store, Server resolves once, Injector fetches + caches" shape
/// <c>RpgClient.RefreshTreeBoundAtomsAsync</c>/<c>CheatState.ActorHub</c> wire it into. Shares
/// <see cref="CheatState"/>'s static entries (same reset/isolation discipline as
/// <c>MatchModifyTests</c>/<c>WaveControlTests</c>).
/// </summary>
[Collection("CheatState statics")]
public class TreeBoundAtomsCacheTests
{
    public TreeBoundAtomsCacheTests()
    {
        CheatState.EmitProof = false;
        CheatState.ResetAll();
        TreeBoundAtomsCache.Apply(Array.Empty<BoundDerivedAtom>());
    }

    [Fact]
    public void For_onAFreshCache_isEmpty_notAnError()
    {
        var result = TreeBoundAtomsCache.For(new StatContext { PlayerId = 1 });
        Assert.Empty(result);
    }

    [Fact]
    public void Apply_thenFor_returnsExactlyWhatWasApplied_regardlessOfCtx()
    {
        var atoms = new[]
        {
            new BoundDerivedAtom("combat.power.fire", DerivedModifierOp.Flat, 40, "tree.might.skill.might-off-t1-n0"),
            new BoundDerivedAtom("combat.power.omni", DerivedModifierOp.Increased, 100, "tree.fortitude.skill.fortitude-off-t1-n0")
        };
        TreeBoundAtomsCache.Apply(atoms);

        // Universal, not per-entity (commander scope, same as TreeBoundAtoms.ForPlayer itself) --
        // two different StatContexts must see the SAME cached list.
        var a = TreeBoundAtomsCache.For(new StatContext { PlayerId = 1, Side = StatSide.Plant });
        var b = TreeBoundAtomsCache.For(new StatContext { PlayerId = 1, Side = StatSide.Zombie });
        Assert.Equal(atoms, a);
        Assert.Equal(atoms, b);
    }

    [Fact]
    public void Apply_replacesThePriorSet_neverMerges()
    {
        TreeBoundAtomsCache.Apply(new[] { new BoundDerivedAtom("combat.power.fire", DerivedModifierOp.Flat, 40, "tree.a.n0") });
        TreeBoundAtomsCache.Apply(new[] { new BoundDerivedAtom("combat.power.ice", DerivedModifierOp.Flat, 10, "tree.b.n0") });

        var result = TreeBoundAtomsCache.For(new StatContext { PlayerId = 1 });
        Assert.Single(result);
        Assert.Equal("combat.power.ice", result[0].Channel);
    }

    [Fact]
    public void Apply_invalidatesStats_soTheNewAtomsReachAnAlreadyResolvedActor()
    {
        // Mirrors CheatState.ApplyCommanderAllocation's own regression proof (aura-skill T5): without
        // an edge-triggered invalidate, a tree spend would silently do nothing until the next
        // unrelated stat invalidation happened to fire.
        CheatState.Stats.ConsumeDirty(out _);
        TreeBoundAtomsCache.Apply(new[] { new BoundDerivedAtom("combat.power.fire", DerivedModifierOp.Flat, 1, "tree.a.n0") });
        Assert.True(CheatState.Stats.ConsumeDirty(out _));
    }
}
