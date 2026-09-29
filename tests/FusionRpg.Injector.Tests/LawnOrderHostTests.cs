using FusionRpg.Core.Match.Ai;
using FusionRpg.Injector.Effects;
using Xunit;

namespace FusionRpg.Injector.Tests;

/// <summary>
/// combat-ai `commander-direct-orders` (module 20, CAI4.9, spec-commander-direct-orders.md §1/§5): the
/// admission adapter's own contract — stamp the tick, resolve the identity, offer to the Core queue, and
/// fail closed on an incomplete payload.
///
/// <para><b>What is tested here and what is not.</b> Every RULE (one live order per actor, the cap, the
/// refusal vocabulary, the lifetime) lives in <c>LawnOrderQueue</c> and is tested in
/// <c>tests/FusionRpg.Core.Balance.Tests/CombatAi/</c>, which CI builds. This file tests the ADAPTER:
/// that the payload reaches the queue with the tick and the run identity stamped, that an incomplete
/// payload is a refusal rather than an exception, and that the two membership edges (death, board reset)
/// drop exactly what they should. That split is the spec's own: *"LawnOrderHost holds only the two
/// lookups and the report"*.</para>
///
/// <para>Not run by CI (<c>ci.yml</c> never compiles FusionRpg.Injector); build/run locally with
/// <c>$env:FUSIONRPG_GAME_DIR</c> set. Shares <c>CheatState</c>'s statics with the rest of the
/// collection, so every test clears first.</para>
/// </summary>
[Collection("CheatState statics")]
public class LawnOrderHostTests
{
    public LawnOrderHostTests() => LawnOrderHost.Clear();

    /// <summary>A Bound subject — the only thing v1 admits (D7). The subject is passed in because the
    /// production lookup needs a live `UniqueBinding`, and the DECISION is pure over the subject.</summary>
    static OrderSubject Bound(string actorKey, string? subjectId = "inst.7") =>
        new(Ptr: actorKey, IsBound: true, IsLive: true, SubjectId: subjectId);

    [Fact]
    public void A_complete_order_reaches_the_queue_with_its_tick_and_run_identity_stamped()
    {
        LawnOrderHost.Admit("match.7", "entity:1a", "act.skill", "entity:2b", Bound("entity:1a"));

        Assert.Equal(1, LawnOrderHost.LiveCount);
        Assert.Equal(1, LawnOrderHost.AdmittedCount);
        Assert.Equal(0, LawnOrderHost.RefusedCount);

        Assert.True(LawnOrderHost.TryPeek("entity:1a", out var order));
        Assert.Equal("entity:1a", order.ActorKey);
        Assert.Equal("act.skill", order.ActionId);
        Assert.Equal("entity:2b", order.TargetKey);
        Assert.Equal("match.7", order.ScopeId);   // the run identity, so a stale order is refusable
        Assert.True(order.IssuedTick >= 0);       // the engine clock's stamp, never a wall clock
        Assert.Equal("inst.7", order.SubjectId);  // the durable identity the caller resolved
    }

    /// <summary>
    /// **D7 (owner ruling 2026-09-20): uniques only in v1.** The spec's §2 states the consequence — "v1
    /// can only order an actor that has a live `UniqueBinding`", because that is the only durable lawn
    /// identity that exists — and `DirectOrderAdmission.CheckSubject` is where it lives. A general
    /// creature is refused `SubjectGone` and NOTHING is queued.
    /// </summary>
    [Fact]
    public void A_general_creature_is_never_orderable()
    {
        LawnOrderHost.Admit("match.7", "entity:general", "act.skill", null,
            new OrderSubject(Ptr: "entity:general", IsBound: false, IsLive: true));

        Assert.Equal(0, LawnOrderHost.LiveCount);
        Assert.Equal(0, LawnOrderHost.AdmittedCount);
        Assert.Equal(1, LawnOrderHost.RefusedCount);
        Assert.False(LawnOrderHost.TryPeek("entity:general", out _));
    }

    /// <summary>The ptr-reuse answer, at admission: the durable identity resolves, but to a DIFFERENT
    /// address than the order names.</summary>
    [Fact]
    public void A_binding_pointing_elsewhere_is_refused_subject_moved()
    {
        LawnOrderHost.Admit("match.7", "entity:1a", "act.skill", null,
            new OrderSubject(Ptr: "entity:reused", IsBound: true, IsLive: true, SubjectId: "inst.7"));

        Assert.Equal(0, LawnOrderHost.LiveCount);
        Assert.Equal(1, LawnOrderHost.RefusedCount);
    }

