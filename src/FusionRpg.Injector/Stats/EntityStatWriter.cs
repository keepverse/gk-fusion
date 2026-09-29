using System.Collections.Concurrent;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats;
using FusionRpg.Injector.Bridges;
using HarmonyLib;

using FusionRpg.Injector.Host;
using FusionRpg.Core.Time;

namespace FusionRpg.Injector.Stats;

/// <summary>
/// Sole Unity combat-field mutator. Features must Resolve → Apply; never assign HP/ATK elsewhere.
/// </summary>
public static class EntityStatWriter
{
    public sealed class AppliedFinal
    {
        public long Hp;
        public long MaxHp;
        public long Atk;
        public string Source = "";
        public DateTime Utc = ServerClock.UtcNowDateTime;
    }

    static readonly ConcurrentDictionary<IntPtr, AppliedFinal> Registry = new();

    public static bool TryGetApplied(IntPtr ptr, out AppliedFinal final) =>
        Registry.TryGetValue(ptr, out final!);

    public static void Forget(IntPtr ptr) => Registry.TryRemove(ptr, out _);

    public static void Clear() => Registry.Clear();

    /// <summary>
    /// Unity-boundary narrowing (combat-numerics, lawn-combat-wire T4) — this int32 width is a
    /// STRUCTURAL HOST LIMIT, not a choice this codebase made: <c>thePlantHealth</c>,
    /// <c>theAttackDamage</c>, the armor/shield fields, etc. are genuinely <c>int</c> fields on the
    /// host game's own <c>Plant</c>/<c>Zombie</c> classes (<c>Bridges/&lt;profile&gt;/ZombieCombatFields.cs</c>),
    /// which this Injector may not rewrite (AGENTS.md hard boundary: "never download or patch the PVZ
    /// Fusion game binary"). AGENTS.md's caps rule exempts a structural host limit from "never clamp
    /// silently" PROVIDED it reports rather than silently saturating — a `long`/per-mille magnitude
    /// that genuinely exceeds <c>int32</c> here is a real gameplay event (gear finally out-scaled a
    /// three-decade-old engine's own field width), not a rounding detail, so it is proofed through the
    /// same channel every other writer proof already uses (<c>ProofWrite</c>'s own
    /// <c>SYS-EMIT-PROOF</c>/<c>GameHooks.Emit</c> shape) before the clamp is applied. Every call site
    /// in the injector that used to call <c>ZombieCombatFields.ClampToInt32</c> directly goes through
    /// this wrapper instead, so the report is never accidentally skipped at a new call site —
    /// <c>internal</c> rather than <c>private</c> precisely so <c>GameHooks</c>'s two Harmony damage
    /// prefixes (plant/zombie <c>TakeDamage</c>, where a defense divide can push the incoming amount
    /// back past <c>int32</c>) route through the same reporting path instead of a silent clamp.
    /// <c>field</c> names the boundary that saturated, so the proof says which value it was.
    /// </summary>
    internal static int ClampToInt32Reporting(long value, string field, string source)
    {
        var clamped = ZombieCombatFields.ClampToInt32(value);
        if (value > int.MaxValue || value < int.MinValue)
        {
            CheatState.Error(
                $"writer.clampBoundary: {field} src={source} value={value} exceeds Unity's int32 field width -- clamped to {clamped}");
            if (CheatState.EmitProof && CheatState.On("SYS-EMIT-PROOF"))
            {
                try
                {
                    var payload = new Dictionary<string, object>
                    {
                        ["field"] = field,
                        ["source"] = source ?? "",
                        ["value"] = value,
                        ["clamped"] = clamped
                    };
                    CheatState.TagProbe(payload);
                    GameHooks.Emit("stat.writer.clampBoundary", payload);
                }
                catch { /* never break combat writes for proof */ }
            }
        }
        return clamped;
    }

