using System.Linq;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Power;
using FusionRpg.Injector;
using FusionRpg.Injector.Effects;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Injector.Tests;

/// <summary>
/// action-enrich AE2.3 (spec-lawn-action-base.md §Refresh triggers): every edge that moves the owner's Θ
/// must re-bake the lawn grant, and the snapshot edge — which arrives on the socket's thread — must only
/// RECORD. The record and the drain are therefore tested apart: <see cref="LawnBasicAttackGrantBinder.MarkThetaDirty"/>
/// must make no grant call, and the next main-thread <c>Tick</c> must re-run <c>Bind</c> for every live
/// grant the binder owns.
///
/// <para><b>Why the drain is observed through a counter and not the grant bag.</b> <c>Bind</c> resolves
/// the owner's element through <c>LawnElementResolverHost</c>, which needs a real board; headless, it
/// early-returns before touching the bag, so the bag cannot show that a rebind was attempted.
/// <c>RebindAttemptsForTest</c> is the <c>HeldActionIdsForTest</c>-style seam that makes the attempt
/// observable. The two assertions that matter are kept separate on purpose: attempts prove the drain ran
/// per live grant, the grant count proves the deterministic GrantId kept it an upsert.</para>
///
/// <para>The four triggers (session start, SignalR reconnect, <c>power.index.reload</c>, an identity
/// change) all funnel through <c>CheatState.ApplyPowerSnapshot</c> → <c>MarkThetaDirty</c>; the last test
/// asserts that wiring, while the identity-change case is driven behaviourally here.</para>
/// </summary>
// The existing "CheatState statics" collection (see LawnBasicAttackFeatureFlagTests/MatchModifyTests):
// these tests mutate the same process-wide statics — CheatState's registry and its LAWN-BASIC-ATTACK
// toggle, the shared EffectRuntime.Bag, and the binder's own pending/dirty state — so xUnit must not run
// this class in parallel with those. This is the isolation that replaces the assembly-wide
// `DisableTestParallelization` tried first: it is scoped to the classes that actually share the state.
[Collection("CheatState statics")]
public class LawnBasicAttackGrantBinderRefreshTests
{
    const string PtrA = "1A2B";
    const string PtrB = "3C4D";

    static LawnBasicAttackGrantBinderRefreshTests()
    {
        // CheatState.ApplyPowerSnapshot hydrates through CheatState.PowerIndex, whose construction reads
        // PowerTuningHub.Tuning — the host configures it at startup and this assembly never runs that.
        // v2 is the version production pins (spec-green-baseline.md).
        PowerTuningHub.Configure(PowerTuningLoader.Parse(ReadTuning("power-scale.v2.json")));
        // Bind now resolves its magnitude BEFORE the board scan (the amount depends on Θ, not on the
        // board), so the two tuning hubs RpgHost configures at startup must be configured here too —
        // without them every bind throws and the amounts this file asserts on stay 0.
        FusionRpg.Core.Actions.ActionBaseTuningHub.Configure(
            FusionRpg.Core.Actions.ActionBaseTuningLoader.Parse(ReadTuning("action-base.v2.json")));
        FusionRpg.Core.Actions.Rungs.RungPolicy.Configure(
            FusionRpg.Core.Actions.Rungs.RungTableLoader.Parse(ReadTuning("action-rungs.v1.json")));
    }

    static string ReadTuning(string file) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", file));

    public LawnBasicAttackGrantBinderRefreshTests()
    {
        // MatchModifyTests' own ordering note: EmitProof first, so nothing below can emit.
        CheatState.EmitProof = false;
        // Deliberately NO `CheatState.ResetAll()` and no `SetToggle` here. This assembly's classes share
        // CheatState and run in parallel collections: a full reset clobbered MatchModifyTests mid-test
        // (it passed alone, failed beside this class), and even a single toggle writes the whole cheat
        // registry through `MaybeSave`, which raced it too. The module is ON by its own default
        // (`LawnBasicAttackFeature.DefaultEnabled`), so the narrowest write is none — only the
        // off-switch test moves the toggle, and it restores it in its own `finally`.
        WithdrawFake(PtrA);
        WithdrawFake(PtrB);
        LawnBasicAttackGrantBinder.ClearPending();
        LawnBasicAttackGrantBinder.ResetRebindAttemptsForTest();
    }

    static ActorLadderSnapshot Snapshot(int daveLevel) => new(daveLevel, RealmsAdvanced: 0, PvzRuns: 0);

    static int BasicAttackGrantCount() => EffectRuntime.Bag.Grants.All()
        .Count(g => FusionRpg.Core.Combat.BasicAttackGrantBuilder.IsBasicAttackGrantId(g.GrantId));

    /// <summary>A grant shaped exactly like the binder's own, without needing a board to bind it.</summary>
    static void GrantFake(string ptr) =>
        EffectRuntime.GrantQuiet(FusionRpg.Core.Combat.BasicAttackGrantBuilder.Build(ptr, primary: null, amount: -100));

