using System.Collections.Generic;
using HarmonyLib;

namespace FusionRpg.Injector.Bridges;

/// <summary>SetZombie place capture — postfix arity matches 3.8.1 Bep (4) vs Melon (5).</summary>
[HarmonyPatch(typeof(CreateZombie), nameof(CreateZombie.SetZombie))]
public static class CreateZombieHooks
{
#if FUSIONRPG_MELON
    public static void Postfix(int theRow, ZombieType theZombieType, float theX, bool isIdle, bool isMindControlled, Zombie __result)
#else
    public static void Postfix(int theRow, ZombieType theZombieType, float theX, bool isMindControlled, Zombie __result)
#endif
    {
        if (__result == null || theZombieType == ZombieType.Nothing) return;
        SpawnCatalog.Note("zombie", (int)theZombieType, GameDumps.EnumName(theZombieType), "place:SetZombie",
            GameDumps.ZombieName(theZombieType));
        CheatState.Select(__result.Pointer, "zombie");
        var place = new Dictionary<string, object>
        {
            ["ptr"] = GameDumps.Ptr(__result),
            ["type"] = (int)theZombieType,
            ["typeName"] = GameDumps.EnumName(theZombieType),
            ["row"] = theRow,
            ["theX"] = theX,
            ["mindControlled"] = isMindControlled
        };
        GameDumps.AddZombiePos(place, __result);
        GameHooks.Emit("zombie.place", place);
    }
}
