using FusionRpg.Contracts;
using FusionRpg.Tools.ProveLiveProbe;
using Xunit;

namespace FusionRpg.Tools.ProveLiveProbe.Tests;

/// <summary>live-probe Task 18: a Mode B acquire funded by kills of debug- or cheat-spawned entities
/// must be visible in the report and must not read as a clean pass. Offline — the three reads are fed
/// in as DTOs.</summary>
public class SoulProvenanceTests
{
    static SoulLedgerEntryDto Kill(long id, long factId, long delta = 1, long runId = 7) =>
        new() { Id = id, RunId = runId, Delta = delta, Reason = "kill", RefKind = "activity_fact", RefId = factId.ToString() };

    static PvzActivityFactDto Fact(long id, string? payload) =>
        new() { Id = id, Kind = "ZombieKilled", PayloadJson = payload };

    [Fact]
    public void Debug_spawned_kills_make_the_step_a_mismatch_that_names_the_debug_souls()
    {
        var ledger = new[]
        {
            Kill(3, 103, delta: 2),
            Kill(2, 102, delta: 5),
            new SoulLedgerEntryDto { Id = 1, Delta = 50, Reason = "victory", RefKind = "activity_fact", RefId = "101" },
        };
        var facts = new[]
        {
            Fact(103, """{"type":0,"spawnOrigin":"game"}"""),
            Fact(102, """{"type":0,"spawnOrigin":"debug"}"""),
        };

        var summary = SoulProvenance.Summarize(57, ledger, ledgerTruncated: false, facts);
        var step = SoulProvenance.ToStep(summary);

        Assert.Equal(5, summary.DebugKillSouls);
        Assert.Equal(2, summary.KillSoulsByOrigin[KillOrigin.Game]);
        Assert.Equal(50, summary.DeltaByReason["victory"]);
        Assert.Equal(StepOutcome.Mismatch, step.Outcome);
        Assert.False(step.IsOk);
        Assert.Contains("DEBUG-FUNDED: 5 souls", step.Detail);
    }

    [Fact]
    public void Cheat_panel_spawns_count_as_command_funded_too()
    {
        var summary = SoulProvenance.Summarize(1, new[] { Kill(1, 11) }, false, new[] { Fact(11, """{"spawnOrigin":"cheat"}""") });
        Assert.True(summary.DebugFunded);
        Assert.Equal(StepOutcome.Mismatch, SoulProvenance.ToStep(summary).Outcome);
    }

    [Fact]
    public void A_balance_earned_only_from_game_spawns_is_ok()
    {
        var summary = SoulProvenance.Summarize(2, new[] { Kill(2, 12), Kill(1, 11) }, false,
            new[] { Fact(12, """{"spawnOrigin":"game"}"""), Fact(11, """{"spawnOrigin":"game"}""") });
        var step = SoulProvenance.ToStep(summary);

        Assert.False(summary.DebugFunded);
        Assert.Equal(StepOutcome.Ok, step.Outcome);
        Assert.DoesNotContain("WARNING", step.Detail);
    }

    [Fact]
    public void Kills_without_a_recorded_origin_or_fact_are_reported_as_unproven_never_as_game()
    {
        var summary = SoulProvenance.Summarize(3, new[] { Kill(3, 13), Kill(2, 12), Kill(1, 999) }, false,
            new[] { Fact(13, """{"type":1}"""), Fact(12, null) });
        var step = SoulProvenance.ToStep(summary);

        Assert.Equal(2, summary.KillSoulsByOrigin[KillOrigin.Unrecorded]);
        Assert.Equal(1, summary.KillSoulsByOrigin[KillOrigin.FactNotFound]);
        Assert.False(summary.KillSoulsByOrigin.ContainsKey(KillOrigin.Game));
        Assert.Equal(StepOutcome.Ok, step.Outcome);
        Assert.Contains("WARNING: 3 kill souls have unproven origin", step.Detail);
    }

    [Fact]
    public void A_truncated_ledger_scan_says_so()
    {
        var step = SoulProvenance.ToStep(SoulProvenance.Summarize(0, Array.Empty<SoulLedgerEntryDto>(), true, Array.Empty<PvzActivityFactDto>()));
        Assert.Contains("TRUNCATED", step.Detail);
    }

    [Theory]
    [InlineData("""{"spawnOrigin":"debug"}""", KillOrigin.Debug)]
    [InlineData("""{"spawnOrigin":"game"}""", KillOrigin.Game)]
    [InlineData("""{"spawnOrigin":"cheat"}""", KillOrigin.Cheat)]
    [InlineData("""{"spawnOrigin":"mystery"}""", KillOrigin.Unrecorded)]
    [InlineData("""{"spawnOrigin":1}""", KillOrigin.Unrecorded)]
    [InlineData("not json", KillOrigin.Unrecorded)]
    [InlineData("[]", KillOrigin.Unrecorded)]
    public void Origin_is_read_only_from_a_string_spawnOrigin(string payload, KillOrigin expected) =>
        Assert.Equal(expected, SoulProvenance.OriginOf(payload));

    [Fact]
    public void Spends_and_non_activity_kill_rows_are_not_attributed_as_kill_earns()
    {
        Assert.False(SoulProvenance.IsKillEarn(new SoulLedgerEntryDto { Delta = -5, Reason = "kill", RefKind = "activity_fact", RefId = "1" }));
        Assert.False(SoulProvenance.IsKillEarn(new SoulLedgerEntryDto { Delta = 5, Reason = "kill", RefKind = null, RefId = "1" }));
        Assert.False(SoulProvenance.IsKillEarn(new SoulLedgerEntryDto { Delta = 5, Reason = "summon", RefKind = "activity_fact", RefId = "1" }));
    }
}