    public static void WritePlant(Plant p, EntityFinal y, long previousHp, long previousMax, bool preserveHpRatio, string source)
    {
        if (p == null || y == null) return;
        try
        {
            var beforeHp = p.thePlantHealth;
            var beforeMax = p.thePlantMaxHealth;
            var beforeAtk = p.attackDamage;

            var max = ClampToInt32Reporting(Math.Max(1L, y.MaxHp), "plant.maxHp", source);
            var preserve = preserveHpRatio || StatSystem.PreserveLiveCurrentHp(source);
            var hp = ClampToInt32Reporting(
                StatSystem.CurrentHpForWrite(preserve, previousHp, previousMax, y.Hp, y.MaxHp), "plant.hp", source);
            p.thePlantMaxHealth = max;
            p.thePlantHealth = hp;
            // ⛔ ATTACK IS NOT WRITTEN. Owner ruling 2026-09-16: "Write attack damage is a bug …
            // we already have battle engine and damage calculator in our rpg, so if we give in game
            // damage it cause our battle engine abundant so that is defect need to fix now."
            //
            // The RPG owns damage. `DamagePacket` → `CombatDamageDispatcher` → `ShieldGate` /
            // `OverlayCombatCalculator` → Funnel → FA10 is the whole resolution path, and the lawn's
            // rider carries its result onto a real hit (lawn-combat-wire proofs 2/6: a landed rider is
            // 1.2–1.8× the vanilla number it rides). Writing the composed `y.Atk` into PvZ's own
            // `attackDamage` on top of that makes the engine redundant: the same progression is paid
            // once by the engine and once by the host game's own multiply, and the number the player
            // sees is neither.
            //
            // It is also what produced defect M1 (`lawn-tuning-profile-ideal.md`): a ladder-sized
            // magnitude assigned onto a vanilla-sized field made a Peashooter's pea read 1,641 at Θ=6
            // and 2,939 at Θ=57, against a vanilla 20. The fix for that defect is this removal, not a
            // rescale — see `species-hub-wire-ideal.md` "The transport ruling".
            //
            // What the lawn receives instead is a DELTA over the fields PvZ actually has — hp, armor1,
            // armor2 (owner, same ruling) — and every RPG stat with no PvZ field stays in the RPG
            // layer, which is this repo's standing rule: an RPG feature is never built by changing what
            // PvZ is.
            //
            // ⚠ `y.Atk` stays composed and is still read by `Remember`/`ProofWrite` below, so the
            // telemetry keeps reporting what the RPG believes alongside what PvZ holds. Nothing else in
            // this method changes; `WritePlantExtras`'s `ModifyDamage(0, p.attackDamage)` refresh now
            // simply refreshes the vanilla value.
            //
            // if (!CheatState.On("D-PROBE-BULLET"))
            //     p.attackDamage = ClampToInt32Reporting(y.Atk, "plant.atk", source);

            // E16: fire rate and sun rate, composed. A zero means the baseline had none, so there
            // is nothing to write — never a zero interval, which is a divide-by-zero or an infinite
            // fire rate depending on which call site reads it.
            if (y.AttackInterval > 0) p.thePlantAttackInterval = (float)y.AttackInterval;
            if (y.ProduceInterval > 0) p.thePlantProduceInterval = (float)y.ProduceInterval;

            // E38 (spec-entity-fields-12plus.md): eight more plant fields, composed. Long
            // magnitudes clamp to int only at this boundary, exactly like MaxHp/Hp above; none of
            // these use the "zero baseline is absent" skip the two intervals above use — every one
            // is captured from a genuine live field on the plant's own side (EntityApply.cs), so a
            // composed zero is an ordinary value ("no shield right now"), never a missing stat, and
            // is written unconditionally, the same as Hp/Atk.
            // ⛔ PLANT SHIELD IS NOT WRITTEN. Same owner ruling as attack, extended 2026-09-16: "no
            // buff damage directly on the pvz, use our damage calculator. This is principle not
            // something to debate." A shield is absorption — a term in the damage equation — and the
            // RPG already owns the whole of it: `combat.shield.capacity` / `.toughness` / `.pen` /
            // `.regen` are registered derived channels, `ShieldRuntime` holds the live per-element
            // stacks, `ShieldGate` sits in the `DamagePacket` -> `CombatDamageDispatcher` path, and
            // `ActorHudBuilder` already reads those stacks for the Band B resource row. Writing a
            // composed absorption pool into PvZ's own `theShieldHealth` on top of that is the attack
            // defect in a second field: the same absorption is paid once by `ShieldGate` and once by
            // the host game, and the number the player sees is neither.
            //
            // `y.PlantShield` stays composed, so nothing downstream of the composer changes.
            // p.theShieldHealth = ClampToInt32Reporting(y.PlantShield, "plant.shield", source);
            // attackCountdown/produceCountdown share the interval floor's structural reason (driven
            // to zero or below is the same divide-by-zero / infinite-fire-rate risk) but compose
            // unconditionally — see StatComposer.IntervalAlways's own doc comment.
            p.thePlantAttackCountDown = (float)y.AttackCountdown;
            p.thePlantProduceCountDown = (float)y.ProduceCountdown;
            // Unguarded by design (§2b, decided 2026-09-03): an adder is a signed delta, so a
            // negative value here is ordinary content, not a mistake. Never clamp this at write —
            // see CheatState.BuildPlantAbsoluteReal's own note and
            // EntityFields12PlusGuardTests.P_ATK_ADD_stays_unguarded_by_a_value_check.
            p.attackSpeedAdder = (float)y.AttackSpeedAdder;
            // plantSpeed/moveSpeed mirror zombieSpeed's own shape: most plants never move, so a
            // zero composed result genuinely means "this plant has no such stat" and the field is
            // left alone, exactly like the two intervals above.
            if (y.PlantSpeed > 0) p.thePlantSpeed = (float)y.PlantSpeed;
            if (y.PlantMoveSpeed > 0) p.moveSpeed = (float)y.PlantMoveSpeed;
            p.theLevel = ClampToInt32Reporting(y.PlantLevel, "plant.level", source);
            p.shootingLevel = ClampToInt32Reporting(y.ShootingLevel, "plant.shootingLevel", source);

            try { p.UpdateText(); } catch { }

            Remember(p.Pointer, p.thePlantHealth, p.thePlantMaxHealth, p.attackDamage, source);
            ProofWrite("plant", p.Pointer, source, beforeHp, beforeMax, beforeAtk,
                p.thePlantHealth, p.thePlantMaxHealth, p.attackDamage);
        }
        catch (Exception ex) { CheatState.Error("writer.plant: " + ex.Message); }
    }

