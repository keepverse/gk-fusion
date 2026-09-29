using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Injector;
using FusionRpg.Injector.Effects;
using Xunit;

namespace FusionRpg.Injector.Tests;

/// <summary>
/// `CheatState`'s entries are static, shared process-wide (both `MatchModifyTests`' own class doc and
/// this file's constructor rely on `CheatState.ResetAll()` giving a clean slate). xUnit parallelizes
/// different test CLASSES by default — different collections run concurrently — so without this,
/// `WaveControlTests` and `MatchModifyTests` race on the same static dictionary and one class's
/// `ResetAll()`/`SetToggle` can land mid-assertion in the other (found running this suite for the
/// first time: <c>The_match_end_clear...</c> failed only when both classes ran together, never solo).
/// Both classes carry this same attribute so xUnit serializes them relative to each other.
/// </summary>
[CollectionDefinition("CheatState statics")]
public class CheatStateStaticsCollection { }

/// <summary>
/// E36 (spec-wave-control.md). wave.control's own executor, `ExecWaveControl`
/// (InjectorEffectActionSink.cs) — the module's own stated "real substance" is the `ChainDepth`
/// recursion guard (§2.3), proven here the same way <c>MatchModifyTests</c> proves E35's scoped
/// restore: through the real <see cref="IEffectActionSink.Execute"/> entry point, not a reimplementation.
///
/// <para>Not run by CI (ci.yml never compiles FusionRpg.Injector — see this project's own csproj
/// comment); build/run locally with <c>$env:FUSIONRPG_GAME_DIR</c> set, the same requirement every
/// other injector build in this repo already carries.</para>
///
/// <para><b>Scope, following MatchModifyTests' own precedent:</b> <c>summon</c>/<c>huge</c>/<c>setTimer</c>
/// all call straight into <c>CheatActions.SummonWave</c>/<c>HugeWave</c>/<c>SetWaveTimer</c>, each of
/// which touches the IL2CPP <c>Board</c> type — merely referencing it throws outside a live IL2CPP
/// host process (confirmed empirically by <c>MatchModifyTests</c>' own class doc, for
/// <c>CheatActions.ApplyBoardConfig</c>). Those three ops have no CheatState-only "before the Board
/// call" logic to observe the way <c>ExecModifyMatch</c> does — the whole of what they do is the
/// Board call — so their happy paths are LIVE-only, exactly as spec-wave-control.md §4 says
/// ("ExecWaveControl is proven by a LIVE run"). What IS fully provable here with no live Board:
/// the <c>ChainDepth</c> guard (fires BEFORE the op switch even runs, for every op including
/// `summon`/`huge`/`setTimer`), the `hold` op end to end (touches only `CheatState`, never `Board`),
/// and the unrecognised-op defence-in-depth arm.</para>
/// </summary>
[Collection("CheatState statics")]
public class WaveControlTests
{
    public WaveControlTests()
    {
        CheatState.EmitProof = false; // see MatchModifyTests' own class doc for why this comes first
        CheatState.ResetAll();
    }

    static EffectActionPlanItem WaveControlItem(params (string Key, object? Value)[] pars)
    {
        var item = new EffectActionPlanItem { Action = EffectActions.WaveControl };
        foreach (var (k, v) in pars) item.Params[k] = v;
        return item;
    }

    static EffectExecuteContext Ctx(int chainDepth = 0) =>
        new() { Event = new EffectEventDto { ChainDepth = chainDepth } };

    // ---- §2.3: the ChainDepth recursion guard ------------------------------------------------------

    // Proven for `summon` specifically: the guard fires BEFORE the op switch runs at all, so this
    // never touches GameHooks.Board and is fully provable in this bare xunit harness — the guard is
    // what makes summon/huge/setTimer's own Board-touching happy paths safe to leave LIVE-only.
    [Fact]
    public void ChainDepth_above_zero_refuses_summon_before_ever_touching_the_board()
    {
        var sink = new InjectorEffectActionSink();
        var ok = sink.Execute(Ctx(chainDepth: 1), WaveControlItem(("op", "summon"), ("wave", 3)));
        Assert.False(ok);
    }

    [Fact]
    public void ChainDepth_zero_does_not_trip_the_guard()
    {
        // hold is fully provable end to end (CheatState only, no Board) — proves the guard does NOT
        // fire at depth 0, the contrast case to the refusal above.
        var sink = new InjectorEffectActionSink();
        var ok = sink.Execute(Ctx(chainDepth: 0), WaveControlItem(("op", "hold"), ("enabled", true)));
        Assert.True(ok);
        Assert.True(CheatState.On("F-WAVE-FREEZE"));
    }

