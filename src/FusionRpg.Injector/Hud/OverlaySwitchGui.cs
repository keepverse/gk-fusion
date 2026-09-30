using FusionRpg.Core.Overlay;
using UnityEngine;

namespace FusionRpg.Injector.Hud;

/// <summary>
/// The in-match "switch to the web UI" button. One action only — it is not a cheats surface.
///
/// Per rift-gate decision 15 this is the **one live-lawn affordance**, restyled to the tombstone
/// treatment. Per decision 18 it **stays IMGUI**: it is match HUD chrome drawn over a live board, not
/// part of a menu hierarchy, so it deliberately does not join the uGUI menu art.
///
/// Interactive IMGUI cannot use the Repaint-only gate the floaters use: the control has to be drawn
/// on the Layout and mouse passes too or it never receives input. Same event filter as
/// <see cref="OverlaySettingsGui"/>, and the cost is held down to one cached rect and one button.
/// </summary>
public static class OverlaySwitchGui
{
    static Rect _rect;
    static int _rectW = -1;
    static int _rectH = -1;

    /// <summary>
    /// The tombstone treatment. Presentation constants, not balance numbers: they exist so the control
    /// reads as the same Rift affordance as the menu tombstone, and no balance pass would move them.
    /// </summary>
    static readonly Color TombstoneControlTint = new(0.43f, 0.16f, 0.82f, 1f);
    static readonly Color TombstoneLabelTint = new(0.90f, 1f, 0.72f, 1f);

    public static void Draw()
    {
        try
        {
            if (!OverlaySwitch.ButtonVisible) return;
            var e = Event.current;
            if (e == null) return;
            if (e.type != EventType.Repaint
                && e.type != EventType.Layout
                && e.type != EventType.MouseDown
                && e.type != EventType.MouseUp)
                return;
        }
        catch
        {
            return;
        }

        try
        {
            EnsureLayout();

            // Decision 15: the tombstone treatment, applied as a tint so the control itself is the
            // same one button. The default skin's control colour is replaced with the rift violet and
            // restored in a finally, so this cannot leak into any other GUI drawn in the same pass.
            var priorBackground = GUI.backgroundColor;
            var priorColor = GUI.contentColor;
            try
            {
                GUI.backgroundColor = TombstoneControlTint;
                GUI.contentColor = TombstoneLabelTint;

                // Still exactly ONE action: open/close the web FE through the shared request path.
                if (GUI.Button(_rect, "RPG"))
                    OverlaySwitch.RequestToggle();
            }
            finally
            {
                GUI.backgroundColor = priorBackground;
                GUI.contentColor = priorColor;
            }
        }
        catch { }
    }

    /// <summary>Bottom-right, clear of the seed bank and the shovel slot. Recomputed only on resize.</summary>
    static void EnsureLayout()
    {
        var w = Screen.width;
        var h = Screen.height;
        if (w == _rectW && h == _rectH) return;

        _rectW = w;
        _rectH = h;
        var r = OverlaySwitchLayout.BottomRight(w, h); // scales with the display; see Core tests
        _rect = new Rect(r.X, r.Y, r.Width, r.Height);
    }
}
