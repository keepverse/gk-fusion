namespace FusionRpg.Launcher.Services;

/// <summary>Registry of supported mod-loader hosts (BepInEx, MelonLoader, …).</summary>
public static class ModLoaderHosts
{
    public static readonly IModLoaderHost BepInEx = new BepInExHost();
    public static readonly IModLoaderHost MelonLoader = new MelonLoaderHost();

    public static IReadOnlyList<IModLoaderHost> All { get; } = new IModLoaderHost[]
    {
        BepInEx,
        MelonLoader
    };

    public static IModLoaderHost? ForKind(LoaderKind kind) => kind switch
    {
        LoaderKind.BepInEx => BepInEx,
        LoaderKind.MelonLoader => MelonLoader,
        _ => null
    };
}