    public static void WriteZombie(Zombie z, EntityFinal y, long previousHp, long previousMax, bool preserveHpRatio, string source)
    {
        if (z == null || y == null) return;
        try
        {
            var beforeHp = ZombieCombatFields.GetHp(z);
            var beforeMax = ZombieCombatFields.GetMaxHp(z);
            var beforeAtk = z.theAttackDamage;

            var max = Math.Max(1L, y.MaxHp);
            var preserve = preserveHpRatio || StatSystem.PreserveLiveCurrentHp(source);
            var hp = StatSystem.CurrentHpForWrite(preserve, previousHp, previousMax, y.Hp, y.MaxHp);
            ZombieCombatFields.SetMaxHp(z, max);
            ZombieCombatFields.SetHp(z, hp);
            if (y.Arm1Max > 0) z.theFirstArmorMaxHealth = ClampToInt32Reporting(y.Arm1Max, "zombie.arm1Max", source);
            if (y.Arm1 > 0) z.theFirstArmorHealth = ClampToInt32Reporting(y.Arm1, "zombie.arm1", source);
            if (y.Arm2Max > 0) z.theSecondArmorMaxHealth = ClampToInt32Reporting(y.Arm2Max, "zombie.arm2Max", source);
            if (y.Arm2 > 0) z.theSecondArmorHealth = ClampToInt32Reporting(y.Arm2, "zombie.arm2", source);
            // ⛔ ATTACK IS NOT WRITTEN — the zombie half of the same owner ruling (2026-09-16). See
            // `WritePlant`'s own block above for the full reason: the RPG's battle engine and damage
            // calculator already resolve damage, and paying the same progression a second time through
            // PvZ's own `theAttackDamage` makes that engine redundant. The zombie side matters just as
            // much as the plant side here — a zombie's bite is what the lawn's own proofs measured the
            // rider against (proof 6: vanilla 985 → rider −1204/−1871).
            //
            // z.theAttackDamage = ClampToInt32Reporting(Math.Max(1L, y.Atk), "zombie.atk", source);
            if (y.ZombieSpeed > 0) z.uniqueSpeed = (float)y.ZombieSpeed;

            // E38 (spec-entity-fields-12plus.md): four more zombie fields, composed — same
            // unconditional-write rule as the plant half above (every one captured from a genuine
            // live field on the zombie's own side, so a composed zero is ordinary, not absent).
            // ⛔ ARMOR AND TAKE-DAMAGE MULTIPLIER ARE NOT WRITTEN — the mitigation half of the same
            // ruling. Both are terms in the damage equation, and the RPG resolves that equation:
            // `combat.defense.omni` plus its seven element channels are registered derived channels
            // read by `OverlayCombatCalculator`, which is what the lawn rider's own proofs measured.
            // Writing a composed flat armor and a composed damage-taken multiplier into PvZ's fields
            // makes that calculator redundant for the same entity, exactly as writing `y.Atk` did on
            // the offence side.
            //
            // ⚠️ `takeDmgMultiplier` is a legal passive-tree target, not merely a debug knob —
            // `ChannelLegality.LowerIsBetterPrimaries` lists it, and `AtomKindRegistry.PrimaryChannels`
            // carries it. So this is a LATENT double-pay, not a hypothetical one: the first tree node
            // that touches it would have been paid twice with no code change anywhere.
            //
            // Both stay composed, so the sheet, the power price and every proof still read them.
            // z.theArmor = (float)y.ArmorFlat;
            // z.takeDmgMultiplier = (float)y.TakeDmgMultiplier;
            // theSpeed/theOriginSpeed mirror uniqueSpeed's own shape immediately above.
            if (y.ZombieSpeedCurrent > 0) z.theSpeed = (float)y.ZombieSpeedCurrent;
            if (y.ZombieOriginSpeed > 0) z.theOriginSpeed = (float)y.ZombieOriginSpeed;

            try { z.UpdateHealthText(); } catch { }

            Remember(z.Pointer, ZombieCombatFields.GetHp(z), ZombieCombatFields.GetMaxHp(z), z.theAttackDamage, source);
            ProofWrite("zombie", z.Pointer, source, beforeHp, beforeMax, beforeAtk,
                ZombieCombatFields.GetHp(z), ZombieCombatFields.GetMaxHp(z), z.theAttackDamage);
        }
        catch (Exception ex) { CheatState.Error("writer.zombie: " + ex.Message); }
    }

