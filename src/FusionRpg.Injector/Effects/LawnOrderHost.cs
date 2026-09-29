using FusionRpg.Core.Actions;
using FusionRpg.Core.Match.Ai;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// combat-ai `commander-direct-orders` (module 20, CAI4.9, spec-commander-direct-orders.md §1/§5): the
/// lawn's ADMISSION ADAPTER — it takes the <c>lawn.order</c> command the Server relays, stamps the lawn
/// tick, resolves the order's durable identity and offers it to the Core queue. The spec's own rule for
/// this file: *"<c>LawnOrderHost</c> holds only the two lookups and the report"* — every rule
/// (one-live-order, the cap, the refusal vocabulary, the lifetime) lives in
/// <see cref="LawnOrderQueue"/> / <c>DirectOrderAdmission</c>, which CI builds and tests.
///
/// <para><b>The two lookups.</b> (1) The TICK is the engine clock's —
/// <see cref="KernelDriveHost.NowTicks"/>, the same clock the lawn's cooldowns and status expiry read
/// (D15), never <c>DateTime</c>; the queue compares it and holds no clock of its own. (2) The IDENTITY is
/// <see cref="CheatState.ResolveBoundInstanceId"/>, the shipped ptr→Bound-instance lookup
/// (`MatchHost.Runtime`'s own binding index), so an order carries the durable subject rather than a ptr
/// alone — IL2CPP reuses pointers, which is what <c>SubjectId</c> exists for. The run identity
/// (<c>ScopeId</c>) is the live match key the Server stamped.</para>
///
/// <para><b>Fail closed, never throw into the frame.</b> The drain already wraps every command in
/// try/catch and reports through <c>CheatState.Error</c>; an incomplete payload is a REFUSAL here, with a
/// count, rather than a killed frame.</para>
///
/// <para><b>What this file deliberately does NOT do, and where each half is.</b> It does not MARK THE
/// ACTOR DUE: that is module 19's frame slot, which decides which actors have a decision this frame
/// (`LawnDecisionTrigger`/`LawnDecisionBudget`, CAI4.8), so an admitted order is visible to the frame slot
/// the moment that slot exists and nothing here is scheduled a frame late. And it does not FIRE the
/// order: firing is <c>IntentRouter</c>'s forced-intent hook (row 14), which the composition root
/// supplies with the gate check — and that check needs module 16's held set, i.e. an
/// <c>ActionCatalog</c> the injector has no feed for yet. So the ORDER PATH is complete and reached from
/// a real host (Server route → command → this adapter → the Core queue); what remains is the fire path.</para>
/// </summary>
public static class LawnOrderHost
{
    /// <summary>The command name the Server relays. Kept equal to
    /// <c>FusionRpg.Server.LawnOrderEndpoints.CommandName</c> by the tests on both sides rather than by a
    /// shared constant, because the injector does not reference the Server assembly.</summary>
    public const string CommandName = "lawn.order";

    static readonly LawnOrderQueue Queue = new();

    static int _admitted;
    static int _refused;

    /// <summary>Orders admitted since the board edge. A reading, not a contract.</summary>
    public static int AdmittedCount => _admitted;

    /// <summary>Orders refused since the board edge (an incomplete payload, or the queue's own rules).</summary>
    public static int RefusedCount => _refused;

    /// <summary>Live orders right now.</summary>
    public static int LiveCount => Queue.Count;

    /// <summary>
    /// One order from the wire: stamp, resolve the subject, admit, offer. Every argument is nullable
    /// because it came from JSON — an incomplete payload is a refusal, not an exception.
    /// </summary>
    public static void Admit(string? matchKey, string? actorKey, string? actionId, string? targetKey)
        => Admit(matchKey, actorKey, actionId, targetKey, ResolveSubject(actorKey));

