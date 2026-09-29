using System.Text.Json;
using System.Text.Json.Serialization;

namespace FusionRpg.Launcher.Services;

/// <summary>
/// Resolves pvzrh-* game profiles from fingerprints (see repo game-profiles.json).
/// </summary>
public sealed class GameProfileCatalog
{
    public const string DefaultProfileId = "pvzrh-3.8.1";
    public const string Profile39 = "pvzrh-3.9";
    public const string CatalogFileName = "game-profiles.json";

    readonly CatalogRoot _root;

    public GameProfileCatalog(CatalogRoot? root = null) =>
        _root = root ?? BuiltIn();

    public string DefaultId => string.IsNullOrWhiteSpace(_root.DefaultProfileId)
        ? DefaultProfileId
        : _root.DefaultProfileId!;

    public IReadOnlyList<ProfileDef> Profiles => _root.Profiles;

    public static GameProfileCatalog LoadFromLauncherBase(string launcherBaseDir)
    {
        foreach (var cand in new[]
                 {
                     Path.Combine(launcherBaseDir, CatalogFileName),
                     Path.Combine(launcherBaseDir, "..", CatalogFileName),
                     Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, CatalogFileName))
                 })
        {
            try
            {
                if (!File.Exists(cand)) continue;
                var json = File.ReadAllText(cand);
                var root = JsonSerializer.Deserialize<CatalogRoot>(json, JsonOpts());
                if (root?.Profiles is { Count: > 0 })
                    return new GameProfileCatalog(root);
            }
            catch { /* try next */ }
        }
        return new GameProfileCatalog();
    }

    public string Detect(string gameFolder, string? overrideProfile = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideProfile))
            return overrideProfile.Trim();

        var ga = Path.Combine(gameFolder, "GameAssembly.dll");
        long gaLen = -1;
        try { if (File.Exists(ga)) gaLen = new FileInfo(ga).Length; } catch { }

        foreach (var p in _root.Profiles)
        {
            if (p.Fingerprints == null) continue;
            foreach (var fp in p.Fingerprints)
            {
                if (fp.GameAssemblyLength is long want && want == gaLen && gaLen > 0)
                    return p.Id ?? DefaultId;

                if (fp.AssemblyCSharpPaths == null || fp.AssemblyCSharpLengths == null) continue;
                for (var i = 0; i < fp.AssemblyCSharpPaths.Count && i < fp.AssemblyCSharpLengths.Count; i++)
                {
                    var acs = Path.Combine(gameFolder, fp.AssemblyCSharpPaths[i].Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        if (File.Exists(acs) && new FileInfo(acs).Length == fp.AssemblyCSharpLengths[i])
                            return p.Id ?? DefaultId;
                    }
                    catch { }
                }
            }
        }

        return DefaultId;
    }

    public ProfileDef? Find(string profileId) =>
        _root.Profiles.FirstOrDefault(p =>
            string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));

    public string InjectorDllName(string profileId, LoaderKind loader)
    {
        var p = Find(profileId);
        var key = loader == LoaderKind.MelonLoader ? "MelonLoader" : "BepInEx";
        if (p?.InjectorDll != null && p.InjectorDll.TryGetValue(key, out var dll) && !string.IsNullOrWhiteSpace(dll))
            return dll;
        return loader == LoaderKind.MelonLoader
            ? "FusionRpg.Injector.MelonLoader.dll"
            : "FusionRpg.Injector.dll";
    }

    public string? DropRelative(string profileId, LoaderKind loader)
    {
        var p = Find(profileId);
        var key = loader == LoaderKind.MelonLoader ? "MelonLoader" : "BepInEx";
        if (p?.DropRelative != null && p.DropRelative.TryGetValue(key, out var rel))
            return rel?.Replace('/', Path.DirectorySeparatorChar);
        return null;
    }

    public bool SupportsLoader(string profileId, LoaderKind loader)
    {
        var p = Find(profileId);
        if (p?.Loaders == null || p.Loaders.Count == 0) return true;
        var name = loader == LoaderKind.MelonLoader ? "MelonLoader" : "BepInEx";
        return p.Loaders.Any(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase));
    }

    static CatalogRoot BuiltIn() => new()
    {
        DefaultProfileId = DefaultProfileId,
        Profiles =
        [
            new ProfileDef
            {
                Id = DefaultProfileId,
                Loaders = ["BepInEx", "MelonLoader"],
                InjectorDll = new Dictionary<string, string>
                {
                    ["BepInEx"] = "FusionRpg.Injector.dll",
                    ["MelonLoader"] = "FusionRpg.Injector.MelonLoader.dll"
                },
                DropRelative = new Dictionary<string, string>
                {
                    ["BepInEx"] = "DropIntoGame/pvzrh-3.8.1/BepInEx",
                    ["MelonLoader"] = "DropIntoGame/pvzrh-3.8.1/MelonLoader"
                },
                Fingerprints =
                [
                    new FingerprintDef
                    {
                        // Structural (tunables-ssot.md T2) — exact byte length of this game
                        // version's binary, used to auto-detect which profile is installed. Not balance.
                        GameAssemblyLength = 47964672,
                        AssemblyCSharpPaths =
                        [
                            "BepInEx/interop/Assembly-CSharp.dll",
                            "MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll"
                        ],
                        AssemblyCSharpLengths = [8316416, 7772672]
                    }
                ]
            },
            new ProfileDef
            {
                Id = Profile39,
                Loaders = ["MelonLoader"],
                InjectorDll = new Dictionary<string, string>
                {
                    ["MelonLoader"] = "FusionRpg.Injector.MelonLoader.39.dll"
                },
                DropRelative = new Dictionary<string, string>
                {
                    ["MelonLoader"] = "DropIntoGame/pvzrh-3.9/MelonLoader"
                },
                Fingerprints =
                [
                    new FingerprintDef
                    {
                        // Structural (tunables-ssot.md T2) — see the 3.8.1 profile's fingerprint above.
                        GameAssemblyLength = 57717248,
                        AssemblyCSharpPaths = ["MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll"],
                        AssemblyCSharpLengths = [8405504]
                    }
                ]
            }
        ]
    };

    static JsonSerializerOptions JsonOpts() => new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public sealed class CatalogRoot
    {
        [JsonPropertyName("defaultProfileId")] public string? DefaultProfileId { get; set; }
        [JsonPropertyName("profiles")] public List<ProfileDef> Profiles { get; set; } = new();
    }

    public sealed class ProfileDef
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("loaders")] public List<string>? Loaders { get; set; }
        [JsonPropertyName("injectorDll")] public Dictionary<string, string>? InjectorDll { get; set; }
        [JsonPropertyName("dropRelative")] public Dictionary<string, string>? DropRelative { get; set; }
        [JsonPropertyName("fingerprints")] public List<FingerprintDef>? Fingerprints { get; set; }
    }

    public sealed class FingerprintDef
    {
        [JsonPropertyName("gameAssemblyLength")] public long? GameAssemblyLength { get; set; }
        [JsonPropertyName("assemblyCSharpPaths")] public List<string>? AssemblyCSharpPaths { get; set; }
        [JsonPropertyName("assemblyCSharpLengths")] public List<long>? AssemblyCSharpLengths { get; set; }
    }
}
