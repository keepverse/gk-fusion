using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Combat.Observability;
using FusionRpg.Core.Stats.Derived;
using Il2CppInterop.Runtime.InteropTypes;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// Injector-side adapter for <see cref="LawnCombatObserver"/> — Task 0 (`lawn-combat-wire`, "Phase 0 —
/// the ruler"). Every public method here is called UNCONDITIONALLY from an already-existing hot-path
/// call site: no <c>DebugRuntime.SessionActive</c> check, no <c>LogDamage</c> check, no
/// <c>EventDrainHost.Enabled</c> check, and it never sets or reads either of those two flags itself. It
/// therefore cannot perturb the branch it observes — the exact defect that disqualifies
/// <c>InjectorCombatBridge.EmitOverlayBreakdown</c> (session-gated) and the legacy <c>combat.hit</c>/
/// <c>plant.damage</c>/<c>zombie.damage</c> telemetry (its own <c>LogDamage</c>/<c>SessionActive</c>
/// gate routes the SAME hit down a DIFFERENT branch than the shipped v2 record path — see
/// <c>GameHooks.cs</c>'s <c>PlantTakeDamage</c>/<c>ZombieTakeDamage</c> prefixes) as this program's
/// own ruler.
///
/// <para>Call sites (exactly two, both additive — neither replaces nor reorders any existing branch):</para>
/// <list type="bullet">
/// <item><description><c>GameHooks.cs</c>'s <c>PlantTakeDamage</c>/<c>ZombieTakeDamage</c> Harmony
/// prefixes call <see cref="RecordVanillaHit"/> once the vanilla damage number is final (post defense
/// scaling), before any of the existing telemetry/record/grant branches run.</description></item>
/// <item><description><c>EffectRuntime.cs</c>'s <c>WireCombatMath</c> callback calls
/// <see cref="RecordRpgDelta"/> immediately after (never instead of)
/// <c>InjectorCombatBridge.EmitOverlayBreakdown</c>.</description></item>
/// </list>
///
/// <para>A single escape-hatch kill switch (<c>FUSIONRPG_LAWN_OBSERVER=0</c>) exists for the same
/// reason every other diagnostic subsystem here has one (<c>PerfProbe</c>'s <c>FUSIONRPG_PERF=0</c>,
/// <c>EventDrainHost</c>'s <c>FUSIONRPG_EVENT_V2=0</c>): an OFF-escape for an emergency, defaulting ON.
/// It is never flipped to observe — observation happens by default, unconditionally.</para>
/// </summary>
public static class LawnCombatObserverBridge
{
    public static bool Enabled { get; set; } =
        !string.Equals(Environment.GetEnvironmentVariable("FUSIONRPG_LAWN_OBSERVER"), "0", StringComparison.Ordinal);

    /// <summary>
    /// Called from <c>Plant.TakeDamage</c>/<c>Zombie.TakeDamage</c>'s Harmony prefix, after any vanilla
    /// defense scaling and after the early cheat/re-entrancy guards (<c>P-GOD</c>/<c>Z-GOD</c>,
    /// <c>OverlayApplyGuard.IsActive</c>) — a hit fully absorbed by one of those never reaches here,
    /// matching how the rest of the method already treats them. <paramref name="victimSide"/> is the
    /// side TAKING damage ("plant" or "zombie") — the attacker is the other side. Never throws: every
    /// call site already wraps hook bodies in try/catch, but this method catches its own failures too
    /// so a resolution problem here can never surface as a combat-hook exception.
    /// </summary>
    public static void RecordVanillaHit(string victimSide, Il2CppObjectBase? victim, IDamageMaker? damageFrom, long vanillaAmount)
    {
        if (!Enabled || victim == null) return;
        try
        {
            var victimPtr = GameDumps.Ptr(victim);
            var frame = SafeFrame();
            // L-N3: a bite's damageFrom does not name the biter; the drain's same-frame melee record does, and the RPG record
            // for this bite carries that same attacker. Melee swing identity is (attacker, frame).
            var (attackerPtr, swingId) = string.Equals(victimSide, "plant", StringComparison.OrdinalIgnoreCase)
                && EventDrainHost.TryPeekMeleeAttacker(victim.Pointer, out var biter)
                ? (biter.ToString("X"), biter.ToString("X") + ":" + frame)
                : ResolveAttackerAndSwingId(damageFrom, victimPtr, frame);
            // L-N39: when damageFrom names nobody (an empty wrapper, or the victim itself — BambooDragon's breath, live
            // 2026-09-16), take the ambient attacker the drain brackets, as ZombieTakeDamage's own ambient fallback does.
            if ((attackerPtr.Length == 0 || CombatPtr.EqualsPtr(attackerPtr, victimPtr))
                && EventDrainHost.TryPeekAmbientMeleeAttacker(out var ambient))
                (attackerPtr, swingId) = (ambient.ToString("X"), ambient.ToString("X") + ":" + frame);
            var attackerSide = string.Equals(victimSide, "plant", StringComparison.OrdinalIgnoreCase) ? "zombie" : "plant";
            LawnCombatObserver.RecordVanillaHit(swingId, attackerPtr, victimPtr, attackerSide, vanillaAmount, frame);
        }
        catch { /* observation must never break the hit it observes */ }
    }

