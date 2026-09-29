using System.Collections.Generic;
using FusionRpg.Injector.Lawn;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace FusionRpg.Injector;

internal static class GameDumps
{
    public static string Ptr(Il2CppObjectBase obj) => obj.Pointer.ToString("X");

    public static string EnumName(object? value)
    {
        try { return value?.ToString() ?? ""; }
        catch { return ""; }
    }

    public static string PlantName(PlantType type)
    {
        try
        {
            var n = Lawnf.GetName(type);
            if (!string.IsNullOrWhiteSpace(n)) return n;
        }
        catch { /* IL2CPP */ }
        return EnumName(type);
    }

    public static string ZombieName(ZombieType type)
    {
        try
        {
            var n = Lawnf.GetName(type);
            if (!string.IsNullOrWhiteSpace(n)) return n;
        }
        catch { /* IL2CPP */ }
        return EnumName(type);
    }

    public static Dictionary<string, object> Plant(Plant p, string source, long hpBase, long maxHpBase, long attackBase)
    {
        var d = new Dictionary<string, object> { ["source"] = source };
        Try(d, "type", () => (int)p.thePlantType);
        Try(d, "typeName", () => EnumName(p.thePlantType));
        Try(d, "displayName", () => PlantName(p.thePlantType));
        Try(d, "ptr", () => Ptr(p));
        Try(d, "col", () => p.thePlantColumn);
        Try(d, "row", () => p.thePlantRow);
        Try(d, "thePlantHealth", () => p.thePlantHealth);
        Try(d, "thePlantMaxHealth", () => p.thePlantMaxHealth);
        Try(d, "theShieldHealth", () => p.theShieldHealth);
        Try(d, "attackDamage", () => p.attackDamage);
        Try(d, "theLevel", () => p.theLevel);
        Try(d, "shootingLevel", () => p.shootingLevel);
        Try(d, "limDamage", () => p.LimDamage);
        Try(d, "thePlantAttackInterval", () => p.thePlantAttackInterval);
        Try(d, "attackSpeedAdder", () => p.attackSpeedAdder);
        d["hpBase"] = hpBase;
        d["maxHpBase"] = maxHpBase;
        Try(d, "hp", () => p.thePlantHealth);
        Try(d, "maxHp", () => p.thePlantMaxHealth);
        d["attackBase"] = attackBase;
        Try(d, "attack", () => p.attackDamage);
        AddRpgShield(d, Ptr(p));
        Hud.ActorHudObserve.AttachRow(d, Ptr(p));
        return d;
    }

    /// <summary>
    /// RPG shield resource keys — deliberately distinct from the vanilla theShieldHealth
    /// keys, which the web fold maps into the armor bar (shield-system-spec.md §2.6).
    /// </summary>
    static void AddRpgShield(Dictionary<string, object> d, string ptrHex)
    {
        try
        {
            var runtime = Effects.EffectRuntime.Bag.ShieldGate?.Runtime;
            if (runtime == null) return;
            // Always emit when the gate is wired — an explicit 0/0 is how the web fold
            // CLEARS a bar after the shield breaks (omitting the keys would leave it stale).
            var totals = runtime.Totals(
                FusionRpg.Contracts.EffectOwnerKeys.Entity(
                    FusionRpg.Core.Combat.CombatPtr.Normalize(ptrHex)));
            d["rpgShieldHp"] = totals.Hp;
            d["rpgShieldMax"] = totals.MaxHp;
        }
        catch { }
    }