    /// <summary>Non-core Tab B fields (intervals, speed, …) — still Writer-owned.</summary>
    public static void WritePlantExtras(Plant p)
    {
        if (p == null) return;
        try
        {
            // E16's two interval keys AND E38's eight plant keys (spec-entity-fields-12plus.md) all
            // moved to the composed path and are deliberately absent here. Writing them in both
            // places would fight the composer, last-write-wins and spawn-order dependent, so the
            // same board could settle differently twice. They arrive as Override modifiers now —
            // see CheatState.BuildPlantAbsoluteReal. P-SHIELD, P-ATK-CD, P-ATK-ADD, P-PROD-CD,
            // P-SPEED, P-MOVE, P-LEVEL and P-SHOOTLVL used to be written here directly.
            //
            // The guard that keeps them gone reads THIS FILE as text, so do not write any of their
            // field assignments in a comment either; it cannot tell one from code.
            if (CheatState.On("P-MOD-HP"))
                try { p.ModifyHealth(0, p.thePlantMaxHealth); } catch { }
            if (CheatState.On("P-MOD-ATK"))
                try { p.ModifyDamage(0, p.attackDamage); } catch { }
            try { p.UpdateText(); } catch { }
        }
        catch (Exception ex) { CheatState.Error("plant extras: " + ex.Message); }
    }

