namespace FusionRpg.Injector.Effects;

/// <summary>
/// E35 (spec-match-modify.md §2.6): the E-* cheat ids a LIVE <c>match.modify</c> grant actually wrote
/// this match — never a snapshot of "everything E-* currently holds". <c>EffectRuntime.NotifyMatchEnd</c>
/// drains this set to restore ONLY those ids, by clearing them (<c>CheatState.ClearField</c>), never
/// by writing the level's own value back in.
///
/// <para>This is the fix for the bug §2.6 corrects: a naive <c>LoadBoardConfigIntoCheats()</c> call on
/// match end would silently overwrite EVERY <c>E-*</c> id — including ones an operator hand-set from
/// the cheat menu that no atom ever touched — with the level's own shipped values, no log, no emit.
/// Recording only what a grant wrote is what makes the restore scoped instead of blanket.</para>
/// </summary>
public static class MatchModifyWrites
{
    static readonly object Gate = new();
    static readonly HashSet<string> Ids = new(StringComparer.Ordinal);

    /// <summary>Called from <c>InjectorEffectActionSink.ExecModifyMatch</c> right after it writes one
    /// <c>E-*</c> id through <c>CheatState</c>.</summary>
    public static void Record(string? cheatId)
    {
        if (string.IsNullOrEmpty(cheatId)) return;
        lock (Gate) Ids.Add(cheatId);
    }

    /// <summary>Snapshot and clear — one match's worth, consumed exactly once at match end (or on a
    /// full reset, so a stale id from a prior match/test run can never leak into the next).</summary>
    public static IReadOnlyCollection<string> TakeAll()
    {
        lock (Gate)
        {
            if (Ids.Count == 0) return Array.Empty<string>();
            var snapshot = new List<string>(Ids);
            Ids.Clear();
            return snapshot;
        }
    }
}