    /// <summary>
    /// Called alongside (never instead of) <c>InjectorCombatBridge.EmitOverlayBreakdown</c> from the
    /// same <c>WireCombatMath</c> callback — fires whenever an overlay breakdown is computed for a real
    /// hit, debug session or not. Today this never fires: no grant exists anywhere on the lawn to
    /// trigger the overlay math in the first place (`basic-attack-grant`/T10 is what starts calling
    /// it). Once it lands, this bridge starts reporting real RPG deltas with no change needed here.
    /// </summary>
    public static void RecordRpgDelta(OverlayCombatBreakdown breakdown, DamagePacket packet, string targetPtr)
    {
        if (!Enabled) return;
        try
        {
            var attackerPtr = packet.ActorPtr ?? "";
            // Provisional swing-id shape (matches RecordVanillaHit's own — T6/`lawn-hit-attribution`
            // formalises real swing identity end to end): attacker ptr + packet tick as the correlator.
            var swingId = attackerPtr + ":" + packet.Tick;

            ElementTypeId? attackerElement = null;
            ElementTypeId? victimElement = null;
            ElementMatchupRelation? relation = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(attackerPtr))
                    attackerElement = InjectorCombatBridge.ResolveActor(attackerPtr, attackerLess: false).ElementTypes.Primary;
                if (!string.IsNullOrWhiteSpace(targetPtr))
                    victimElement = InjectorCombatBridge.ResolveActor(targetPtr, attackerLess: false).ElementTypes.Primary;
                if (attackerElement is { } a && victimElement is { } v)
                    relation = ElementRingMatrix.GetRelation(a, v);
            }
            catch { /* elements/matchup are informational-only fields on this record */ }

            LawnCombatObserver.RecordRpgDelta(
                swingId, attackerPtr, targetPtr ?? "", breakdown.FinalSignedDelta,
                attackerElement, victimElement, relation,
                LawnCombatObserver.Outcomes.Of(breakdown.Hit, breakdown.Parried, breakdown.Blocked, breakdown.Crit));
        }
        catch { }
    }

    /// <summary>
    /// Rides the already-shipped ~5s <c>/api/perf</c> window (<c>PerfReporter.Flush</c>) — no new HTTP
    /// surface, no new event kind. Converts the strongly-typed Core snapshot to the same flat
    /// <c>Dictionary&lt;string,object&gt;</c> shape every other perf sub-section already uses
    /// (mirrors <c>EventDrainHost.SnapshotStats()</c>).
    /// </summary>
    public static Dictionary<string, object> SnapshotAndReset()
    {
        var snap = LawnCombatObserver.SnapshotAndReset();
        return new Dictionary<string, object>
        {
            ["enabled"] = Enabled,
            ["totalHits"] = snap.TotalHits,
            ["totalSwings"] = snap.TotalSwings,
            ["actionTriggers"] = snap.ActionTriggers,
            ["staminaSpent"] = snap.StaminaSpent,
            ["regenAccrued"] = snap.RegenAccrued,
            ["exhaustionEvents"] = snap.ExhaustionEvents,
            ["rpgDeltaMergedHits"] = snap.RpgDeltaMergedHits,
            ["rpgDeltaUnmergedRecords"] = snap.RpgDeltaUnmergedRecords,
            ["rpgMisses"] = snap.RpgMisses,
            ["droppedRecords"] = snap.DroppedRecords,
            ["recentHits"] = snap.RecentHits.Select(h => new Dictionary<string, object>
            {
                ["seq"] = h.Seq,
                ["frame"] = h.Frame,
                ["swingId"] = h.SwingId,
                ["attackerPtr"] = h.AttackerPtr,
                ["victimPtr"] = h.VictimPtr,
                ["attackerSide"] = h.AttackerSide,
                ["vanillaAmount"] = h.VanillaAmount,
                ["rpgDelta"] = h.RpgDelta,
                ["rpgDeltaObserved"] = h.RpgDeltaObserved,
                ["attackerElement"] = h.AttackerElement?.ToString() ?? "",
                ["victimElement"] = h.VictimElement?.ToString() ?? "",
                ["matchupRelation"] = h.MatchupRelation?.ToString() ?? "",
                ["rpgOutcome"] = h.RpgOutcome
            }).ToList()
        };
    }

    /// <summary>
    /// Bullet: identifies the attacker as the bullet's own ptr — deliberately the SAME baseline
    /// identity <c>EventDrainHost.TryRecordDealtFromBullet</c> already uses today, not an idealised
    /// "true shooter" resolution. T6 (`lawn-hit-attribution`) is the task that resolves the real
    /// shooter (`bullet.from`/`from_zombie`) for the GAMEPLAY-affecting v2 record; this ruler's
    /// baseline is honest about matching what the pipeline it measures actually does today. One
    /// bullet ptr is one swing, even across multiple pierced victims (D8's shape).
    /// Melee/direct: <paramref name="damageFrom"/> IS the attacker already (a Zombie biting a Plant),
    /// so the resolution is already correct — no T6 dependency on this branch.
    /// </summary>
    static (string AttackerPtr, string SwingId) ResolveAttackerAndSwingId(IDamageMaker? damageFrom, string victimPtr, int frame)
    {
        if (damageFrom is not Il2CppObjectBase obj)
            return ("", "unknown:" + victimPtr);

        try
        {
            var bullet = obj.TryCast<Bullet>();
            if (bullet != null)
            {
                // The swing stays the bullet (one pierce = one swing, D8). The attacker is the shooter the drain resolved
                // at the bullet's spawn, so the RPG record for this hit (actor = shooter) can find this one (L-N35).
                var ptr = GameDumps.Ptr(bullet);
                return EventDrainHost.TryPeekBulletShooter(bullet.Pointer, out var shooter)
                    ? (shooter.ToString("X"), ptr)
                    : (ptr, ptr);
            }
        }
        catch { }

        var attackerPtr = GameDumps.Ptr(obj);
        return (attackerPtr, attackerPtr + ":" + frame);
    }

    static int SafeFrame()
    {
        try { return UnityEngine.Time.frameCount; }
        catch { return 0; }
    }
}
