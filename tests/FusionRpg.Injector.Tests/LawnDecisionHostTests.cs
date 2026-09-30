using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Actions.Ai.Lawn;
using FusionRpg.Core.Combat;
using FusionRpg.Injector;
using FusionRpg.Injector.Effects;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Injector.Tests;

/// <summary>
/// combat-ai `lawn-cast-trigger` (module 19, CAI4.8, spec-lawn-cast-trigger.md §"The frame slot"): the
/// decision slot's own contract — the due set, the budget, the token, the swing feed, the switch's release
/// semantics and the actor-class resolution.
///
/// <para><b>What is tested here and what is not.</b> The three Core structures have 33 tests of their own
/// (`tests/FusionRpg.Core.Balance.Tests/CombatAi/`); this file tests the SLOT: that a due actor reaches
/// <see cref="LawnDecisionHost.Decide"/>, that a throwing decision is contained and the next frame still
/// ticks, that OFF releases everything, and that the death edge leaves a reused address clean. The
/// decision itself is a seam here for the same reason `LawnBattleView` takes seven: its inputs (module
/// 16's held sets, the ledger's held-action rows) are `CAI4.3`'s blocker, and a fake decision tests the
/// slot without pretending those exist.</para>
///
/// <para>Not run by CI (<c>ci.yml</c> never compiles FusionRpg.Injector); build/run locally with
/// <c>$env:FUSIONRPG_GAME_DIR</c> set.</para>
/// </summary>
[Collection("CheatState statics")]
public class LawnDecisionHostTests
{
    const string Actor = "entity:1a";

    /// <summary>One swing decides (`swingsPerDecision: 1`) and the timer never fires
    /// (`ticksPerDecision: 1000`), so the tests drive the due set by swings alone.</summary>
    /// <summary>Three swings decide and the timer never fires in a test's tick range
    /// (), so the due set is driven by swings alone and the outcome does not
    /// depend on the seeded offset a given matchSeed derives.</summary>
    static CombatAiLawnTuning Tuning() => new(
        SwingsPerDecision: 3, TicksPerDecision: 100000, PostCastLockTicks: 0, OffsetStream: "test.offset");

    /// <summary>The cadence: three first-of-swing, non-cast records make the actor due.</summary>
    static void SwingThreeTimes(long fromTick)
    {
        for (var i = 0; i < 3; i++)
            LawnDecisionHost.RecordSwing(Actor, isFirstOfSwing: true, castOrigin: false, nowTick: fromTick + i);
    }

    static BoardSnapshot Census(params string[] ptrs) =>
        new(ptrs.Select(p => new BoardEntitySnap { Ptr = p, Side = "plant", Living = true }));

    /// <summary>The view host's census seam, fed a synthetic census: the production read needs a live
    /// Unity runtime, and this test is about the SLOT, not about the board.</summary>
    static readonly Func<BoardSnapshot> PreviousCensus = LawnActorViewHost.CensusOf;

    public LawnDecisionHostTests()
    {
        CheatState.EmitProof = false;
        CheatState.ResetAll();
        LawnDecisionHost.Decide = null;
        LawnDecisionHost.Clear();
        LawnDecisionHost.ResetForTest();
        LawnActorViewHost.Clear();
        LawnActorViewHost.CensusOf = () => Census(Actor);
        // The feature is default-OFF; the tests that exercise the slot turn it on through the same
        // explicit toggle the debug surface uses.
        CheatState.SetToggle(LawnCombatAiFeature.CheatToggleId, true, source: "test", emitInject: false);
    }

    [Fact]
    public void A_due_actor_reaches_the_decision_seam_with_a_view_for_its_own_side()
    {
        LawnDecisionHost.BeginMatch(Tuning(), matchSeed: 42);
        var seen = new List<string>();
        LawnBattleView? seenView = null;
        LawnDecisionHost.Decide = (actorKey, view) => { seen.Add(actorKey); seenView = view; return true; };

        SwingThreeTimes(fromTick: 0);
        LawnDecisionHost.Tick(nowTick: 10, frame: 1, Census(Actor));

        Assert.Equal(new[] { Actor }, seen);
        // CAI4.1's line: the view host now has a PRODUCTION caller, and the view handed over is scoped to
        // the actor's own side (its `SideOf` answers MySideCode for that actor).
        Assert.NotNull(seenView);
        Assert.Equal(0, seenView!.SideOf(Actor));
        Assert.Equal(1, LawnDecisionHost.DecisionCount);
        Assert.Equal(1, LawnDecisionHost.CastCount);
        Assert.Equal(0, LawnDecisionHost.TokensInUse);   // released in the finally, always
    }

