namespace FusionRpg.Launcher.Services;

public sealed class PluginInstaller
{
    /// <summary>Legacy Bep DLL name (flat DropIntoGame).</summary>
    public const string InjectorDllName = "FusionRpg.Injector.dll";
    public const string BepInExPluginId = BepInExHost.PluginId;

    public string ResolveDropIntoGameDir(string launcherBaseDir, IModLoaderHost? host = null)
    {
        host ??= ModLoaderHosts.BepInEx;
        return host.DropPayloadDir(launcherBaseDir);
    }

    public bool HasDropPayload(string launcherBaseDir, IModLoaderHost host) =>
        host.HasDropPayload(launcherBaseDir);

    public bool IsPluginPresent(string pluginDir, IModLoaderHost? host = null)
    {
        var dll = host?.InjectorDllName ?? InjectorDllName;
        return File.Exists(Path.Combine(pluginDir, dll));
    }

    public bool NeedsInstallOrUpdate(string dropDir, string pluginDir, IModLoaderHost? host = null, string? dllName = null)
    {
        var dll = dllName ?? host?.InjectorDllName ?? InjectorDllName;
        var src = Path.Combine(dropDir, dll);
        var dst = Path.Combine(pluginDir, dll);
        if (!File.Exists(src)) return false;
        if (!File.Exists(dst)) return true;
        var srcInfo = new FileInfo(src);
        var dstInfo = new FileInfo(dst);
        if (srcInfo.Length != dstInfo.Length) return true;
        if (srcInfo.LastWriteTimeUtc > dstInfo.LastWriteTimeUtc.AddSeconds(1)) return true;
        return false;
    }

    public int Install(string dropDir, string pluginDir)
    {
        if (!Directory.Exists(dropDir))
            throw new DirectoryNotFoundException("DropIntoGame folder missing: " + dropDir);
        Directory.CreateDirectory(pluginDir);
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(dropDir))
        {
            var ext = Path.GetExtension(file);
            if (ext is not (".dll" or ".json" or ".pdb" or ".cfg")) continue;
            var dest = Path.Combine(pluginDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
            count++;
        }
        if (count == 0)
            throw new InvalidOperationException("No plugin files found in " + dropDir);
        return count;
    }

    /// <summary>
    /// Remove FusionRpg plugin files. For shared dirs (Melon Mods\) only owned files are deleted.
    /// For dedicated dirs (BepInEx\plugins\FusionRpg) the whole folder contents are removed.
    /// </summary>
    public int UninstallPlugin(string pluginDir, IModLoaderHost? host = null)
    {
        if (!Directory.Exists(pluginDir)) return 0;
        var n = 0;
        foreach (var file in Directory.EnumerateFiles(pluginDir).ToList())
        {
            var name = Path.GetFileName(file);
            if (host != null && host.IsSharedPluginDirectory && !host.IsOwnedPluginFile(name))
                continue;
            File.Delete(file);
            n++;
        }
        if (host is not { IsSharedPluginDirectory: true })
        {
            try { Directory.Delete(pluginDir, recursive: false); } catch { /* leave if not empty */ }
        }
        return n;
    }

    /// <summary>Write ServerUrl into the active host's config so a game start without launcher still hits the last port.</summary>
    public void WriteServerUrlConfig(string gameFolder, string serverUrl, IModLoaderHost? host = null)
    {
        if (host == null)
            throw new ArgumentNullException(nameof(host), "Host is required to write ServerUrl config (refuse Bep default on Melon packs).");
        host.WriteServerUrl(gameFolder, serverUrl);
    }
}
