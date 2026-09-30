using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Actions.Ai.Lawn;
using FusionRpg.Core.Combat;
using FusionRpg.Injector;
using FusionRpg.Injector.Effects;
using System;
using System.IO;
using System.Linq;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Injector.Tests;

/// <summary>
/// combat-ai `lawn-cast-trigger` (module 19, CAI4.8): the frame slot's READ ROUTE. `debug.combat.snapshot`
/// carries `lawnDecision`, projected by <see cref="DebugCombatActions.LawnDecisionDump"/>. Before that
/// block existed, the five readings on <see cref="LawnDecisionHost"/> had **no reader anywhere in `src/`**
/// — an instrument nobody can read is not an instrument (the CAI2.5 precedent).
///
/// <para>Read-only is the contract being asserted as much as the numbers: a second read must be identical,
/// because the dump is an observation, never a step. The slot's own behaviour is
/// <c>LawnDecisionHostTests</c>'s subject; this file is only about what the dump exposes.</para>
///
/// <para>Not run by CI (<c>ci.yml</c> never compiles FusionRpg.Injector); build/run locally with
/// <c>$env:FUSIONRPG_GAME_DIR</c> set.</para>
/// </summary>
[Collection("CheatState statics")]
public class LawnDecisionDumpTests
{
    const string Actor = "entity:1a";

    /// <summary>Three swings decide and the timer never fires in a test's tick range, so the due set is
    /// driven by swings alone — the same fixture <c>LawnDecisionHostTests</c> uses.</summary>
    static CombatAiLawnTuning Tuning() => new(
        SwingsPerDecision: 3, TicksPerDecision: 100000, PostCastLockTicks: 0, OffsetStream: "test.offset");

    static void SwingThreeTimes(long fromTick)
    {
        for (var i = 0; i < 3; i++)
            LawnDecisionHost.RecordSwing(Actor, isFirstOfSwing: true, castOrigin: false, nowTick: fromTick + i);
    }

    static BoardSnapshot Census(params string[] ptrs) =>
        new(ptrs.Select(p => new BoardEntitySnap { Ptr = p, Side = "plant", Living = true }));

    static readonly Func<BoardSnapshot> PreviousCensus = LawnActorViewHost.CensusOf;

    public LawnDecisionDumpTests()
    {
        CheatState.EmitProof = false;
        CheatState.ResetAll();
        LawnDecisionHost.Decide = null;
        LawnDecisionHost.Clear();
        LawnDecisionHost.ResetForTest();
        LawnActorViewHost.Clear();
        LawnActorViewHost.CensusOf = () => Census(Actor);
    }

    [Fact]
    public void Every_slot_reading_is_projected_and_a_second_read_is_identical()
    {
        CheatState.SetToggle(LawnCombatAiFeature.CheatToggleId, true, source: "test", emitInject: false);
        LawnDecisionHost.Configure(Tuning());
        LawnDecisionHost.BeginMatch(matchSeed: 42);
        LawnDecisionHost.Decide = (_, _) => true;

        SwingThreeTimes(fromTick: 0);
        LawnDecisionHost.Tick(nowTick: 10, frame: 1, Census(Actor));

        var first = DebugCombatActions.LawnDecisionDump();
        var second = DebugCombatActions.LawnDecisionDump();

        Assert.Equal(1, (int)first["decisions"]);
        Assert.Equal(1, (int)first["casts"]);
        Assert.Equal(0, (int)first["failures"]);
        Assert.Equal(1, (int)first["tracked"]);
        Assert.Equal(0, (int)first["tokensInUse"]);

        // The dump is an observation, never a step: reading twice changes nothing.
        Assert.Equal(first.Count, second.Count);
        foreach (var pair in first) Assert.Equal(pair.Value, second[pair.Key]);
    }

    /// <summary>The default state, and the one a live probe reads before flipping the switch: every
    /// reading is 0 and the projection still builds rather than throwing on an unconfigured slot.</summary>
    [Fact]
    public void With_the_feature_off_every_reading_is_zero_and_the_dump_still_builds()
    {
        var dump = DebugCombatActions.LawnDecisionDump();

        Assert.Equal(0, (int)dump["decisions"]);
        Assert.Equal(0, (int)dump["casts"]);
        Assert.Equal(0, (int)dump["failures"]);
        Assert.Equal(0, (int)dump["tracked"]);
        Assert.Equal(0, (int)dump["tokensInUse"]);
    }

    /// <summary>
    /// The WIRING, not the projection: `debug.combat.snapshot`'s dump must carry the block. The projection
    /// above is reachable from a test, but the snapshot builder itself is not — it reads live entities and
    /// then emits through <c>DebugRuntime</c> — so this pins the one line the builder owns by scanning the
    /// source, the pattern `StanceSeamTests` already uses for a seam whose call site no test can enter.
    /// Without it the two tests above would pass while the dump carried nothing, which is exactly the
    /// "one wire remains" shape this repo treats as not-done.
    /// </summary>
    [Fact]
    public void The_snapshot_builder_carries_the_lawn_decision_block()
    {
        var file = Path.Combine(RepoRoot(), "src", "FusionRpg.Injector", "DebugCombatActions.cs");
        Assert.True(File.Exists(file), $"dump source not found: {file}");

        // Comments stripped: this file's own comment NAMES the key on purpose, and the criterion is about
        // code. Line numbers deliberately not pinned — an absolute line goes stale on the next edit, which
        // is the drift this repo's citation discipline exists to avoid.
        var occurrences = File.ReadAllLines(file)
            .Select((line, index) => (Line: index + 1, Text: line))
            .Where(x => !x.Text.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(x => x.Text.Split("//")[0].Contains("\"lawnDecision\"", StringComparison.Ordinal))
            .ToList();

        var only = Assert.Single(occurrences);
        Assert.Contains("LawnDecisionDump()", only.Text.Split("//")[0], StringComparison.Ordinal);
    }

    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        return KeepverseRoots.Core();
    }
}
