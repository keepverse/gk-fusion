namespace FusionRpg.Launcher.Services;

/// <summary>
/// Per-loader file contracts (install dir, drop payload, config, log).
/// New loader = new class — do not grow if/else in PlaySession.
/// </summary>
public interface IModLoaderHost
{
    LoaderKind Kind { get; }
    string InjectorDllName { get; }

    /// <summary>DLL name for a game profile (e.g. Melon 3.9 uses MelonLoader.39.dll).</summary>
    string InjectorDllNameFor(string? profileId);

    /// <summary>
    /// True when PluginInstallDir is a shared mods root (e.g. Melon Mods\) —
    /// uninstall must only remove FusionRpg-owned files.
    /// </summary>
    bool IsSharedPluginDirectory { get; }

    bool HasAnyMarker(string gameFolder);
    bool IsComplete(string gameFolder);
    string PluginInstallDir(string gameFolder);

    /// <summary>Resolve DropIntoGame payload for this host + optional game profile.</summary>
    string DropPayloadDir(string launcherBaseDir, string? profileId = null);

    /// <summary>True when DropIntoGame payload contains this host's injector DLL for the profile.</summary>
    bool HasDropPayload(string launcherBaseDir, string? profileId = null);

    /// <summary>Whether uninstall may delete this file name inside PluginInstallDir.</summary>
    bool IsOwnedPluginFile(string fileName);

    string? LogPath(string gameFolder);
    void WriteServerUrl(string gameFolder, string serverUrl);
}
