using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Injector;
using FusionRpg.Injector.Effects;
using Xunit;

namespace FusionRpg.Injector.Tests;

/// <summary>
/// combat-ai `decision-inspector` module 10, CAI2.5 (spec-decision-inspector.md §4–§5): the injector
/// half's own contracts — the three-layer switch, the sink's gate, and the two membership edges the
/// ring's live index is withdrawn on.
///
/// <para><b>Why the switch rule is tested as a function.</b> The env half of
/// <see cref="AiInspectFeature"/> is read once at process start (deliberately — it sits on the decision
/// path), so "the env var wins over an explicit toggle" cannot be reproduced by setting an environment
/// variable inside a test process. <see cref="AiInspectFeature.Resolve"/> is the whole rule as a pure
/// function of its three inputs, so the ORDER is pinned rather than argued from a field initialiser.
/// The ambient half (<see cref="AiInspectFeature.Enabled"/> with no env var set) is then asserted
/// against the real <c>CheatState</c>.</para>
///
/// <para>Shares <c>CheatState</c>'s static entries with every other test in the "CheatState statics"
/// collection, and the ring is a process-wide static, so every test clears both first. Not run by CI
/// (<c>ci.yml</c> never compiles FusionRpg.Injector); build/run locally with <c>$env:FUSIONRPG_GAME_DIR</c>
/// set, the same requirement every other injector test project in this repo carries.</para>
/// </summary>
[Collection("CheatState statics")]
public class AiInspectFeatureFlagTests
{
    public AiInspectFeatureFlagTests()
    {
        CheatState.EmitProof = false; // see MatchModifyTests' own class doc for why this must be first
        CheatState.ResetAll();
        LawnAiDecisionObservability.Clear();
    }

    static AiDecisionRecord Decision(string actorKey, long tick = 1) => new(
        NowTick: tick, Round: 1, ActorKey: actorKey,
        Tier: null, ProfileId: null, Personality: null,
        Trigger: AiTriggerState.None, Origin: AiDecisionOrigin.Policy,
        Candidates: Array.Empty<AiCandidateVerdict>(),
        ChosenActionId: null, ChosenTargetKey: null, ChosenSaturatedBy: 0,
        TopThree: Array.Empty<(string ActorKey, ScoreBreakdown Breakdown)>());

    // ---- the switch: D4 "hidden by default" ----

    /// <summary>The simplest proof in the whole switch: the module's own default is a literal constant.
    /// Zero dependency on CheatState/CheatSchema/CheatRegistry, so no rehydrate bug, reset or stale
    /// session document can move it.</summary>
    [Fact]
    public void DefaultEnabled_constant_is_false_by_owner_decision()
    {
        Assert.False(AiInspectFeature.DefaultEnabled);
    }

    /// <summary>Reproduces the fresh-boot shape: nobody has ever explicitly toggled the id, so the
    /// inspector is off — and off is also its zero-cost state.</summary>
    [Fact]
    public void Enabled_defaults_off_with_no_explicit_toggle_ever_set()
    {
        Assert.False(CheatState.IsUserSet(AiInspectFeature.CheatToggleId));
        Assert.False(AiInspectFeature.Enabled);
    }

    /// <summary>The historical failure shape, reproduced directly: a backing <c>Enabled=true</c> with
    /// <c>IsSet=false</c> must never leak into the default once nothing has explicitly set the id.
    /// Before the <c>IsUserSet</c> gate this was indistinguishable from "really on".</summary>
    [Fact]
    public void Enabled_ignores_a_stale_true_backing_field_when_never_explicitly_set()
    {
        CheatState.Get(AiInspectFeature.CheatToggleId).Enabled = true;
        Assert.False(CheatState.IsUserSet(AiInspectFeature.CheatToggleId));

        Assert.False(AiInspectFeature.Enabled);
    }

    /// <summary>The debug/QA override surface is real: an explicit toggle is the only way the switch
    /// can read true with the env var unset.</summary>
    [Fact]
    public void Explicit_debug_override_on_reads_true()
    {
        CheatState.SetToggle(AiInspectFeature.CheatToggleId, true, source: "test", emitInject: false);

        Assert.True(CheatState.IsUserSet(AiInspectFeature.CheatToggleId));
        Assert.True(AiInspectFeature.Enabled);
    }

    // ---- the env half, pinned as the pure rule ----

    [Fact]
    public void Env_forced_off_wins_over_an_explicit_on_toggle()
    {
        Assert.False(AiInspectFeature.Resolve("0", debugOverride: true, defaultEnabled: true));
    }

    [Fact]
    public void Env_forced_on_wins_over_an_explicit_off_toggle_and_needs_no_toggle_at_all()
    {
        Assert.True(AiInspectFeature.Resolve("1", debugOverride: false, defaultEnabled: false));
        Assert.True(AiInspectFeature.Resolve("1", debugOverride: null, defaultEnabled: false));
    }

    /// <summary>An unrecognised env value is not a parse: it defers, in order, to the explicit toggle
    /// and then to the module's own default — and an empty string is unrecognised, not "off".</summary>
    [Fact]
    public void An_unrecognised_env_value_defers_to_the_toggle_then_to_the_module_default()
    {
        Assert.True(AiInspectFeature.Resolve("yes", debugOverride: true, defaultEnabled: false));
        Assert.False(AiInspectFeature.Resolve("yes", debugOverride: false, defaultEnabled: true));
        Assert.False(AiInspectFeature.Resolve("", debugOverride: null, defaultEnabled: false));
        Assert.True(AiInspectFeature.Resolve(null, debugOverride: null, defaultEnabled: true));
        // Not a parse: "00" is an unrecognised value, so it defers to the explicit toggle rather
        // than being read as "0".
        Assert.True(AiInspectFeature.Resolve("00", debugOverride: true, defaultEnabled: false));
    }

