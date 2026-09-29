using FusionRpg.Tools.ProveLiveProbe;
using Xunit;

namespace FusionRpg.Tools.ProveLiveProbe.Tests;

/// <summary>Offline coverage for the refusal logic and the Shares-dict translation — no live server
/// needed (tasks/live-probe-todo.md Task 8).</summary>
public class GuardrailsTests
{
    [Fact]
    public void ModeB_with_debug_shortcut_is_refused()
    {
        var reason = Guardrails.CheckModeBAcquisition(ProbeMode.B, AcquireVia.DebugShortcut);
        Assert.NotNull(reason);
        Assert.Contains("Mode B", reason);
    }

    [Fact]
    public void ModeB_with_real_summon_is_allowed()
    {
        Assert.Null(Guardrails.CheckModeBAcquisition(ProbeMode.B, AcquireVia.RealSummon));
    }

    [Fact]
    public void ModeA_with_debug_shortcut_is_allowed()
    {
        Assert.Null(Guardrails.CheckModeBAcquisition(ProbeMode.A, AcquireVia.DebugShortcut));
    }

    [Fact]
    public void ModeA_never_refused_regardless_of_acquireVia()
    {
        // The refusal is specific to Mode B — Mode A's whole point is that no live board exists to
        // read from, so a synthetic ptr is harmless there.
        Assert.Null(Guardrails.CheckModeBAcquisition(ProbeMode.A, AcquireVia.RealSummon));
    }

    [Fact]
    public void Nonempty_loadout_override_is_refused()
    {
        var reason = Guardrails.CheckLoadoutOverride("{\"absolutes\":{}}");
        Assert.NotNull(reason);
        Assert.Contains("fabrication", reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_or_missing_loadout_override_is_allowed(string? value)
    {
        Assert.Null(Guardrails.CheckLoadoutOverride(value));
    }

    [Fact]
    public void Preflight_catches_loadout_override_before_modeB_check()
    {
        var o = new Options
        {
            Mode = ProbeMode.B,
            AcquireVia = AcquireVia.DebugShortcut,
            DangerousLoadoutJsonOverride = "{\"x\":1}",
        };
        var reason = Guardrails.PreflightRefusal(o);
        Assert.NotNull(reason);
        Assert.Contains("fabrication", reason);
    }

    [Fact]
    public void Preflight_catches_modeB_debug_shortcut_when_no_loadout_override_given()
    {
        var o = new Options { Mode = ProbeMode.B, AcquireVia = AcquireVia.DebugShortcut };
        var reason = Guardrails.PreflightRefusal(o);
        Assert.NotNull(reason);
        Assert.Contains("Mode B", reason);
    }

    [Fact]
    public void Preflight_is_null_for_a_clean_modeA_run()
    {
        var o = new Options { Mode = ProbeMode.A, AptitudeId = "Might", AptitudePoints = 30 };
        Assert.Null(Guardrails.PreflightRefusal(o));
    }

    [Fact]
    public void Preflight_is_null_for_a_clean_modeB_run_with_real_summon()
    {
        var o = new Options { Mode = ProbeMode.B, AcquireVia = AcquireVia.RealSummon };
        Assert.Null(Guardrails.PreflightRefusal(o));
    }

    [Fact]
    public void BuildShares_translates_single_aptitude_id_and_points()
    {
        var shares = Guardrails.BuildShares("Might", 30);
        Assert.Single(shares);
        Assert.Equal(30, shares["Might"]);
    }

    [Fact]
    public void BuildShares_is_empty_when_aptitudeId_is_missing()
    {
        Assert.Empty(Guardrails.BuildShares(null, 30));
        Assert.Empty(Guardrails.BuildShares("", 30));
        Assert.Empty(Guardrails.BuildShares("   ", 30));
    }

    [Fact]
    public void BuildShares_trims_the_aptitude_id()
    {
        var shares = Guardrails.BuildShares("  Might  ", 5);
        Assert.True(shares.ContainsKey("Might"));
    }
}
