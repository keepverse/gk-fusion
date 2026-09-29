using System.Runtime.CompilerServices;

/// <summary>
/// The host tunings this assembly's classes need, configured ONCE before any test runs.
///
/// <para><b>Why a module initializer and not a static ctor per class.</b> Several classes here configure
/// a tuning from a static ctor — `UniqueAptitudeRefreshCadenceTests` does `MatchTuningPolicy.Configure`,
/// documented as "idempotent, safe even if another class does it too". That is true, but it is not
/// enough: `MatchHost`'s own static initializer reads `MatchTuningPolicy` (<c>CapPolicyConfig.Defaults()</c>),
/// and whichever class touches <c>MatchHost</c> FIRST wins. If that class has not configured the policy,
/// the static initializer throws and the CLR caches the <c>TypeInitializationException</c> on the type for
/// the rest of the process — so every later test that touches <c>MatchHost</c> fails, permanently and
/// confusingly (found 2026-09-19: two <c>UniqueAptitudeRefreshCadenceTests</c> debounce tests failed
/// whenever another class reached <c>MatchHost</c> first). Ordering is not a contract, so the tunings are
/// configured before any of that can happen.</para>
///
/// <para>Each value is read from the real shipped file, the same one the hosts read, rooted by walking up
/// to the directory holding <c>gk-fusion/src/FusionRpg.Injector</c> — never from the CWD.</para>
/// </summary>
internal static class InjectorTestTuningBootstrap
{
    [ModuleInitializer]
    public static void Init()
    {
        FusionRpg.Core.Match.MatchTuningPolicy.Configure(
            FusionRpg.Core.Match.MatchTuningLoader.Parse(Read("match.v1.json")));
        FusionRpg.Core.Power.PowerTuningHub.Configure(
            FusionRpg.Core.Power.PowerTuningLoader.Parse(Read("power-scale.v2.json")));
        FusionRpg.Core.Stats.Derived.DerivedStatPolicy.Configure(
            FusionRpg.Core.Stats.Derived.DerivedStatTuningLoader.Parse(Read("derived-stats.v2.json")));
        FusionRpg.Core.Status.StatusPolicy.Configure(
            FusionRpg.Core.Status.StatusTuningLoader.Parse(Read("status.v1.json")));
        FusionRpg.Core.Actions.ActionBaseTuningHub.Configure(
            FusionRpg.Core.Actions.ActionBaseTuningLoader.Parse(Read("action-base.v2.json")));
        FusionRpg.Core.Actions.Rungs.RungPolicy.Configure(
            FusionRpg.Core.Actions.Rungs.RungTableLoader.Parse(Read("action-rungs.v1.json")));
    }

    static string Read(string file) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", file));

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }
}
