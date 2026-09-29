namespace FusionRpg.Launcher.Services;

public enum LoaderKind
{
    None,
    BepInEx,
    MelonLoader,
    Both
}

public sealed record LoaderProbeResult(
    LoaderKind Kind,
    bool OkForV1,
    string Message,
    string? PluginDir,
    bool PluginInstalled = false,
    IModLoaderHost? Host = null)
{
    public static LoaderProbeResult Fail(string message) =>
        new(LoaderKind.None, false, message, null);

    /// <summary>Any Melon marker — refuse installing BepInEx (dual-load).</summary>
    public bool BlocksBepInExInstall =>
        Kind is LoaderKind.MelonLoader or LoaderKind.Both;

    /// <summary>Any Bep marker — refuse installing MelonLoader (dual-load).</summary>
    public bool BlocksMelonLoaderInstall =>
        Kind is LoaderKind.BepInEx or LoaderKind.Both;
}

public sealed class LoaderProbe
{
    public LoaderProbeResult Probe(string gameFolder)
    {
        if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            return LoaderProbeResult.Fail("Game folder does not exist.");

        var bep = ModLoaderHosts.BepInEx;
        var melon = ModLoaderHosts.MelonLoader;
        var bepSignal = bep.HasAnyMarker(gameFolder);
        var bepComplete = bep.IsComplete(gameFolder);
        var melonSignal = melon.HasAnyMarker(gameFolder);
        var melonComplete = melon.IsComplete(gameFolder);

        if (bepSignal && melonSignal)
        {
            return new LoaderProbeResult(
                LoaderKind.Both,
                false,
                "Both BepInEx and MelonLoader are present. Do not dual-load. Use one loader pack only.",
                null);
        }

        if (melonSignal)
        {
            if (!melonComplete)
            {
                var hints = new List<string>();
                if (!File.Exists(Path.Combine(gameFolder, "version.dll"))) hints.Add("missing version.dll");
                if (!Directory.Exists(Path.Combine(gameFolder, "MelonLoader"))) hints.Add("missing MelonLoader\\");
                return new LoaderProbeResult(
                    LoaderKind.MelonLoader,
                    false,
                    "Incomplete MelonLoader (" + string.Join(", ", hints) +
                    "). Remove MelonLoader files before installing BepInEx (dual-load forbidden).",
                    null,
                    Host: melon);
            }

            var pluginDir = melon.PluginInstallDir(gameFolder);
            var catalog = GameProfileCatalog.LoadFromLauncherBase(
                Path.GetDirectoryName(typeof(LoaderProbe).Assembly.Location) ?? ".");
            var profileId = catalog.Detect(gameFolder);
            var dll = melon.InjectorDllNameFor(profileId);
            var hasPlugin = File.Exists(Path.Combine(pluginDir, dll))
                            || File.Exists(Path.Combine(pluginDir, melon.InjectorDllName))
                            || File.Exists(Path.Combine(pluginDir, "FusionRpg.Injector.MelonLoader.39.dll"));
            var pluginNote = hasPlugin
                ? "FusionRpg MelonMod installed under Mods\\."
                : "FusionRpg MelonMod MISSING — use Install FusionRpg plugin (or Play, which copies DropIntoGame).";
            return new LoaderProbeResult(
                LoaderKind.MelonLoader,
                true,
                "MelonLoader detected (" + profileId + "). " + pluginNote,
                pluginDir,
                PluginInstalled: hasPlugin,
                Host: melon);
        }

        if (bepSignal && !bepComplete)
        {
            var hints = new List<string>();
            if (!File.Exists(Path.Combine(gameFolder, "winhttp.dll"))) hints.Add("missing winhttp.dll");
            if (!Directory.Exists(Path.Combine(gameFolder, "BepInEx", "core"))) hints.Add("missing BepInEx\\core");
            return new LoaderProbeResult(
                LoaderKind.BepInEx,
                false,
                "Incomplete BepInEx (" + string.Join(", ", hints) +
                "). Reinstall BepInEx, or remove BepInEx files before installing MelonLoader.",
                null,
                Host: bep);
        }

        if (!bepComplete)
        {
            return new LoaderProbeResult(
                LoaderKind.None,
                false,
                "No mod loader detected. Install BepInEx 6 (IL2CPP) or MelonLoader — never both. PVZ Fusion is IL2CPP; Mono BepInEx 5.4.x will not work.",
                null);
        }

        {
            var pluginDir = bep.PluginInstallDir(gameFolder);
            var injectorPath = Path.Combine(pluginDir, bep.InjectorDllName);
            var hasPlugin = File.Exists(injectorPath);
            var pluginNote = hasPlugin
                ? "FusionRpg plugin installed under BepInEx\\plugins\\FusionRpg."
                : "FusionRpg plugin MISSING — use Install FusionRpg plugin (or Play, which copies DropIntoGame).";
            return new LoaderProbeResult(
                LoaderKind.BepInEx,
                true,
                "BepInEx IL2CPP (v6 line) detected. " + pluginNote +
                " Loader pin is BepInEx 6 Unity.IL2CPP — not Mono 5.4.x (5.4 cannot load this game).",
                pluginDir,
                PluginInstalled: hasPlugin,
                Host: bep);
        }
    }
}
