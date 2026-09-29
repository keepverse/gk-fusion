using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Match;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Injector.Effects;

namespace FusionRpg.Injector.Match;

/// <summary>
/// On Bound: maxHp becomes a durable Hub `progression.bonus.maxHp` grant (`entity:{ptr}`), current hp
/// becomes a one-shot Funnel delta toward the loadout's target — never a Writer absolute beside Hub
/// (`bound-loadout-hub`, T14). Never applies type-wide <c>plant:N</c> / <c>zombie:N</c> loadout keys.
///
/// <para><b>Why a durable grant, not a one-shot field poke (the defect this replaces).</b> The old
/// `ApplyAbsolutes` wrote `p.attackDamage`/`p.thePlantMaxHealth` directly (the old `atk` write bypassed
/// `EntityStatWriter` entirely) at Bind time only — the very next ordinary `EntityApply.RunPlant`
/// reapply (stat invalidation, PushScales, a status/effect tick) recomposes `AppliedCombat` from Hub
/// + StatSystem with no memory of this override and silently reverts it. A grant lives in
/// `EffectRuntime.Bag` and is re-read by `GrantedDerivedAtoms`/`AtomDerivedSubsystem` on every single
/// resolve, so the bonus survives every future reapply the way any other Hub contribution does.</para>
///
/// <para><b>Delta, not a literal set.</b> `progression.bonus.maxHp` is a `FlatSum` channel
/// `ActorHub.MergeAppliedCombat` adds onto the StatSystem-resolved primary (same shape
/// `RpgProgressionSubsystem`'s own leveling bonuses already use) — never an independent absolute.
/// A loadout's `atk` key no longer grants anything: `progression.bonus.atk` was retired 2026-09-18
/// (solid-enforcement `retire-atk` R4), so there is no channel left for it to feed. The key is still
/// accepted (stored loadout JSON keeps parsing); it is simply inert.</para> The delta is computed once, from this ptr's
/// live field value at bind time, toward the loadout's target: a faithful port of the old code's own
/// "read live, reach target" behavior, just landed as a durable additive bonus instead of a transient
/// overwrite.</para>
///
/// <para><b>Current hp is a Funnel delta, not folded into the maxHp bonus.</b> `MergeAppliedCombat`
/// raises `Hp` by the SAME amount as `MaxHp` (a max-hp bonus heals by the bonus, matching every other
/// progression-bonus consumer) — that alone cannot express "spawn already below the new max" the
/// loadout's separate `hp` key asks for. A Funnel mutation (`channel=hp`, `mode` unset = add) is the
/// one sanctioned HP-delta path (`guard-funnel-delta.py`); TryGuardMutation rejects `mode=set` and any
/// `absoluteHp`/`setHp` overlay key outright, so an absolute current-hp write is structurally
/// impossible here — a target below the eventual composed max can only be reached as a delta, exactly
/// like this same specimen taking damage or healing in an ordinary match.</para>
/// </summary>
public static class UniqueBoundLoadout
{
    public static void TryApply(UniqueBinding? bound)
    {
        if (bound == null || bound.Phase != UniqueBindingPhase.Bound) return;
        if (string.IsNullOrWhiteSpace(bound.Ptr)) return;

        try
        {
            var spec = UniqueLoadoutSpec.Parse(bound.LoadoutJson).BindToPtr(bound.Ptr!);
            if (spec.IsEmpty) return;

            var funnel = EffectRuntime.Bag.Funnel;
            ApplyAbsolutesAsHubBonuses(bound.Side, bound.Ptr!, spec, funnel);
            foreach (var g in spec.Grants)
            {
                if (UniqueOwnerBinder.WouldRejectOnHot(g.OwnerKey))
                    continue; // binder must have rewritten; fail-closed skip
                try { funnel?.EnqueueModifier(g); } catch { /* fail-closed */ }
            }

            try { funnel?.Flush(); } catch { /* fail-closed */ }
        }
        catch (Exception ex)
        {
            try { CheatState.Error("UniqueBoundLoadout: " + ex.Message); } catch { }
        }
    }

