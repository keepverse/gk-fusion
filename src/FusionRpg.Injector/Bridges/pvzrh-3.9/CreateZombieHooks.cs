using System.Collections.Generic;
using HarmonyLib;

namespace FusionRpg.Injector.Bridges;

/// <summary>SetZombie place capture — 3.9 Melon matches Bep 4-arg.</summary>
[HarmonyPatch(typeof(CreateZombie), nameof(CreateZombie.SetZombie))]
public static class CreateZombieHooks
{
    public static void Postfix(int theRow, ZombieType theZombieType, float theX, bool isMindControlled, Zombie __result)
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
