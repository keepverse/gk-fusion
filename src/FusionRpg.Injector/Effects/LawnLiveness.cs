using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// The injector's liveness seam — `lawn-playable` `LW1.6`
/// (<c>docs/architecture/lawn-playable/spec-actor-liveness-refresh.md</c>): it owns the received
/// invalidations as revisions and answers "has this actor's input moved since revision N", which is
/// exactly the read <c>LawnDerivedCache</c> was built to take
/// (<c>gk-core/src/FusionRpg.Core/Actions/Ai/Lawn/LawnDerivedCache.cs</c>, whose own doc names this module as
/// the missing seam).
///
/// <para><b>It marks dirty; it never recomposes.</b> <see cref="Apply"/> writes a counter. The next
/// read of a derived snapshot for an affected actor re-resolves once, through the memo the caller
/// already keys on the revision — so one invalidation in a 300-zombie wave costs at most one further
/// resolve per actor actually read, and nothing for the rest (the bound `rider-hit-cost` needs kept).</para>
///
/// <para><b>A refusal is reported, never swallowed.</b> <see cref="Apply"/> returns the refusal so the
/// SignalR handler can log it: an unknown kind means the server is newer than this injector, and
/// quietly ignoring it is the failure mode this module's rule 5 names.</para>
/// </summary>
internal static class LawnLiveness
{
    static readonly ActorLivenessRevisions Revisions = new();

    /// <summary>The player id the last invalidation named. Used only when the host has not yet applied a
    /// power snapshot (<c>CheatState.CurrentPlayerId</c> is still 0 at session start), so a revision read
    /// from a lawn view is keyed on the same player the server is invalidating.</summary>
    static long _lastPlayerId;

    /// <summary>The revision the derived memo compares against for one live actor.</summary>
    public static long RevisionOf(string ptr)
    {
        if (string.IsNullOrWhiteSpace(ptr)) return 0L;
        return Revisions.Read(new LivenessActorKey(CurrentPlayerId(), CombatPtr.Normalize(ptr)));
    }

    /// <summary>Apply one received invalidation. The caller logs the refusal; nothing here throws on a
    /// malformed payload (a SignalR handler must not die on one).</summary>
    public static bool Apply(string? wireKind, long playerId, string? entityKey, out LivenessInvalidationKind kind, out LivenessRefusal refusal)
    {
        if (playerId > 0) _lastPlayerId = playerId;
        return LivenessInvalidationRouter.TryApply(Revisions, wireKind, playerId, entityKey, out kind, out refusal);
    }

    /// <summary>The death / pointer-reuse edge: a new actor at a reused address must not inherit a
    /// stranger's revision.</summary>
    public static void Forget(string? ptr)
    {
        if (string.IsNullOrWhiteSpace(ptr)) return;
        Revisions.ForgetActor(CombatPtr.Normalize(ptr));
    }

    /// <summary>The board-start / match-end barrier.</summary>
    public static void Clear() => Revisions.Clear();

    static long CurrentPlayerId()
    {
        var id = CheatState.CurrentPlayerId;
        return id > 0 ? id : _lastPlayerId;
    }
}
