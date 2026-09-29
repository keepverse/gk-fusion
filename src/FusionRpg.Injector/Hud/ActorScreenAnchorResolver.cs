using FusionRpg.Core.Hud;
using UnityEngine;

namespace FusionRpg.Injector.Hud;

/// <summary>Unity adapter from actor silhouette to the pure, top-centred HUD screen anchor.</summary>
internal static class ActorScreenAnchorResolver
{
    public static bool TryResolve(
        Transform actorRoot,
        ActorHudTuning tuning,
        out ActorHudScreenRect visual,
        out ActorHudScreenAnchor anchor)
    {
        visual = default;
        anchor = default;
        return ActorVisualResolver.TryResolveScreenRect(actorRoot, out visual)
            && ActorHudScreenAnchorMath.TryResolve(
                visual,
                (float)tuning.ScreenGapPixels,
                (float)tuning.ScreenWidthFactor,
                (float)tuning.ScreenMinWidthPixels,
                (float)tuning.ScreenMaxWidthPixels,
                out anchor);
    }
}
