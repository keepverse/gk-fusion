using FusionRpg.Core.Actions;
using FusionRpg.Injector.Host;

namespace FusionRpg.Injector.Actions;

/// <summary>
/// `lawn-combat-wire` T8 (spec-lawn-action-bridge.md): the injector's own read of the ONE shared
/// basic-attack factory (<see cref="BasicAttackFactory"/>) — the second half of "one factory, two
/// callers" (<c>BattleRunState</c>, in Core, is the first). This is where a future lawn-side grant
/// binder (T10, <c>basic-attack-grant</c>) reads the row from — never a second hand-built
/// <c>CompiledAction</c>.
///
/// <para><b>Lazily constructed and cached for the process lifetime.</b> A construction failure — in
/// practice, <see cref="ActionTimingPolicy.Tuning"/> throwing because
/// <see cref="ActionTimingPolicy.Configure"/> has not run yet (<see cref="RpgHost.Initialize"/> is
/// supposed to have done this before any lawn actor can be granted anything) — is reported <b>once</b>,
/// loudly, through <see cref="RpgHost.Log"/>, and never retried per call. That is the spec's explicit
/// correction of the first draft's posture ("no contribution and no exception" was itself the defect
/// this whole program exists to fix): a silent per-hit skip is indistinguishable from working, and a
/// per-hit exception would crash the lawn on every swing once instead of once, ever.</para>
/// </summary>
public static class LawnBasicAttackRow
{
    static CompiledAction? _row;
    static bool _diagnosed;

    /// <summary>
    /// The shared basic-attack row, or <c>null</c> if construction has already failed once this
    /// process (checked first — never retried, never thrown from here). Never throws.
    /// </summary>
    public static CompiledAction? TryGet()
    {
        if (_row is not null) return _row;
        if (_diagnosed) return null;

        try
        {
            _row = BasicAttackFactory.Create(ActionTimingPolicy.Tuning);
            return _row;
        }
        catch (Exception ex)
        {
            _diagnosed = true;
            RpgHost.Log.Error(
                "lawn-action-bridge: basic-attack row construction failed and will NOT be retried -- " +
                $"{ex.GetType().Name}: {ex.Message}. Every lawn actor's RPG basic-attack contribution " +
                "is disabled until this is fixed -- most likely ActionTimingPolicy.Configure did not " +
                "run at RpgHost.Initialize before this was first read.");
            return null;
        }
    }

    /// <summary>
    /// Test-only reset (public — mirrors <c>TreeBoundAtomsCache.Apply</c>'s own reset-by-call
    /// convention rather than adding an <c>InternalsVisibleTo</c> this assembly does not otherwise
    /// need). This class holds process-global cached state on purpose (constructing the row is pure
    /// and its result never changes within one process), which means a test suite that exercises both
    /// the success and failure path must be able to clear it between cases.
    /// </summary>
    public static void ResetForTest()
    {
        _row = null;
        _diagnosed = false;
    }
}
