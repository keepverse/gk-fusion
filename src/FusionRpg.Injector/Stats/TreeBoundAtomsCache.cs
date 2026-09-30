using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived.Subsystems;

namespace FusionRpg.Injector.Stats;

/// <summary>
/// lawn-tree-hydrate (T13): the injector-side cache for the commander-scope shared-tree
/// <see cref="BoundDerivedAtom"/>s <c>TreeBoundAtoms.ForPlayer</c> already fans into the Server sheet's
/// Hub (<c>UniqueActorHubCompose</c>). The Injector has no SQL store to call that method itself, so
/// <see cref="RpgClient.RefreshTreeBoundAtomsAsync"/> fetches the already-resolved atoms over HTTP and
/// stores them here — this class only ever reads/replaces the cache, exactly the shape
/// <see cref="CheatState.ApplyCommanderAllocation"/> already uses for the sibling aptitude cache.
///
/// <para><b>Universal, not per-entity.</b> Like commander aptitude, these atoms apply to EVERY actor
/// this player controls on the lawn, never filtered by <see cref="StatContext"/> — the same "commander
/// scope" semantics <c>TreeBoundAtoms.ForPlayer</c> itself already locks (it explicitly excludes
/// species/per-creature trees). <see cref="For"/> therefore ignores its <paramref name="ctx"/>
/// parameter entirely; it exists only to match the <c>boundDerivedAtoms</c> delegate shape
/// <see cref="CheatState.ActorHub"/> requires.</para>
/// </summary>
public static class TreeBoundAtomsCache
{
    static volatile IReadOnlyList<BoundDerivedAtom> _atoms = Array.Empty<BoundDerivedAtom>();

    /// <summary>Called from the transport (<c>RpgClient.RefreshTreeBoundAtomsAsync</c>) after a
    /// successful fetch. A tree spend changes every living entity's resolve, so this is a stat
    /// invalidation like <see cref="CheatState.ApplyCommanderAllocation"/>'s own — never touched from
    /// a hot-path stat resolve.</summary>
    public static void Apply(IReadOnlyList<BoundDerivedAtom> atoms)
    {
        _atoms = atoms;
        CheatState.Stats.Invalidate();
    }

    /// <summary>Every cached commander-scope tree atom. Never null; never throws — an empty cache (no
    /// fetch yet, or the player owns no shared-tree nodes) is a normal state, not an error.</summary>
    public static IReadOnlyList<BoundDerivedAtom> For(StatContext ctx) => _atoms;
}