    // ---- the sink's gate (spec §4's trigger table) ----

    [Fact]
    public void Record_is_a_no_op_while_the_switch_is_off()
    {
        Assert.False(AiInspectFeature.Enabled);

        LawnAiDecisionObservability.Sink.Record(Decision("entity:aa"));

        Assert.Empty(LawnAiDecisionObservability.Recent());
        Assert.Null(LawnAiDecisionObservability.LastFor("entity:aa"));
    }

    [Fact]
    public void Record_stores_the_decision_when_the_switch_is_on()
    {
        CheatState.SetToggle(AiInspectFeature.CheatToggleId, true, source: "test", emitInject: false);

        LawnAiDecisionObservability.Sink.Record(Decision("entity:aa", tick: 7));

        var stored = Assert.Single(LawnAiDecisionObservability.Recent());
        Assert.Equal(7, stored.NowTick);
        Assert.Equal("entity:aa", stored.ActorKey);
        Assert.Equal(7, LawnAiDecisionObservability.LastFor("entity:aa")!.Value.NowTick);
    }

    /// <summary>Spec §4: turning the switch off mid-match stops NEW records; existing entries stay
    /// readable until cleared.</summary>
    [Fact]
    public void Turning_the_switch_off_mid_match_stops_new_records_but_keeps_existing_ones_readable()
    {
        CheatState.SetToggle(AiInspectFeature.CheatToggleId, true, source: "test", emitInject: false);
        LawnAiDecisionObservability.Sink.Record(Decision("entity:aa", tick: 1));

        CheatState.SetToggle(AiInspectFeature.CheatToggleId, false, source: "test", emitInject: false);
        LawnAiDecisionObservability.Sink.Record(Decision("entity:bb", tick: 2));

        var stored = Assert.Single(LawnAiDecisionObservability.Recent());
        Assert.Equal("entity:aa", stored.ActorKey);
        Assert.Null(LawnAiDecisionObservability.LastFor("entity:bb"));
    }

    [Fact]
    public void An_unknown_actor_returns_nothing_never_a_synthesised_record()
    {
        CheatState.SetToggle(AiInspectFeature.CheatToggleId, true, source: "test", emitInject: false);
        LawnAiDecisionObservability.Sink.Record(Decision("entity:aa"));

        Assert.Null(LawnAiDecisionObservability.LastFor("entity:zz"));
    }

    // ---- the membership edges (InjectorEntityRegistry.Remove / Clear) ----

    /// <summary>
    /// The death edge, and the additive property in one test: <c>Remove</c> withdraws the ring's live
    /// index entry for that ptr (so a reused ptr cannot inherit a stranger's decision) while the
    /// record already IN the ring keeps its own actor key — history is not falsified by a later reuse.
    /// The pre-existing per-actor drop (the resource pools) still happens in the same call, which is
    /// what "additive" means here: the new drop was appended, nothing already there was removed.
    /// </summary>
    [Fact]
    public void Remove_withdraws_the_actors_index_entry_still_drops_its_pools_and_leaves_ring_history()
    {
        CheatState.SetToggle(AiInspectFeature.CheatToggleId, true, source: "test", emitInject: false);
        var ptr = new IntPtr(0x7A11);
        var key = LawnAiDecisionObservability.ActorKeyOf(ptr);
        LawnAiDecisionObservability.Sink.Record(Decision(key));

        // A pre-existing drop this class already owned, so the assertion below is about ADDITIVITY
        // rather than about the new line alone.
        InjectorEntityRegistry.ResourcePools.GetOrCreate(
            ptr.ToString("X"), ActorDerivedSnapshot.StubNeutral(), atTick: 0);
        Assert.True(InjectorEntityRegistry.ResourcePools.TryGet(ptr.ToString("X"), out _));

        InjectorEntityRegistry.Remove(ptr);

        Assert.Null(LawnAiDecisionObservability.LastFor(key));                        // new: the index
        Assert.False(InjectorEntityRegistry.ResourcePools.TryGet(ptr.ToString("X"), out _)); // existing drop intact
        Assert.Single(LawnAiDecisionObservability.Recent());                          // history untouched
    }

    [Fact]
    public void Clear_empties_the_ring_and_its_index()
    {
        CheatState.SetToggle(AiInspectFeature.CheatToggleId, true, source: "test", emitInject: false);
        LawnAiDecisionObservability.Sink.Record(Decision("entity:aa"));

        InjectorEntityRegistry.Clear();

        Assert.Empty(LawnAiDecisionObservability.Recent());
        Assert.Null(LawnAiDecisionObservability.LastFor("entity:aa"));
    }

    /// <summary>The one key derivation the lawn host and the registry both use, so the index entry can
    /// never be stranded under a key nobody removes. Two spellings of one ptr are one actor.</summary>
    [Fact]
    public void ActorKeyOf_normalizes_hex_case_and_an_optional_0x_prefix()
    {
        Assert.Equal("entity:1a", LawnAiDecisionObservability.ActorKeyOf(new IntPtr(0x1A)));
        Assert.Equal("entity:1a", LawnAiDecisionObservability.ActorKeyOf("0x1A"));
        Assert.Equal("entity:1a", LawnAiDecisionObservability.ActorKeyOf("1a"));
        Assert.Equal("", LawnAiDecisionObservability.ActorKeyOf(IntPtr.Zero));
        Assert.Equal("", LawnAiDecisionObservability.ActorKeyOf((string?)null));
    }
}