    public static void WriteZombieExtras(Zombie z)
    {
        if (z == null) return;
        try
        {
            // The unique-speed key (E16) AND E38's four zombie keys (Z-ARMOR-F, Z-TAKEMULT, Z-SPD,
            // Z-SPD-O — spec-entity-fields-12plus.md) all moved to the composed path — see the note
            // in WritePlantExtras, including why they are not quoted here either.
            if (CheatState.IsUserSet("Z-SLOW-FREEZE") && CheatState.FVal("Z-SLOW-FREEZE") >= 0)
                try { z.freezeSpeed = CheatState.FVal("Z-SLOW-FREEZE"); } catch { }
            if (CheatState.IsUserSet("Z-SLOW-COLD") && CheatState.FVal("Z-SLOW-COLD") >= 0)
                try { z.coldSpeed = CheatState.FVal("Z-SLOW-COLD"); } catch { }
            if (CheatState.IsUserSet("Z-SLOW-BUTTER") && CheatState.FVal("Z-SLOW-BUTTER") >= 0)
                try { z.butterSpeed = CheatState.FVal("Z-SLOW-BUTTER"); } catch { }
            try { z.UpdateHealthText(); } catch { }
        }
        catch (Exception ex) { CheatState.Error("zombie extras: " + ex.Message); }
    }

    /// <summary>FA10 overlay current-HP add. Heal clamps to max. HP≤0 → ForceKill (Die). Never TakeDamage.</summary>
    public static void AddPlantHp(Plant p, long delta, string source)
    {
        if (p == null) return;
        using (OverlayApplyGuard.Enter())
        {
            try
            {
                var live = (long)p.thePlantHealth;
                var max = (long)p.thePlantMaxHealth;
                var next = ResourceDeltaMath.Apply(live, delta, max);
                if (next <= 0)
                {
                    ForceKillPlant(p, source);
                    return;
                }

                var hp = ClampToInt32Reporting(next, "plant.hp", source);
                p.thePlantHealth = hp;
                try { p.UpdateText(); } catch { }
                Remember(p.Pointer, hp, p.thePlantMaxHealth, p.attackDamage, source);
                ProofWrite("plant", p.Pointer, source, live, max, p.attackDamage,
                    p.thePlantHealth, p.thePlantMaxHealth, p.attackDamage);
            }
            catch (Exception ex) { CheatState.Error("add plant hp: " + ex.Message); }
        }
    }

