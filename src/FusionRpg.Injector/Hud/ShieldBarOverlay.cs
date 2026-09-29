namespace FusionRpg.Injector.Hud;

/// <summary>
/// Status helper for debug.shield.bar-status — drawing lives in <see cref="ActorHudPool"/> resource row.
/// </summary>
public static class ShieldBarOverlay
{
    /// <summary>Retired — shield bar is world HUD under VfxDirector, not OnGUI.</summary>
    public static void Draw() { }

    public static Dictionary<string, object> CaptureStatus() => ActorHudDirector.CaptureStatus();
}
