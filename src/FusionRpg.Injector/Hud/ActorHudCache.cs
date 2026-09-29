using FusionRpg.Core.Combat;
using FusionRpg.Core.Hud;

namespace FusionRpg.Injector.Hud;

/// <summary>
/// Per-ptr HUD snapshot cache. A read rebuilds only when the ptr has no entry or has been marked
/// dirty; the dirty set drives both the rebuild and the observe delta emit.
///
/// <para>⚡ <b>It used to rebuild on every read</b> ("always rebuilds on observe read; dirty set drives
/// delta emit only"), which was invisible while the only reader was observe, and very visible once
/// <see cref="ActorHudPool.TickSync"/> became a per-frame reader of every live plant and zombie: at
/// ~290 zombies that is ~290 full <see cref="ActorHudBuilder.Build"/> calls per frame, each one
/// re-reading the binding table, the derived override, the shield runtime and the status runtime for
/// an actor whose HUD had not changed. Measured 2026-09-16: <c>vfx.tick</c> (which contains the HUD
/// walk) ran <b>2,098 ms per five-second window with the HUD on against 5 ms with it off</b>, i.e. 42%
/// of wall against the vfx-v2 spec's own locked budget of ≤0.5%.</para>
///
/// <para><b>Why the dirty set is sufficient.</b> Every input the snapshot reads has a producer that
/// marks the ptr dirty: status apply/end (<see cref="ActorHudInvalidator"/>), scope membership
/// (<c>MatchHost.Runtime.MembershipChanged</c>), stat apply (<c>EntityApply</c>, both sites), shield
/// events (<c>EffectRuntime.FlushShieldEvents</c>), meter writes
/// (<c>InjectorUiPresentSink.SetMeter</c>), unique marking (<c>GameCaptureHooks</c>) and the debug
/// cheat paths. Death calls <see cref="Remove"/>; match end calls <see cref="Clear"/>. A miss is
/// therefore a bug in a producer, not a reason to rebuild blindly — and
/// <see cref="ReconcileDirty"/> still runs once a frame as the fallback the dump spec requires.</para>
/// </summary>
public static class ActorHudCache
{
    static readonly object Gate = new();
    static readonly Dictionary<string, ActorHudSnapshot> Cache = new(StringComparer.Ordinal);
    static readonly HashSet<string> Dirty = new(StringComparer.Ordinal);

    /// <summary>Production builder — set once from <see cref="ActorHudInvalidator.Install"/>.</summary>
    public static Func<string, ActorHudSnapshot>? Build { get; set; }

    /// <summary>Optional delta observe hook — wired by <see cref="ActorHudInvalidator"/>.</summary>
    public static Action<string, Dictionary<string, object>>? DeltaEmit { get; set; }

    public static ActorHudSnapshot? GetOrBuild(string? ptrHex)
    {
        var ptr = CombatPtr.Normalize(ptrHex);
        if (string.IsNullOrEmpty(ptr)) return null;

        lock (Gate)
        {
            var wasDirty = Dirty.Remove(ptr);
            if (!wasDirty && Cache.TryGetValue(ptr, out var cached))
                return cached;

            var built = BuildSnapshot(ptr);
            Cache[ptr] = built;

            if (wasDirty)
                TryEmitDelta(ptr, built);

            return built;
        }
    }

    public static void MarkDirty(string? ptrHex)
    {
        var ptr = CombatPtr.Normalize(ptrHex);
        if (string.IsNullOrEmpty(ptr)) return;
        lock (Gate)
            Dirty.Add(ptr);
    }

    public static void Remove(string? ptrHex)
    {
        var ptr = CombatPtr.Normalize(ptrHex);
        if (string.IsNullOrEmpty(ptr)) return;
        lock (Gate)
        {
            Cache.Remove(ptr);
            Dirty.Remove(ptr);
        }

        ActorHudUniqueFlags.Remove(ptr);
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Cache.Clear();
            Dirty.Clear();
        }

        ActorHudUniqueFlags.Clear();
    }

    /// <summary>Fallback tick — reconcile at most one dirty ptr per frame.</summary>
    public static void ReconcileDirty()
    {
        string? ptr;
        lock (Gate)
        {
            if (Dirty.Count == 0) return;
            ptr = Dirty.First();
        }

        GetOrBuild(ptr);
    }

    static ActorHudSnapshot BuildSnapshot(string ptr) =>
        Build?.Invoke(ptr)
        ?? throw new InvalidOperationException(
            "ActorHudCache.Build is not wired. Call ActorHudInvalidator.Install() during bootstrap.");

    static void TryEmitDelta(string ptr, ActorHudSnapshot snapshot)
    {
        try
        {
            DeltaEmit?.Invoke(ptr, ActorHudWireSerializer.ToDictionary(snapshot));
        }
        catch { /* observe must not block gameplay */ }
    }
}