    /// <summary>The spec's own rule: *"a throwing policy is caught at the tick boundary and the next frame
    /// still ticks"*.</summary>
    [Fact]
    public void A_throwing_decision_is_contained_and_the_next_frame_still_ticks()
    {
        LawnDecisionHost.BeginMatch(Tuning(), matchSeed: 42);
        var calls = 0;
        LawnDecisionHost.Decide = (_, _) =>
        {
            calls++;
            if (calls == 1) throw new InvalidOperationException("policy blew up");
            return false;
        };

        SwingThreeTimes(fromTick: 0);
        LawnDecisionHost.Tick(nowTick: 10, frame: 1, Census(Actor));   // throws inside, contained
        Assert.Equal(1, LawnDecisionHost.FailureCount);
        Assert.Equal(0, LawnDecisionHost.TokensInUse);          // the token came back

        SwingThreeTimes(fromTick: 20);
        LawnDecisionHost.Tick(nowTick: 30, frame: 2, Census(Actor));   // the NEXT frame still ticks

        Assert.Equal(2, calls);
        Assert.Equal(1, LawnDecisionHost.FailureCount);         // and did not throw again
    }

    /// <summary>OFF IS A RELEASE, not a pause — the acceptance's own words for the switch.</summary>
    [Fact]
    public void Turning_the_switch_off_releases_every_token_and_drops_every_counter()
    {
        LawnDecisionHost.BeginMatch(Tuning(), matchSeed: 42);
        LawnDecisionHost.Decide = (_, _) => false;             // refused: the state stays, the token returns
        SwingThreeTimes(fromTick: 0);
        LawnDecisionHost.Tick(nowTick: 10, frame: 1, Census(Actor));
        Assert.True(LawnDecisionHost.TrackedCount > 0);

        CheatState.SetToggle(LawnCombatAiFeature.CheatToggleId, false, source: "test", emitInject: false);
        LawnDecisionHost.Tick(nowTick: 20, frame: 2, Census(Actor));

        Assert.Equal(0, LawnDecisionHost.TrackedCount);
        Assert.Equal(0, LawnDecisionHost.TokensInUse);
        Assert.False(LawnDecisionHost.TryTriggerState(Actor, out _));
    }

    /// <summary>The death edge, and the reused-ptr answer: a reused address starts at zero swings and no
    /// lock, because <c>Remove</c> drops the trigger state, the queued budget entry and the token.</summary>
    [Fact]
    public void Death_drops_the_state_and_a_reused_ptr_starts_clean()
    {
        LawnDecisionHost.BeginMatch(Tuning(), matchSeed: 42);
        LawnDecisionHost.Decide = (_, _) => false;
        SwingThreeTimes(fromTick: 0);

        LawnDecisionHost.Remove(Actor);

        Assert.False(LawnDecisionHost.TryTriggerState(Actor, out _));

        // A fresh swing on the same address re-registers it and starts from its OWN seeded offset --
        // never from the dead actor accumulated count (which was 3) and never with a lock.
        LawnDecisionHost.RecordSwing(Actor, isFirstOfSwing: true, castOrigin: false, nowTick: 5);
        Assert.True(LawnDecisionHost.TryTriggerState(Actor, out var fresh));
        Assert.True(fresh.Swings <= 3, "a reused address must not inherit the dead actor accumulated swings");
        Assert.Equal(-1, fresh.LockUntilTick);   // -1: registered, never locked
    }

    /// <summary>The inspector's `Trigger` field is populated from here (CAI4.8's owed line for CAI2.4).</summary>
    [Fact]
    public void The_trigger_state_is_readable_for_the_inspector()
    {
        LawnDecisionHost.BeginMatch(Tuning(), matchSeed: 42);
        LawnDecisionHost.Decide = (_, _) => false;

        SwingThreeTimes(fromTick: 0);
        LawnDecisionHost.Tick(nowTick: 10, frame: 1, Census(Actor));

        Assert.True(LawnDecisionHost.TryTriggerState(Actor, out var trigger));
        Assert.True(trigger.Swings >= 0);
        Assert.False(LawnDecisionHost.TryTriggerState("entity:nobody", out _));   // never a synthesised state
    }

    /// <summary>An actor class comes from the deploy path: a live `UniqueBinding` is a unique, everything
    /// else is general. In a test process the lookup cannot resolve (it reaches the game assembly), so the
    /// host answers GENERAL — the cheap tier, never the smart one by accident.</summary>
    [Fact]
    public void An_unresolvable_actor_is_general_never_unique_by_accident()
    {
        Assert.Equal(AiActorClass.General, LawnDecisionHost.ClassOf("entity:1a"));
    }

