using System.Text.RegularExpressions;

namespace FusionRpg.Launcher.Services;

public sealed class BepInExHost : IModLoaderHost
{
    public const string PluginId = "com.fusionrpg.injector";

    public LoaderKind Kind => LoaderKind.BepInEx;
    public string InjectorDllName => "FusionRpg.Injector.dll";
    public bool IsSharedPluginDirectory => false;

    public string InjectorDllNameFor(string? profileId) => InjectorDllName;

    public bool HasAnyMarker(string gameFolder)
    {
        var hasWinHttp = File.Exists(Path.Combine(gameFolder, "winhttp.dll"));
        var hasBepCore = Directory.Exists(Path.Combine(gameFolder, "BepInEx", "core"));
        return hasWinHttp || hasBepCore;
    }

    public bool IsComplete(string gameFolder) =>
        File.Exists(Path.Combine(gameFolder, "winhttp.dll"))
        && Directory.Exists(Path.Combine(gameFolder, "BepInEx", "core"));

    public string PluginInstallDir(string gameFolder) =>
        Path.Combine(gameFolder, "BepInEx", "plugins", "FusionRpg");

    public string DropPayloadDir(string launcherBaseDir, string? profileId = null)
    {
        var catalog = GameProfileCatalog.LoadFromLauncherBase(launcherBaseDir);
        var profile = string.IsNullOrWhiteSpace(profileId) ? catalog.DefaultId : profileId!;
        if (!catalog.SupportsLoader(profile, Kind))
            return UnsupportedDropDir(launcherBaseDir, catalog, profile);

        var dll = InjectorDllNameFor(profileId);
        var rel = catalog.DropRelative(profile, Kind);
        if (!string.IsNullOrEmpty(rel))
        {
            var scoped = Path.Combine(launcherBaseDir, rel);
            if (Directory.Exists(scoped) && File.Exists(Path.Combine(scoped, dll)))
                return scoped;
        }

        var nested = Path.Combine(launcherBaseDir, "DropIntoGame", "BepInEx");
        if (Directory.Exists(nested) && File.Exists(Path.Combine(nested, dll)))
            return nested;
        // Legacy flat DropIntoGame\FusionRpg.Injector.dll
        var flat = Path.Combine(launcherBaseDir, "DropIntoGame");
        if (Directory.Exists(flat) && File.Exists(Path.Combine(flat, dll)))
            return flat;
        var parent = Directory.GetParent(launcherBaseDir)?.FullName;
        if (parent != null)
        {
            if (!string.IsNullOrEmpty(rel))
            {
                var scopedParent = Path.Combine(parent, rel);
                if (Directory.Exists(scopedParent) && File.Exists(Path.Combine(scopedParent, dll)))
                    return scopedParent;
            }
            var nestedParent = Path.Combine(parent, "DropIntoGame", "BepInEx");
            if (Directory.Exists(nestedParent) && File.Exists(Path.Combine(nestedParent, dll)))
                return nestedParent;
            var flatParent = Path.Combine(parent, "DropIntoGame");
            if (Directory.Exists(flatParent) && File.Exists(Path.Combine(flatParent, dll)))
                return flatParent;
        }
        return string.IsNullOrEmpty(rel) ? nested : Path.Combine(launcherBaseDir, rel);
    }

    public bool HasDropPayload(string launcherBaseDir, string? profileId = null)
    {
        var catalog = GameProfileCatalog.LoadFromLauncherBase(launcherBaseDir);
        var profile = string.IsNullOrWhiteSpace(profileId) ? catalog.DefaultId : profileId!;
        if (!catalog.SupportsLoader(profile, Kind)) return false;
        var drop = DropPayloadDir(launcherBaseDir, profileId);
        return File.Exists(Path.Combine(drop, InjectorDllNameFor(profileId)));
    }

    static string UnsupportedDropDir(string launcherBaseDir, GameProfileCatalog catalog, string profile)
    {
        var rel = catalog.DropRelative(profile, LoaderKind.BepInEx);
        if (!string.IsNullOrEmpty(rel))
            return Path.Combine(launcherBaseDir, rel);
        return Path.Combine(launcherBaseDir, "DropIntoGame", profile, "BepInEx");
    }

    public bool IsOwnedPluginFile(string fileName) =>
        // Dedicated FusionRpg plugin folder — every file there is ours.
        !string.IsNullOrEmpty(fileName);

    public string? LogPath(string gameFolder) =>
        Path.Combine(gameFolder, "BepInEx", "LogOutput.log");

    public void WriteServerUrl(string gameFolder, string serverUrl)
    {
        var cfgDir = Path.Combine(gameFolder, "BepInEx", "config");
        Directory.CreateDirectory(cfgDir);
        var cfgPath = Path.Combine(cfgDir, PluginId + ".cfg");
        var url = serverUrl.Trim().TrimEnd('/');

        if (!File.Exists(cfgPath))
        {
            File.WriteAllText(cfgPath,
                "[General]" + Environment.NewLine +
                "## RPG server" + Environment.NewLine +
                "# Setting type: String" + Environment.NewLine +
                "# Default value: http://127.0.0.1:5088" + Environment.NewLine +
                "ServerUrl = " + url + Environment.NewLine);
            return;
        }

        var text = File.ReadAllText(cfgPath);
        if (Regex.IsMatch(text, @"^\s*ServerUrl\s*=", RegexOptions.Multiline))
        {
            text = Regex.Replace(
                text,
                @"^(\s*ServerUrl\s*=\s*).*$",
                "$1" + url,
                RegexOptions.Multiline);
        }
        else if (text.Contains("[General]", StringComparison.Ordinal))
        {
            text = text.Replace(
                "[General]",
                "[General]" + Environment.NewLine + "ServerUrl = " + url,
                StringComparison.Ordinal);
        }
        else
        {
            text = "[General]" + Environment.NewLine + "ServerUrl = " + url + Environment.NewLine + text;
        }

        File.WriteAllText(cfgPath, text);
    }
}
