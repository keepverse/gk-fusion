using System.Net;
using System.Net.Http;
using System.Reflection;
using HarmonyLib;

namespace FusionRpg.Injector.Host;

/// <summary>
/// Host-agnostic boot after RpgHost.Initialize: client, Harmony SafePatchAll, cheat/effect init.
/// Tick loop is started by each host (MonoBehaviour or OnUpdate).
/// </summary>
public static class InjectorBootstrap
{
    static int _combatHitPatchState; // 0=pending, 1=ok, 2=fail

    public static void Start()
    {
        if (!RpgHost.IsInitialized)
            throw new InvalidOperationException("Call RpgHost.Initialize before InjectorBootstrap.Start");

        try
        {
            HttpClient.DefaultProxy = new WebProxy();
            RpgHost.Log.Info("HTTP proxy disabled (IL2CPP WinHTTP workaround)");
        }
        catch (Exception ex)
        {
            RpgHost.Log.Warning("DefaultProxy: " + ex.Message);
        }

        RpgHost.Harmony = new HarmonyLib.Harmony(RpgHost.HarmonyId);
        RpgHost.Client = new RpgClient(RpgHost.ServerUrl);

        var pluginDir = RpgHost.PluginDir;
        if (string.IsNullOrEmpty(pluginDir))
            pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";

        CheatState.Init(pluginDir);
        CheatState.MenuEnabled = false;
        CheatState.MenuOpen = false;
        try { Hud.OverlaySettings.Init(pluginDir); }
        catch (Exception ex) { RpgHost.Log.Warning("OverlaySettings: " + ex.Message); }
        CheatState.PersistEnabled = RpgHost.Config.PersistCheats;
        if (RpgHost.Config.PersistCheats) CheatState.TryLoad();
        CheatState.MenuEnabled = false;
        CheatState.MenuOpen = false;

        try { Effects.EffectRuntime.Ensure(); }
        catch (Exception ex) { RpgHost.Log.Warning("EffectRuntime: " + ex.Message); }

        try { Hud.ActorHudInvalidator.Install(); }
        catch (Exception ex) { RpgHost.Log.Warning("ActorHudInvalidator: " + ex.Message); }

        SafePatchAll();
        RpgHost.Log.Info("FusionRpg injector loaded, game=" + RpgHost.GameProfileId + " server=" + RpgHost.ServerUrl + " cheats=web-only overlay-hud=on");
    }

    public static void SafePatchAll()
    {
        var harmony = RpgHost.Harmony;
        if (harmony == null) return;

        Type[] types;
        try { types = typeof(InjectorBootstrap).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray()!;
        }
        var ok = 0;
        var fail = 0;
        foreach (var type in types)
        {
            // Defer Bullet.Hit* — patching them at chainloader time makes Il2Cpp trampolines
            // run during other mods' AssetBundle load with invalid Plant ptrs (Handle not initialized / AV).
            if (IsDeferredCombatHitPatch(type)) continue;
            // ⛔ Rift menu hide/exit — see IsRiftMenuVirtualPatch. Default OFF: they crash boot.
            if (IsRiftMenuVirtualPatch(type)) continue;
            try
            {
                var patched = harmony.CreateClassProcessor(type).Patch();
                if (patched != null && patched.Count > 0) ok++;
            }
            catch (Exception ex)
            {
                fail++;
                RpgHost.Log.Error($"[Harmony] {type.Name}: {ex.Message}");
                RpgHost.Client?.Enqueue("patch.failed", new Dictionary<string, object>
                {
                    ["typeName"] = type.Name,
                    ["error"] = ex.Message
                });
            }
        }
        RpgHost.Log.Info($"[Harmony] ok={ok} fail={fail} (Hit* deferred until board.start — HitLand only by default)");
    }

    static bool IsDeferredCombatHitPatch(Type type) =>
        type == typeof(GameHooks.BulletHitZombie)
        || type == typeof(GameHooks.BulletHitPlant)
        || type == typeof(GameHooks.BulletHitLand)
        || type == typeof(GameHooks.ZombieAttackPlant);

