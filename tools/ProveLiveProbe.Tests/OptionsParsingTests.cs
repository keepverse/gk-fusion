using FusionRpg.Tools.ProveLiveProbe;
using Xunit;

namespace FusionRpg.Tools.ProveLiveProbe.Tests;

/// <summary>Offline coverage for CLI parsing — the surface spec-live-probe-tool.md's "Commands"
/// section names: -Mode, -PlayerId, -Side, -TypeId, -BannerId, -AptitudeId, -AptitudePoints, -Role,
/// -ItemInstanceId, -TimeoutSec, -NoCleanup, plus this tool's own -AcquireVia/-BaseUrl/-Col/-Row/
/// -MatchKey/-DangerousLoadoutJsonOverride extras.</summary>
public class OptionsParsingTests
{
    [Fact]
    public void Parses_modeA_full_surface()
    {
        var o = Options.Parse(new[]
        {
            "-Mode", "A", "-PlayerId", "1", "-Side", "plant", "-TypeId", "42",
            "-AptitudeId", "Might", "-AptitudePoints", "30", "-Role", "armament-primary",
            "-ItemInstanceId", "item-1",
        });

        Assert.Equal(ProbeMode.A, o.Mode);
        Assert.Equal(1, o.PlayerId);
        Assert.Equal("plant", o.Side);
        Assert.Equal(42, o.TypeId);
        Assert.Equal("Might", o.AptitudeId);
        Assert.Equal(30, o.AptitudePoints);
        Assert.Equal("armament-primary", o.Role);
        Assert.Equal("item-1", o.ItemInstanceId);
    }

    [Fact]
    public void Parses_modeB_with_bannerId_and_timeout()
    {
        var o = Options.Parse(new[] { "-Mode", "B", "-BannerId", "standard-rift", "-TimeoutSec", "45" });
        Assert.Equal(ProbeMode.B, o.Mode);
        Assert.Equal("standard-rift", o.BannerId);
        Assert.Equal(45, o.TimeoutSec);
    }

    [Fact]
    public void NoCleanup_is_a_bare_switch()
    {
        var o = Options.Parse(new[] { "-Mode", "B", "-NoCleanup" });
        Assert.True(o.NoCleanup);
    }

    [Fact]
    public void Defaults_are_modeA_and_cleanup_on()
    {
        var o = Options.Parse(Array.Empty<string>());
        Assert.Equal(ProbeMode.A, o.Mode);
        Assert.False(o.NoCleanup);
        Assert.Equal(AcquireVia.Auto, o.AcquireVia);
    }

    [Theory]
    [InlineData("a", ProbeMode.A)]
    [InlineData("A", ProbeMode.A)]
    [InlineData("b", ProbeMode.B)]
    [InlineData("B", ProbeMode.B)]
    public void Mode_is_case_insensitive(string flagValue, ProbeMode expected)
    {
        var o = Options.Parse(new[] { "-Mode", flagValue });
        Assert.Equal(expected, o.Mode);
    }

    [Fact]
    public void Unknown_mode_throws()
    {
        Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "-Mode", "C" }));
    }

    [Fact]
    public void Unknown_flag_throws()
    {
        Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "-NotARealFlag", "x" }));
    }

    [Theory]
    [InlineData(ProbeMode.A, AcquireVia.Auto, AcquireVia.DebugShortcut)]
    [InlineData(ProbeMode.B, AcquireVia.Auto, AcquireVia.RealSummon)]
    [InlineData(ProbeMode.B, AcquireVia.DebugShortcut, AcquireVia.DebugShortcut)]
    [InlineData(ProbeMode.A, AcquireVia.RealSummon, AcquireVia.RealSummon)]
    public void ResolvedAcquireVia_follows_mode_when_auto(ProbeMode mode, AcquireVia given, AcquireVia expected)
    {
        var o = new Options { Mode = mode, AcquireVia = given };
        Assert.Equal(expected, o.ResolvedAcquireVia);
    }

    [Fact]
    public void AcquireVia_accepts_debug_shortcut_and_summon_spellings()
    {
        Assert.Equal(AcquireVia.DebugShortcut, Options.Parse(new[] { "-AcquireVia", "debug-shortcut" }).AcquireVia);
        Assert.Equal(AcquireVia.RealSummon, Options.Parse(new[] { "-AcquireVia", "summon" }).AcquireVia);
        Assert.Equal(AcquireVia.RealSummon, Options.Parse(new[] { "-AcquireVia", "real-summon" }).AcquireVia);
    }

    [Fact]
    public void Dangerous_loadout_override_flag_round_trips()
    {
        var o = Options.Parse(new[] { "-DangerousLoadoutJsonOverride", "{\"x\":1}" });
        Assert.Equal("{\"x\":1}", o.DangerousLoadoutJsonOverride);
    }
}