    /// <summary>
    /// FA10 overlay current-HP add. Heal clamps to max. HP≤0 → ForceKill (Die). Never TakeDamage.
    ///
    /// <para><b>Damage spends armour first (fixed 2026-09-17).</b> This method used to hand a negative
    /// delta straight to <see cref="ResourceDeltaMath.Apply"/>, which only knows about health — so every
    /// overlay damage source <b>bypassed both armour layers</b>, and a Buckethead bled health with its
    /// bucket untouched. Damage now runs through <see cref="ArmorCascade"/> in the owner-ruled order
    /// <c>armor2 → armor1 → hp</c>, and only what survives both layers reaches health.</para>
    ///
    /// <para><b>Healing is unchanged and deliberately asymmetric:</b> a positive delta still goes to
    /// health alone and never refills armour. Restoring armour is a separate feature (what refills it, at
    /// what rate, whether a destroyed bucket returns) and inventing it inside a damage fix would be a
    /// silent behaviour change.</para>
    /// </summary>
    public static void AddZombieHp(Zombie z, long delta, string source)
    {
        if (z == null) return;
        using (OverlayApplyGuard.Enter())
        {
            try
            {
                var live = ZombieCombatFields.GetHp(z);
                var max = ZombieCombatFields.GetMaxHp(z);

                long next;
                if (delta < 0)
                {
                    // Read both layers defensively: a game build without a second armour field must cost
                    // that layer, not the whole damage application.
                    long arm1 = 0, arm2 = 0;
                    try { arm1 = z.theFirstArmorHealth; } catch { }
                    try { arm2 = z.theSecondArmorHealth; } catch { }

                    // Converted to a positive magnitude at the boundary so the sign convention cannot be
                    // lost inside the cascade. long.MinValue has no safe negation, and ResourceDeltaMath
                    // would have refused it anyway — refuse it here for the same reason, loudly.
                    if (ResourceDeltaMath.ExceedsAmountCap(delta))
                        throw new ArgumentOutOfRangeException(
                            nameof(delta), delta, $"exceeds AmountCap ({ResourceDeltaMath.AmountCap})");

                    var cascade = ArmorCascade.Apply(-delta, arm2, arm1, live);

                    // Write back only what actually moved — an unchanged layer must not be re-written,
                    // because EntityStatWriter's whole contract is that it is the single writer and every
                    // write it makes is a real change.
                    if (cascade.Armor2 != arm2)
                        try { z.theSecondArmorHealth = ClampToInt32Reporting(cascade.Armor2, "zombie.arm2", source); } catch { }
                    if (cascade.Armor1 != arm1)
                        try { z.theFirstArmorHealth = ClampToInt32Reporting(cascade.Armor1, "zombie.arm1", source); } catch { }

                    // Armour ate all of it: health is untouched, so there is no HP write and no kill check.
                    if (cascade.HpLost <= 0)
                    {
                        try { z.UpdateHealthText(); } catch { }
                        return;
                    }

                    next = cascade.Hp;
                }
                else
                {
                    next = ResourceDeltaMath.Apply(live, delta, max);
                }

                if (next <= 0)
                {
                    ForceKillZombie(z, source);
                    return;
                }

                ZombieCombatFields.SetHp(z, next);
                try { z.UpdateHealthText(); } catch { }
                Remember(z.Pointer, next, max, z.theAttackDamage, source);
                ProofWrite("zombie", z.Pointer, source, live, max, z.theAttackDamage,
                    ZombieCombatFields.GetHp(z), ZombieCombatFields.GetMaxHp(z), z.theAttackDamage);
            }
            catch (Exception ex) { CheatState.Error("add zombie hp: " + ex.Message); }
        }
    }

    public static void ForceSetPlantHp(Plant p, long hp, string source)
    {
        if (p == null) return;
        try
        {
            // hp arrives long (RPG-scaled); thePlantHealth is Unity's own int field, so it is
            // clamped at the write boundary — the same pattern WritePlant already uses — instead
            // of the implicit narrowing cast this signature used to hide.
            var max = ClampToInt32Reporting(Math.Max(p.thePlantMaxHealth, hp), "plant.maxHp", source);
            var clamped = ClampToInt32Reporting(hp, "plant.hp", source);
            p.thePlantMaxHealth = max;
            p.thePlantHealth = clamped;
            try { p.UpdateText(); } catch { }
            Remember(p.Pointer, clamped, max, p.attackDamage, source);
            ProofWrite("plant", p.Pointer, source, clamped, max, p.attackDamage, clamped, max, p.attackDamage);
        }
        catch (Exception ex) { CheatState.Error("force plant hp: " + ex.Message); }
    }

    public static void ForceSetZombieHp(Zombie z, long hp, string source)
    {
        if (z == null) return;
        try
        {
            var max = Math.Max(ZombieCombatFields.GetMaxHp(z), hp);
            ZombieCombatFields.SetMaxHp(z, max);
            ZombieCombatFields.SetHp(z, hp);
            try { z.UpdateHealthText(); } catch { }
            Remember(z.Pointer, hp, max, z.theAttackDamage, source);
            ProofWrite("zombie", z.Pointer, source, hp, max, z.theAttackDamage, hp, max, z.theAttackDamage);
        }
        catch (Exception ex) { CheatState.Error("force zombie hp: " + ex.Message); }
    }

    public static void ForceKillPlant(Plant p, string source)
    {
        if (p == null) return;
        try
        {
            p.thePlantHealth = 0;
            Forget(p.Pointer);
            ProofNote($"writer.forceKill plant ptr={p.Pointer.ToString("X")} src={source}");
            p.Die(Plant.DieReason.BySelf);
        }
        catch (Exception ex) { CheatState.Error("forceKill plant: " + ex.Message); }
    }

