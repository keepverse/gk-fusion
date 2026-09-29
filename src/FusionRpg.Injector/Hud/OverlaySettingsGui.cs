using FusionRpg.Core.Overlay;
using UnityEngine;

namespace FusionRpg.Injector.Hud;

/// <summary>
/// Lightweight presentation settings panel (F7). Does not host gameplay cheats.
/// </summary>
public static class OverlaySettingsGui
{
    // Config-backed (tunables-ssot.md T1) — gk-core/data/tuning/overlay.v1.json's settingsGui.
    static float PanelW => OverlayTuningHub.Tuning.SettingsGui.PanelW;
    static float PanelH => OverlayTuningHub.Tuning.SettingsGui.PanelH;

    public static void Draw()
    {
        try
        {
            // Previously also skipped every event except Repaint/Layout/MouseDown/MouseUp — meant to
            // cut per-frame cost, but this panel is only drawn while F7 is open, so that cost is
            // already avoided by the SettingsOpen check above. Filtering event types before an
            // interactive GUI.Toggle/GUI.Button is the classic Unity IMGUI footgun: a click that
            // moves even one pixel between mouse-down and mouse-up fires a MouseDrag event, and
            // dropping it desynced GUIUtility's hot-control tracking for whichever toggle had focus —
            // an intermittent, unreliable click, reported as "Pause while away won't turn off."
            if (!OverlaySettings.SettingsOpen) return;
            if (Event.current == null) return;
        }
        catch
        {
            return;
        }

        try
        {
            var x = 16f;
            var y = 16f;
            GUI.Box(new Rect(x, y, PanelW, PanelH), "Overlay Settings");
            var cy = y + 28f;
            var on = OverlaySettings.ShieldBarEnabled;
            var next = GUI.Toggle(new Rect(x + 12f, cy, PanelW - 24f, 22f), on, " Shield bar");
            if (next != on)
                OverlaySettings.ShieldBarEnabled = next;
            cy += 28f;
            var btn = OverlaySettings.OverlayButtonEnabled;
            var btnNext = GUI.Toggle(new Rect(x + 12f, cy, PanelW - 24f, 22f), btn, " Web UI button");
            if (btnNext != btn)
                OverlaySettings.OverlayButtonEnabled = btnNext;
            cy += 28f;
            var pause = OverlaySettings.PauseWhileAway;
            var pauseNext = GUI.Toggle(new Rect(x + 12f, cy, PanelW - 24f, 22f), pause, " Pause while away");
            if (pauseNext != pause)
                OverlaySettings.PauseWhileAway = pauseNext;
            cy += 28f;
            GUI.Label(new Rect(x + 12f, cy, PanelW - 24f, 20f),
                "Toggle bar: " + OverlaySettings.ShieldBarHotKey);
            cy += 22f;
            GUI.Label(new Rect(x + 12f, cy, PanelW - 24f, 20f),
                "This panel: " + OverlaySettings.SettingsHotKey);
            cy += 28f;
            if (GUI.Button(new Rect(x + 12f, cy, 80f, 24f), "Close"))
                OverlaySettings.SettingsOpen = false;
        }
        catch { }
    }
}
