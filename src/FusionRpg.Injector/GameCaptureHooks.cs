using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

using FusionRpg.Injector.Host;

namespace FusionRpg.Injector;

public static class GameCaptureHooks
{
    static void Emit(string kind, object payload) => GameHooks.Emit(kind, payload);

    [HarmonyPatch(typeof(CreatePlant), nameof(CreatePlant.SetPlant))]
    public static class SetPlantPlace
    {
        public static void Postfix(int newColumn, int newRow, PlantType theSeedType, bool isFreeSet, Plant __result)
        {
            if (__result == null || theSeedType == PlantType.Nothing) return;
            SpawnCatalog.Note("plant", (int)theSeedType, GameDumps.EnumName(theSeedType), "place:SetPlant",
                GameDumps.PlantName(theSeedType));
            CheatState.Select(__result.Pointer, "plant");
            var place = new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(__result),
                ["type"] = (int)theSeedType,
                ["typeName"] = GameDumps.EnumName(theSeedType),
                ["col"] = newColumn,
                ["row"] = newRow,
                ["isFreeSet"] = isFreeSet
            };
            CheatState.TagProbe(place);
            Emit("plant.place", place);
        }
    }

    [HarmonyPatch(typeof(CreatePlant), nameof(CreatePlant.MixEvent))]
    public static class MixEventHook
    {
        static List<string>? _rowPtrs;

        public static void Prefix(int theRow)
        {
            _rowPtrs = new List<string>();
            try
            {
                foreach (var p in UnityEngine.Object.FindObjectsOfType<Plant>())
                {
                    try
                    {
                        if (p == null || p.thePlantType == PlantType.Nothing) continue;
                        if (p.thePlantRow != theRow) continue;
                        _rowPtrs.Add(GameDumps.Ptr(p));
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static void Postfix(PlantType theSeedType, Plant plant, int theRow)
        {
            var resultPtr = "";
            try { if (plant != null) resultPtr = GameDumps.Ptr(plant); } catch { }
            var used = new List<string>();
            var still = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var p in UnityEngine.Object.FindObjectsOfType<Plant>())
                {
                    try
                    {
                        if (p == null || p.thePlantType == PlantType.Nothing) continue;
                        still.Add(GameDumps.Ptr(p));
                    }
                    catch { }
                }
            }
            catch { }
            if (_rowPtrs != null)
            {
                foreach (var ptr in _rowPtrs)
                {
                    if (string.IsNullOrEmpty(ptr)) continue;
                    if (!string.IsNullOrEmpty(resultPtr) &&
                        string.Equals(ptr, resultPtr, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!still.Contains(ptr)) used.Add(ptr);
                }
            }
            _rowPtrs = null;
            SpawnCatalog.Note("plant", (int)theSeedType, GameDumps.EnumName(theSeedType), "mix:MixEvent");
            Emit("plant.mix", new Dictionary<string, object>
            {
                ["usedType"] = (int)theSeedType,
                ["usedTypeName"] = GameDumps.EnumName(theSeedType),
                ["plantPtr"] = resultPtr,
                ["row"] = theRow,
                ["usedPtrs"] = used
            });
        }
    }

    [HarmonyPatch(typeof(CreatePlant), nameof(CreatePlant.UniqueEvent))]
    public static class UniqueEventHook
    {
        public static void Postfix(PlantType theSeedType, Plant plant, int theRow)
        {
            SpawnCatalog.Note("plant", (int)theSeedType, GameDumps.EnumName(theSeedType), "unique:UniqueEvent");
            Emit("plant.unique", new Dictionary<string, object>
            {
                ["usedType"] = (int)theSeedType,
                ["usedTypeName"] = GameDumps.EnumName(theSeedType),
                ["plantPtr"] = plant != null ? GameDumps.Ptr(plant) : "",
                ["row"] = theRow
            });
            if (plant != null)
            {
                try
                {
                    var ptr = GameDumps.Ptr(plant);
                    Hud.ActorHudUniqueFlags.Mark(ptr);
                    Hud.ActorHudCache.MarkDirty(ptr);
                }
                catch { }
            }
        }
    }

    [HarmonyPatch(typeof(CreatePlant), nameof(CreatePlant.SetPlantAttributes))]
    public static class SetPlantAttributesHook
    {
        public static void Postfix(Plant plant)
        {
            if (!GameHooks.Ready() || plant == null) return;
            if (plant.thePlantType == PlantType.Nothing) return;
            if (GameHooks.Applied.Contains(plant.Pointer)) return;
            GameHooks.ApplyPlant(plant, "setPlantAttributes");
        }
    }

    [HarmonyPatch(typeof(CreateZombie), nameof(CreateZombie.SetZombieWithMindControl))]
    public static class SetZombieMindPlace
    {
        public static void Postfix(int theRow, ZombieType theZombieType, float theX, bool withEffect, Zombie __result)
        {
            if (__result == null || theZombieType == ZombieType.Nothing) return;
            SpawnCatalog.Note("zombie", (int)theZombieType, GameDumps.EnumName(theZombieType), "place:SetZombieMC",
                GameDumps.ZombieName(theZombieType));
            CheatState.Select(__result.Pointer, "zombie");
            var place = new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(__result),
                ["type"] = (int)theZombieType,
                ["typeName"] = GameDumps.EnumName(theZombieType),
                ["row"] = theRow,
                ["theX"] = theX,
                ["mindControlled"] = true,
                ["withEffect"] = withEffect
            };
            GameDumps.AddZombiePos(place, __result);
            Emit("zombie.place", place);
        }
    }

    [HarmonyPatch(typeof(Board), nameof(Board.UseSun))]
    public static class UseSunHook
    {
        public static void Postfix(float count) =>
            Emit("sun.spend", new Dictionary<string, object> { ["count"] = count });
    }

    [HarmonyPatch(typeof(Board), nameof(Board.GetSun))]
    public static class GetSunHook
    {
        public static void Postfix(float count, bool save) =>
            Emit("sun.gain", new Dictionary<string, object> { ["count"] = count, ["save"] = save });
    }

    [HarmonyPatch(typeof(Board), nameof(Board.UseMoney))]
    public static class UseMoneyHook
    {
        public static void Postfix(int value) =>
            Emit("money.spend", new Dictionary<string, object> { ["value"] = value });
    }

    [HarmonyPatch(typeof(Board), nameof(Board.GetMoney))]
    public static class GetMoneyHook
    {
        public static void Postfix(float count) =>
            Emit("money.gain", new Dictionary<string, object> { ["count"] = count });
    }

    [HarmonyPatch(typeof(Board), nameof(Board.GetPoint))]
    public static class GetPointHook
    {
        public static void Postfix(float count, bool killZombie) =>
            Emit("points.gain", new Dictionary<string, object> { ["count"] = count, ["killZombie"] = killZombie });
    }

    [HarmonyPatch(typeof(InGameUI), nameof(InGameUI.SetLevelName))]
    public static class SetLevelNameHook
    {
        public static void Postfix(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            Emit("level.name", new Dictionary<string, object> { ["levelName"] = name });
        }
    }

    [HarmonyPatch(typeof(BoardSpawner), nameof(BoardSpawner.SummonZombies))]
    public static class SummonZombiesHook
    {
        public static void Postfix(int wave) =>
            Emit("wave.spawn", new Dictionary<string, object> { ["wave"] = wave });
    }

    [HarmonyPatch(typeof(Board), nameof(Board.HugeWaveEvent))]
    public static class HugeWaveHook
    {
        public static void Postfix(int currentWave) =>
            Emit("wave.huge", new Dictionary<string, object> { ["wave"] = currentWave });
    }

    [HarmonyPatch(typeof(Plant), nameof(Plant.RealTakeDamage))]
    public static class PlantRealTakeDamage
    {
        public static void Prefix(Plant __instance, int damage)  // overflow-bounded: PvZ native signature - Harmony binds this int; widened to long on entry to the RPG layer
        {
            if (RpgHost.Client?.Stats.LogDamage != true) return;
            Emit("plant.damage", new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(__instance),
                ["damage"] = damage,
                ["path"] = "real"
            });
        }
    }

    [HarmonyPatch(typeof(Plant), nameof(Plant.Crashed))]
    public static class PlantCrashed
    {
        public static void Postfix(Plant __instance, int level)
        {
            if (__instance == null) return;
            Emit("plant.crash", new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(__instance),
                ["level"] = level
            });
        }
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.BodyTakeDamage))]
    public static class ZombieBodyTakeDamage
    {
        public static void Prefix(Zombie __instance, int theDamage)  // overflow-bounded: PvZ native signature - Harmony binds this int; widened to long on entry to the RPG layer
        {
            if (RpgHost.Client?.Stats.LogDamage != true) return;
            Emit("zombie.damage", new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(__instance),
                ["damage"] = theDamage,
                ["path"] = "body"
            });
        }
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.ApplyDamage))]
    public static class ZombieApplyDamage
    {
        public static void Prefix(Zombie __instance, int dmg)
        {
            if (RpgHost.Client?.Stats.LogDamage != true) return;
            Emit("zombie.damage", new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(__instance),
                ["damage"] = dmg,
                ["path"] = "apply"
            });
        }
    }

    /// <summary>
    /// ⛔ Deliberately does NOT invalidate <c>LawnElementResolverHost</c>, and that is checked by
    /// <c>LawnElementResolverTests</c>, not left to memory. That cache stores the actor's OBJECT KIND,
    /// which mind control does not change: <c>InjectorEntityRegistry.CollectSnaps</c> writes
    /// <c>Side = "zombie"</c> as a literal for every Zombie and carries control state in the separate
    /// <c>BoardEntitySnap.MindControlled</c> flag, which <c>MechanicalOwnSideOracle</c> is the SSOT for
    /// folding into an allegiance. A charmed zombie is still a Zombie with a zombie type id, so its
    /// species row is still the zombie one — re-resolving here would return the identical value at the
    /// cost of a board scan, and flipping the stored side to "plant" would make the
    /// <c>(side, gameTypeId)</c> species lookup miss (degrading a charmed zombie to Neutral element)
    /// and break <c>GateCounterHost</c>, which requires spawn kind by spec-gate-counters.md §2.1.
    /// </summary>
    [HarmonyPatch(typeof(Zombie), nameof(Zombie.SetMindControl))]
    public static class ZombieMindControl
    {
        public static void Postfix(Zombie __instance, int controlLevel)
        {
            if (__instance == null) return;
            Emit("zombie.hypno", new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(__instance),
                ["controlLevel"] = controlLevel,
                ["isMindControlled"] = __instance.isMindControlled
            });
        }
    }

    static void EmitZombieStatus(Zombie z, string status, bool on)
    {
        if (z == null) return;
        Emit("zombie.status", new Dictionary<string, object>
        {
            ["ptr"] = GameDumps.Ptr(z),
            ["status"] = status,
            ["on"] = on
        });
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.Buttered))]
    public static class ZombieButtered
    {
        public static void Postfix(Zombie __instance) => EmitZombieStatus(__instance, "butter", true);
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.UnButtered))]
    public static class ZombieUnButtered
    {
        public static void Postfix(Zombie __instance) => EmitZombieStatus(__instance, "butter", false);
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.SetFreeze))]
    public static class ZombieSetFreeze
    {
        public static void Postfix(Zombie __instance) => EmitZombieStatus(__instance, "freeze", true);
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.SetCold))]
    public static class ZombieSetCold
    {
        public static void Postfix(Zombie __instance) => EmitZombieStatus(__instance, "cold", true);
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.SetPoison))]
    public static class ZombieSetPoison
    {
        public static void Postfix(Zombie __instance) => EmitZombieStatus(__instance, "poison", true);
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.Warm))]
    public static class ZombieWarm
    {
        public static void Postfix(Zombie __instance) => EmitZombieStatus(__instance, "warm", false);
    }

    [HarmonyPatch(typeof(Zombie), nameof(Zombie.KillDebuff))]
    public static class ZombieKillDebuff
    {
        public static void Postfix(Zombie __instance)
        {
            if (__instance == null) return;
            Emit("zombie.status", new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(__instance),
                ["on"] = false
            });
        }
    }

    [HarmonyPatch(typeof(CreateBullet), nameof(CreateBullet.SetBullet))]
    public static class SetBulletPlace
    {
        public static void Postfix(float x, float y, int theRow, BulletType theBulletType, BulletMoveWay theMovingWay, bool fromEnermy, Bullet __result)
        {
            if (__result == null) return;
            // No consumer outside debug sessions (v2 audit §4c.2) — per-bullet rate.
            if (!DebugRuntime.SessionActive) return;
            Emit("bullet.place", new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(__result),
                ["x"] = x,
                ["y"] = y,
                ["row"] = theRow,
                ["type"] = (int)theBulletType,
                ["typeName"] = GameDumps.EnumName(theBulletType),
                ["moveWay"] = GameDumps.EnumName(theMovingWay),
                ["fromEnemy"] = fromEnermy
            });
        }
    }

    [HarmonyPatch(typeof(GameLose), nameof(GameLose.ProcessZombieEnter))]
    public static class ProcessZombieEnterHook
    {
        public static void Postfix(Zombie zombie)
        {
            if (zombie == null) return;
            Emit("match.invade", new Dictionary<string, object>
            {
                ["zombiePtr"] = GameDumps.Ptr(zombie),
                ["type"] = (int)zombie.theZombieType,
                ["typeName"] = GameDumps.EnumName(zombie.theZombieType)
            });
        }
    }

    [HarmonyPatch(typeof(Board), nameof(Board.SetHealthInTravel))]
    public static class SetHealthInTravelHook
    {
        public static void Postfix(Zombie z) => GameHooks.RecaptureZombie(z, "setHealthInTravel");
    }

    [HarmonyPatch(typeof(Lawnf), nameof(Lawnf.SetZombieHealth))]
    public static class SetZombieHealthHook
    {
        public static void Postfix(Zombie zombie) => GameHooks.RecaptureZombie(zombie, "setZombieHealth");
    }

    [HarmonyPatch(typeof(TravelMgr), nameof(TravelMgr.ReinforceZombie))]
    public static class ReinforceZombieHook
    {
        public static void Postfix(Zombie zombie) => GameHooks.RecaptureZombie(zombie, "reinforce");
    }

    [HarmonyPatch(typeof(TravelMgr), nameof(TravelMgr.ReinforcePlant))]
    public static class ReinforcePlantHook
    {
        public static void Postfix(Plant plant) => GameHooks.RecapturePlant(plant, "reinforce");
    }

    [HarmonyPatch(typeof(CreateItem), nameof(CreateItem.SetCoin))]
    public static class SetCoinHook
    {
        public static void Postfix(int theColumn, int theRow, int theItemType, GameObject __result)
        {
            // No consumer outside debug sessions (v2 audit §4c.2) — ~per-kill rate.
            if (!DebugRuntime.SessionActive) return;
            Emit("item.drop", new Dictionary<string, object>
            {
                ["col"] = theColumn,
                ["row"] = theRow,
                ["itemType"] = theItemType,
                ["ptr"] = __result != null ? GameDumps.Ptr(__result) : ""
            });
        }
    }

    [HarmonyPatch(typeof(Mouse), nameof(Mouse.ClickOnCard))]
    public static class ClickOnCardHook
    {
        public static void Postfix(CardUI card)
        {
            if (card == null) return;
            Emit("card.pick", CardPayload(card));
        }
    }

    [HarmonyPatch(typeof(Mouse), nameof(Mouse.TryToSetPlantByCard))]
    public static class TrySetPlantByCardHook
    {
        public static void Postfix(Mouse __instance)
        {
            Emit("card.place", new Dictionary<string, object>
            {
                ["side"] = "plant",
                ["col"] = __instance.theMouseColumn,
                ["row"] = __instance.theMouseRow,
                ["type"] = (int)__instance.thePlantTypeOnMouse,
                ["typeName"] = GameDumps.EnumName(__instance.thePlantTypeOnMouse)
            });
        }
    }

    [HarmonyPatch(typeof(Mouse), nameof(Mouse.TryToSetZombieByCard))]
    public static class TrySetZombieByCardHook
    {
        public static void Postfix(Mouse __instance)
        {
            Emit("card.place", new Dictionary<string, object>
            {
                ["side"] = "zombie",
                ["row"] = __instance.theMouseRow,
                ["type"] = (int)__instance.theZombieTypeOnMouse,
                ["typeName"] = GameDumps.EnumName(__instance.theZombieTypeOnMouse)
            });
        }
    }

    [HarmonyPatch(typeof(CardUI), nameof(CardUI.UseOnce))]
    public static class CardUseOnceHook
    {
        public static void Postfix(CardUI __instance)
        {
            if (__instance == null) return;
            var d = CardPayload(__instance);
            d["thePlantLevel"] = __instance.thePlantLevel;
            Emit("card.use", d);
        }
    }

    [HarmonyPatch(typeof(InitBoard), nameof(InitBoard.CreateCard), typeof(PlantType), typeof(bool), typeof(bool))]
    public static class CreateCardPlant
    {
        public static void Postfix(PlantType theSeedType, CardUI __result)
        {
            SpawnCatalog.Note("plant", (int)theSeedType, GameDumps.EnumName(theSeedType), "card.bank");
            Emit("card.bank", new Dictionary<string, object>
            {
                ["side"] = "plant",
                ["type"] = (int)theSeedType,
                ["typeName"] = GameDumps.EnumName(theSeedType),
                ["ptr"] = __result != null ? GameDumps.Ptr(__result) : ""
            });
        }
    }

    [HarmonyPatch(typeof(InitBoard), nameof(InitBoard.CreateCard), typeof(FunctionCardType), typeof(bool))]
    public static class CreateCardFunction
    {
        public static void Postfix(FunctionCardType functionCardType, CardUI __result)
        {
            Emit("card.bank", new Dictionary<string, object>
            {
                ["side"] = "function",
                ["type"] = (int)functionCardType,
                ["typeName"] = GameDumps.EnumName(functionCardType),
                ["ptr"] = __result != null ? GameDumps.Ptr(__result) : ""
            });
        }
    }

    [HarmonyPatch(typeof(InitBoard), nameof(InitBoard.CreateCard), typeof(ZombieType), typeof(bool))]
    public static class CreateCardZombie
    {
        public static void Postfix(ZombieType theSeedType, IZECard __result)
        {
            SpawnCatalog.Note("zombie", (int)theSeedType, GameDumps.EnumName(theSeedType), "card.bank");
            Emit("card.bank", new Dictionary<string, object>
            {
                ["side"] = "zombie",
                ["type"] = (int)theSeedType,
                ["typeName"] = GameDumps.EnumName(theSeedType),
                ["ptr"] = __result != null ? GameDumps.Ptr(__result) : ""
            });
        }
    }

    [HarmonyPatch(typeof(AlmanacPlantMenu), nameof(AlmanacPlantMenu.SelectCard))]
    public static class AlmanacPlantSelect
    {
        public static void Postfix(AlmanacCardUI card)
        {
            if (card == null) return;
            var t = (int)card.PlantType;
            SpawnCatalog.Note("plant", t, GameDumps.EnumName(card.PlantType), "almanac",
                GameDumps.PlantName(card.PlantType));
            CheatState.LastAlmanacType = t;
            CheatState.LastAlmanacSide = "plant";
            TypeIconCapture.TryCaptureAlmanacCard("plant", t, card);
            AlmanacTextCapture.TryCapture("plant", t, card.PlantType, null);
            Emit("almanac.select", new Dictionary<string, object>
            {
                ["side"] = "plant",
                ["type"] = t,
                ["typeName"] = GameDumps.EnumName(card.PlantType)
            });
        }
    }

    [HarmonyPatch(typeof(AlmanacZombieMenu), nameof(AlmanacZombieMenu.SelectCard))]
    public static class AlmanacZombieSelect
    {
        public static void Postfix(AlmanacCardUI card)
        {
            if (card == null) return;
            var t = (int)card.ZombieType;
            SpawnCatalog.Note("zombie", t, GameDumps.EnumName(card.ZombieType), "almanac",
                GameDumps.ZombieName(card.ZombieType));
            CheatState.LastAlmanacType = t;
            CheatState.LastAlmanacSide = "zombie";
            TypeIconCapture.TryCaptureAlmanacCard("zombie", t, card);
            AlmanacTextCapture.TryCapture("zombie", t, null, card.ZombieType);
            Emit("almanac.select", new Dictionary<string, object>
            {
                ["side"] = "zombie",
                ["type"] = t,
                ["typeName"] = GameDumps.EnumName(card.ZombieType)
            });
        }
    }

    [HarmonyPatch(typeof(Shovel), nameof(Shovel.Use))]
    public static class ShovelUseHook
    {
        public static void Postfix(Mouse mouse)
        {
            Emit("plant.shovel", new Dictionary<string, object>
            {
                ["col"] = mouse != null ? mouse.theMouseColumn : 0,
                ["row"] = mouse != null ? mouse.theMouseRow : 0
            });
        }
    }

    [HarmonyPatch(typeof(Shovel), nameof(Shovel.PayBackSun))]
    public static class PayBackSunHook
    {
        public static void Postfix(Plant plant)
        {
            Emit("sun.refund", new Dictionary<string, object>
            {
                ["ptr"] = plant != null ? GameDumps.Ptr(plant) : ""
            });
        }
    }

    [HarmonyPatch(typeof(Mouse), nameof(Mouse.DisassemblePlant))]
    public static class DisassembleHook
    {
        public static void Postfix(Plant plant, bool __result)
        {
            if (!__result) return;
            Emit("plant.unfuse", new Dictionary<string, object>
            {
                ["ptr"] = plant != null ? GameDumps.Ptr(plant) : ""
            });
        }
    }

    [HarmonyPatch(typeof(Mouse), nameof(Mouse.TryToSetPlantByGlove))]
    public static class GlovePlaceHook
    {
        public static void Postfix(Mouse __instance)
        {
            Emit("plant.glove", new Dictionary<string, object>
            {
                ["col"] = __instance.theMouseColumn,
                ["row"] = __instance.theMouseRow,
                ["path"] = "place"
            });
        }
    }

    [HarmonyPatch(typeof(Glove), nameof(Glove.Use))]
    public static class GloveUseHook
    {
        public static void Postfix() =>
            Emit("plant.glove", new Dictionary<string, object> { ["path"] = "use" });
    }

    [HarmonyPatch(typeof(Fertilize), nameof(Fertilize.Use), typeof(int), typeof(int))]
    public static class FertilizeUseHook
    {
        public static void Postfix(int theColumn, int theRow) =>
            Emit("item.fertilize", new Dictionary<string, object> { ["col"] = theColumn, ["row"] = theRow });
    }

    [HarmonyPatch(typeof(Hammer), nameof(Hammer.Use))]
    public static class HammerUseHook
    {
        public static void Postfix() => Emit("item.hammer", new Dictionary<string, object>());
    }

    [HarmonyPatch(typeof(Wheel), nameof(Wheel.Use))]
    public static class WheelUseHook
    {
        public static void Postfix() => Emit("item.wheel", new Dictionary<string, object>());
    }

    [HarmonyPatch(typeof(MiniPet), nameof(MiniPet.SetPet))]
    public static class SetPetHook
    {
        public static void Postfix(PetType petType, MiniPet __result)
        {
            if (__result == null) return;
            SpawnCatalog.Note("pet", (int)petType, GameDumps.EnumName(petType), "spawn:SetPet");
            Emit("pet.spawn", new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(__result),
                ["type"] = (int)petType,
                ["typeName"] = GameDumps.EnumName(petType)
            });
        }
    }

    [HarmonyPatch(typeof(MiniPet), nameof(MiniPet.GetExperience))]
    public static class PetXpHook
    {
        public static void Postfix(MiniPet __instance, int value)
        {
            if (__instance == null) return;
            // No consumer outside debug sessions (v2 audit §4c.2) — ~per-kill rate.
            if (!DebugRuntime.SessionActive) return;
            Emit("pet.xp", new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(__instance),
                ["value"] = value
            });
        }
    }

    [HarmonyPatch(typeof(GridItem), nameof(GridItem.SetGridItem))]
    public static class SetGridItemHook
    {
        public static void Postfix(int theColumn, int theRow, GridItemType theType, GridItem __result)
        {
            if (__result == null) return;
            SpawnCatalog.Note("grid", (int)theType, GameDumps.EnumName(theType), "spawn:SetGridItem");
            Emit("grid.place", new Dictionary<string, object>
            {
                ["ptr"] = GameDumps.Ptr(__result),
                ["type"] = (int)theType,
                ["typeName"] = GameDumps.EnumName(theType),
                ["col"] = theColumn,
                ["row"] = theRow
            });
        }
    }

    [HarmonyPatch(typeof(GridItem), nameof(GridItem.Die))]
    public static class GridDieHook
    {
        public static void Postfix(GridItem __instance)
        {
            if (__instance == null) return;
            Emit("grid.die", new Dictionary<string, object> { ["ptr"] = GameDumps.Ptr(__instance) });
        }
    }

    [HarmonyPatch(typeof(Present), nameof(Present.RandomPlant))]
    public static class PresentOpenHook
    {
        public static void Postfix(Present __instance)
        {
            Emit("present.open", new Dictionary<string, object>
            {
                ["ptr"] = __instance != null ? GameDumps.Ptr(__instance) : ""
            });
        }
    }

    [HarmonyPatch(typeof(Lawnf), nameof(Lawnf.SetDroppedCard))]
    public static class SetDroppedCardHook
    {
        public static void Postfix(PlantType theSeedType)
        {
            Emit("card.drop", new Dictionary<string, object>
            {
                ["type"] = (int)theSeedType,
                ["typeName"] = GameDumps.EnumName(theSeedType)
            });
        }
    }

    [HarmonyPatch(typeof(Lawnf), nameof(Lawnf.SetAward))]
    public static class SetAwardHook
    {
        public static void Postfix() => Emit("prize.spawn", new Dictionary<string, object>());
    }

    [HarmonyPatch(typeof(PrizeMgr), nameof(PrizeMgr.Click))]
    public static class PrizeClickHook
    {
        public static void Postfix() => Emit("prize.click", new Dictionary<string, object>());
    }

    [HarmonyPatch(typeof(PauseMenu_Btn), nameof(PauseMenu_Btn.Restart))]
    public static class RestartHook
    {
        public static void Postfix() => Emit("match.restart", new Dictionary<string, object>());
    }

    // W3-A: UIMgr.EnterPauseMenu / BackToGame (Assembly-CSharp) — NotifyPaused, not Core Apply kinds.
    // Real gap found live 2026-09-14 (lawn-run-state-machine.md §1/§3): these four hooks already
    // existed but emitted no event at all -- NotifyPaused only flips an in-process MatchPhase never
    // surfaced through /api/debug/events, so a paused match read identically to a running one, and
    // returning to the main menu was invisible to every live-probe state check. Wiring real emits
    // into the already-hooked methods below (no new Harmony patch needed) closes both gaps.
    [HarmonyPatch(typeof(UIMgr), nameof(UIMgr.EnterPauseMenu))]
    public static class EnterPauseMenuHook
    {
        public static void Postfix()
        {
            Match.MatchHost.NotifyPaused(true);
            Emit("match.pause", new Dictionary<string, object>());
        }
    }

    [HarmonyPatch(typeof(UIMgr), nameof(UIMgr.BackToGame))]
    public static class BackToGameHook
    {
        public static void Postfix()
        {
            Match.MatchHost.NotifyPaused(false);
            Emit("match.resume", new Dictionary<string, object>());
        }
    }

    // Escape / in-game pause path may call PauseGame without going through UIMgr first.
    [HarmonyPatch(typeof(InGameUI), nameof(InGameUI.PauseGame))]
    public static class InGamePauseGameHook
    {
        public static void Postfix()
        {
            Match.MatchHost.NotifyPaused(true);
            Emit("match.pause", new Dictionary<string, object>());
        }
    }

    // Leaving to menu while Paused: clear overlay pause (Idle/board.end still via Die).
    [HarmonyPatch(typeof(UIMgr), nameof(UIMgr.BackToMenu))]
    public static class BackToMenuClearPauseHook
    {
        public static void Postfix()
        {
            Match.MatchHost.NotifyPaused(false);
            Emit("menu.enter", new Dictionary<string, object>());
        }
    }

    [HarmonyPatch(typeof(ItemManager), nameof(ItemManager.SetBucket))]
    public static class SetBucketHook
    {
        public static void Postfix(BucketType theBucketType, Bucket __result)
        {
            Emit("item.bucket", new Dictionary<string, object>
            {
                ["type"] = (int)theBucketType,
                ["typeName"] = GameDumps.EnumName(theBucketType),
                ["ptr"] = __result != null ? GameDumps.Ptr(__result) : ""
            });
        }
    }

    [HarmonyPatch(typeof(TravelMgr), nameof(TravelMgr.OnBoardStart))]
    public static class TravelBoardStart
    {
        public static void Postfix(TravelMgr __instance)
        {
            Emit("travel.start", new Dictionary<string, object>
            {
                ["damageAmplification"] = __instance.DamageAmplification,
                ["luckyStrike"] = __instance.LuckyStrike,
                ["plantZeroHealth"] = __instance.plantZeroHealth
            });
        }
    }

    [HarmonyPatch(typeof(TravelMgr), nameof(TravelMgr.GetNormalBuff))]
    public static class GetNormalBuffHook
    {
        public static void Postfix(AdvBuff buff) => Buff("normal", buff);
    }

    [HarmonyPatch(typeof(TravelMgr), nameof(TravelMgr.GetUltiBuff))]
    public static class GetUltiBuffHook
    {
        public static void Postfix(UltiBuff buff, bool upgrade) =>
            Buff("ulti", buff, upgrade);
    }

    [HarmonyPatch(typeof(TravelMgr), nameof(TravelMgr.GetDebuff))]
    public static class GetDebuffHook
    {
        public static void Postfix(TravelDebuff buff) => Buff("debuff", buff);
    }

    [HarmonyPatch(typeof(TravelMgr), nameof(TravelMgr.GetInvestBuff))]
    public static class GetInvestBuffHook
    {
        public static void Postfix(InvestBuff buff) => Buff("invest", buff);
    }

    [HarmonyPatch(typeof(TravelMgr), nameof(TravelMgr.UnlockPlant))]
    public static class UnlockPlantHook
    {
        public static void Postfix(TravelUnlocks unlock)
        {
            Buff("unlock", unlock);
            try
            {
                // Best-effort: unlock enum may encode plant ids
                SpawnCatalog.Note("plant", (int)unlock, GameDumps.EnumName(unlock), "travel.unlock");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(MultipleChoiceMenu), nameof(MultipleChoiceMenu.OnSelect))]
    public static class TravelPickHook
    {
        public static void Postfix(MultipleChoiceMenu __instance)
        {
            var d = new Dictionary<string, object>
            {
                ["page"] = __instance.currentPage
            };
            try
            {
                if (__instance.windowOptionPairs != null && __instance.currentSelect != null
                    && __instance.windowOptionPairs.TryGetValue(__instance.currentSelect, out var opt)
                    && opt != null)
                {
                    d["title"] = opt.title ?? "";
                    d["text"] = opt.text ?? "";
                    d["plantType"] = (int)opt.thePlantType;
                    d["zombieType"] = (int)opt.theZombieType;
                    if (opt.thePlantType != PlantType.Nothing)
                        SpawnCatalog.Note("plant", (int)opt.thePlantType, GameDumps.EnumName(opt.thePlantType), "travel.pick");
                    if (opt.theZombieType != ZombieType.Nothing)
                        SpawnCatalog.Note("zombie", (int)opt.theZombieType, GameDumps.EnumName(opt.theZombieType), "travel.pick");
                }
            }
            catch { }
            Emit("travel.pick", d);
        }
    }

    [HarmonyPatch(typeof(InitZombieList), nameof(InitZombieList.InitZombie))]
    public static class InitZombieCatalog
    {
        public static void Postfix(LevelType theLevelType, int theLevelNumber)
        {
            var types = new List<Dictionary<string, object>>();
            try
            {
                var set = InitZombieList.zombieTypeList ?? InitZombieList.zombieToSpawns;
                if (set != null)
                {
                    foreach (var z in set)
                    {
                        SpawnCatalog.Note("zombie", (int)z, GameDumps.EnumName(z), "initZombieList");
                        types.Add(new Dictionary<string, object>
                        {
                            ["type"] = (int)z,
                            ["typeName"] = GameDumps.EnumName(z)
                        });
                    }
                }
            }
            catch { }
            Emit("catalog.zombies", new Dictionary<string, object>
            {
                ["levelType"] = GameDumps.EnumName(theLevelType),
                ["levelNumber"] = theLevelNumber,
                ["types"] = types
            });
        }
    }

    static void Buff(string kind, object buff, bool? upgrade = null)
    {
        var d = new Dictionary<string, object>
        {
            ["kind"] = kind,
            ["name"] = buff?.ToString() ?? ""
        };
        if (upgrade is { } u) d["upgrade"] = u;
        Emit("travel.buff", d);
    }

    static Dictionary<string, object> CardPayload(CardUI card)
    {
        var d = new Dictionary<string, object>
        {
            ["cost"] = card.theSeedCost,
            ["thePlantLevel"] = card.thePlantLevel
        };
        try
        {
            d["plantType"] = (int)card.thePlantType;
            d["typeName"] = GameDumps.EnumName(card.thePlantType);
        }
        catch { }
        try { d["zombieType"] = (int)card.theZombieType; } catch { }
        return d;
    }
}