    /// <summary>
    /// The same admission over an ALREADY-RESOLVED subject. This is the seam that makes the rule testable
    /// without a live match: the decision itself (<see cref="DirectOrderAdmission.CheckSubject"/>) is
    /// pure over the subject, so a caller that has resolved one can be answered without inventing a
    /// binding — which is the spec's own *"admission cannot invent a subject"*.
    /// </summary>
    public static void Admit(string? matchKey, string? actorKey, string? actionId, string? targetKey, in OrderSubject subject)
    {
        if (string.IsNullOrWhiteSpace(matchKey) || string.IsNullOrWhiteSpace(actorKey)
            || string.IsNullOrWhiteSpace(actionId))
        {
            _refused++;
            try { CheatState.Error("lawn.order: incomplete (matchKey/actorKey/actionId required)"); } catch { }
            return;
        }

        // D7 (owner ruling 2026-09-20, spec Open question 1): **uniques only in v1** — and the spec's §2
        // states the consequence plainly: "v1 can only order an actor that has a live `UniqueBinding`",
        // because that is the only durable lawn identity that exists. `CheckSubject` is where that lives,
        // so this adapter asks it rather than restating it: a general creature (no binding) is refused
        // `SubjectGone`, and a binding that points at a different address is `SubjectMoved` (the ptr was
        // reused). A refused order is never queued.
        var refusal = DirectOrderAdmission.CheckSubject(subject, actorKey);
        if (refusal != DirectOrderRefusal.None)
        {
            _refused++;
            try { CheatState.Note($"lawn.order refused {refusal} for {actorKey}"); } catch { }
            return;
        }

        var order = new DirectOrder(
            ActorKey: actorKey,
            ActionId: actionId,
            TargetKey: targetKey,
            IssuedTick: KernelDriveHost.NowTicks,
            SubjectId: subject.SubjectId,
            ScopeId: matchKey);

        var result = Queue.Offer(actorKey, order);
        if (result.Accepted) _admitted++;
        else _refused++;
    }

    /// <summary>The subject, from the two lookups this adapter is allowed to hold: the durable Bound
    /// instance id, and the ptr's liveness in the board registry.</summary>
    static OrderSubject ResolveSubject(string? actorKey)
    {
        try
        {
            var subjectId = CheatState.ResolveBoundInstanceId(actorKey ?? "");
            var live = InjectorEntityRegistry.FindPlant(actorKey) is not null
                || InjectorEntityRegistry.FindZombie(actorKey) is not null;

            return new OrderSubject(Ptr: actorKey, IsBound: subjectId is not null, IsLive: live, SubjectId: subjectId);
        }
        catch
        {
            // FAIL CLOSED, and the spec asks for it in its own words ("fail closed, never throw into the
            // frame"): the identity lookups reach the live match and therefore the game assembly, and an
            // admission that throws would kill the frame. An unresolvable subject is NOT Bound, so
            // `CheckSubject` refuses the order -- never an admitted order whose subject nobody resolved.
            // (Measured: in a test process the lookup throws FileNotFoundException for Assembly-CSharp,
            // which is exactly the shape this catch exists for.)
            return new OrderSubject(Ptr: actorKey, IsBound: false, IsLive: false);
        }
    }

    /// <summary>The live order for one actor, or false. Read-only: it never scores, never records and
    /// never synthesises an order.</summary>
    public static bool TryPeek(string actorKey, out DirectOrder order) => Queue.TryPeek(actorKey, out order);

    /// <summary>The membership edge — the death drop. Both <see cref="InjectorEntityRegistry.Remove"/>
    /// and <c>Clear</c> call this for the same reason every other per-actor cache there does: a reused
    /// ptr must not inherit a dead actor's order.</summary>
    public static bool Remove(string actorKey) => Queue.Remove(actorKey);

    /// <summary>The board edge: every order goes.</summary>
    public static void Clear()
    {
        Queue.Clear();
        _admitted = 0;
        _refused = 0;
    }
}
