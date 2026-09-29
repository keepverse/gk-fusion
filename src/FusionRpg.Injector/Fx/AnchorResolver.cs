using FusionRpg.Injector.Effects;
using UnityEngine;

namespace FusionRpg.Injector.Fx;

/// <summary>
/// ptr → Transform via the hook-fed <see cref="InjectorEntityRegistry"/> — vfx-ssot.md §9.
/// VFX owns no cache and no scene scan; a miss triggers the registry's own frame-throttled
/// resync (mid-match attach backstop), so worst case is one shared scan per ~1024 frames.
/// </summary>
public static class AnchorResolver
{
    public static Transform? Resolve(string? ptr)
    {
        if (string.IsNullOrWhiteSpace(ptr)) return null;
        try
        {
            var t = Lookup(ptr);
            if (t != null) return t;

            var frame = Time.frameCount;
            if (!InjectorEntityRegistry.NeedsResync(frame)) return null;
            InjectorEntityRegistry.Resync(frame);
            return Lookup(ptr);
        }
        catch
        {
            // Destroyed native objects mid-lookup — a miss, never a throw into the loop.
            return null;
        }
    }

    static Transform? Lookup(string ptr)
    {
        var z = InjectorEntityRegistry.FindZombie(ptr);
        if (z != null) return z.transform;
        var p = InjectorEntityRegistry.FindPlant(ptr);
        return p != null ? p.transform : null;
    }
}