    public static Dictionary<string, object> Zombie(Zombie z, string source, long hpBase, long maxHpBase, long attackBase, long armorBase, long armorMaxBase)
    {
        var d = new Dictionary<string, object> { ["source"] = source };
        Try(d, "type", () => (int)z.theZombieType);
        Try(d, "typeName", () => EnumName(z.theZombieType));
        Try(d, "displayName", () => ZombieName(z.theZombieType));
        Try(d, "ptr", () => Ptr(z));
        AddZombiePos(d, z);
        Try(d, "theHealth", () => Bridges.ZombieCombatFields.GetHp(z));
        Try(d, "theMaxHealth", () => Bridges.ZombieCombatFields.GetMaxHp(z));
        Try(d, "theFirstArmorHealth", () => z.theFirstArmorHealth);
        Try(d, "theFirstArmorMaxHealth", () => z.theFirstArmorMaxHealth);
        Try(d, "theSecondArmorHealth", () => z.theSecondArmorHealth);
        Try(d, "theSecondArmorMaxHealth", () => z.theSecondArmorMaxHealth);
        Try(d, "theAttackDamage", () => z.theAttackDamage);
        Try(d, "level", () => z.level);
        Try(d, "theArmor", () => z.theArmor);
        Try(d, "takeDmgMultiplier", () => z.takeDmgMultiplier);
        Try(d, "damageMultiplier", () => z.DamageMultiplier);
        Try(d, "theSpeed", () => z.theSpeed);
        Try(d, "theOriginSpeed", () => z.theOriginSpeed);
        Try(d, "uniqueSpeed", () => z.uniqueSpeed);
        Try(d, "currentAllHealth", () => Bridges.ZombieCombatFields.GetCurrentAllHealth(z));
        Try(d, "totalAllHealth", () => Bridges.ZombieCombatFields.GetTotalAllHealth(z));
        Try(d, "currentFirstHealth", () => Bridges.ZombieCombatFields.GetCurrentFirstHealth(z));
        Try(d, "totalFirstHealth", () => Bridges.ZombieCombatFields.GetTotalFirstHealthNumber(z));
        Try(d, "isMindControlled", () => z.isMindControlled);
        d["hpBase"] = hpBase;
        d["maxHpBase"] = maxHpBase;
        Try(d, "hp", () => Bridges.ZombieCombatFields.GetHp(z));
        Try(d, "maxHp", () => Bridges.ZombieCombatFields.GetMaxHp(z));
        d["attackBase"] = attackBase;
        Try(d, "attack", () => z.theAttackDamage);
        d["armorBase"] = armorBase;
        Try(d, "armor", () => z.theFirstArmorHealth);
        d["armorMaxBase"] = armorMaxBase;
        Try(d, "armorMax", () => z.theFirstArmorMaxHealth);
        AddRpgShield(d, Ptr(z));
        Hud.ActorHudObserve.AttachRow(d, Ptr(z));
        return d;
    }

    public static Dictionary<string, object> BoardStats(Board? board, BoardStatistics? s)
    {
        var d = new Dictionary<string, object>();
        if (s != null)
        {
            Try(d, "sun", () => s.finalSun);
            Try(d, "finalSun", () => s.finalSun);
            Try(d, "sunProduced", () => s.sunProduced);
            Try(d, "sunConsumed", () => s.sunConsumed);
            Try(d, "finalMoney", () => s.finalMoney);
            Try(d, "moneyEarned", () => s.moneyEarned);
            Try(d, "moneyConsumed", () => s.moneyConsumed);
            Try(d, "wave", () => s.currentWave);
            Try(d, "maxWave", () => s.maxWave);
            Try(d, "currentWave", () => s.currentWave);
            Try(d, "mowerUsedCount", () => s.mowerUsedCount);
            Try(d, "plantsPlanted", () => s.plantsPlanted);
            Try(d, "plantsDied", () => s.plantsDeath);
            Try(d, "plantsShoveled", () => s.plantsShoveled);
            Try(d, "zombiesKilled", () => s.zombiesKilled);
            Try(d, "zombiesMindControlled", () => s.zombiesMindControlled);
            Try(d, "totalZombieDamage", () => s.totalZombieDamage);
            Try(d, "duration", () => s.gameDuration);
            Try(d, "gameDuration", () => s.gameDuration);
            Try(d, "gameResult", () => s.gameResult.ToString());
        }
        if (board != null)
        {
            Try(d, "theWave", () => board.theWave);
            Try(d, "theMaxWave", () => board.theMaxWave);
            Try(d, "timeUntilNextWave", () => board.timeUntilNextWave);
            Try(d, "theSun", () => board.theSun);
            Try(d, "theMoney", () => board.theMoney);
            Try(d, "thePoints", () => board.thePoints);
            Try(d, "plantedCount", () => board.plantedCount);
            Try(d, "theCurrentPlantCount", () => board.theCurrentPlantCount);
            Try(d, "theTotalNumOfZombie", () => board.theTotalNumOfZombie);
            Try(d, "zombieCurrentWaveHealth", () => board.zombieCurrentWaveHealth);
            Try(d, "zombieSpawnHealth", () => board.zombieSpawnHealth);
            Try(d, "zombieTotalHealth", () => board.zombieTotalHealth);
            Try(d, "isHugeWave", () => board.isHugeWave);
            Try(d, "maxSun", () => board.maxSun);
            Try(d, "maxMoney", () => board.maxMoney);
        }
        try
        {
            d["levelType"] = GameAPP.theBoardType.ToString();
            d["boardLevel"] = GameAPP.theBoardLevel;
        }
        catch { }
        return d;
    }