    static void ApplyAbsolutesAsHubBonuses(string side, string ptrHex, UniqueLoadoutSpec spec, EffectFunnel? funnel)
    {
        var abs = spec.ToCheatAbsoluteMap();
        if (abs.Count == 0) return;

        var want = MatchUniqueBindingsFacet.NormalizePtr(ptrHex);
        var isPlant = string.Equals(side, "plant", StringComparison.OrdinalIgnoreCase);
        var hasHp = abs.TryGetValue("hp", out var hp);
        var hasMax = abs.TryGetValue("maxHp", out var maxHp);
        // `atk` is deliberately not read (see the class note): the retired channel has no grant target.
        // A loadout carrying only `atk` has nothing to grant and returns below.
        if (!hasHp && !hasMax) return;

        // Same grammar UniqueOwnerBinder.ToEntityKey/BindGrant already use for spec.Grants above --
        // one owner-key construction for every Bound-ptr grant this class ever produces.
        var ownerKey = UniqueOwnerBinder.ToEntityKey(instanceId: null, ptrHex);

        if (isPlant)
        {
            foreach (var p in UnityEngine.Object.FindObjectsOfType<Plant>())
            {
                try
                {
                    if (p == null) continue;
                    if (!string.Equals(MatchUniqueBindingsFacet.NormalizePtr(GameDumps.Ptr(p)), want,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (hasMax || hasHp)
                    {
                        var targetMax = Math.Max(1, hasMax ? maxHp : hp);
                        var targetHp = hasHp ? Math.Max(1, hp) : targetMax;
                        if (targetHp > targetMax) targetHp = targetMax;

                        GrantBonus(funnel, ownerKey, ptrHex, "maxhp", DerivedStatChannels.ProgressionBonusMaxHp,
                            targetMax - p.thePlantMaxHealth);
                        EnqueueHpDelta(funnel, ptrHex, targetHp - p.thePlantHealth);
                    }
                    return;
                }
                catch { /* next */ }
            }
        }
        else
        {
            foreach (var z in UnityEngine.Object.FindObjectsOfType<Zombie>())
            {
                try
                {
                    if (z == null) continue;
                    if (!string.Equals(MatchUniqueBindingsFacet.NormalizePtr(GameDumps.Ptr(z)), want,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (hasMax || hasHp)
                    {
                        var targetMax = Math.Max(1L, hasMax ? maxHp : hp);
                        var targetHp = hasHp ? Math.Max(1L, hp) : targetMax;
                        if (targetHp > targetMax) targetHp = targetMax;

                        GrantBonus(funnel, ownerKey, ptrHex, "maxhp", DerivedStatChannels.ProgressionBonusMaxHp,
                            targetMax - Bridges.ZombieCombatFields.GetMaxHp(z));
                        EnqueueHpDelta(funnel, ptrHex, targetHp - Bridges.ZombieCombatFields.GetHp(z));
                    }
                    return;
                }
                catch { /* next */ }
            }
        }
    }

    /// <summary>One durable `progression.bonus.*` grant, GrantId stable per (ptr, tag) so a re-bind
    /// replaces rather than accumulates (`EffectFunnel.EnqueueModifier` keys by GrantId). `EffectId`
    /// deliberately never resolves in the catalog -- `GrantedDerivedAtomReader.CollectFromDef` finds no
    /// def and falls through to its overlay path, exactly the "direct-grant/debug shape" that path
    /// exists for, so this needs no registered EffectDef.</summary>
    static void GrantBonus(EffectFunnel? funnel, string ownerKey, string ptrHex, string tag, string channel, double delta)
    {
        if (funnel is null || delta == 0) return;
        try
        {
            funnel.EnqueueModifier(new EffectGrantDto
            {
                GrantId = "unique.bound." + tag + ":" + ptrHex,
                EffectId = "unique.bound.derived",
                OwnerKind = "entity",
                OwnerKey = ownerKey,
                PluginId = "unique.bound",
                Overlay = new Dictionary<string, object?>
                {
                    ["derived.channel"] = channel,
                    ["derived.op"] = "flat",
                    ["derived.amount"] = delta
                }
            });
        }
        catch { /* fail-closed */ }
    }

    /// <summary>One-shot delta toward the loadout's current-hp target -- `channel` defaults to "hp",
    /// `mode` stays unset (Add), matching every other in-match heal/damage. Stable GrantId means a
    /// re-bind before this drains simply re-sums the same mutation slot rather than double-applying.</summary>
    static void EnqueueHpDelta(EffectFunnel? funnel, string ptrHex, long delta)
    {
        if (funnel is null || delta == 0) return;
        try
        {
            funnel.EnqueueMutation("entity:" + ptrHex, delta,
                pluginId: "unique.bound", effectId: "unique.bound.hp", grantId: "unique.bound.hp:" + ptrHex);
        }
        catch { /* fail-closed */ }
    }
}
