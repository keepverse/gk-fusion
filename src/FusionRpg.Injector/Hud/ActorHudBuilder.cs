using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Hud;
using FusionRpg.Core.Match;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Injector.Effects;
using FusionRpg.Injector.Match;
using FusionRpg.Injector.Stats;
using FusionRpg.Bridge.Hud;
using FusionRpg.Bridge.Stats;

namespace FusionRpg.Injector.Hud;

/// <summary>Single Hot gather entry for per-unit HUD snapshots (actor-hud-dump spec).</summary>
public static class ActorHudBuilder
{
    public static ActorHudSnapshot Build(string? ptrHex)
    {
        var ptr = CombatPtr.Normalize(ptrHex);
        if (string.IsNullOrEmpty(ptr))
            throw new ArgumentException("actor HUD build requires a non-empty ptr", nameof(ptrHex));

        var tuning = ActorHudTuningHub.Tuning;
        UniqueBindingPhase? bindingPhase = null;
        if (MatchHost.Runtime.TryGetBindingByPtr(ptr, out var binding) && binding != null)
            bindingPhase = binding.Phase;

        int? levelBand = null;
        if (InjectorDerivedOverride.TryGet(ptr, out var derived))
        {
            var theta = (long)derived.Get(DerivedStatChannels.ProgressionPower);
            levelBand = PowerBandDisplay.FromTheta(theta);
        }

        long shieldHp = 0, shieldMax = 0;
        IReadOnlyList<ActorHudShieldStack> shieldStacks = Array.Empty<ActorHudShieldStack>();
        var shieldRuntime = TryShieldRuntime();
        if (shieldRuntime != null)
        {
            var ownerKey = EffectOwnerKeys.Entity(ptr);
            var shields = shieldRuntime.GetShields(ownerKey);
            (shieldHp, shieldMax) = ActorHudShieldStacks.Totals(shields);
            shieldStacks = ActorHudShieldStacks.AggregateByElement(shields);
        }

        var statusTokens = BuildStatusTokens(TryStatusRuntime(), ptr);
        var elements = BuildElements(ptr);

        // E41 (spec-ui-attach-point.md §4): ActorHudResources.Meters' first-ever producer.
        // ActorHudMeterOverride is null (no meters authored for this ptr) far more often than not, so
        // this is a cheap dictionary lookup on the hot HUD-read path, not a new scan.
        var meters = ActorHudMeterOverride.TryGet(ptr);

        var compose = new ActorHudComposer.ActorHudComposeInput(
            ActorHudUniqueFlags.TryIsUnique(ptr),
            bindingPhase,
            levelBand,
            shieldRuntime != null ? shieldStacks : null,
            shieldHp,
            shieldMax,
            statusTokens,
            tuning.StatusStripMax,
            tuning.HpSliverEnabled,
            Meters: meters,
            Elements: elements);

        return ActorHudComposer.Compose(compose);
    }

    static ShieldRuntime? TryShieldRuntime()
    {
        try { return EffectRuntime.Bag.ShieldGate?.Runtime; }
        catch { return null; }
    }

    static StatusRuntime? TryStatusRuntime()
    {
        try { return EffectRuntime.Status; }
        catch { return null; }
    }

    static IReadOnlyList<ActorHudStatusToken> BuildStatusTokens(StatusRuntime? runtime, string ptr)
    {
        if (runtime == null)
            return Array.Empty<ActorHudStatusToken>();

        var instances = runtime.ForHost(ptr);
        if (instances.Count == 0)
            return Array.Empty<ActorHudStatusToken>();

        var tokens = new List<ActorHudStatusToken>(instances.Count);
        for (var i = 0; i < instances.Count; i++)
        {
            var inst = instances[i];
            tokens.Add(new ActorHudStatusToken(
                inst.StatusId,
                inst.IsCrowdControl,
                MagnitudeBandDisplay.FromEffectiveMagnitude(inst.EffectiveMagnitude)));
        }

        return tokens;
    }

    /// <summary>
    /// The HUD consumes the same side + game-type species resolution as elemental combat. It does not
    /// infer a species from position, Unity hierarchy, or a second per-type map.
    /// </summary>
    static ActorHudElements? BuildElements(string ptr)
    {
        try
        {
            var (_, _, types, found) = LawnElementResolverHost.Resolve(ptr);
            if (!found || types.Primary is not { } primary)
                return null;
            return new ActorHudElements(primary.ToElementId(), types.Secondary?.ToElementId());
        }
        catch
        {
            return null;
        }
    }
}
