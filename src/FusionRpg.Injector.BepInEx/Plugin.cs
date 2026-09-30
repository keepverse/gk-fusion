using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using FusionRpg.Injector.Fx;
using FusionRpg.Injector.Host;
using FusionRpg.Bridge.Hud;
using UnityEngine;

namespace FusionRpg.Injector;

/// <summary>BepInEx 6 IL2CPP entry — thin host; game logic lives in FusionRpg.Injector shared sources.</summary>
[BepInPlugin("com.fusionrpg.injector", "FusionRpg Injector", "1.0.0")]
public class Plugin : BasePlugin
{
    public override void Load()
    {
        var log = new BepInExRpgLog(base.Log);
        var config = new BepInExRpgConfig(Config);
        var pluginDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
                        ?? Paths.PluginPath;
        RpgHost.Initialize(log, config, pluginDir ?? "");
        InjectorBootstrap.Start();
        AddComponent<RpgLoop>();
    }

    /// <summary>Per-frame bridge to <see cref="InjectorLoop"/>.</summary>
    public class RpgLoop : MonoBehaviour
    {
        void Update() => InjectorLoop.TickFromUnity();
        /// <summary>Game is closing: tear the in-game web view down so no browser process is orphaned.</summary>
        void OnApplicationQuit()
        {
            try { OverlaySwitch.Shutdown(); } catch { }
        }

        void OnGUI()
        {
            VfxDirector.Draw();
            OverlaySettingsGui.Draw();
            OverlaySwitchGui.Draw();
        }
    }
}

sealed class BepInExRpgLog : IRpgLog
{
    readonly ManualLogSource _log;
    public BepInExRpgLog(ManualLogSource log) => _log = log;
    public void Info(string message) => _log.LogInfo(message);
    public void Warning(string message) => _log.LogWarning(message);
    public void Error(string message) => _log.LogError(message);
}

sealed class BepInExRpgConfig : IRpgConfig
{
    public string ServerUrl { get; }
    public bool ServerUrlFromFallback { get; }
    public string OverlayHost { get; }
    public bool PersistCheats { get; }
    public bool EnableUnsafeHitPatches { get; }

    public BepInExRpgConfig(ConfigFile config)
    {
        ServerUrl = config.Bind("General", "ServerUrl", RpgHost.DefaultServerUrl, "RPG server").Value;
        ServerUrlFromFallback = string.Equals(ServerUrl, RpgHost.DefaultServerUrl, System.StringComparison.Ordinal);
        OverlayHost = config.Bind(
            "General",
            "OverlayHost",
            "launcher",
            "Who owns the web overlay window: launcher (default) or injector (in-game). FUSIONRPG_OVERLAY_HOST wins.").Value;
        // In-game GUI retired — web FE at ServerUrl is the only cheat UI / monitor (SSOT).
        config.Bind("Cheat", "CheatMenuEnabled", false, "DEPRECATED: in-game menu disabled; use the web UI.");
        PersistCheats = config.Bind("Cheat", "PersistCheats", false, "Load/save cheat-state.json beside plugin").Value;
        EnableUnsafeHitPatches = config.Bind(
            "Combat",
            "EnableUnsafeHitPatches",
            false,
            "CRASH RISK: also Harmony-patch Bullet.HitZombie/HitPlant after board.start. Default off — pea overrides (~167) miss base patches; IL2CPP trampoline historically AV'd.").Value;
    }
}
