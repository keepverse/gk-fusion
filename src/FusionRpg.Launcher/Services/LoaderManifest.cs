using System.Text.Json;
using System.Text.RegularExpressions;

namespace FusionRpg.Launcher.Services;

public sealed class LoaderManifest
{
    public LoaderChannel BepInEx { get; set; } = new();
    public LoaderChannel MelonLoader { get; set; } = new();
    public FusionRpgChannel FusionRpg { get; set; } = new();

    public sealed class LoaderChannel
    {
        public string Owner { get; set; } = "";
        public string Repo { get; set; } = "";
        public string Tag { get; set; } = "latest";
        public string AssetRegex { get; set; } = "";
    }

    public sealed class FusionRpgChannel
    {
        public string Owner { get; set; } = "letuhao";
        public string Repo { get; set; } = "plant-vs-zombie-rise-of-summoner";
        public string AssetRegex { get; set; } = "^FusionRpg-win-x64\\.zip$";
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static LoaderManifest Default { get; } = new()
    {
        BepInEx = new LoaderChannel
        {
            Owner = "BepInEx",
            Repo = "BepInEx",
            Tag = "v6.0.0-pre.2",
            AssetRegex = @"BepInEx-Unity\.IL2CPP-win-x64.*\.zip$"
        },
        MelonLoader = new LoaderChannel
        {
            Owner = "LavaGang",
            Repo = "MelonLoader",
            Tag = "latest",
            AssetRegex = @"MelonLoader\.(x64|win-x64).*\.zip$|MelonLoader\.Windows\.x64.*\.zip$"
        },
        FusionRpg = new FusionRpgChannel()
    };

    public static LoaderManifest LoadFromLauncherDir(string launcherBaseDir)
    {
        var path = Path.Combine(launcherBaseDir, "loader-manifest.json");
        if (!File.Exists(path))
            return Default;
        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<LoaderManifest>(json, JsonOpts) ?? Default;
            loaded.BepInEx ??= Default.BepInEx;
            loaded.MelonLoader ??= Default.MelonLoader;
            loaded.FusionRpg ??= Default.FusionRpg;
            if (string.IsNullOrWhiteSpace(loaded.BepInEx.AssetRegex))
                loaded.BepInEx = Default.BepInEx;
            if (string.IsNullOrWhiteSpace(loaded.MelonLoader.AssetRegex))
                loaded.MelonLoader = Default.MelonLoader;
            if (string.IsNullOrWhiteSpace(loaded.FusionRpg.AssetRegex))
                loaded.FusionRpg = Default.FusionRpg;
            return loaded;
        }
        catch
        {
            return Default;
        }
    }

    public static bool AssetMatches(string assetName, string regex)
    {
        if (string.IsNullOrWhiteSpace(regex)) return false;
        return Regex.IsMatch(assetName, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
