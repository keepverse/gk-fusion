using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Injector;
using FusionRpg.Injector.Effects;
using Xunit;

namespace FusionRpg.Injector.Tests;

/// <summary>
/// E35 (spec-match-modify.md). CheatState's new `long` channel and the scoped match-end restore —
/// the module's own stated "real substance" (§2.6's corrected bug, and §2.3's float-hop removal).
///
/// <para>Not run by CI (ci.yml never compiles FusionRpg.Injector — see this project's own csproj
/// comment); build/run locally with <c>$env:FUSIONRPG_GAME_DIR</c> set to a folder with
/// <c>BepInEx\core</c> / <c>BepInEx\interop</c>, the same requirement every other injector build in
/// this repo already carries.</para>
///
/// <para>Tests share <see cref="CheatState"/>'s static entries, so every test calls
/// <see cref="CheatState.ResetAll"/> first — the same isolation discipline
/// <c>EffectRuntime.ResetForTests</c> gives its own shared statics.</para>
///
/// <para><b>A real, pre-existing defect found running this harness for the first time, out of this
/// module's scope to fix:</b> <c>CheatState.Note</c>'s emit branch calls <c>GameHooks.Emit</c>
/// unguarded by a try/catch; <c>GameHooks.Emit</c>'s own internal catch calls
/// <c>CheatState.Error</c> on failure, which calls <c>Note</c> again — with no live
/// <c>Match.MatchHost</c>/<c>RpgHost</c> to succeed against (as here, a bare xunit process), that is
/// unbounded mutual recursion and a real stack-overflow crash, confirmed by running it. Never
/// triggered before because nothing exercised <c>CheatState</c> outside a live game session before
/// this test project existed. Worked around here (<see cref="CheatState.EmitProof"/> = false, set
/// before anything else) rather than patched, since the recursive pair belongs to shared cheat/telemetry
/// plumbing this module does not own and did not introduce.</para>
/// </summary>
// E36 (spec-wave-control.md): added alongside WaveControlTests, whose own class doc explains why --
// CheatState's entries are static and shared, so this and WaveControlTests must not run in parallel
// with each other (xUnit parallelizes different classes/collections by default).
[Collection("CheatState statics")]
public class MatchModifyTests
{
    public MatchModifyTests()
    {
        CheatState.EmitProof = false; // see the class doc's recursion note -- must be set FIRST
        CheatState.ResetAll();
        MatchModifyWrites.TakeAll(); // drain any stale ids a prior test's throw left behind
    }

    // ---- §2.3: the long channel, and the float hop it replaces ------------------------------------

    [Fact]
    public void SetLong_and_LVal_round_trip_20_million_exactly()
    {
        CheatState.SetLong("E-ZARM", 20_000_000L);
        Assert.Equal(20_000_000L, CheatState.LVal("E-ZARM"));
    }

    [Fact]
    public void SetLong_and_LVal_round_trip_long_MaxValue_exactly()
    {
        // Proves no float hop anywhere on this path -- long.MaxValue has no exact float
        // representation at all, so any hop through FloatValue would corrupt it silently.
        CheatState.SetLong("E-ZARM", long.MaxValue);
        Assert.Equal(long.MaxValue, CheatState.LVal("E-ZARM"));
    }

    [Fact]
    public void SetLong_marks_the_id_user_set_and_locks_board_config()
    {
        Assert.False(CheatState.IsUserSet("E-ZARM"));
        Assert.False(CheatState.BoardConfigLocked);

        CheatState.SetLong("E-ZARM", 20_000_000L);

        Assert.True(CheatState.IsUserSet("E-ZARM"));
        Assert.True(CheatState.BoardConfigLocked);
    }

    // The contrast §4 asks for: the OLD SetFloat/FVal path does NOT preserve 20,000,000 exactly, once
    // it is a value where the float hop actually bites. Demonstrated directly against float's own
    // documented integer-exactness ceiling (16,777,216, docs/architecture/numeric-types.md's overflow table row 1) rather than
    // against 20,000,000 itself, which happens to still survive a single float round-trip on some
    // runtimes -- the CEILING is the real defect surface, and this pins it precisely.
    [Fact]
    public void The_old_SetFloat_FVal_path_stops_being_exact_past_floats_integer_ceiling()
    {
        const long aboveFloatCeiling = 16_777_217L; // 2^24 + 1 -- the first int a float cannot hold exactly
        CheatState.SetFloat("E-ZARM", aboveFloatCeiling);

        // IVal rounds FVal (a float) back to an int -- this is the exact hop §2.3 describes
        // (SetFloat -> double -> FVal -> float -> IVal -> int) and the exact reason it was removed
        // from the zombieStartAmmor path.
        Assert.NotEqual(aboveFloatCeiling, (long)CheatState.IVal("E-ZARM"));

        // The long channel, on the identical value, stays exact -- the fix, proven side by side with
        // the defect it replaces.
        CheatState.SetLong("E-ZARM", aboveFloatCeiling);
        Assert.Equal(aboveFloatCeiling, CheatState.LVal("E-ZARM"));
    }

