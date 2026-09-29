using FusionRpg.Injector;
using FusionRpg.Injector.Effects;
using Xunit;

namespace FusionRpg.Injector.Tests;

/// <summary>
/// combat-ai `lawn-cast-trigger` (module 19, CAI4.8, spec-lawn-cast-trigger.md §"Kill switch"): the lawn
/// decision loop's switch. Same three-layer shape as <see cref="LawnBasicAttackFeature"/> — its own
/// default const, the env var read once at process start, and an explicit toggle as the only other input.
///
/// <para><b>Default OFF is the shipping state, not a placeholder:</b> the loop's content feed (module 16's
/// held sets, which need an `ActionCatalog` the injector has no feed for) is still owed, so a default-on
/// lawn would decide with nothing to decide. Off is also the zero-cost state — `LawnDecisionHost` releases
/// every counter and token the moment this reads false.</para>
/// </summary>
[Collection("CheatState statics")]
public class LawnCombatAiFeatureFlagTests
{
    public LawnCombatAiFeatureFlagTests()
    {
        CheatState.EmitProof = false;
        CheatState.ResetAll();
    }

    [Fact]
    public void DefaultEnabled_constant_is_false_by_design()
    {
        Assert.False(LawnCombatAiFeature.DefaultEnabled);
    }

    [Fact]
    public void Enabled_defaults_off_with_no_explicit_toggle_ever_set()
    {
        Assert.False(CheatState.IsUserSet(LawnCombatAiFeature.CheatToggleId));
        Assert.False(LawnCombatAiFeature.Enabled);
    }

    /// <summary>The historical failure shape from the sibling switch: a stale backing field must never
    /// leak into the default once nothing has explicitly set the id.</summary>
    [Fact]
    public void Enabled_ignores_a_stale_backing_field_when_never_explicitly_set()
    {
        CheatState.Get(LawnCombatAiFeature.CheatToggleId).Enabled = true;
        Assert.False(CheatState.IsUserSet(LawnCombatAiFeature.CheatToggleId));

        Assert.False(LawnCombatAiFeature.Enabled);
    }

    [Fact]
    public void An_explicit_toggle_is_the_only_way_it_reads_true_with_no_env_var()
    {
        CheatState.SetToggle(LawnCombatAiFeature.CheatToggleId, true, source: "test", emitInject: false);

        Assert.True(LawnCombatAiFeature.Enabled);
    }

    [Fact]
    public void Env_forced_off_wins_over_an_explicit_on_toggle()
    {
        Assert.False(LawnCombatAiFeature.Resolve("0", debugOverride: true, defaultEnabled: true));
    }

    [Fact]
    public void Env_forced_on_wins_over_an_explicit_off_toggle_and_needs_no_toggle()
    {
        Assert.True(LawnCombatAiFeature.Resolve("1", debugOverride: false, defaultEnabled: false));
        Assert.True(LawnCombatAiFeature.Resolve("1", debugOverride: null, defaultEnabled: false));
    }

    [Fact]
    public void An_unrecognised_env_value_defers_to_the_toggle_then_to_the_module_default()
    {
        Assert.True(LawnCombatAiFeature.Resolve("yes", debugOverride: true, defaultEnabled: false));
        Assert.False(LawnCombatAiFeature.Resolve("yes", debugOverride: false, defaultEnabled: true));
        Assert.False(LawnCombatAiFeature.Resolve("", debugOverride: null, defaultEnabled: false));
        Assert.True(LawnCombatAiFeature.Resolve(null, debugOverride: null, defaultEnabled: true));
        // Not a parse: "00" is unrecognised, so it defers rather than reading as "0".
        Assert.True(LawnCombatAiFeature.Resolve("00", debugOverride: true, defaultEnabled: false));
    }
}
