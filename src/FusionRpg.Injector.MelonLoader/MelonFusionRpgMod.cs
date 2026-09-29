using System.Reflection;
using FusionRpg.Injector.Fx;
using FusionRpg.Injector.Host;
using FusionRpg.Injector.Hud;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(FusionRpg.Injector.MelonFusionRpgMod), "FusionRpg", FusionRpg.Injector.MelonHostVersion.Value, "FusionRpg")]
[assembly: MelonGame("LanPiaoPiao", "PlantsVsZombiesRH")]
// Melon auto-PatchAll would apply Bullet.HitPlant/HitZombie and crash IL2CPP.
// Only InjectorBootstrap.SafePatchAll / EnsureCombatHitPatches may patch.
[assembly: HarmonyDontPatchAll]

namespace FusionRpg.Injector;

/// <summary>Keep MelonInfo in sync with csproj Version / InformationalVersion.</summary>
internal static class MelonHostVersion
{
    public const string Value = "0.1.2";
}

/// <summary>
/// MelonLoader entry — thin host. Same InjectorBootstrap / InjectorLoop as BepInEx.
/// Requires MelonLoader Il2CppAssemblies with global Plant/Zombie/Board (P0 gate —
/// docs/research/melonloader-assembly-csharp-p0.md).
/// Set FUSIONRPG_MELON_SKIP_HARMONY=1 for log-only stub (emergency / P0 fail).
/// </summary>
public sealed class MelonFusionRpgMod : MelonMod
{
    public override void OnInitializeMelon()
    {
        var log = new MelonRpgLog(LoggerInstance);
        var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                        ?? Path.Combine(ResolveGameRoot(), "Mods");
        var cfgPath = Path.Combine(pluginDir, "fusionrpg.cfg");
        var config = new FileRpgConfig(cfgPath);
        RpgHost.Initialize(log, config, pluginDir);

        var skipHarmony = string.Equals(
            Environment.GetEnvironmentVariable("FUSIONRPG_MELON_SKIP_HARMONY"),
            "1",
            StringComparison.Ordinal);
        if (skipHarmony)
        {
            LoggerInstance.Warning(
                "FUSIONRPG_MELON_SKIP_HARMONY=1 — MelonMod stub only (no SafePatchAll / RpgClient).");
            return;
        }

        InjectorBootstrap.Start();
        LoggerInstance.Msg("FusionRpg MelonMod host ready, server=" + RpgHost.ServerUrl);
    }

    public override void OnUpdate()
    {
        if (!RpgHost.IsInitialized) return;
        if (RpgHost.Client == null && RpgHost.Harmony == null) return; // skip-harmony stub
        InjectorLoop.Tick(Time.unscaledDeltaTime);
    }

    /// <summary>Game is closing: tear the in-game web view down so no browser process is orphaned.</summary>
    public override void OnApplicationQuit()
    {
        try { OverlaySwitch.Shutdown(); } catch { }
    }

    /// <summary>
    /// Single IMGUI entry (same as BepInEx RpgLoop.OnGUI). Do not dual-subscribe MelonEvents
    /// with Event.GetHashCode dedupe — Unity reuses one Event object, so Layout+Repaint share
    /// a hash and Repaint (floaters + shield bars) gets skipped while world VFX still runs.
    /// </summary>
    public override void OnGUI()
    {
        if (!RpgHost.IsInitialized) return;
        if (RpgHost.Client == null && RpgHost.Harmony == null) return; // skip-harmony stub
        VfxDirector.Draw();
        OverlaySettingsGui.Draw();
        OverlaySwitchGui.Draw();
    }

    static string ResolveGameRoot()
    {
        try
        {
            var root = MelonLoader.Utils.MelonEnvironment.GameRootDirectory;
            if (!string.IsNullOrWhiteSpace(root)) return root;
        }
        catch { /* ignore */ }
        return ".";
    }
}

sealed class MelonRpgLog : IRpgLog
{
    readonly MelonLogger.Instance _log;

    public MelonRpgLog(MelonLogger.Instance log) => _log = log;

    public void Info(string message) => _log.Msg(message);
    public void Warning(string message) => _log.Warning(message);
    public void Error(string message) => _log.Error(message);
}