    // PLANTED VIOLATION (§4): "remove the ChainDepth refusal -- an OnSpawn-triggered summon atom
    // fired against a spawn event with ChainDepth = 1 must fail [to stay refused]." Simulated the same
    // way MatchModifyTests simulates its own planted violations: by proving what the guard actually
    // prevents, right beside the guard itself, using the one op (`hold`) whose effect is directly
    // observable without a live Board -- `summon`/`huge` cause the real unbounded-spawn-loop failure
    // mode this guard exists for, but that loop can only be observed LIVE (§2.3's own point: "cannot
    // be diagnosed after the fact"). `hold` proves the SAME code path (the ChainDepth check, common to
    // every op) refuses before any op-specific work runs.
    [Fact]
    public void PLANTED_VIOLATION_a_chainDepth_one_event_must_not_be_allowed_to_reach_any_op()
    {
        var sink = new InjectorEffectActionSink();

        // The real, shipped, guarded path: refused, and nothing it would have done (setting
        // F-WAVE-FREEZE) happened.
        var ok = sink.Execute(Ctx(chainDepth: 1), WaveControlItem(("op", "hold"), ("enabled", true)));
        Assert.False(ok);
        Assert.False(CheatState.On("F-WAVE-FREEZE"));

        // The violation, spelled out rather than merely asserted-against: WITHOUT the ChainDepth check
        // at the top of ExecWaveControl, this exact same call would fall straight into the `hold` arm
        // and set the toggle -- demonstrated here by calling the identical op at depth 0, which DOES
        // reach it. A `summon`/`huge` atom bound to OnSpawn or OnWave, re-triggering on its own
        // spawns, is the unbounded version of this same gap.
        var okAtDepthZero = sink.Execute(Ctx(chainDepth: 0), WaveControlItem(("op", "hold"), ("enabled", true)));
        Assert.True(okAtDepthZero);
        Assert.True(CheatState.On("F-WAVE-FREEZE"));
    }

    // ---- §2.2/§2.5: the `hold` op -- the one op fully provable end to end here ---------------------

    [Fact]
    public void Hold_true_sets_F_WAVE_FREEZE()
    {
        var sink = new InjectorEffectActionSink();
        var ok = sink.Execute(Ctx(), WaveControlItem(("op", "hold"), ("enabled", true)));

        Assert.True(ok);
        Assert.True(CheatState.On("F-WAVE-FREEZE"));
    }

    [Fact]
    public void Hold_false_clears_F_WAVE_FREEZE()
    {
        var sink = new InjectorEffectActionSink();
        sink.Execute(Ctx(), WaveControlItem(("op", "hold"), ("enabled", true)));
        Assert.True(CheatState.On("F-WAVE-FREEZE"));

        var ok = sink.Execute(Ctx(), WaveControlItem(("op", "hold"), ("enabled", false)));

        Assert.True(ok);
        Assert.False(CheatState.On("F-WAVE-FREEZE"));
    }

    // §2.5: NotifyMatchEnd clears F-WAVE-FREEZE, the same way MatchModifyRestore clears match.modify's
    // own E-* writes -- proven here at the CheatState level (the same shape MatchModifyTests uses for
    // §2.6, since EffectRuntime itself threads through VfxDirector/InjectorCombatBridge and cannot run
    // in this harness).
    [Fact]
    public void The_match_end_clear_EffectRuntime_NotifyMatchEnd_performs_removes_a_bound_hold()
    {
        var sink = new InjectorEffectActionSink();
        sink.Execute(Ctx(), WaveControlItem(("op", "hold"), ("enabled", true)));
        Assert.True(CheatState.On("F-WAVE-FREEZE"));

        // The exact call EffectRuntime.NotifyMatchEnd makes beside MatchModifyRestore.Restore.
        CheatState.SetToggle("F-WAVE-FREEZE", false, "match-end");

        Assert.False(CheatState.On("F-WAVE-FREEZE"));
    }

    // ---- defence in depth: an unrecognised op at the executor itself --------------------------------

    // AtomKindRegistry.Validate's own op vocabulary already refuses this at bind time -- this proves
    // the executor arm refuses too, rather than silently doing nothing, if it is ever reached anyway.
    [Fact]
    public void An_unrecognised_op_refuses_rather_than_doing_nothing_silently()
    {
        var sink = new InjectorEffectActionSink();
        var ok = sink.Execute(Ctx(), WaveControlItem(("op", "freeze")));
        Assert.False(ok);
    }
}
