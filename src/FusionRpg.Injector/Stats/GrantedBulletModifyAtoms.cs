using System;
using System.Collections.Generic;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Injector.Stats;

/// <summary>
/// The injector half of the lawn <c>bullet.modify</c> executor (E37, spec-projectile-control.md §2b)
/// — a thin adapter over <see cref="GrantedBulletModifyAtomReader"/>, the same split
/// <see cref="GrantedDerivedAtoms"/> already uses for <c>stat.derived</c> and for the identical reason:
/// everything Unity-free (walking owner scopes, reading a def's action row) lives in Core where a test
/// can reach it; only reaching the live <c>EffectRuntime.Bag</c> static and the fired <see cref="Bullet"/>
/// itself are genuinely host-specific.
/// </summary>
public static class GrantedBulletModifyAtoms
{
    /// <summary>Every bound <c>bullet.modify</c> atom for the plant that fired this bullet, plus any
    /// match-scoped grant. Never null; never throws — no live match (or no <c>from</c> plant, e.g. a
    /// zombie-fired or debug-spawned bullet) is a normal state, not an error.</summary>
    public static IReadOnlyList<BoundBulletModifyAtom> For(Bullet bullet)
    {
        if (bullet == null) return Array.Empty<BoundBulletModifyAtom>();

        IEffectGrantStore grants;
        IEffectCatalog catalog;
        try
        {
            var bag = Effects.EffectRuntime.Bag;
            grants = bag.Grants;
            catalog = bag.Catalog;
        }
        catch { return Array.Empty<BoundBulletModifyAtom>(); }

        string? plantOwnerKey = null;
        try { plantOwnerKey = EffectOwnerKeys.PlantType((int)bullet.fromType); }
        catch { /* no `from` plant on this bullet (e.g. zombie-fired) -- match-scope grants still apply */ }

        return GrantedBulletModifyAtomReader.Read(grants, catalog, plantOwnerKey);
    }
}
