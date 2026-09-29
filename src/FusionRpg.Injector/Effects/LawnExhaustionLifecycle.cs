using System.Collections.Generic;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Status;
using FusionRpg.Core.Time;
using FusionRpg.Injector.Host;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// The lawn's real caller for <see cref="ExhaustionPolicy"/> — `lawn-playable` `LW1.3`
/// (<c>docs/architecture/lawn-playable/spec-exhaustion-event.md</c>, defect E3: the machinery was built
/// and inert).
///
/// <para><b>What this adds, in one sentence:</b> the charge site's already-resolved stamina pool value
/// is passed through an <see cref="ExhaustionEdgeDetector"/>, and on a real transition the status is
/// applied or withdrawn (by its host-and-resource-scoped grant id) and one event is emitted through the
/// existing <c>GameHooks.Emit</c> drain — <c>actor.exhausted</c> / <c>actor.recovered</c>.</para>
///
/// <para><b>The payload is EMPTY on purpose.</b> <c>exhaustion.stamina</c> is registered with an empty
/// <see cref="StatusStatMod"/> list, which is a legal, validated payload and changes no number: which
/// channels an exhaustion status actually weakens is authored balance and belongs to
/// <c>lawn-tuning-profile</c>. What this buys today is that exhaustion is <b>visible and bindable</b> —
/// the HUD and the VFX have a real status to hang off — without making an unmeasured balance decision.
/// The policy's own constructor validation (no self-regen spiral, never <c>hp</c>) still runs.</para>
///
/// <para><b>The status is one instance per actor AND resource</b>, never a board-wide flag: the grant id
/// <c>ExhaustionStatusIds.GrantIdFor(ptr, resourceId)</c> means a withdraw clears exactly this actor's
/// this-resource exhaustion, never a sibling resource's and never another actor's.</para>
///
/// <para><b>Fail closed, never silently.</b> Every call site is wrapped: a detector or status failure
/// logs and returns without emitting, exactly as the charge site itself fails closed. Nothing here can
/// make a swing cheaper or more expensive — it only observes and reports.</para>
/// </summary>
internal static class LawnExhaustionLifecycle
{
    /// <summary>The lawn basic attack's cost resource — the same id
    /// <see cref="LawnBasicAttackCostCharger.KernelTick"/> already resolves per ptr, and the resource
    /// the shipped `action-corpus-cost-templates.v2.json` `kinds.basic` row charges
    /// (<c>LawnBasicAttackCostRow</c>). Named once here rather than repeated as a literal at each
    /// observation site.</summary>
    public const string ResourceId = "stamina";

    static readonly ExhaustionEdgeDetector Detector = new();

    static ExhaustionPolicy? _policy;
    static bool _statusSyncWarned;

    /// <summary>Built once: the policy registers <c>exhaustion.stamina</c> into the process-wide
    /// catalog, so a fresh instance per observed swing would re-register it on the hot path for no
    /// gain (<c>StatusCatalog.Register</c> overwrites by id, so it is idempotent — this is a cost
    /// choice, not a correctness one).</summary>
    static ExhaustionPolicy Policy() => _policy ??= new ExhaustionPolicy(
        StatusCatalogHub.Current,
        new Dictionary<string, IReadOnlyList<StatusStatMod>>
        {
            // The EMPTY payload: one entry, zero stat mods. `Sync` still applies and withdraws the
            // status by grant id; it simply moves no channel.
            [ResourceId] = Array.Empty<StatusStatMod>(),
        });

    /// <summary>
    /// Observe one resolved pool value for one actor. On a real edge: sync
    /// <c>exhaustion.{resourceId}</c> against the live <see cref="StatusRuntime"/> and emit the one
    /// event the edge owns. A non-edge is a no-op — which is the whole of defect E1's fix (a hundred
    /// refused swings inside one window produce one event, not a hundred).
    /// </summary>
    public static void ObserveAndEmit(string? hostPtr, long resolvedValue, long nowTick, string? side, string? matchKey)
    {
        if (string.IsNullOrWhiteSpace(hostPtr)) return; // no identity -- the detector would refuse too

        var ptr = CombatPtr.Normalize(hostPtr);
        ExhaustionTransition transition;
        try
        {
            transition = Detector.Observe(ptr, ResourceId, matchKey, resolvedValue, nowTick);
        }
        catch (Exception ex)
        {
            RpgHost.Log.Warning("lawn-exhaustion: edge detection failed: " + ex.Message);
            return;
        }

        if (!transition.IsEdge) return;

        try
        {
            // The stamp is the seam's (RS3): `now` is the status's applied-at, never a deadline comparison
            // -- ExhaustionPolicy.Sync passes it to StatusRuntime.Apply with BaseDuration 0, so the grant
            // persists until ClearGrant and no clock read here decides anything. Caught by
            // gk-core/scripts/guard-clock-seam.py the moment this file landed on the merged tree, which is what
            // that guard exists for.
            Policy().Sync(EffectRuntime.Status, transition.HostPtr, transition.ResourceId, resolvedValue, ServerClock.UtcNow);
        }
        catch (Exception ex)
        {
            // Loud once: a status-runtime failure must not spam the log on every exhaustion window in a
            // 300-zombie wave, but it must not vanish either.
            if (!_statusSyncWarned)
            {
                _statusSyncWarned = true;
                RpgHost.Log.Warning("lawn-exhaustion: status sync failed (further failures muted): " + ex.Message);
            }
        }

        try
        {
            var resolvedSide = side;
            if (string.IsNullOrWhiteSpace(resolvedSide))
            {
                // A cache hit when anything already resolved this ptr this match (`LawnElementResolverHost`
                // is the shared resolver, not a second board scan) -- the recovering edge observed from
                // the kernel grid has no record to read the side off.
                var (resolved, _, _, _) = LawnElementResolverHost.Resolve(ptr);
                resolvedSide = resolved;
            }

            GameHooks.Emit(
                ExhaustionEdgeEvents.KindFor(transition.Edge),
                ExhaustionEdgeEvents.Build(transition, resolvedSide, matchKey ?? GameHooks.MatchKey));
        }
        catch (Exception ex)
        {
            RpgHost.Log.Warning("lawn-exhaustion: emit failed: " + ex.Message);
        }
    }

    /// <summary>The death / pointer-reuse edge — IL2CPP reuses addresses, so a new actor must not
    /// inherit a stranger's window. Called from <c>InjectorEntityRegistry.Remove</c>.</summary>
    public static void Forget(string? hostPtr) => Detector.Forget(CombatPtr.Normalize(hostPtr));

    /// <summary>The board-start / match-end barrier. Called from <c>InjectorEntityRegistry.Clear</c>, so
    /// a new match is a fresh window for every actor rather than a carried-over one.</summary>
    public static void Clear() => Detector.Clear();
}