    [Fact]
    public void A_tick_with_no_decision_seam_still_computes_the_due_set_and_casts_nothing()
    {
        LawnDecisionHost.BeginMatch(Tuning(), matchSeed: 42);
        LawnDecisionHost.Decide = null;

        SwingThreeTimes(fromTick: 0);
        LawnDecisionHost.Tick(nowTick: 10, frame: 1, Census(Actor));

        Assert.Equal(0, LawnDecisionHost.DecisionCount);
        Assert.Equal(0, LawnDecisionHost.CastCount);
        Assert.Equal(0, LawnDecisionHost.TokensInUse);
    }

    /// <summary>The board edge `MatchHost` uses: the tuning was configured once at host start, so
    /// `board.start` passes only the seed. This is the overload whose absence let the slot stay inert in
    /// production -- `BeginMatch(tuning, seed)` had no caller outside tests.</summary>
    [Fact]
    public void The_board_edge_builds_the_slot_from_the_configured_tuning()
    {
        LawnDecisionHost.Configure(Tuning());
        LawnDecisionHost.BeginMatch(matchSeed: 42);
        LawnDecisionHost.Decide = (_, _) => true;

        SwingThreeTimes(fromTick: 0);
        LawnDecisionHost.Tick(nowTick: 10, frame: 1, Census(Actor));

        Assert.Equal(1, LawnDecisionHost.DecisionCount);
        Assert.Equal(1, LawnDecisionHost.CastCount);
    }

    /// <summary>An unconfigured host reports once and builds nothing -- it never throws into the
    /// board.start block, which is on the frame path.</summary>
    [Fact]
    public void The_board_edge_without_a_configured_tuning_builds_nothing_and_never_throws()
    {
        LawnDecisionHost.BeginMatch(matchSeed: 42);   // no Configure() in this case
        LawnDecisionHost.Decide = (_, _) => true;

        SwingThreeTimes(fromTick: 0);
        LawnDecisionHost.Tick(nowTick: 10, frame: 1, Census(Actor));

        Assert.Equal(0, LawnDecisionHost.DecisionCount);
        Assert.Equal(0, LawnDecisionHost.TrackedCount);
        Assert.Equal(0, LawnDecisionHost.TokensInUse);
    }

    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        return KeepverseRoots.Core();
    }

    /// <summary>Every CODE line of <paramref name="relativePath"/>, comments stripped — a file's own
    /// comment names these members on purpose, and the criterion is about code. Line numbers are
    /// deliberately not pinned: an absolute line goes stale on the next edit, which is the drift this
    /// repo's citation discipline exists to avoid.</summary>
    static List<string> CodeLines(string relativePath)
    {
        var file = System.IO.Path.Combine(RepoRoot(), relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Assert.True(System.IO.File.Exists(file), $"source not found: {file}");
        return System.IO.File.ReadAllLines(file).Select(line => line.Split("//")[0]).ToList();
    }

    /// <summary>
    /// The START EDGE this lane wired (`CAI4.8`), pinned so removing it cannot pass silently. `MatchHost`
    /// needs a live game, so no test can enter its `board.start` block — the situation `StanceSeamTests`
    /// solves by scanning the source, and the reason a grep in a report is not enough: a report cannot fail.
    /// The argument is asserted over the whole joined body rather than one line, because the call and its
    /// seed legitimately sit on two.
    /// </summary>
    [Fact]
    public void The_board_start_edge_is_wired_in_MatchHost()
    {
        var code = CodeLines("src/FusionRpg.Injector/Match/MatchHost.cs");

        Assert.Single(code.Where(line => line.Contains("LawnDecisionHost.BeginMatch(", StringComparison.Ordinal)));
        Assert.Contains("MatchSeed.For(", string.Join("\n", code), StringComparison.Ordinal);
    }

    /// <summary>The tuning half of the same edge: `RpgHost.Initialize` parses the lawn section from the
    /// combat-ai document it already read, so the board.start call has something to build the slot from.</summary>
    [Fact]
    public void The_tuning_is_configured_in_RpgHost()
    {
        var code = CodeLines("src/FusionRpg.Injector/Host/RpgHost.cs");

        Assert.Single(code.Where(line => line.Contains("LawnDecisionHost.Configure(", StringComparison.Ordinal)));
        Assert.Contains("CombatAiLawnTuning.Parse(", string.Join("\n", code), StringComparison.Ordinal);
    }
}