    public static void ForceKillZombie(Zombie z, string source)
    {
        if (z == null) return;
        try
        {
            ZombieCombatFields.SetHp(z, 0);
            Forget(z.Pointer);
            ProofNote($"writer.forceKill zombie ptr={z.Pointer.ToString("X")} src={source} frame={UnityEngine.Time.frameCount}");
            try { z.Die(0); } catch { z.DestoryZombie(); }
        }
        catch (Exception ex) { CheatState.Error("forceKill zombie: " + ex.Message); }
    }

    public static void ScalePlantHp(Plant p, int factor, string source)
    {
        if (p == null || factor == 0) return;
        try
        {
            var beforeHp = p.thePlantHealth;
            var beforeMax = p.thePlantMaxHealth;
            p.thePlantHealth *= factor;
            p.thePlantMaxHealth *= factor;
            Remember(p.Pointer, p.thePlantHealth, p.thePlantMaxHealth, p.attackDamage, source);
            ProofWrite("plant", p.Pointer, source, beforeHp, beforeMax, p.attackDamage,
                p.thePlantHealth, p.thePlantMaxHealth, p.attackDamage);
        }
        catch (Exception ex) { CheatState.Error("scale plant: " + ex.Message); }
    }

    public static void ScaleZombieHp(Zombie z, int factor, string source)
    {
        if (z == null || factor == 0) return;
        try
        {
            var beforeHp = ZombieCombatFields.GetHp(z);
            var beforeMax = ZombieCombatFields.GetMaxHp(z);
            ZombieCombatFields.SetHp(z, beforeHp * factor);
            ZombieCombatFields.SetMaxHp(z, beforeMax * factor);
            Remember(z.Pointer, ZombieCombatFields.GetHp(z), ZombieCombatFields.GetMaxHp(z), z.theAttackDamage, source);
            ProofWrite("zombie", z.Pointer, source, beforeHp, beforeMax, z.theAttackDamage,
                ZombieCombatFields.GetHp(z), ZombieCombatFields.GetMaxHp(z), z.theAttackDamage);
        }
        catch (Exception ex) { CheatState.Error("scale zombie: " + ex.Message); }
    }

    static void Remember(IntPtr ptr, long hp, long maxHp, long atk, string source)
    {
        Registry[ptr] = new AppliedFinal
        {
            Hp = hp,
            MaxHp = maxHp,
            Atk = atk,
            Source = source ?? "",
            Utc = ServerClock.UtcNowDateTime
        };
    }

    static void ProofWrite(
        string side, IntPtr ptr, string source,
        long hpBefore, long maxBefore, long atkBefore,
        long hpAfter, long maxAfter, long atkAfter)
    {
        if (!(CheatState.EmitProof && CheatState.On("SYS-EMIT-PROOF"))) return;
        try
        {
            var payload = new Dictionary<string, object>
            {
                ["side"] = side,
                ["ptr"] = ptr.ToString("X"),
                ["source"] = source ?? "",
                ["hpBefore"] = hpBefore,
                ["maxBefore"] = maxBefore,
                ["atkBefore"] = atkBefore,
                ["hpAfter"] = hpAfter,
                ["maxAfter"] = maxAfter,
                ["atkAfter"] = atkAfter
            };
            CheatState.TagProbe(payload);
            GameHooks.Emit("stat.writer", payload);
            // One short overlay note — avoid flooding when PushScales hits many entities.
            CheatState.LastNote =
                $"writer.{side} src={source} ptr={ptr.ToString("X")} hp {hpBefore}/{maxBefore}->{hpAfter}/{maxAfter}";
            try { RpgHost.Log.Info("[cheat] " + CheatState.LastNote); } catch { }
        }
        catch { /* never break combat writes for proof */ }
    }

    static void ProofNote(string msg)
    {
        if (!(CheatState.EmitProof && CheatState.On("SYS-EMIT-PROOF"))) return;
        try { CheatState.Note(msg); } catch { }
    }

