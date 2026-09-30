using FusionRpg.Core.Hud;
using UnityEngine;

namespace FusionRpg.Injector.Hud;

/// <summary>
/// Resolves the visible plant/zombie footprint. The HUD deliberately consumes the union of the
/// actor's qualifying sprite renderers: a composite actor is one visual object, not its largest part.
/// FusionRpg presentation objects are excluded by ownership before this resolver runs.
/// </summary>
internal static class ActorVisualResolver
{
    static int _cacheFrame = -1;
    static readonly Dictionary<int, ActorHudScreenRect?> Cache = new();

    public static bool TryResolveScreenRect(Transform actorRoot, out ActorHudScreenRect rect)
    {
        rect = default;
        EnsureFrameCache();
        var id = actorRoot.GetInstanceID();
        if (Cache.TryGetValue(id, out var cached))
        {
            rect = cached ?? default;
            return cached.HasValue;
        }

        var camera = LawnCameraResolver.Resolve(actorRoot);
        if (camera == null || !TryResolve(actorRoot, camera, out rect))
        {
            Cache[id] = null;
            return false;
        }

        Cache[id] = rect;
        return true;
    }

    static void EnsureFrameCache()
    {
        var frame = Time.frameCount;
        if (frame == _cacheFrame) return;
        _cacheFrame = frame;
        Cache.Clear();
    }

    static bool TryResolve(Transform actorRoot, Camera camera, out ActorHudScreenRect rect)
    {
        rect = default;
        SpriteRenderer[] sprites;
        try { sprites = actorRoot.GetComponentsInChildren<SpriteRenderer>(true); }
        catch { return false; }
        if (sprites == null || sprites.Length == 0) return false;

        var any = false;
        var left = float.PositiveInfinity;
        var bottom = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var top = float.NegativeInfinity;
        foreach (var sprite in sprites)
        {
            if (!IsActorVisual(sprite)) continue;
            if (!TryProjectBounds(camera, sprite.bounds, out var projected)) continue;
            any = true;
            left = Mathf.Min(left, projected.Left);
            bottom = Mathf.Min(bottom, projected.Bottom);
            right = Mathf.Max(right, projected.Left + projected.Width);
            top = Mathf.Max(top, projected.Bottom + projected.Height);
        }

        if (!any) return false;
        var viewport = camera.pixelRect;
        left = Mathf.Max(left, viewport.xMin);
        bottom = Mathf.Max(bottom, viewport.yMin);
        right = Mathf.Min(right, viewport.xMax);
        top = Mathf.Min(top, viewport.yMax);
        if (right <= left || top <= bottom) return false;
        rect = new ActorHudScreenRect(left, bottom, right - left, top - bottom);
        return true;
    }

    static bool IsActorVisual(SpriteRenderer? sprite)
    {
        if (sprite == null) return false;
        try
        {
            if (!sprite.enabled || sprite.sprite == null) return false;
            // The actor HUD canvas is outside the actor hierarchy. This guard additionally prevents
            // any legacy in-tree FusionRpg marker from defining the gameplay actor's silhouette.
            return !sprite.gameObject.name.StartsWith("FusionRpg", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    static bool TryProjectBounds(Camera camera, Bounds bounds, out ActorHudScreenRect rect)
    {
        rect = default;
        var min = bounds.min;
        var max = bounds.max;
        var any = false;
        var left = float.PositiveInfinity;
        var bottom = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var top = float.NegativeInfinity;
        for (var x = 0; x < 2; x++)
        for (var y = 0; y < 2; y++)
        for (var z = 0; z < 2; z++)
        {
            var world = new Vector3(x == 0 ? min.x : max.x, y == 0 ? min.y : max.y, z == 0 ? min.z : max.z);
            Vector3 point;
            try { point = camera.WorldToScreenPoint(world); }
            catch { continue; }
            if (point.z <= 0f) continue;
            any = true;
            left = Mathf.Min(left, point.x);
            bottom = Mathf.Min(bottom, point.y);
            right = Mathf.Max(right, point.x);
            top = Mathf.Max(top, point.y);
        }

        if (!any || right <= left || top <= bottom) return false;
        rect = new ActorHudScreenRect(left, bottom, right - left, top - bottom);
        return true;
    }
}

/// <summary>Finds the gameplay camera by render eligibility; HUD code never relies on a tag lookup.</summary>
internal static class LawnCameraResolver
{
    public static Camera? Resolve(Transform actorRoot)
    {
        Camera[] cameras;
        try { cameras = Camera.allCameras; }
        catch { return null; }
        if (cameras == null) return null;

        Camera? selected = null;
        var selectedDepth = float.NegativeInfinity;
        var layer = actorRoot.gameObject.layer;
        foreach (var camera in cameras)
        {
            if (camera == null) continue;
            try
            {
                if (!camera.enabled || !camera.gameObject.activeInHierarchy) continue;
                if ((camera.cullingMask & (1 << layer)) == 0) continue;
                var point = camera.WorldToScreenPoint(actorRoot.position);
                if (point.z <= 0f || !camera.pixelRect.Contains(new Vector2(point.x, point.y))) continue;
                if (selected == null || camera.depth > selectedDepth)
                {
                    selected = camera;
                    selectedDepth = camera.depth;
                }
            }
            catch { }
        }

        return selected;
    }
}
