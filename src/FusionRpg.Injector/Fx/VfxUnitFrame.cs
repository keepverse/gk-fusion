using FusionRpg.Core.Vfx;
using UnityEngine;

namespace FusionRpg.Injector.Fx;

/// <summary>
/// Snapshot of a unit or cell anchor+scale basis — resolved once per host per frame via
/// <see cref="UnitFrameResolver"/>. Primitives call <see cref="World"/> + <see cref="Span"/> only.
/// </summary>
public readonly struct VfxUnitFrame
{
    public float PivotX { get; init; }
    public float LaneY { get; init; }
    public float BoundsCenterX { get; init; }
    public float BoundsCenterY { get; init; }
    /// <summary>Selected sprite's transformed local bottom-center. Unlike <see cref="BoundsCenterY"/>,
    /// this remains the visual lower center when the lawn camera is rotated.</summary>
    public float BoundsBottomCenterX { get; init; }
    public float BoundsBottomCenterY { get; init; }
    public float BoundsBottomCenterZ { get; init; }
    public float BoundsWidth { get; init; }
    public float BoundsHeight { get; init; }
    public float CellSpan { get; init; }
    public Vector2 CellSize { get; init; }
    public float DepthZ { get; init; }
    public bool HasBounds { get; init; }
    public int SortingOrderHint { get; init; }

    public Vector3 World(VfxAnchorKind kind)
    {
        var halfCell = CellSpan * 0.5f;
        var boundsHalfH = BoundsHeight * 0.5f;
        var x = VfxUnitFrameMath.WorldX(PivotX, BoundsCenterX, HasBounds);
        var y = VfxUnitFrameMath.WorldY(LaneY, BoundsCenterY, halfCell, boundsHalfH, HasBounds, kind);
        var lift = (float)VfxTuningHub.Tuning.Render.SustainedWorldYOffset;
        // Crown already targets the upper sprite band — global lift is for body/feet auras only.
        if (lift != 0f && kind is VfxAnchorKind.Feet or VfxAnchorKind.Body)
        {
            var factor = kind == VfxAnchorKind.Feet ? 0.5f : 1f;
            y += lift * factor;
        }

        return new Vector3(x, y, DepthZ);
    }

    public float Span(float recipeSizeScale = 1f) =>
        VfxSpanMath.ComputeSpan(
            CellSpan, BoundsWidth, BoundsHeight, HasBounds,
            (float)VfxTuningHub.Tuning.Sustained.SpanScale, recipeSizeScale);

    public int ParticleSortingOrder
    {
        get
        {
            var baseOrder = VfxTuningHub.Tuning.Render.ParticleSortingOrder;
            var offset = VfxTuningHub.Tuning.Render.SortOffsetAboveUnit;
            if (SortingOrderHint != 0)
                return Math.Max(baseOrder, SortingOrderHint + offset);
            return baseOrder + offset;
        }
    }
}
