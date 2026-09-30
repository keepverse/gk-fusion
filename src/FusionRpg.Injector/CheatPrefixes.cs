using HarmonyLib;
using UnityEngine;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Injector.Stats;

namespace FusionRpg.Injector;

/// <summary>Prefix/postfix cheats for godmode, extra DEF paths, bullets, QoL, free-set.</summary>
public static class CheatPrefixes
{
    [HarmonyPatch(typeof(Plant), nameof(Plant.RealTakeDamage))]
    public static class PlantRealTakeCheat
    {
        // DEF %/flat only on Plant.TakeDamage (GameHooks) — avoid double scale on RealTakeDamage.
        public static void Prefix(ref int damage)  // overflow-bounded: PvZ native signature - Harmony binds this int; widened to long on entry to the RPG layer
        {
            if (CheatState.On("P-GOD")) damage = 0;
        }
    }

    [HarmonyPatch(typeof(Plant), nameof(Plant.Crashed))]
    public static class PlantCrashCheat
    {
        public static bool Prefix()
        {
            if (CheatState.On("P-GOD")) return false;
            return true;
        }
    }

    [HarmonyPatch(typeof(Plant), nameof(Plant.Die))]
    public static class PlantDieCheat
    {
        public static bool Prefix(Plant __instance, Plant.DieReason reason)
        {
            if (!CheatState.On("P-GOD-DIE")) return true;
            if (reason == Plant.DieReason.ByShovel) return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.TakeDamage))]
    public static class ZombieTakeCheat
    {
        public static void Prefix(ref int theDamage)  // overflow-bounded: PvZ native signature - Harmony binds this int; widened to long on entry to the RPG layer
        {
            if (CheatState.On("Z-GOD")) theDamage = 0;
        }
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.BodyTakeDamage))]
    public static class ZombieBodyCheat
    {
        // DEF only on Zombie.TakeDamage — BodyTakeDamage is godmode only (Z-DEF-BODY = enable note for Take path).
        public static void Prefix(ref int theDamage)  // overflow-bounded: PvZ native signature - Harmony binds this int; widened to long on entry to the RPG layer
        {
            if (CheatState.On("Z-GOD")) theDamage = 0;
        }
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.ApplyDamage))]
    public static class ZombieApplyCheat
    {
        public static void Prefix(ref int dmg)
        {
            if (CheatState.On("Z-GOD")) dmg = 0;
        }
    }

    [HarmonyPatch(typeof(Bullet), nameof(Bullet.InitData))]
    public static class BulletInitCheat
    {
        public static void Postfix(Bullet __instance)
        {
            if (__instance == null) return;
            try
            {
                if (CheatState.On("D-PROBE-PLANT")) return;

                // E37 (spec-projectile-control.md §2b): the ordering rule itself — bullet.modify grants
                // fold first, D- cheat state applies last and always wins (criterion 6) — lives in
                // BulletFireResolver.Resolve (FusionRpg.Core), Unity-free and unit-tested there. This
                // postfix is now a thin shell: read the bullet's current fields in, resolve, write the
                // result back out. Added alongside the pre-existing cheat reads, never rewriting them
                // (§3's own "do not rewrite BulletInitCheat" rule) — the resolver's own tail is
                // byte-identical to what this method used to do inline.
                IReadOnlyList<BoundBulletModifyAtom> grants;
                try { grants = GrantedBulletModifyAtoms.For(__instance); }
                catch { grants = Array.Empty<BoundBulletModifyAtom>(); } // a grant read failing must never block the cheat reads below

                var resolved = BulletFireResolver.Resolve(
                    new BulletFireState(__instance.Damage, BulletType: null, MoveWay: null),
                    grants,
                    cheatDamageSet: CheatState.IVal("D-DMG-SET"),
                    cheatDamagePercent: CheatState.FVal("D-DMG-%"),
                    cheatTypeSwap: CheatState.IVal("D-TYPE-SWAP"),
                    cheatHoming: CheatState.On("D-HOMING"));

                __instance.Damage = resolved.Damage;
                if (resolved.BulletType is { } bt)
                    try { __instance.theBulletType = (BulletType)bt; } catch { }
                if (!string.IsNullOrEmpty(resolved.MoveWay)
                    && Enum.TryParse<BulletMoveWay>(resolved.MoveWay, ignoreCase: false, out var mw))
                    try { __instance.MoveWay = mw; } catch { }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(CreatePlant), nameof(CreatePlant.SetPlant))]
    public static class FreeSetPlant
    {
        public static void Prefix(ref bool isFreeSet)
        {
            if (CheatState.On("G-FREE-SET")) isFreeSet = true;
        }
    }

    [HarmonyPatch(typeof(CreatePlant), nameof(CreatePlant.CheckBox))]
    public static class PlantAnywhere
    {
        // Force allow plant box checks when H-ANYWHERE is on (safe alternative to banned CheckMix).
        public static void Postfix(ref bool __result)
        {
            if (CheatState.On("H-ANYWHERE")) __result = true;
        }
    }

    [HarmonyPatch(typeof(CardUI), nameof(CardUI.UseOnce))]
    public static class CardNoCd
    {
        public static void Postfix(CardUI __instance)
        {
            if (!CheatState.On("H-NOCD-CARD") || __instance == null) return;
            try
            {
                __instance.CD = 0f;
                __instance.fullCD = 0.01f;
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Mower), nameof(Mower.Die))]
    public static class MowerInf
    {
        public static bool Prefix(Mower __instance)
        {
            if (!CheatState.On("H-MOWER-INF")) return true;
            try
            {
                return false;
            }
            catch { return true; }
        }
    }
}
