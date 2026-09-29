using FusionRpg.Core.Vfx;
using FusionRpg.Injector.Host;
using FusionRpg.Injector.Lawn;
using UnityEngine;

namespace FusionRpg.Injector.Fx;

/// <summary>
/// Bounds-aware unit frame resolution — vfx-ssot.md §9.1 / spec-unit-frame.md.
/// One read per Transform per frame; consumers must not call BodyWorld or bounds directly.
/// </summary>
public static class UnitFrameResolver
{
    static int _cacheFrame = -1;
    static readonly Dictionary<int, VfxUnitFrame> Cache = new();
    static int _cameraFrame = -1;
    static Camera? _camera;

    /// <summary>Resolve using the unit's board cell for span (plant col/row or zombie col/row).</summary>
    public static VfxUnitFrame Resolve(Transform follow)
    {
        var (col, row) = CellForTransform(follow);
        return Resolve(follow, col, row);
    }

    public static VfxUnitFrame Resolve(Transform follow, int col, int row)
    {
        EnsureFrameCache();
        var id = follow.GetInstanceID();
        if (Cache.TryGetValue(id, out var cached)) return cached;

        var frame = BuildUnitFrame(follow, col, row);
        Cache[id] = frame;
        return frame;
    }

    public static VfxUnitFrame ResolveCell(int col, int row)
    {
        var cellSize = EstimateCellSize(col, row);
        var cellSpan = Mathf.Max(VfxSpanMath.MinSpan, Mathf.Min(cellSize.x, cellSize.y));
        Vector3 center;
        try { center = LawnCoords.CellCenter(col, row); }
        catch { center = Vector3.zero; }

        return new VfxUnitFrame
        {
            PivotX = center.x,
            LaneY = center.y,
            BoundsCenterX = center.x,
            BoundsCenterY = center.y,
            BoundsBottomCenterX = center.x,
            BoundsBottomCenterY = center.y,
            BoundsBottomCenterZ = center.z,
            BoundsWidth = 0f,
            BoundsHeight = 0f,
            CellSpan = cellSpan,
            CellSize = cellSize,
            DepthZ = center.z,
            HasBounds = false,
            SortingOrderHint = 0
        };
    }

    /// <summary>
    /// The sprite's own largest pixel extent — the sanctioned way for an Fx primitive to size itself
    /// against its own texture (the Earth impact stamp converting a world span into a local scale).
    /// <c>Sprite.bounds</c> IS a bounds read, and
    /// <c>Fx_only_UnitFrameResolver_reads_BodyWorld_or_bounds</c> (LawnCoordsGuardTests) reserves
    /// bounds reads in the Fx plane to this one file, so a primitive that needs its own span asks
    /// here rather than reading the sprite itself. Returns 0 for a null sprite or a degenerate
    /// extent, which callers read as "cannot scale" — the same non-positive guard they had inline.
    /// </summary>
    public static float SpriteSpan(Sprite? sprite)
    {
        if (sprite == null) return 0f;
        try
        {
            var size = sprite.bounds.size;
            return Mathf.Max(size.x, size.y);
        }
        catch { return 0f; }
    }

    static (int col, int row) CellForTransform(Transform follow)
    {
        try
        {
            var plant = follow.GetComponent<Plant>() ?? follow.GetComponentInParent<Plant>();
            if (plant != null)
                return (LawnCoords.ClampCol(plant.thePlantColumn), LawnCoords.ClampRow(plant.thePlantRow));
        }
        catch { }

        try
        {
            var zombie = follow.GetComponent<Zombie>() ?? follow.GetComponentInParent<Zombie>();
            if (zombie != null)
            {
                var row = LawnCoords.ClampRow(zombie.theZombieRow);
                var col = CheatState.SpawnCol;
                try
                {
                    var fromX = LawnCoords.ColFromX(zombie.transform.position.x);
                    if (fromX >= 0) col = LawnCoords.ClampCol(fromX);
                }
                catch { }

                try
                {
                    var zCol = zombie.Column;
                    if (zCol >= 0) col = LawnCoords.ClampCol(zCol);
                }
                catch { }

                return (col, row);
            }
        }
        catch { }

        return (CheatState.SpawnCol, CheatState.SpawnRow);
    }

    static void EnsureFrameCache()
    {
        var fc = Time.frameCount;
        if (fc == _cacheFrame) return;
        _cacheFrame = fc;
        Cache.Clear();
    }

    static Camera? RenderCamera()
    {
        var frame = Time.frameCount;
        if (frame == _cameraFrame) return _camera;
        _cameraFrame = frame;
        try { _camera = Camera.main; }
        catch { _camera = null; }
        return _camera;
    }

    /// <summary>
    /// The active render camera, cached once for the current Unity frame. World-space HUDs use its
    /// plane for their screen-relative layout; using global X/Y on the lawn's tilted camera shears
    /// vertical HUD rows sideways across the screen.
    /// </summary>
    internal static Camera? CurrentCamera() => RenderCamera();