    static void WithdrawFake(string ptr) =>
        EffectRuntime.Withdraw(FusionRpg.Core.Combat.BasicAttackGrantBuilder.GrantIdFor(ptr));

    /// <summary>The record half: a Θ-moving snapshot must not touch the bag or attempt a bind. Making
    /// the call several times is the point — the edge is O(1) and idempotent on its own.</summary>
    [Fact]
    public void The_snapshot_edge_records_only_and_never_rebinds()
    {
        GrantFake(PtrA);
        var before = BasicAttackGrantCount();

        CheatState.ApplyPowerSnapshot(1, Snapshot(20));
        CheatState.ApplyPowerSnapshot(1, Snapshot(25));

        Assert.Equal(0, LawnBasicAttackGrantBinder.RebindAttemptsForTest);
        Assert.Equal(before, BasicAttackGrantCount());
    }

    [Fact]
    public void The_next_drain_rebinds_every_live_grant_this_binder_owns()
    {
        GrantFake(PtrA);
        GrantFake(PtrB);

        CheatState.ApplyPowerSnapshot(1, Snapshot(20));
        LawnBasicAttackGrantBinder.Tick();

        Assert.Equal(2, LawnBasicAttackGrantBinder.RebindAttemptsForTest);
    }

    /// <summary>A Tick with neither a dirty Θ nor a queued spawn is a no-op — no speculative rebinds.</summary>
    [Fact]
    public void A_tick_with_nothing_dirty_rebinds_nothing()
    {
        GrantFake(PtrA);

        LawnBasicAttackGrantBinder.Tick();

        Assert.Equal(0, LawnBasicAttackGrantBinder.RebindAttemptsForTest);
    }

    /// <summary>Spec test: two dirty drains in a row leave exactly one grant per ptr. The second is an
    /// identity change (a new player id and a new Θ), which is the trigger row this doubles as.</summary>
    [Fact]
    public void Two_dirty_drains_leave_one_grant_per_ptr()
    {
        GrantFake(PtrA);

        CheatState.ApplyPowerSnapshot(1, Snapshot(20));
        LawnBasicAttackGrantBinder.Tick();
        CheatState.ApplyPowerSnapshot(2, Snapshot(30));
        LawnBasicAttackGrantBinder.Tick();

        Assert.Equal(2, LawnBasicAttackGrantBinder.RebindAttemptsForTest);
        Assert.Equal(1, BasicAttackGrantCount());
    }

    /// <summary>The trigger table's off-switch row: with the module off, nothing is rebindable. Turning
    /// it off takes the L-N8 withdraw edge on the next Tick, and a Θ that moves while it is off drains
    /// to nothing.</summary>
    [Fact]
    public void The_off_switch_leaves_nothing_to_rebind()
    {
        GrantFake(PtrA);

        try
        {
            // The off EDGE is a transition, so it needs an observed "on" first — otherwise this row
            // would depend on whichever test happened to run before it.
            CheatState.SetToggle(LawnBasicAttackFeature.CheatToggleId, true);
            LawnBasicAttackGrantBinder.Tick();

            CheatState.SetToggle(LawnBasicAttackFeature.CheatToggleId, false);
            LawnBasicAttackGrantBinder.Tick();          // the L-N8 off edge withdraws everything bound
            Assert.False(LawnBasicAttackFeature.Enabled);
            Assert.Equal(0, BasicAttackGrantCount());

            LawnBasicAttackGrantBinder.ResetRebindAttemptsForTest();
            CheatState.ApplyPowerSnapshot(1, Snapshot(40));   // records while off
            LawnBasicAttackGrantBinder.Tick();

            Assert.Equal(0, LawnBasicAttackGrantBinder.RebindAttemptsForTest);
            Assert.Equal(0, BasicAttackGrantCount());
        }
        finally
        {
            CheatState.SetToggle(LawnBasicAttackFeature.CheatToggleId, true);
        }
    }

    /// <summary>The trigger table's FIRST row: a spawn binds. Named here because the table starts with
    /// it — the bake happens on the spawn path and not as a drain rebind.</summary>
    [Fact]
    public void A_spawn_binds_and_bakes_the_amount()
    {
        CheatState.ApplyPowerSnapshot(1, Snapshot(20));
        LawnBasicAttackGrantBinder.ResetRebindAttemptsForTest();

        LawnBasicAttackGrantBinder.QueueSpawn(PtrA);
        LawnBasicAttackGrantBinder.Tick();

        Assert.NotEqual(0, LawnBasicAttackGrantBinder.LastBakedAmountForTest);
        Assert.Equal(0, LawnBasicAttackGrantBinder.RebindAttemptsForTest);
    }

