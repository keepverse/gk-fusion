namespace FusionRpg.Injector.Effects;

/// <summary>
/// E35 (spec-match-modify.md §2.6): the scoped match-end restore itself, extracted from
/// <c>EffectRuntime.NotifyMatchEnd</c> into a pure function so its "restore only what a live grant
/// wrote, by clearing, never by writing a value back in" behaviour is provable with no Unity/game
/// dependency — <c>EffectRuntime</c> as a whole is not (it threads through <c>VfxDirector</c>,
/// <c>InjectorCombatBridge</c> and the rest of the live combat graph), but this one piece of logic
/// does not need to be.
///
/// <para>This is the fix for the bug §2.6 corrects: a naive <c>CheatActions.LoadBoardConfigIntoCheats()</c>
/// call reads Board.config back into EVERY <c>E-*</c> id unconditionally — including ones an operator
/// hand-set from the cheat menu that no atom ever touched — silently overwriting them with the level's
/// own shipped values, no log, no emit. Restoring only <see cref="MatchModifyWrites"/>'s own recorded
/// ids, and clearing rather than overwriting, is what keeps an operator's own cheat state untouched.</para>
/// </summary>
public static class MatchModifyRestore
{
    /// <summary>
    /// Drains <paramref name="takeWrittenIds"/> and clears each returned id via
    /// <paramref name="clearField"/> — never re-applies a value onto it. Returns the ids restored.
    /// </summary>
    public static IReadOnlyCollection<string> Restore(
        Func<IReadOnlyCollection<string>> takeWrittenIds, Action<string> clearField)
    {
        var ids = takeWrittenIds();
        foreach (var id in ids)
            clearField(id);
        return ids;
    }
}
