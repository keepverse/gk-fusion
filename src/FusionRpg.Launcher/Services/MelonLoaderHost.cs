using System.Text.RegularExpressions;

namespace FusionRpg.Launcher.Services;

public sealed class MelonLoaderHost : IModLoaderHost
{
    public const string CfgFileName = "fusionrpg.cfg";

    public LoaderKind Kind => LoaderKind.MelonLoader;
    public string InjectorDllName => "FusionRpg.Injector.MelonLoader.dll";
    public bool IsSharedPluginDirectory => true;

    public string InjectorDllNameFor(string? profileId)
    {
        if (string.Equals(profileId, GameProfileCatalog.Profile39, StringComparison.OrdinalIgnoreCase))
            return "FusionRpg.Injector.MelonLoader.39.dll";
        return InjectorDllName;
    }

    public bool HasAnyMarker(string gameFolder)
    {
        var hasVersionDll = File.Exists(Path.Combine(gameFolder, "version.dll"));
        var hasMelonDir = Directory.Exists(Path.Combine(gameFolder, "MelonLoader"));
        return hasVersionDll || hasMelonDir;
    }

    public bool IsComplete(string gameFolder) =>
        File.Exists(Path.Combine(gameFolder, "version.dll"))
        && Directory.Exists(Path.Combine(gameFolder, "MelonLoader"));

    public string PluginInstallDir(string gameFolder) =>
        Path.Combine(gameFolder, "Mods");

    public string DropPayloadDir(string launcherBaseDir, string? profileId = null)
    {
        var catalog = GameProfileCatalog.LoadFromLauncherBase(launcherBaseDir);
        var profile = string.IsNullOrWhiteSpace(profileId) ? catalog.DefaultId : profileId!;
        if (!catalog.SupportsLoader(profile, Kind))
            return UnsupportedDropDir(launcherBaseDir, catalog, profile);

        var dll = InjectorDllNameFor(profile);
        var rel = catalog.DropRelative(profile, Kind);
        if (!string.IsNullOrEmpty(rel))
        {
            var scoped = Path.Combine(launcherBaseDir, rel);
            if (Directory.Exists(scoped) && File.Exists(Path.Combine(scoped, dll)))
                return scoped;
        }

        var nested = Path.Combine(launcherBaseDir, "DropIntoGame", "MelonLoader");
        if (Directory.Exists(nested) && File.Exists(Path.Combine(nested, dll)))
            return nested;
        var parent = Directory.GetParent(launcherBaseDir)?.FullName;
        if (parent != null)
        {
            if (!string.IsNullOrEmpty(rel))
            {
                var scopedParent = Path.Combine(parent, rel);
                if (Directory.Exists(scopedParent) && File.Exists(Path.Combine(scopedParent, dll)))
                    return scopedParent;
            }
            var nestedParent = Path.Combine(parent, "DropIntoGame", "MelonLoader");
            if (Directory.Exists(nestedParent) && File.Exists(Path.Combine(nestedParent, dll)))
                return nestedParent;
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
        var rel = catalog.DropRelative(profile, LoaderKind.MelonLoader);
        if (!string.IsNullOrEmpty(rel))
            return Path.Combine(launcherBaseDir, rel);
        return Path.Combine(launcherBaseDir, "DropIntoGame", profile, "MelonLoader");
    }

    public bool IsOwnedPluginFile(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        if (fileName.Equals(CfgFileName, StringComparison.OrdinalIgnoreCase)) return true;
        return fileName.StartsWith("FusionRpg.", StringComparison.OrdinalIgnoreCase);
    }

    public string? LogPath(string gameFolder)
    {
        var latest = Path.Combine(gameFolder, "MelonLoader", "Latest.log");
        if (File.Exists(latest)) return latest;
        var nestedLatest = Path.Combine(gameFolder, "MelonLoader", "Logs", "Latest.log");
        if (File.Exists(nestedLatest)) return nestedLatest;
        var logDir = Path.Combine(gameFolder, "MelonLoader");
        return Directory.Exists(logDir) ? logDir : null;
    }

    public void WriteServerUrl(string gameFolder, string serverUrl)
    {
        var mods = PluginInstallDir(gameFolder);
        Directory.CreateDirectory(mods);
        var cfgPath = Path.Combine(mods, CfgFileName);
        var url = serverUrl.Trim().TrimEnd('/');

        if (!File.Exists(cfgPath))
        {
            File.WriteAllText(cfgPath,
                "# FusionRpg MelonLoader host config" + Environment.NewLine +
                "ServerUrl=" + url + Environment.NewLine +
                "PersistCheats=false" + Environment.NewLine +
                "EnableUnsafeHitPatches=false" + Environment.NewLine);
            return;
        }

        var text = File.ReadAllText(cfgPath);
        if (Regex.IsMatch(text, @"^\s*ServerUrl\s*=", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            text = Regex.Replace(
                text,
                @"^(\s*ServerUrl\s*=\s*).*$",
                "${1}" + url,
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
        }
        else
        {
            text = "ServerUrl=" + url + Environment.NewLine + text;
        }
        File.WriteAllText(cfgPath, text);
    }
}