    /// <summary>
    /// NOT unit-testable, and named rather than faked: the PRODUCTION resolution reaches
    /// <c>MatchHost.Runtime</c> and therefore the game assembly, so in a test process it throws
    /// <c>FileNotFoundException: Assembly-CSharp</c>. <c>LawnOrderHost.ResolveSubject</c> catches exactly
    /// that and returns a NOT-BOUND subject, which is the spec's own *"fail closed, never throw into the
    /// frame"* — so the production path refuses the order rather than admitting one whose subject nobody
    /// resolved. The two subject-based cases above cover the DECISION; the catch covers the lookup.
    /// </summary>
    [Fact]
    public void The_subject_seam_is_what_the_decision_is_tested_through()
    {
        // The production overload exists and is reachable; the decision it delegates to is covered above.
        Assert.Equal("lawn.order", LawnOrderHost.CommandName);
    }

    /// <summary>An incomplete payload is a REFUSAL with a count -- never an exception into the frame,
    /// which is the drain's own fail-closed rule. A VALID subject is passed so the refusal can only come
    /// from the missing field.</summary>
    [Theory]
    [InlineData(null, "entity:1a", "act.skill")]
    [InlineData("match.7", null, "act.skill")]
    [InlineData("match.7", "entity:1a", null)]
    [InlineData("  ", "entity:1a", "act.skill")]
    public void An_incomplete_order_is_refused_and_queues_nothing(string? matchKey, string? actorKey, string? actionId)
    {
        LawnOrderHost.Admit(matchKey, actorKey, actionId, targetKey: null, Bound("entity:1a"));

        Assert.Equal(0, LawnOrderHost.LiveCount);
        Assert.Equal(0, LawnOrderHost.AdmittedCount);
        Assert.Equal(1, LawnOrderHost.RefusedCount);
    }

    /// <summary>The death edge drops exactly that actor's order and nobody else's.</summary>
    [Fact]
    public void Remove_drops_exactly_that_actors_order()
    {
        LawnOrderHost.Admit("match.7", "entity:1a", "act.skill", null, Bound("entity:1a"));
        LawnOrderHost.Admit("match.7", "entity:2b", "act.skill", null, Bound("entity:2b"));

        Assert.True(LawnOrderHost.Remove("entity:1a"));

        Assert.Equal(1, LawnOrderHost.LiveCount);
        Assert.False(LawnOrderHost.TryPeek("entity:1a", out _));
        Assert.True(LawnOrderHost.TryPeek("entity:2b", out _));
    }

    /// <summary>A reused ptr finds no order: the drop happens on the death edge, so the address comes
    /// back clean (the same property the ring and the resource pools have).</summary>
    [Fact]
    public void A_reused_ptr_starts_with_no_order()
    {
        LawnOrderHost.Admit("match.7", "entity:1a", "act.skill", null, Bound("entity:1a"));
        LawnOrderHost.Remove("entity:1a");

        Assert.False(LawnOrderHost.TryPeek("entity:1a", out _));

        LawnOrderHost.Admit("match.7", "entity:1a", "act.other", null, Bound("entity:1a"));
        Assert.True(LawnOrderHost.TryPeek("entity:1a", out var fresh));
        Assert.Equal("act.other", fresh.ActionId);
    }

    [Fact]
    public void Clear_drops_every_order_and_the_counters()
    {
        LawnOrderHost.Admit("match.7", "entity:1a", "act.skill", null, Bound("entity:1a"));
        LawnOrderHost.Admit("match.7", "entity:2b", "act.skill", null, Bound("entity:2b"));

        LawnOrderHost.Clear();

        Assert.Equal(0, LawnOrderHost.LiveCount);
        Assert.Equal(0, LawnOrderHost.AdmittedCount);
        Assert.False(LawnOrderHost.TryPeek("entity:1a", out _));
        Assert.False(LawnOrderHost.TryPeek("entity:2b", out _));
    }

    /// <summary>The queue's own rule, reached through the adapter: a second order for the same actor
    /// REPLACES the first rather than queueing behind it.</summary>
    [Fact]
    public void A_second_order_for_the_same_actor_supersedes_the_first()
    {
        LawnOrderHost.Admit("match.7", "entity:1a", "act.first", null, Bound("entity:1a"));
        LawnOrderHost.Admit("match.7", "entity:1a", "act.second", "entity:9z", Bound("entity:1a"));

        Assert.Equal(1, LawnOrderHost.LiveCount);
        Assert.True(LawnOrderHost.TryPeek("entity:1a", out var order));
        Assert.Equal("act.second", order.ActionId);
        Assert.Equal("entity:9z", order.TargetKey);
    }

    /// <summary>The verb the Server relays is the name this host dispatches on. Both sides name it, and
    /// the injector does not reference the Server assembly, so the pair is pinned here by value.</summary>
    [Fact]
    public void The_command_name_is_the_one_the_server_relays()
    {
        Assert.Equal("lawn.order", LawnOrderHost.CommandName);
    }
}
