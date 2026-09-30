using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;

namespace FusionRpg.Injector.Stats;

/// <summary>
/// Injector-side Θ index — replaces <c>InjectorProgressionPowerProvider</c> (T1.4). Wraps
/// <see cref="HydratedPowerIndexProvider"/> rather than duplicating it: hydration mechanics belong to
/// Core (one implementation to keep correct); this class exists so an Injector-specific hydration
/// source (a future SignalR handler, a Harmony hook) has an Injector-namespaced home to attach to
/// without Core ever needing to know about it.
///
/// <para>The one hydration source is <c>RpgClient.RefreshPowerIndexAsync</c> via
/// <c>CheatState.ApplyPowerSnapshot</c>, which hydrates the current player. Every lawn context carrying that
/// player id reads it, on both sides; a context with no hydrated player reads Θ = 0.</para>
/// </summary>
public sealed class InjectorPowerIndexProvider : IPowerIndexProvider
{
    readonly HydratedPowerIndexProvider _inner;

    public InjectorPowerIndexProvider(PowerTuning tuning) => _inner = new HydratedPowerIndexProvider(tuning);

    /// <summary>The one hydration mechanism today — a future push source calls this per actor.</summary>
    public void Hydrate(StatContext ctx, ActorLadderSnapshot snapshot) => _inner.Hydrate(ctx, snapshot);

    public void Clear() => _inner.Clear();

    public int ActorIndex(StatContext ctx) => _inner.ActorIndex(ctx);
    /// <summary>Delegates to the hydrated inner provider, which is keyed by player and therefore has
    /// no per-empire snapshot to answer with (EP4.18: an empire-keyed Theta comes from the
    /// store-backed provider).</summary>
    public int ActorIndexFor(FusionRpg.Core.Saves.SaveId save, FusionRpg.Core.Commanders.EmpireId empire) =>
        _inner.ActorIndexFor(save, empire);
    public int ContentIndex(ContentContext ctx) => _inner.ContentIndex(ctx);
    public PowerAxisReport Explain(StatContext ctx) => _inner.Explain(ctx);
}
