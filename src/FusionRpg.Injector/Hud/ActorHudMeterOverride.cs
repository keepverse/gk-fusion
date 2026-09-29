using FusionRpg.Core.Combat;
using FusionRpg.Core.Hud;

namespace FusionRpg.Injector.Hud;

/// <summary>
/// E41 (spec-ui-attach-point.md §2b/§4): per-ptr atom-authored HUD meters — the store
/// <see cref="InjectorUiPresentSink"/> writes into and <see cref="ActorHudBuilder"/> reads out of.
/// Mirrors <see cref="FusionRpg.Injector.Stats.InjectorDerivedOverride"/>'s exact shape (a ptr-keyed
/// Hot cache, no SQL mid-match, cleared on match end) — the same "atom mutates state, HUD reads it"
/// pattern that class already established for derived-channel overrides.
///
/// <para>A meter id is set, never accumulated: the atom's own <c>op:meter</c> executor is set-only
/// (spec §2b's executor table — "an ActorHudMeter on the target's snapshot"), so the last write for a
/// given (ptr, meterId) pair wins, the same "assigns, never accumulates" semantics match.modify's own
/// Board.config fields already carry.</para>
/// </summary>
public static class ActorHudMeterOverride
{
    static readonly object Gate = new();
    static readonly Dictionary<string, Dictionary<string, double>> Meters = new(StringComparer.Ordinal);

    public static void Set(string? ptr, string meterId, double ratio)
    {
        var key = CombatPtr.Normalize(ptr);
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(meterId)) return;
        lock (Gate)
        {
            if (!Meters.TryGetValue(key, out var byId))
            {
                byId = new Dictionary<string, double>(StringComparer.Ordinal);
                Meters[key] = byId;
            }
            byId[meterId] = ratio;
        }
    }

    /// <summary>Ordinal by meter id, so a rebuild is byte-identical run to run — the same discipline
    /// <c>ActorHudShieldStacks.AggregateByElement</c> already applies to shield stacks.</summary>
    public static IReadOnlyList<ActorHudMeter>? TryGet(string? ptr)
    {
        var key = CombatPtr.Normalize(ptr);
        if (string.IsNullOrEmpty(key)) return null;

        lock (Gate)
        {
            if (!Meters.TryGetValue(key, out var byId) || byId.Count == 0) return null;
            return byId
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new ActorHudMeter(kv.Key, kv.Value))
                .ToList();
        }
    }

    public static void Remove(string? ptr)
    {
        var key = CombatPtr.Normalize(ptr);
        if (string.IsNullOrEmpty(key)) return;
        lock (Gate)
            Meters.Remove(key);
    }

    public static void Clear()
    {
        lock (Gate)
            Meters.Clear();
    }
}
