using FusionRpg.Contracts;

namespace FusionRpg.Injector.Bridges;

/// <summary>Game-profile combat field access for zombie HP (Int64 on 3.9).</summary>
public static class ZombieCombatFields
{
    public static string ProfileId => RpgConstants.GameId39;

    public static long GetHp(Zombie z) => z.theHealth;
    public static long GetMaxHp(Zombie z) => z.theMaxHealth;

    // theHealth/theMaxHealth width is a property of the interop generation, not the game build:
    // MelonLoader's Il2CppAssemblies expose Int64, BepInEx's interop exposes Int32 (verified by
    // metadata decode of both Assembly-CSharp.dll). Reads widen either way; only the writes need
    // the host split. The BepInEx arm reuses the same saturation the 3.8.1 bridge uses.
    public static void SetHp(Zombie z, long hp) =>
#if FUSIONRPG_MELON
        z.theHealth = hp;
#else
        z.theHealth = ClampToInt32(hp);
#endif

    public static void SetMaxHp(Zombie z, long max) =>
#if FUSIONRPG_MELON
        z.theMaxHealth = max;
#else
        z.theMaxHealth = ClampToInt32(max);
#endif

    public static long GetCurrentAllHealth(Zombie z) => z.CurrentAllHealth;
    public static long GetTotalAllHealth(Zombie z) => z.TotalAllHealth;
    public static long GetCurrentFirstHealth(Zombie z) => z.CurrentFirstHealth;

    /// <summary>3.9 exposes Int64; dumps use double for continuity with 3.8.1 Single.</summary>
    public static double GetTotalFirstHealthNumber(Zombie z) => z.TotalFirstHealth;

    public static int ClampToInt32(long value)
    {
        if (value > int.MaxValue) return int.MaxValue;
        if (value < int.MinValue) return int.MinValue;
        return (int)value;
    }
}