    // §5 criterion 5 / §4: "zombieStartAmmor at long.MaxValue -> the narrowing cast throws (no wrap,
    // no clamp)." CheatActions.ApplyBoardConfig performs exactly `checked((int)CheatState.LVal("E-
    // ZARM"))` at the Unity boundary (CheatActions.cs), inside a try/catch that logs and continues --
    // the same defensive shape every method in that file already has, and not this module's to change.
    // Pinned here against the identical expression so the "throws, never wraps or clamps" claim is
    // provable with no live Board (ApplyBoardConfig itself no-ops when GameHooks.Board is null, which
    // it always is in this harness, so it cannot be exercised end to end outside a live game).
    [Fact]
    public void The_checked_narrow_CheatActions_performs_on_LVal_throws_rather_than_wrapping_or_clamping()
    {
        CheatState.SetLong("E-ZARM", long.MaxValue);
        Assert.Throws<OverflowException>(() => checked((int)CheatState.LVal("E-ZARM")));
    }

    // ---- §2.6: the scoped restore, and its two planted violations ---------------------------------

    [Fact]
    public void MatchModifyWrites_records_and_drains_exactly_once()
    {
        MatchModifyWrites.Record("E-ZS");
        MatchModifyWrites.Record("E-ZH");
        MatchModifyWrites.Record("E-ZS"); // duplicate write within a match -- still one id

        var first = MatchModifyWrites.TakeAll();
        Assert.Equal(new[] { "E-ZH", "E-ZS" }, first.OrderBy(x => x, StringComparer.Ordinal));

        // Drained -- a second take is empty until something records again.
        Assert.Empty(MatchModifyWrites.TakeAll());
    }

    [Fact]
    public void Scoped_restore_clears_only_the_ids_a_grant_wrote_by_clearing_not_overwriting()
    {
        // Simulates ExecModifyMatch's own write for one match: a match.modify atom set E-ZS.
        CheatState.SetFloat("E-ZS", 0.4);
        MatchModifyWrites.Record("E-ZS");
        Assert.True(CheatState.IsUserSet("E-ZS"));
        Assert.True(CheatState.BoardConfigLocked);

        // The real match-end call EffectRuntime.NotifyMatchEnd makes.
        var restored = MatchModifyRestore.Restore(
            MatchModifyWrites.TakeAll, id => CheatState.ClearField(id, "match-end"));

        Assert.Equal(new[] { "E-ZS" }, restored);
        Assert.False(CheatState.IsUserSet("E-ZS"));
        // Nothing else holds an E-* key user-set, so the latch clears too (CheatState.ClearField's
        // own existing HasAnySetWithPrefix("E-") rule -- reused here, not reimplemented).
        Assert.False(CheatState.BoardConfigLocked);
    }

    // PLANTED VIOLATION #1 (§4): "remove the scoped match-end restore -- a two-match test fails: match
    // 2 starts with match 1's multipliers still applied." Simulated by simply never calling the
    // restore between the two matches.
    [Fact]
    public void PLANTED_VIOLATION_skipping_the_restore_leaks_match_ones_multiplier_into_match_two()
    {
        // Match 1: a match.modify atom sets a curse.
        CheatState.SetFloat("E-ZS", 0.4);
        MatchModifyWrites.Record("E-ZS");
        Assert.True(CheatState.IsUserSet("E-ZS"));

        // Match 2 starts with NO restore call in between (the violation -- what E35 must not ship).
        // The curse is still live: IsUserSet is still true and the multiplier is still 0.4, exactly
        // as if the operator had set it by hand for match 2, which is the leak this test proves.
        Assert.True(CheatState.IsUserSet("E-ZS"));
        Assert.Equal(0.4, CheatState.FVal("E-ZS"), 3);
        Assert.True(CheatState.BoardConfigLocked);

        // The real fix (this module's own shipped path) does close the leak, proven right beside the
        // violation so a reader sees both halves in one place.
        MatchModifyRestore.Restore(MatchModifyWrites.TakeAll, id => CheatState.ClearField(id, "match-end"));
        Assert.False(CheatState.IsUserSet("E-ZS"));
    }

