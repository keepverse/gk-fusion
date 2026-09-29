using FusionRpg.Core.Combat;
using FusionRpg.Core.Diagnostics;
using FusionRpg.Injector.Lawn;
using UnityEngine;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// Lawn census → Core <see cref="BoardSnapshot"/> for TargetResolver.
/// Two layers keep this off the ~10ms <c>FindObjectsOfType</c> path (b2-live-x2 baseline):
/// snapshots are cached per rendered frame (lifecycle hooks call <see cref="Invalidate"/> so a
/// same-frame spawn/death stays visible), and entities come from the hook-fed
/// <see cref="InjectorEntityRegistry"/> — a full scan runs only on the registry's throttled
/// resync. Never add an uncached scan on the damage path.
/// </summary>
public static class InjectorBoardSnapshot
{
    static BoardSnapshot? _cached;
    static int _cachedFrame = -1;

    public static BoardSnapshot Capture()
    {
        int frame;
        try { frame = Time.frameCount; }
        catch { frame = -1; }
        if (frame >= 0 && frame == _cachedFrame && _cached != null)
            return _cached;

        var snap = CaptureUncached(frame);
        _cached = snap;
        _cachedFrame = frame;
        return snap;
    }

    /// <summary>Drop the per-frame cache after board mutations (spawn/death/board swap).</summary>
    public static void Invalidate()
    {
        _cached = null;
        _cachedFrame = -1;
    }

    static BoardSnapshot CaptureUncached(int frame)
    {
        using var _perf = PerfProbe.Measure(PerfSection.BoardCapture);
        if (InjectorEntityRegistry.NeedsResync(frame))
            InjectorEntityRegistry.Resync(frame);

        // v3 A4: entries carry cached immutable snaps — plants once per lifetime, zombies
        // refreshed on a throttle — so capture cost no longer scales with interop reads per
        // entity per freeze. The BoardSnapshot instance itself stays immutable as before.
        var list = new List<BoardEntitySnap>(InjectorEntityRegistry.PlantCount + InjectorEntityRegistry.ZombieCount);
        InjectorEntityRegistry.CollectSnaps(list);
        return new BoardSnapshot(list);
    }
}