    /// <summary>
    /// ⛔ <b>The rift menu's two VIRTUAL patches are skipped by default — they crash the game at boot.</b>
    ///
    /// <para>Reproduced live 2026-09-16 on the merged tree: the process dies about one second after
    /// <c>FusionRpg MelonMod host ready</c> with Windows Application event 1000, exception code
    /// <b>0xc00000fd — stack overflow</b>, and no managed log line after it. The log shows
    /// <c>Internal_SceneLoaded</c> hooked but <b>never fired</b>, and no <c>[rift]</c> line at all, so
    /// the crash lands before the first scene — <c>MainMenu.Start</c> and the tombstone attach never
    /// run and are not the cause.</para>
    ///
    /// <para><b>What is.</b> <c>RiftMenuPresenceHook</c> patches <c>BaseMenu.OnHide</c> and
    /// <c>BaseMenu.OnExit</c>, which that file's own header records as <b>virtual</b>. Patching a
    /// virtual Il2Cpp method rewrites the vtable slot, and the trampoline Harmony installs to reach the
    /// "original" re-enters through that same slot — unbounded recursion the instant the game's boot
    /// calls hide/exit on any menu. A stack overflow is not caught by a <c>try</c>: it takes the
    /// process down with no unwind, which is exactly the silent death observed.</para>
    ///
    /// <para><c>MainMenu.Start</c> is <b>not</b> skipped: it is a private, non-virtual method, so it has
    /// no vtable slot to recurse through, and the presence signal it sets is the half that works.</para>
    ///
    /// <para>⚠️ This is a <b>containment</b>, not the repair. With hide/exit unpatched the presence
    /// signal never clears, so the tombstone is not rebuilt after the menu goes away — a correctness
    /// gap the rift-gate program owns, and one that needs a non-virtual clearance path (a liveness
    /// check on the anchored transform, or a concrete override if <c>MainMenu</c> declares one).
    /// <c>FUSIONRPG_RIFT_MENU_VIRTUAL=1</c> re-enables them for anyone bisecting this again; it exists
    /// to make the crash reproducible on demand, never to ship.</para>
    /// </summary>
    static bool IsRiftMenuVirtualPatch(Type type)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("FUSIONRPG_RIFT_MENU_VIRTUAL"), "1", StringComparison.Ordinal))
            return false;

        var name = type.FullName ?? "";
        return name.Contains("RiftMenuPresenceHook+MenuHidden", StringComparison.Ordinal)
            || name.Contains("RiftMenuPresenceHook+MenuExited", StringComparison.Ordinal);
    }

    /// <summary>
    /// Apply combat hit Harmony after board is live (not at chainloader).
    /// Default: BulletHitLand + ZombieAttackPlant.
    /// HitZombie/HitPlant stay off unless EnableUnsafeHitPatches.
    /// </summary>
    public static void EnsureCombatHitPatches()
    {
        if (_combatHitPatchState != 0) return;
        var harmony = RpgHost.Harmony;
        if (harmony == null) return;

        try
        {
            var patched = harmony.CreateClassProcessor(typeof(GameHooks.BulletHitLand)).Patch();
            var n = patched?.Count ?? 0;
            RpgHost.Log.Info($"[Harmony] HitLand applied patches={n}");

            try
            {
                var atk = harmony.CreateClassProcessor(typeof(GameHooks.ZombieAttackPlant)).Patch();
                RpgHost.Log.Info($"[Harmony] AttackPlant applied patches={atk?.Count ?? 0}");
            }
            catch (Exception ex) { RpgHost.Log.Error("[Harmony] AttackPlant: " + ex.Message); }

            if (RpgHost.EnableUnsafeHitPatches)
            {
                RpgHost.Log.Warning("[Harmony] EnableUnsafeHitPatches=true — applying HitZombie/HitPlant (crash risk)");
                try
                {
                    var z = harmony.CreateClassProcessor(typeof(GameHooks.BulletHitZombie)).Patch();
                    RpgHost.Log.Info($"[Harmony] HitZombie applied patches={z?.Count ?? 0}");
                }
                catch (Exception ex) { RpgHost.Log.Error("[Harmony] HitZombie: " + ex.Message); }
                try
                {
                    var p = harmony.CreateClassProcessor(typeof(GameHooks.BulletHitPlant)).Patch();
                    RpgHost.Log.Info($"[Harmony] HitPlant applied patches={p?.Count ?? 0}");
                }
                catch (Exception ex) { RpgHost.Log.Error("[Harmony] HitPlant: " + ex.Message); }
            }
            else
            {
                RpgHost.Log.Info("[Harmony] HitZombie/HitPlant skipped (use TakeDamage→combat.hit; set Combat.EnableUnsafeHitPatches=true to force)");
            }

            _combatHitPatchState = 1;
            RpgHost.Client?.Enqueue("patch.hitland", new Dictionary<string, object>
            {
                ["ok"] = true,
                ["attackPlant"] = true,
                ["unsafeHitPatches"] = RpgHost.EnableUnsafeHitPatches
            });
        }
        catch (Exception ex)
        {
            _combatHitPatchState = 2;
            RpgHost.Log.Error("[Harmony] HitLand failed: " + ex.Message);
            RpgHost.Client?.Enqueue("patch.failed", new Dictionary<string, object>
            {
                ["typeName"] = "BulletHitLand",
                ["error"] = ex.Message
            });
        }
    }
}