    // PLANTED VIOLATION #2 (§4): "replace the scoped restore with a blanket LoadBoardConfigIntoCheats()
    // -- a cheat-state test fails: hand-set E-ZS, bind zero match.modify atoms, play one match,
    // IsUserSet('E-ZS') must still be true with the operator's own value afterward."
    [Fact]
    public void PLANTED_VIOLATION_a_blanket_restore_would_erase_an_operators_hand_set_value_the_scoped_one_does_not()
    {
        // The operator sets E-ZS from the cheat menu -- no atom involved, nothing recorded.
        CheatState.SetFloat("E-ZS", 0.4, source: "web");
        Assert.True(CheatState.IsUserSet("E-ZS"));
        Assert.Empty(MatchModifyWrites.TakeAll()); // zero match.modify atoms bound this match

        // The SHIPPED, scoped restore: nothing was recorded, so nothing is touched.
        MatchModifyRestore.Restore(MatchModifyWrites.TakeAll, id => CheatState.ClearField(id, "match-end"));
        Assert.True(CheatState.IsUserSet("E-ZS"));
        Assert.Equal(0.4, CheatState.FVal("E-ZS"), 3);

        // The REJECTED alternative, spelled out rather than merely asserted-against: a blanket
        // LoadBoardConfigIntoCheats()-shaped restore clears every E-* id unconditionally, operator-set
        // or not -- simulated here at the CheatState level (LoadBoardConfigIntoCheats itself needs a
        // live Board.config and cannot run in this harness) to show it WOULD erase the operator's own
        // value, which is exactly why §2.6 forbids it.
        CheatState.ClearField("E-ZS", "match-end"); // what an unconditional restore does to every id
        Assert.False(CheatState.IsUserSet("E-ZS"));
    }

    // ---- §2.5: the executor itself, through the real IEffectActionSink.Execute entry point --------

    static EffectActionPlanItem ModifyMatchItem(string field, object amount) => new()
    {
        Action = EffectActions.ModifyMatch,
        Params = new Dictionary<string, object?> { ["field"] = field, ["amount"] = amount },
    };

    [Fact]
    public void ExecModifyMatch_divides_a_per_mille_ratio_field_by_1000_once_and_records_the_write()
    {
        var sink = new InjectorEffectActionSink();
        // `ok` is NOT asserted here: merely referencing the IL2CPP `Board` type (as
        // CheatActions.ApplyBoardConfig's own null-guard does) throws outside a live IL2CPP host
        // process, in this bare xunit harness -- confirmed empirically, and exactly the kind of thing
        // spec-match-modify.md §4 means by "ExecModifyMatch is proven by a LIVE run, not a green
        // pipeline." What IS provable here, and is this test's real subject, is everything ExecModifyMatch
        // does BEFORE that call: the field -> cheat id mapping, the /1000 division, and the write
        // recording -- all of which already ran and are observable regardless of what Execute returns.
        sink.Execute(new EffectExecuteContext(), ModifyMatchItem("zombieCountMultiplier", 1500L));

        Assert.Equal(1.5, CheatState.FVal("E-ZC"), 3);
        Assert.True(CheatState.IsUserSet("E-ZC"));
        Assert.Contains("E-ZC", MatchModifyWrites.TakeAll());
    }

    [Fact]
    public void ExecModifyMatch_divides_an_interval_ms_field_by_1000_once()
    {
        var sink = new InjectorEffectActionSink();
        sink.Execute(new EffectExecuteContext(), ModifyMatchItem("waveInterval", 45_000L));

        Assert.Equal(45.0, CheatState.FVal("E-WAVE-I"), 3);
    }

    // §2.3/§2.5: zombieStartAmmor is the one field that skips the /1000 division and travels through
    // SetLong, never SetFloat -- the whole point of the long channel this module adds.
    [Fact]
    public void ExecModifyMatch_routes_zombieStartAmmor_through_SetLong_with_no_division_and_no_float_hop()
    {
        var sink = new InjectorEffectActionSink();
        sink.Execute(new EffectExecuteContext(), ModifyMatchItem("zombieStartAmmor", 20_000_000L));

        Assert.Equal(20_000_000L, CheatState.LVal("E-ZARM"));
        Assert.True(CheatState.IsUserSet("E-ZARM"));
        Assert.Contains("E-ZARM", MatchModifyWrites.TakeAll());
    }

    [Fact]
    public void ExecModifyMatch_with_an_unmapped_field_refuses_rather_than_writing_nothing_silently()
    {
        // Defence-in-depth arm (bind-time Vocabulary already refuses this in AtomKindRegistry) --
        // proven here as a named refusal (returns false), not a silent no-op.
        var sink = new InjectorEffectActionSink();
        var ok = sink.Execute(new EffectExecuteContext(), ModifyMatchItem("zombieHelthMultiplier", 1500L));

        Assert.False(ok);
        Assert.Empty(MatchModifyWrites.TakeAll());
    }
}