    /// <summary>Spec test, ordering 1 of 3: bind, THEN a snapshot. The drain must re-bake to the new Θ —
    /// a binder that cached the amount at bind time would keep the low one.</summary>
    [Fact]
    public void The_bind_then_snapshot_ordering_ends_on_the_new_amount()
    {
        var expected = PeakAmount();

        try
        {
            CheatState.ApplyPowerSnapshot(1, Snapshot(10));       // low
            LawnBasicAttackGrantBinder.QueueSpawn(PtrA);
            LawnBasicAttackGrantBinder.Tick();                    // binds at the LOW Θ
            Assert.NotEqual(expected, LawnBasicAttackGrantBinder.LastBakedAmountForTest);

            GrantFake(PtrA);                                      // the live grant the drain rebinds
            CheatState.ApplyPowerSnapshot(1, Snapshot(30));       // the snapshot arrives AFTER the bind
            LawnBasicAttackGrantBinder.Tick();

            Assert.Equal(expected, LawnBasicAttackGrantBinder.LastBakedAmountForTest);
        }
        finally
        {
            LawnBasicAttackGrantBinder.ClearPending();
        }
    }

    /// <summary>Spec test, ordering 2 of 3: a snapshot, THEN a bind.</summary>
    [Fact]
    public void The_snapshot_then_bind_ordering_ends_on_the_same_amount()
    {
        var expected = PeakAmount();

        try
        {
            CheatState.ApplyPowerSnapshot(1, Snapshot(30));       // the snapshot arrives FIRST
            LawnBasicAttackGrantBinder.QueueSpawn(PtrA);
            LawnBasicAttackGrantBinder.Tick();

            Assert.Equal(expected, LawnBasicAttackGrantBinder.LastBakedAmountForTest);
        }
        finally
        {
            LawnBasicAttackGrantBinder.ClearPending();
        }
    }

    /// <summary>Spec test, ordering 3 of 3: a spawn is queued, the snapshot arrives BETWEEN the queue
    /// and the drain.</summary>
    [Fact]
    public void The_snapshot_between_queue_and_drain_ordering_ends_on_the_same_amount()
    {
        var expected = PeakAmount();

        try
        {
            LawnBasicAttackGrantBinder.QueueSpawn(PtrA);          // queued ...
            CheatState.ApplyPowerSnapshot(1, Snapshot(30));       // ... snapshot arrives here ...
            LawnBasicAttackGrantBinder.Tick();                    // ... and only now is it drained

            Assert.Equal(expected, LawnBasicAttackGrantBinder.LastBakedAmountForTest);
        }
        finally
        {
            LawnBasicAttackGrantBinder.ClearPending();
        }
    }

    /// <summary>The amount the OTHER two orderings are compared against, produced by a real bind at the
    /// peak Θ rather than by re-deriving the formula here.</summary>
    static long PeakAmount()
    {
        CheatState.ApplyPowerSnapshot(1, Snapshot(30));
        LawnBasicAttackGrantBinder.QueueSpawn(PtrB);
        LawnBasicAttackGrantBinder.Tick();
        LawnBasicAttackGrantBinder.ClearPending();
        var peak = LawnBasicAttackGrantBinder.LastBakedAmountForTest;
        Assert.NotEqual(0, peak);
        return peak;
    }

    /// <summary>The trigger table's wiring rows: session start, SignalR reconnect and
    /// <c>power.index.reload</c> all reach <c>RefreshPowerIndexAsync</c>, which is the one caller of
    /// <c>CheatState.ApplyPowerSnapshot</c> — the funnel the record edge hangs from. Source scan,
    /// matching how this assembly already proves cadence wiring it cannot drive headlessly.</summary>
    [Fact]
    public void Every_Theta_moving_trigger_reaches_ApplyPowerSnapshot()
    {
        var client = ReadInjectorFile("RpgClient.cs");
        Assert.True(CountOf(client, "RefreshPowerIndexAsync()") >= 2,
            "session start AND the SignalR reconnect handler must both refresh the power index");
        Assert.Contains("CheatState.ApplyPowerSnapshot(", client);

        var runner = ReadInjectorFile("CheatCommandRunner.cs");
        var reload = runner.IndexOf("\"power.index.reload\"", StringComparison.Ordinal);
        Assert.True(reload >= 0, "the reload command must exist");
        Assert.Contains("RefreshPowerIndexAsync()", runner[reload..Math.Min(runner.Length, reload + 400)]);

        var cheatState = ReadInjectorFile("CheatState.cs");
        var apply = cheatState.IndexOf("public static void ApplyPowerSnapshot(", StringComparison.Ordinal);
        Assert.True(apply >= 0, "ApplyPowerSnapshot must exist");
        Assert.Contains("MarkThetaDirty()", cheatState[apply..Math.Min(cheatState.Length, apply + 900)]);
    }

    static int CountOf(string haystack, string needle)
    {
        var count = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    static string ReadInjectorFile(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot(), "src", "FusionRpg.Injector" }.Concat(relative).ToArray()));

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
