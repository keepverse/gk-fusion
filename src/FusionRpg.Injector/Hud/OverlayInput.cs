using UnityEngine;

namespace FusionRpg.Injector.Hud;

/// <summary>
/// Edge-detect overlay hotkeys on the Update thread — never inside OnGUI.
/// </summary>
public static class OverlayInput
{
    public static void Tick()
    {
        try
        {
            if (KeyDown(OverlaySettings.SettingsHotKey))
                OverlaySettings.ToggleSettingsOpen();
            if (KeyDown(OverlaySettings.ShieldBarHotKey))
                OverlaySettings.ToggleShieldBar();
        }
        catch { }
    }

    static bool KeyDown(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!Enum.TryParse(typeof(KeyCode), name.Trim(), true, out var boxed) || boxed is not KeyCode code)
            return false;
        return Input.GetKeyDown(code);
    }
}
