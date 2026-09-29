using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Injector;
using FusionRpg.Injector.Effects;
using Xunit;

namespace FusionRpg.Injector.Tests;

/// <summary>
/// combat-ai `decision-inspector` (module 10, CAI2.4/CAI2.5): the ring's READ ROUTE, projected into
/// `debug.combat.snapshot`. The ring itself has its own tests (`AiDecisionRingTests`) and the injector
/// adapter has fourteen (`AiInspectFeatureFlagTests`); this file is only about what the dump exposes,
/// which is the half a live probe actually reads and the half nothing asserted before.
///
/// <para><b>Two contracts, both the spec's own words:</b> enums render by NAME and the candidate list by
/// its COUNT, so the dump never depends on how a nested record struct serialises; and the top-three goes
/// through <c>CandidateScorer.FormatTopThree</c>, the ONE formatter, so no second formatting path exists
/// (`spec-decision-inspector.md` §6). A reading never scores, never records and never synthesises an
/// entry — an empty ring projects nothing, which is the SWITCH (D4: hidden by default), not a defect.</para>
///
/// <para>Not run by CI (<c>ci.yml</c> never compiles FusionRpg.Injector); build/run locally with
/// <c>$env:FUSIONRPG_GAME_DIR</c> set.</para>
/// </summary>
[Collection("CheatState statics")]
public class AiDecisionDumpTests
{
    static readonly (string ActorKey, ScoreBreakdown Breakdown)[] TopThree =
        { ("entity:9", default), ("entity:8", default) };

    /// <summary>One record with every field set to a distinguishable value, so a projection that drops
    /// or transposes one is visible rather than silently equal.</summary>
    static AiDecisionRecord One() => new(
        NowTick: 120, Round: 3, ActorKey: "entity:1a",
        Tier: AiTier.Smart, ProfileId: "siege/default", Personality: null,
        Trigger: new AiTriggerState(Swings: 2, TimerTick: 50, LockUntilTick: 0, Tokens: 0),
        Origin: AiDecisionOrigin.Policy,
        Candidates: Array.Empty<AiCandidateVerdict>(),
        ChosenActionId: "act.skill", ChosenTargetKey: "entity:9", ChosenSaturatedBy: 0,
        TopThree: TopThree);

    public AiDecisionDumpTests()
    {
        CheatState.EmitProof = false;
        CheatState.ResetAll();
        LawnAiDecisionObservability.Clear();
    }

    /// <summary>The D4 switch, turned on through the same explicit toggle the debug surface uses. The
    /// gate lives in <see cref="LawnAiDecisionObservability.Record"/>, not at the call site, so it is
    /// also what the "off" test below asserts.</summary>
    static void SwitchOn() =>
        CheatState.SetToggle(AiInspectFeature.CheatToggleId, true, source: "test", emitInject: false);

    [Fact]
    public void Every_recorded_decision_is_projected_with_enums_by_name_and_the_one_formatter()
    {
        SwitchOn();
        var record = One();
        LawnAiDecisionObservability.Sink.Record(in record);

        var row = Assert.Single(DebugCombatActions.AiDecisionDump());

        Assert.Equal(120L, row["tick"]);
        Assert.Equal(3, row["round"]);
        Assert.Equal("entity:1a", row["actor"]);
        Assert.Equal("Smart", row["tier"]);              // by NAME, not a nested struct's ToString
        Assert.Equal("siege/default", row["profile"]);
        Assert.Equal("Policy", row["origin"]);           // the closed three-member vocabulary, by name
        Assert.Equal(2, row["swings"]);                  // AiDecisionRecord.Trigger, flattened
        Assert.Equal("act.skill", row["chosenAction"]);
        Assert.Equal("entity:9", row["chosenTarget"]);
        Assert.Equal(0, row["candidates"]);              // by COUNT, never the list itself
        // The ONE formatter, called with the record's own list -- asserted rather than restated, so a
        // second formatting path cannot be introduced without this failing.
        Assert.Equal(CandidateScorer.FormatTopThree(TopThree, TopThree.Length), row["topThree"]);
    }

    [Fact]
    public void An_empty_ring_projects_nothing_rather_than_a_synthesised_row()
    {
        Assert.Empty(DebugCombatActions.AiDecisionDump());
    }

    /// <summary>The switch, not a defect: with `AI-INSPECT` off (the default) the sink writes NOTHING
    /// even though the ring is empty and the host holds the sink unconditionally — so an empty
    /// `aiDecisions` in a live dump means "hidden by default", which is exactly how a probe must read
    /// it.</summary>
    [Fact]
    public void With_the_switch_off_recording_writes_nothing_and_the_dump_stays_empty()
    {
        var record = One();
        LawnAiDecisionObservability.Sink.Record(in record);   // no SwitchOn() in this case

        Assert.Empty(DebugCombatActions.AiDecisionDump());
    }

    [Fact]
    public void Reading_twice_changes_nothing()
    {
        SwitchOn();
        var record = One();
        LawnAiDecisionObservability.Sink.Record(in record);

        var first = DebugCombatActions.AiDecisionDump();
        var second = DebugCombatActions.AiDecisionDump();

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first[0].Count, second[0].Count);
        foreach (var pair in first[0]) Assert.Equal(pair.Value, second[0][pair.Key]);
    }

    /// <summary>
    /// The WIRING, not the projection — the same gap <c>LawnDecisionDumpTests</c> closes for the slot.
    /// The dump builder reads live entities and then emits through <c>DebugRuntime</c>, so no test can
    /// enter it; this pins the one line it owns by scanning the source, the pattern
    /// <c>StanceSeamTests</c> already uses for a call site no test can reach. Without it the three tests
    /// above would pass while the snapshot carried nothing.
    /// </summary>
    [Fact]
    public void The_snapshot_builder_carries_the_ai_decisions_block()
    {
        var file = Path.Combine(RepoRoot(), "src", "FusionRpg.Injector", "DebugCombatActions.cs");
        Assert.True(File.Exists(file), $"dump source not found: {file}");

        // Comments stripped: the file's own comment NAMES the key on purpose. Line numbers deliberately
        // not pinned -- an absolute line goes stale on the next edit.
        var occurrences = File.ReadAllLines(file)
            .Select((line, index) => (Line: index + 1, Text: line))
            .Where(x => !x.Text.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(x => x.Text.Split("//")[0].Contains("\"aiDecisions\"", StringComparison.Ordinal))
            .ToList();

        var only = Assert.Single(occurrences);
        Assert.Contains("AiDecisionDump()", only.Text.Split("//")[0], StringComparison.Ordinal);
    }

    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(testsDir, "..", ".."));
    }
}
