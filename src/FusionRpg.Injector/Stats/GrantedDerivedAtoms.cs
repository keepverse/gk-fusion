using System;
using System.Collections.Generic;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived.Subsystems;

namespace FusionRpg.Injector.Stats;

/// <summary>
/// The injector half of the lawn `stat.derived` executor (decisions.md "Derived-write lawn executor",
/// 2026-08-30) — now a thin adapter over <see cref="GrantedDerivedAtomReader"/>.
///
/// <para><b>Why the reading logic moved to Core (aura-skill-todo.md Phase 5 / TC3).</b> Everything this
/// class used to do — walking owner scopes, matching the namespaced overlay keys, parsing op/amount —
/// is Unity-free, but living here made it <b>untestable by anything CI runs</b>: this assembly targets
/// net6.0 against the game's BepInEx/Il2Cpp interop DLLs, so it needs a real PVZ Fusion install to
/// build, and `ci.yml` names ten test projects, none of them the injector. The FA1/FA10 overlay-key
/// collision guard in particular was asserted only by a comment. It now has a real regression test
/// (`GrantedDerivedAtomReaderTests`) because the logic it guards lives somewhere a test can reach.</para>
///
/// <para>What is left here is exactly what is genuinely host-specific and cannot be tested off-host:
/// reaching the live <c>EffectRuntime.Bag</c> static. Which owner keys apply to a given Unity entity
/// stays an injector fact (`entity:{ptr}` needs a ptr, `plant:{typeId}` needs the game's type) — but
/// those all arrive on <see cref="StatContext"/>, so the derivation itself travels with the reader.</para>
/// </summary>
public static class GrantedDerivedAtoms
{
    /// <summary>Every bound derived atom that applies to this actor. Never null; never throws — a bag
    /// that is not up yet (no live match) is a normal state, not an error.</summary>
    public static IReadOnlyList<BoundDerivedAtom> For(StatContext ctx)
    {
        if (ctx is null) return Array.Empty<BoundDerivedAtom>();

        IEffectGrantStore grants;
        IEffectCatalog catalog;
        try
        {
            var bag = Effects.EffectRuntime.Bag;
            grants = bag.Grants;
            // The CATALOG is not optional in production (aura-skill-todo.md Phase 5 / TC2). The real
            // grant path -- BattlefieldOwnSideReactor.BuildGrant -- emits an EffectId and NO overlay,
            // so the values live on the def's ModifyDerivedStat action rows. Reading grants without
            // the catalog is exactly the state that made this executor inert on the live lawn while
            // every offline test passed.
            catalog = bag.Catalog;
        }
        catch { return Array.Empty<BoundDerivedAtom>(); }

        return GrantedDerivedAtomReader.Read(grants, catalog, ctx);
    }
}
