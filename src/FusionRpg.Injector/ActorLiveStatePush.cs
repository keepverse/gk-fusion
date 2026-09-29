using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Match;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using FusionRpg.Injector.Effects;
using FusionRpg.Injector.Host;
using FusionRpg.Injector.Match;
using FusionRpg.Core.Time;

namespace FusionRpg.Injector;

/// <summary>
/// CG-A4b / S3 — push Hot statuses + shield layers into Server
/// <c>POST /api/internal/actors/{instanceId}/live-state</c>.
/// <para>
/// Path: no existing match/dump ingest carried GetShields-shaped layers for UniqueActor sheets.
/// This helper gathers from <see cref="EffectRuntime"/> + <see cref="MatchHost"/> Bound bindings
/// and POSTs via <see cref="RpgClient.PostActorLiveStateAsync"/>. Wired from
/// <see cref="Host.InjectorLoop"/> on a slow cadence (not per-frame).
/// </para>
/// </summary>
public static class ActorLiveStatePush
{
    /// <summary>
    /// Build live-state for one Bound UniqueActor. Returns null when ptr missing or both
    /// statuses and shields are empty (still OK to POST empty for clear — caller decides).
    /// </summary>
    public static ActorLiveStatePayload? TryBuildForBinding(UniqueBinding binding, DateTimeOffset now)
    {
        if (binding.Phase != UniqueBindingPhase.Bound) return null;
        var ptr = MatchUniqueBindingsFacet.NormalizePtr(binding.Ptr ?? "");
        if (string.IsNullOrEmpty(ptr)) return null;

        var statuses = BuildStatuses(TryStatusRuntime(), ptr, now);
        var layers = BuildShieldLayers(TryShieldRuntime(), ptr);
        return new ActorLiveStatePayload(statuses, layers);
    }

    /// <summary>Push every Bound UniqueActor that has a live ptr. Best-effort; never throws.</summary>
    public static void FlushBound(RpgClient? client)
    {
        if (client == null) return;
        try
        {
            var now = ServerClock.UtcNow;
            UniqueBinding[] bindings;
            try { bindings = MatchHost.Runtime.ToSnapshot().Bindings; }
            catch { return; }

            for (var i = 0; i < bindings.Length; i++)
            {
                var b = bindings[i];
                var payload = TryBuildForBinding(b, now);
                if (payload == null) continue;
                // Skip no-op empties to avoid SignalR spam when nothing live is attached.
                if (payload.LiveStatuses.Count == 0 && payload.ShieldLayers.Count == 0) continue;
                _ = client.PostActorLiveStateAsync(b.InstanceId, payload);
            }
        }
        catch
        {
            /* live-state push is enrichment — never fail the injector loop */
        }
    }

    static IReadOnlyList<ActorStatusGlyphDto> BuildStatuses(
        StatusRuntime? runtime, string ptr, DateTimeOffset now)
    {
        if (runtime == null) return Array.Empty<ActorStatusGlyphDto>();
        var instances = runtime.ForHost(ptr);
        if (instances.Count == 0) return Array.Empty<ActorStatusGlyphDto>();

        var list = new List<ActorStatusGlyphDto>(instances.Count);
        for (var i = 0; i < instances.Count; i++)
        {
            var inst = instances[i];
            list.Add(new ActorStatusGlyphDto
            {
                StatusId = inst.StatusId,
                RemainingPermille = RemainingPermille(inst, now)
            });
        }
        return list;
    }

    static int? RemainingPermille(StatusInstance inst, DateTimeOffset now)
    {
        var total = (inst.ExpiresAt - inst.AppliedAt).TotalMilliseconds;
        if (total <= 0) return null;
        var left = (inst.ExpiresAt - now).TotalMilliseconds;
        var pm = (int)Math.Round(1000.0 * Math.Clamp(left / total, 0.0, 1.0));
        return pm;
    }

    static IReadOnlyList<ActorShieldLayerDto> BuildShieldLayers(ShieldRuntime? runtime, string ptr)
    {
        if (runtime == null) return Array.Empty<ActorShieldLayerDto>();
        var ownerKey = EffectOwnerKeys.Entity(ptr);
        var shields = runtime.GetShields(ownerKey);
        if (shields.Count == 0) return Array.Empty<ActorShieldLayerDto>();

        var list = new List<ActorShieldLayerDto>(shields.Count);
        for (var i = 0; i < shields.Count; i++)
        {
            var s = shields[i];
            list.Add(new ActorShieldLayerDto
            {
                ShieldId = s.ShieldId,
                ElementId = s.Element is { } el ? el.ToElementId() : null,
                Current = s.Hp,
                Max = s.MaxHp,
                Priority = s.Priority,
                SourceId = s.SourceId,
                IsInnate = s.IsInnate,
                // RegenPerSecond deferred (shield-sheet D8) — omit until runtime exposes it.
                RegenPerSecond = null,
                Broken = s.Hp <= 0
            });
        }
        return list;
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
}

/// <summary>JSON body for <c>POST /api/internal/actors/{id}/live-state</c> (matches Server bag).</summary>
public sealed class ActorLiveStatePayload
{
    public ActorLiveStatePayload(
        IReadOnlyList<ActorStatusGlyphDto> liveStatuses,
        IReadOnlyList<ActorShieldLayerDto> shieldLayers)
    {
        LiveStatuses = liveStatuses;
        ShieldLayers = shieldLayers;
    }

    public IReadOnlyList<ActorStatusGlyphDto> LiveStatuses { get; }
    public IReadOnlyList<ActorShieldLayerDto> ShieldLayers { get; }
}