    /// <summary>
    /// Opt-in LimHealth policy. Default: no Harmony body work (observe/gate off) so we cannot
    /// stall the game. Enable SYS-LIMHEALTH-OBSERVE / SYS-LIMHEALTH-GATE only for diagnosis.
    /// </summary>
    [HarmonyPatch(typeof(Plant), nameof(Plant.LimHealth))]
    public static class PlantLimHealthPolicy
    {
        // combat-numerics (lawn-combat-wire T4): `long`, not `int` -- audit-overflow.py A3 flags any
        // `int` holding an hp/max-shaped value. thePlantHealth/thePlantMaxHealth are themselves Unity
        // `int` fields (the same structural host limit ClampToInt32Reporting documents above), so this
        // widen loses nothing and just keeps the diagnostic snapshot off the audit's A3 list.
        static readonly ConcurrentDictionary<IntPtr, (long hp, long max)> BeforeCall = new();
        static DateTime _lastObserveUtc = DateTime.MinValue;

        public static bool Prefix(Plant __instance)
        {
            try
            {
                if (__instance == null) return true;
                var observe = CheatState.On("SYS-LIMHEALTH-OBSERVE");
                var gate = CheatState.On("SYS-LIMHEALTH-GATE");
                if (!observe && !gate) return true;

                var ptr = __instance.Pointer;
                if (observe)
                    BeforeCall[ptr] = (__instance.thePlantHealth, __instance.thePlantMaxHealth);

                if (gate && TryGetApplied(ptr, out var applied))
                {
                    var appliedMax = ClampToInt32Reporting(applied.MaxHp, "plant.maxHp", "limhealth.gate");
                    var appliedHp = ClampToInt32Reporting(applied.Hp, "plant.hp", "limhealth.gate");
                    if (__instance.thePlantMaxHealth < appliedMax)
                        __instance.thePlantMaxHealth = appliedMax;
                    if (__instance.thePlantHealth > __instance.thePlantMaxHealth)
                        __instance.thePlantHealth = __instance.thePlantMaxHealth;
                    if (__instance.thePlantHealth < 1 && appliedHp > 0)
                        __instance.thePlantHealth = Math.Min(appliedHp, __instance.thePlantMaxHealth);
                    return false;
                }
            }
            catch { /* never block LimHealth */ }
            return true;
        }

        public static void Postfix(Plant __instance)
        {
            try
            {
                if (__instance == null) return;
                if (!CheatState.On("SYS-LIMHEALTH-OBSERVE")) return;
                if (!(CheatState.EmitProof && CheatState.On("SYS-EMIT-PROOF"))) return;
                var ptr = __instance.Pointer;
                if (!BeforeCall.TryRemove(ptr, out var before)) return;
                if (!TryGetApplied(ptr, out var applied)) return;

                var afterHp = __instance.thePlantHealth;
                var afterMax = __instance.thePlantMaxHealth;
                var changed = afterHp != before.hp || afterMax != before.max;
                if (!changed && afterMax >= applied.MaxHp) return;

                // Rate-limit: at most one observe event / note per 500ms.
                var now = ServerClock.UtcNowDateTime;
                if ((now - _lastObserveUtc).TotalMilliseconds < 500) return;
                _lastObserveUtc = now;

                var payload = new Dictionary<string, object>
                {
                    ["ptr"] = ptr.ToString("X"),
                    ["hpBefore"] = before.hp,
                    ["maxBefore"] = before.max,
                    ["hpAfter"] = afterHp,
                    ["maxAfter"] = afterMax,
                    ["writerMax"] = applied.MaxHp,
                    ["writerHp"] = applied.Hp,
                    ["writerSource"] = applied.Source,
                    ["revertedVsWriter"] = afterMax < applied.MaxHp
                };
                CheatState.TagProbe(payload);
                GameHooks.Emit("stat.limhealth", payload);
                CheatState.LastNote =
                    $"limhealth.observe ptr={ptr.ToString("X")} {before.hp}/{before.max}->{afterHp}/{afterMax} revert={afterMax < applied.MaxHp}";
                try { RpgHost.Log.Info("[cheat] " + CheatState.LastNote); } catch { }
            }
            catch { }
        }
    }
}
