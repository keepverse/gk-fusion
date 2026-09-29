using FusionRpg.Core.Lawn;
using UnityEngine;

namespace FusionRpg.Injector.Lawn;

/// <summary>
/// Injector lawn XY adapter. Pure math is <see cref="LawnCoordMath"/>; cell centers come from Mouse.GetBox*.
/// Phaser gridMath.ts is FE-only.
/// </summary>
public static class LawnCoords
{
    public const int DefaultLastCol = LawnCoordMath.DefaultLastCol;
    public const int DefaultLastRow = LawnCoordMath.DefaultLastRow;

    public static int LastCol
    {
        get
        {
            try
            {
                var board = GameHooks.Board ?? Board.Instance;
                var gs = board?.gridSystem;
                if (gs != null)
                {
                    var n = gs.ColumnCount;
                    if (n > 0) return n - 1;
                }
            }
            catch { }

            return DefaultLastCol;
        }
    }

    public static int LastRow
    {
        get
        {
            try
            {
                var board = GameHooks.Board ?? Board.Instance;
                var gs = board?.gridSystem;
                if (gs != null)
                {
                    var n = gs.RowCount;
                    if (n > 0) return n - 1;
                }
            }
            catch { }

            return DefaultLastRow;
        }
    }

    public static int ClampCol(int col) => LawnCoordMath.ClampIndex(col, LastCol);
    public static int ClampRow(int row) => LawnCoordMath.ClampIndex(row, LastRow);

    /// <summary>Lane cell center. Never follows a living transform (walking zombie X / feet Y).</summary>
    public static Vector2 CellCenter(int col, int row)
    {
        col = ClampCol(col);
        row = ClampRow(row);
        try
        {
            var mouse = Mouse.Instance;
            if (mouse != null)
                return new Vector2(mouse.GetBoxXFromColumn(col), mouse.GetBoxYFromRow(row));
        }
        catch { }

        return new Vector2(col, row);
    }

    /// <returns>Column from world X, or -1 if Mouse is missing.</returns>
    public static int ColFromX(float x)
    {
        try
        {
            var mouse = Mouse.Instance;
            if (mouse != null) return mouse.GetColumnFromX(x);
        }
        catch { }

        return -1;
    }

    public static float HalfCellY
    {
        get
        {
            try
            {
                var mouse = Mouse.Instance;
                if (mouse != null)
                    return LawnCoordMath.HalfCellY(mouse.GetBoxYFromRow(0), mouse.GetBoxYFromRow(1));
            }
            catch { }

            return 0.5f;
        }
    }

    /// <summary>
    /// Floater follow: lane Y from Mouse box row, X from transform (walker).
    /// Fallback: renderer center, then feet + half-cell.
    /// </summary>
    public static Vector3 BodyWorld(Transform t)
    {
        if (t == null) return Vector3.zero;
        var p = t.position;
        if (TryLaneY(t, out var laneY))
            return new Vector3(p.x, laneY, p.z);
        if (TryRendererCenter(t, out var center)) return center;
        return new Vector3(p.x, p.y + HalfCellY, p.z);
    }

    /// <summary>Row membership for a zombie — row semantics stay here, not in callers.</summary>
    public static bool ZombieInRow(Zombie z, int row) => z != null && z.theZombieRow == row;

    public static bool TryWorldToGui(Camera cam, Vector3 world, float t, out Vector2 gui)
    {
        gui = default;
        if (cam == null) return false;
        Vector3 sp;
        try { sp = cam.WorldToScreenPoint(world); }
        catch { return false; }
        if (sp.z < 0f) return false;
        var rect = cam.pixelRect;
        var pt = LawnCoordMath.GuiPoint(Screen.height, sp.x, sp.y, rect.x, rect.y, t);
        gui = new Vector2(pt.X, pt.Y);
        return true;
    }

    static bool TryLaneY(Transform t, out float y)
    {
        y = 0f;
        try
        {
            var z = t.GetComponent<Zombie>() ?? t.GetComponentInParent<Zombie>();
            if (z != null)
            {
                var mouse = Mouse.Instance;
                if (mouse == null) return false;
                y = mouse.GetBoxYFromRow(z.theZombieRow);
                return true;
            }
        }
        catch { }

        try
        {
            var plant = t.GetComponent<Plant>() ?? t.GetComponentInParent<Plant>();
            if (plant != null)
            {
                var mouse = Mouse.Instance;
                if (mouse == null) return false;
                y = mouse.GetBoxYFromRow(plant.thePlantRow);
                return true;
            }
        }
        catch { }

        return false;
    }

    static bool TryRendererCenter(Transform t, out Vector3 center)
    {
        center = default;
        Renderer? r = null;
        try { r = t.GetComponent<Renderer>(); } catch { }
        if (r == null)
        {
            try { r = t.GetComponentInChildren<Renderer>(); } catch { }
        }

        if (r == null) return false;
        Bounds b;
        try { b = r.bounds; }
        catch { return false; }
        if (b.size.sqrMagnitude < 1e-6f) return false;
        center = b.center;
        return true;
    }
}