    static VfxUnitFrame BuildUnitFrame(Transform follow, int col, int row)
    {
        var cellSize = EstimateCellSize(col, row);
        var cellSpan = Mathf.Max(VfxSpanMath.MinSpan, Mathf.Min(cellSize.x, cellSize.y));

        Vector3 pivot;
        try { pivot = follow.position; }
        catch
        {
            return ResolveCell(col, row);
        }

        Vector3 body;
        try { body = LawnCoords.BodyWorld(follow); }
        catch { body = pivot; }

        var hasBounds = false;
        var boundsCenterX = body.x;
        var boundsCenterY = body.y;
        var boundsBottomCenterX = body.x;
        var boundsBottomCenterY = body.y;
        var boundsBottomCenterZ = pivot.z;
        var boundsW = 0f;
        var boundsH = 0f;
        var sortingOrder = 0;

        // Prefer encapsulated SpriteRenderer bounds — a single first Renderer is often a tiny FX
        // mesh, so "bottom = center - h/2" collapsed to mid-body and HUD sat on faces (LIVE 2026-09-06).
        if (TryCollectSpriteBounds(
                follow, cellSpan,
                out var bx, out var by, out var bottomX, out var bottomY, out var bottomZ,
                out var bw, out var bh, out var sort))
        {
            hasBounds = true;
            boundsCenterX = bx;
            boundsCenterY = by;
            boundsBottomCenterX = bottomX;
            boundsBottomCenterY = bottomY;
            boundsBottomCenterZ = bottomZ;
            boundsW = bw;
            boundsH = bh;
            sortingOrder = sort;
        }

        return new VfxUnitFrame
        {
            PivotX = pivot.x,
            LaneY = body.y,
            BoundsCenterX = boundsCenterX,
            BoundsCenterY = boundsCenterY,
            BoundsBottomCenterX = boundsBottomCenterX,
            BoundsBottomCenterY = boundsBottomCenterY,
            BoundsBottomCenterZ = boundsBottomCenterZ,
            BoundsWidth = boundsW,
            BoundsHeight = boundsH,
            CellSpan = cellSpan,
            CellSize = cellSize,
            DepthZ = pivot.z,
            HasBounds = hasBounds,
            SortingOrderHint = sortingOrder
        };
    }

    /// <summary>
    /// Largest enabled SpriteRenderer under <paramref name="follow"/> (by world area).
    /// First-child Renderer / full-tree encapsulate both failed LIVE: tiny FX → mid-body HUD;
    /// union with shadows/FX → inflated Span. Skips FusionRpg overlays and decoys
    /// (&lt; 35% cell on both axes, or taller than 2.5× cell).
    /// </summary>
    static bool TryCollectSpriteBounds(
        Transform follow, float cellSpan,
        out float centerX, out float centerY, out float bottomCenterX, out float bottomCenterY, out float bottomCenterZ,
        out float width, out float height, out int sortingOrder)
    {
        centerX = centerY = bottomCenterX = bottomCenterY = bottomCenterZ = width = height = 0f;
        sortingOrder = 0;
        try
        {
            var sprites = follow.GetComponentsInChildren<SpriteRenderer>(true);
            if (sprites == null) return false;

            var minAxis = cellSpan * 0.35f;
            var maxH = cellSpan * 2.5f;
            var bestArea = 0f;
            var best = default(Bounds);
            SpriteRenderer? bestRenderer = null;
            var bestOrder = 0;
            var found = false;

            foreach (var sr in sprites)
            {
                if (sr == null) continue;
                try { if (!sr.enabled) continue; } catch { continue; }
                try
                {
                    var n = sr.gameObject.name;
                    if (n != null && n.StartsWith("FusionRpg", StringComparison.Ordinal)) continue;
                }
                catch { }

                Bounds b;
                try { b = sr.bounds; }
                catch { continue; }
                if (b.size.sqrMagnitude < 1e-6f) continue;
                if (b.size.y < minAxis && b.size.x < minAxis) continue;
                if (b.size.y > maxH) continue;

                var area = b.size.x * b.size.y;
                if (area <= bestArea) continue;
                bestArea = area;
                best = b;
                bestRenderer = sr;
                found = true;
                try { bestOrder = sr.sortingOrder; } catch { bestOrder = 0; }
            }

            if (!found) return false;

            centerX = best.center.x;
            centerY = best.center.y;
            var bottom = SpriteVisualBottomCenter(bestRenderer, best);
            bottomCenterX = bottom.x;
            bottomCenterY = bottom.y;
            bottomCenterZ = bottom.z;
            width = best.size.x;
            height = best.size.y;
            sortingOrder = bestOrder;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The sprite's local lower-center transformed into world space. This is the foot-center of the
    /// rendered sprite itself; screen-relative HUD spacing is applied later on the camera plane.
    /// </summary>
    static Vector3 SpriteVisualBottomCenter(SpriteRenderer? renderer, Bounds fallback)
    {
        if (renderer == null) return new Vector3(fallback.center.x, fallback.min.y, fallback.center.z);
        try
        {
            var sprite = renderer.sprite;
            if (sprite == null) return new Vector3(fallback.center.x, fallback.min.y, fallback.center.z);
            var b = sprite.bounds;
            return renderer.transform.TransformPoint(new Vector3(b.center.x, b.min.y, b.center.z));
        }
        catch
        {
            return new Vector3(fallback.center.x, fallback.min.y, fallback.center.z);
        }
    }

    internal static Vector2 EstimateCellSize(int col, int row)
    {
        try
        {
            var a = LawnCoords.CellCenter(col, row);
            var col2 = col >= LawnCoords.LastCol ? Math.Max(0, col - 1) : col + 1;
            var row2 = row >= LawnCoords.LastRow ? Math.Max(0, row - 1) : row + 1;
            var b = LawnCoords.CellCenter(col2, row);
            var c = LawnCoords.CellCenter(col, row2);
            var w = Mathf.Abs(b.x - a.x);
            var h = Mathf.Abs(c.y - a.y);
            if (w < 0.05f) w = 1f;
            if (h < 0.05f) h = 1f;
            return new Vector2(w, h);
        }
        catch
        {
            return Vector2.one;
        }
    }
}
