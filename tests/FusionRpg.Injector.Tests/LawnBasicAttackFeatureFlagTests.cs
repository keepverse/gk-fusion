using FusionRpg.Injector;
using FusionRpg.Injector.Effects;
using Xunit;

namespace FusionRpg.Injector.Tests;

/// <summary>
/// lawn-combat-wire T10/T12 live-inert investigation (2026-09-14): a real live combat run found
/// <c>LawnBasicAttackFeature.Enabled</c> reading <c>false</c> despite every other signal (env var
/// unset, <c>CheatSchemaTests.LawnBasicAttackFeature_kill_switch_defaults_on_before_any_explicit_set</c>
/// proving the schema fallback itself is correct) saying it should default on.
///
/// <para><b>The actual defect was architectural, not a bug to patch in <c>CheatState</c>'s rehydrate
/// path.</b> <c>CheatState</c>'s own class doc names its real scope: "Session cheat registry keyed by
/// coverage ids from cheat-menu-coverage.md" — an ephemeral debug/QA toggle store. It never promised a
/// durable, always-correct default for a real production gameplay switch, so borrowing
/// <c>CheatSchema.EffectiveToggle</c>'s fallback as this flag's ONLY source of "on by default" (T10's
/// original shape, mirroring the pre-existing <c>OVERLAY-COMBAT</c> pattern) was itself the defect —
/// a SOLID-boundary violation (this repo's AGENTS.md: depend on the system that owns a concern, never
/// borrow one whose contract doesn't promise what you need), not a broken rehydrate. The fix gives
/// <see cref="LawnBasicAttackFeature"/> its own independent <c>DefaultOn</c> const and treats
/// <c>CheatState</c> as an optional, EXPLICIT debug/QA override layered on top — never the source of
/// the default itself. These tests prove exactly that shape. <c>OVERLAY-COMBAT</c> has the identical
/// pre-existing shape and is a same-class finding for a separate task — not touched here.</para>
///
/// <para>Not run by CI (ci.yml never compiles FusionRpg.Injector); build/run locally with
/// <c>$env:FUSIONRPG_GAME_DIR</c> set, the same requirement every other injector test project in this
/// repo already carries. Shares <c>CheatState</c>'s static entries with every other test in the
/// "CheatState statics" collection (defined in <c>WaveControlTests.cs</c>) — same isolation discipline,
/// <c>ResetAll()</c> first.</para>
/// </summary>
[Collection("CheatState statics")]
public class LawnBasicAttackFeatureFlagTests
{
    public LawnBasicAttackFeatureFlagTests()
    {
        CheatState.EmitProof = false; // see MatchModifyTests' own class doc for why this must be first
        CheatState.ResetAll();
    }

    /// <summary>The simplest, most robust proof in the whole feature: the module's own default is a
    /// literal constant. Zero dependency on CheatState, CheatSchema, CheatRegistry, or any server round
    /// trip whatsoever — this assertion cannot be broken by a rehydrate bug, a reset, or a stale
    /// session document, because nothing in its evaluation touches any of those systems.</summary>
    [Fact]
    public void DefaultEnabled_constant_is_true_by_owner_decision()
    {
        // Owner decision 2026-09-16 (recorded in the constant's own comment): default ON again, after the
        // perf pass the 2026-09-15 L-N1 default-off was conditioned on. The switch is still real — the env
        // var and the explicit toggle below both force it off — but the DEFAULT is true, and this test is
        // the stale side's correction, not a change to the constant.
        Assert.True(LawnBasicAttackFeature.DefaultEnabled);
    }

    /// <summary>Reproduces the exact live shape: nobody has ever explicitly toggled the id this
    /// session (a fresh <c>CheatState.ResetAll()</c> — the same state a fresh boot or a rehydrate
    /// leaves it in). Before the fix, <c>Enabled</c> read <c>CheatState.On(CheatToggleId)</c> directly,
    /// so this was entirely at the mercy of <c>CheatSchema.EffectiveToggle</c>'s own fallback being
    /// reached correctly end-to-end in production. After the fix, <c>Enabled</c> never consults that
    /// fallback at all for its default — only an explicit override (<see cref="CheatState.IsUserSet"/>
    /// true) can move it off <see cref="LawnBasicAttackFeature.DefaultEnabled"/>.</summary>
    [Fact]
    public void Enabled_defaults_on_with_no_explicit_toggle_ever_set()
    {
        Assert.False(CheatState.IsUserSet(LawnBasicAttackFeature.CheatToggleId));
        Assert.True(LawnBasicAttackFeature.Enabled);
    }

    /// <summary>The debug/QA override surface this module's doc comment promises is still real: a
    /// script or a future debug endpoint CAN still force this off for a controlled A/B measurement via
    /// the ordinary <c>CheatState.SetToggle</c> path (the same one <c>/api/cheats/toggle</c> drives).
    /// This is the ONLY way <see cref="LawnBasicAttackFeature.Enabled"/> can read false with the env
    /// var unset — an explicit, traceable user/script action, never an implicit rehydrate artifact.
    /// </summary>
    [Fact]
    public void Explicit_debug_override_off_wins_over_the_default()
    {
        CheatState.SetToggle(LawnBasicAttackFeature.CheatToggleId, false, source: "test", emitInject: false);

        Assert.True(CheatState.IsUserSet(LawnBasicAttackFeature.CheatToggleId));
        Assert.False(LawnBasicAttackFeature.Enabled);
    }

    /// <summary>An explicit override back to "on" is a no-op relative to the default, but must still
    /// read true — confirms the override path itself is not inverted.</summary>
    [Fact]
    public void Explicit_debug_override_on_reads_true()
    {
        CheatState.SetToggle(LawnBasicAttackFeature.CheatToggleId, true, source: "test", emitInject: false);

        Assert.True(CheatState.IsUserSet(LawnBasicAttackFeature.CheatToggleId));
        Assert.True(LawnBasicAttackFeature.Enabled);
    }

    /// <summary>The historical failure shape, reproduced directly: an entry present in CheatState with
    /// <c>Enabled=false</c> but <c>IsSet=false</c> (exactly the shape <c>EnsureDefaults</c>'s own
    /// internal <c>T(id)</c> seed briefly holds before its explicit <c>Enabled=true</c> override runs,
    /// and exactly the shape ANY document round-trip that resets <c>IsSet</c> without repopulating
    /// <c>Enabled</c> would leave behind). Before the fix this was indistinguishable from "really off"
    /// because <c>CheatState.On</c> only inspects <c>IsSet</c> through the schema fallback — but the
    /// raw <see cref="CheatEntry.Enabled"/> field itself being <c>false</c> must never leak into this
    /// flag's default once nothing has explicitly set it.</summary>
    [Fact]
    public void Enabled_ignores_a_stale_backing_field_when_never_explicitly_set()
    {
        // Corrupt the backing field to the OPPOSITE of the module default, so the assertion is not
        // vacuous: with `DefaultEnabled` true, setting it to `true` and reading `true` would prove nothing.
        CheatState.Get(LawnBasicAttackFeature.CheatToggleId).Enabled = !LawnBasicAttackFeature.DefaultEnabled;
        Assert.False(CheatState.IsUserSet(LawnBasicAttackFeature.CheatToggleId)); // still not user-set

        Assert.True(LawnBasicAttackFeature.Enabled);   // the module's own default wins over the stale field
    }
}
