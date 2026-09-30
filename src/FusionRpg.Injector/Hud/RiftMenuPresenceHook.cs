#if FUSIONRPG_MELON
using HarmonyLib;
using FusionRpg.Core.Overlay;
using FusionRpg.Injector.Host;

namespace FusionRpg.Injector.Hud;

/// <summary>
/// The menu-screen **presence** signal (map Decision 17): "the PVZ main menu exists right now".
///
/// It patches the menu screen's own **lifecycle**, never a navigation static. <c>UIMgr.EnterMainMenu</c>
/// / <c>BackToMenu</c> are forbidden — they are a documented live hazard and calling them mid-run
/// breaks a live run. This class only *observes*: `Start` on the screen, and the base menu's own
/// hide/exit. Nothing here changes what the player sees; switching the WebView is the whole job.
///
/// Melon-only: the game types nest under <c>Il2Cpp</c> only on the MelonLoader host (BepInEx exposes
/// no 3.9 <c>Assembly-CSharp</c> interop on this machine — verified). On BepInEx the tombstone does
/// not exist and F10 remains the entry.
///
/// Verified against the 3.9 interop: <c>MainMenu.Start</c> is a real, **private** method (token
/// 100674800), so it is patched by string name; <c>BaseMenu</c> declares <c>OnHide</c>/<c>OnExit</c>
/// as virtual and declares no <c>OnDestroy</c>, so the hide/exit callbacks are the clearance path.
/// </summary>
public static class RiftMenuPresenceHook
{
    /// <summary>Read by the tombstone's placement path. Must stay allocation-free.</summary>
    internal static readonly RiftMenuAnchor Anchor = new();

    static bool _loggedStart;
    static bool _loggedClear;

    /// <summary>
    /// The screen's own <c>Start</c> ran — the menu object now exists. A Presence fact, not a
    /// navigation call. <c>Start</c> is private in 3.9, hence the string name.
    /// </summary>
    [HarmonyPatch(typeof(MainMenu), "Start")]
    public static class MainMenuStart
    {
        // ⚠️ PUBLIC, deliberately. Every one of the other ~106 Harmony patches in this injector
        // (GameHooks 34, GameCaptureHooks 72) declares `public static class` + `public static void
        // Postfix`, and so does the reference mod that proves this exact target works
        // (darkthemer/PvZF_MainMenuFlowers, `[HarmonyPatch(typeof(MainMenu), "Start")]`). These three
        // were the only private ones in the assembly and the postfix never fired — the menu loaded,
        // the game ran at 60 fps, and no `[rift]` line was ever logged. Matching the shape that works
        // everywhere else is the fix; do not re-narrow the accessibility to "tidy" it.
        public static void Postfix(MainMenu __instance)
        {
            Anchor.Set(RiftMenuSurface.MainMenu);
            if (!_loggedStart)
            {
                _loggedStart = true;
                RpgHost.Log.Info("[rift] main menu present (presence signal set, observe-only)");
            }

            // Decision 17/18: attach the uGUI affordance into the menu's own hierarchy. This is the
            // only place the tombstone is built, and it is an attach — never a navigation call.
            RiftMenuTombstone.Attach(__instance.transform);
        }
    }

    /// <summary>
    /// The screen is hidden/exited. Guarded by the instance type so a **submenu** hiding cannot clear
    /// a main menu that is still up — the load-bearing half of the signal.
    /// </summary>
    [HarmonyPatch(typeof(BaseMenu), nameof(BaseMenu.OnHide))]
    public static class MenuHidden
    {
        public static void Postfix(BaseMenu __instance)
        {
            if (__instance is not MainMenu) return;
            Anchor.Clear(RiftMenuSurface.MainMenu);
            RiftMenuTombstone.OnMenuGone();
            LogClearOnce("OnHide");
        }
    }

    /// <summary>Exit path — same guard, same clearance.</summary>
    [HarmonyPatch(typeof(BaseMenu), nameof(BaseMenu.OnExit))]
    public static class MenuExited
    {
        public static void Postfix(BaseMenu __instance)
        {
            if (__instance is not MainMenu) return;
            Anchor.Clear(RiftMenuSurface.MainMenu);
            RiftMenuTombstone.OnMenuGone();
            LogClearOnce("OnExit");
        }
    }

    static void LogClearOnce(string via)
    {
        if (_loggedClear) return;
        _loggedClear = true;
        RpgHost.Log.Info($"[rift] main menu gone ({via}) — presence signal cleared");
    }
}
#endif