    public static Dictionary<string, object> BoardConfig(BoardConfig? c)
    {
        var d = new Dictionary<string, object>();
        if (c == null) return d;
        Try(d, "zombieHealthMultiplier", () => c.zombieHealthMultiplier);
        Try(d, "zombieDamageMultiplier", () => c.zombieDamageMultiplier);
        Try(d, "zombieSpeedMultiplier", () => c.zombieSpeedMultiplier);
        Try(d, "zombieCountMultiplier", () => c.zombieCountMultiplier);
        Try(d, "zombieStartAmmor", () => c.zombieStartAmmor);
        Try(d, "plantModifyMin", () => c.plantModifyMin);
        Try(d, "plantModifyMax", () => c.plantModifyMax);
        Try(d, "zombieModifyMin", () => c.zombieModifyMin);
        Try(d, "zombieModifyMax", () => c.zombieModifyMax);
        Try(d, "waveInterval", () => c.waveInterval);
        Try(d, "conveyInterval", () => c.conveyInterval);
        return d;
    }

    public static Dictionary<string, object> LiveBoard(Board board)
    {
        var d = new Dictionary<string, object>();
        Try(d, "sun", () => board.theSun);
        Try(d, "theSun", () => board.theSun);
        Try(d, "money", () => board.theMoney);
        Try(d, "theMoney", () => board.theMoney);
        Try(d, "points", () => board.thePoints);
        Try(d, "thePoints", () => board.thePoints);
        Try(d, "wave", () => board.theWave);
        Try(d, "theWave", () => board.theWave);
        Try(d, "maxWave", () => board.theMaxWave);
        Try(d, "theMaxWave", () => board.theMaxWave);
        Try(d, "timeUntilNextWave", () => board.timeUntilNextWave);
        Try(d, "plantedCount", () => board.plantedCount);
        Try(d, "theCurrentPlantCount", () => board.theCurrentPlantCount);
        Try(d, "theTotalNumOfZombie", () => board.theTotalNumOfZombie);
        Try(d, "zombieCurrentWaveHealth", () => board.zombieCurrentWaveHealth);
        Try(d, "zombieSpawnHealth", () => board.zombieSpawnHealth);
        Try(d, "zombieTotalHealth", () => board.zombieTotalHealth);
        Try(d, "isHugeWave", () => board.isHugeWave);
        Try(d, "maxSun", () => board.maxSun);
        Try(d, "maxMoney", () => board.maxMoney);
        return d;
    }

    /// <summary>
    /// Lane + grid col for lawn FE. Prefers Mouse.GetColumnFromX(world X);
    /// Zombie.Column is fallback. Do not use Zombie.col (that is Collider2D).
    /// </summary>
    public static void AddZombiePos(Dictionary<string, object> d, Zombie z)
    {
        Try(d, "row", () => z.theZombieRow);
        Try(d, "column", () => z.Column);
        var haveX = false;
        var x = 0f;
        try
        {
            var t = z.transform;
            if (t != null)
            {
                var p = t.position;
                x = p.x;
                d["x"] = x;
                d["y"] = p.y;
                haveX = true;
            }
        }
        catch { }

        var col = -1;
        if (haveX)
            col = LawnCoords.ColFromX(x);
        if (col < 0)
        {
            try { col = z.Column; } catch { }
        }
        d["col"] = col;
    }

    /// <summary>
    /// Public twin of <see cref="Try"/>, for a caller outside this class that is reading a wide
    /// reflection surface over game types (<c>GameHooks.EnqueueBaseStats</c>). Same contract: one
    /// unexpected field costs that field, never the sweep around it.
    /// </summary>
    public static void TryInto(Dictionary<string, object> d, string key, Func<object> read) => Try(d, key, read);

    static void Try(Dictionary<string, object> d, string key, Func<object> read)
    {
        try { d[key] = read(); }
        catch { }
    }
}
