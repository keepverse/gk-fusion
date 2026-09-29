using System.Text.Json;
using FusionRpg.Launcher.Services;

namespace FusionRpg.Launcher.Tests;

public class GameProfileMatrixTests
{
    [Fact]
    public void Catalog_json_arity_hp_and_drop_match_ssot()
    {
        var path = FindCatalog();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var profiles = doc.RootElement.GetProperty("profiles");

        var p381 = FindProfile(profiles, "pvzrh-3.8.1");
        Assert.Equal("int32", p381.GetProperty("zombieHpWidth").GetString());
        var arity381 = p381.GetProperty("setZombieArity");
        Assert.Equal(4, arity381.GetProperty("bepInEx").GetInt32());
        Assert.Equal(5, arity381.GetProperty("melonLoader").GetInt32());
        var drop381 = p381.GetProperty("dropRelative");
        Assert.Equal("DropIntoGame/pvzrh-3.8.1/BepInEx", drop381.GetProperty("BepInEx").GetString());
        Assert.Equal("DropIntoGame/pvzrh-3.8.1/MelonLoader", drop381.GetProperty("MelonLoader").GetString());

        var p39 = FindProfile(profiles, "pvzrh-3.9");
        Assert.Equal("int64", p39.GetProperty("zombieHpWidth").GetString());
        var arity39 = p39.GetProperty("setZombieArity");
        Assert.Equal(JsonValueKind.Null, arity39.GetProperty("bepInEx").ValueKind);
        Assert.Equal(4, arity39.GetProperty("melonLoader").GetInt32());
        Assert.Equal("DropIntoGame/pvzrh-3.9/MelonLoader",
            p39.GetProperty("dropRelative").GetProperty("MelonLoader").GetString());
        Assert.False(p39.GetProperty("dropRelative").TryGetProperty("BepInEx", out _));
    }

    [Fact]
    public void SupportsLoader_refuses_3_9_on_BepInEx()
    {
        var catalog = GameProfileCatalog.LoadFromLauncherBase(Path.GetDirectoryName(FindCatalog())!);
        Assert.False(catalog.SupportsLoader(GameProfileCatalog.Profile39, LoaderKind.BepInEx));
        Assert.True(catalog.SupportsLoader(GameProfileCatalog.Profile39, LoaderKind.MelonLoader));
        Assert.True(catalog.SupportsLoader(GameProfileCatalog.DefaultProfileId, LoaderKind.BepInEx));
        Assert.True(catalog.SupportsLoader(GameProfileCatalog.DefaultProfileId, LoaderKind.MelonLoader));
    }

    [Fact]
    public async Task PlayAsync_refuses_3_9_BepInEx_pack()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgW10_39bep_" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "game");
        var launcher = Path.Combine(root, "FusionRpg");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(Path.Combine(game, "BepInEx", "core"));
        Directory.CreateDirectory(launcher);
        File.WriteAllText(Path.Combine(game, GameLocator.GameExeName), "x");
        File.WriteAllText(Path.Combine(game, "winhttp.dll"), "x");
        using (var fs = new FileStream(Path.Combine(game, "GameAssembly.dll"), FileMode.Create, FileAccess.Write))
            fs.SetLength(57717248);
        try
        {
            var session = new PlaySession();
            var settings = new LauncherSettings { PersistToUserStore = false };
            var (ok, message) = await session.PlayAsync(game, launcher, settings);
            Assert.False(ok);
            Assert.Contains("does not support BepInEx", message, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Dual_load_probe_blocks_both_installs()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgW10_both_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "BepInEx", "core"));
        Directory.CreateDirectory(Path.Combine(root, "MelonLoader"));
        File.WriteAllText(Path.Combine(root, "winhttp.dll"), "x");
        File.WriteAllText(Path.Combine(root, "version.dll"), "x");
        try
        {
            var r = new LoaderProbe().Probe(root);
            Assert.Equal(LoaderKind.Both, r.Kind);
            Assert.False(r.OkForV1);
            Assert.True(r.BlocksBepInExInstall);
            Assert.True(r.BlocksMelonLoaderInstall);
            Assert.Contains("dual-load", r.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void DropPayloadDir_resolves_scoped_profile_loader_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgW10_drop_" + Guid.NewGuid().ToString("N"));
        var launcher = Path.Combine(root, "FusionRpg");
        var bep = Path.Combine(launcher, "DropIntoGame", "pvzrh-3.8.1", "BepInEx");
        var melon = Path.Combine(launcher, "DropIntoGame", "pvzrh-3.8.1", "MelonLoader");
        var melon39 = Path.Combine(launcher, "DropIntoGame", "pvzrh-3.9", "MelonLoader");
        Directory.CreateDirectory(bep);
        Directory.CreateDirectory(melon);
        Directory.CreateDirectory(melon39);
        File.Copy(FindCatalog(), Path.Combine(launcher, GameProfileCatalog.CatalogFileName));
        File.WriteAllText(Path.Combine(bep, "FusionRpg.Injector.dll"), "bep");
        File.WriteAllText(Path.Combine(melon, "FusionRpg.Injector.MelonLoader.dll"), "ml");
        File.WriteAllText(Path.Combine(melon39, "FusionRpg.Injector.MelonLoader.39.dll"), "ml39");
        try
        {
            var bepHost = ModLoaderHosts.BepInEx;
            var melonHost = ModLoaderHosts.MelonLoader;
            Assert.Equal(bep, bepHost.DropPayloadDir(launcher, GameProfileCatalog.DefaultProfileId));
            Assert.Equal(melon, melonHost.DropPayloadDir(launcher, GameProfileCatalog.DefaultProfileId));
            Assert.Equal(melon39, melonHost.DropPayloadDir(launcher, GameProfileCatalog.Profile39));
            Assert.Equal("FusionRpg.Injector.MelonLoader.39.dll",
                melonHost.InjectorDllNameFor(GameProfileCatalog.Profile39));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Bep_HasDropPayload_3_9_false_when_only_3_8_1_drop_exists()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgW10_39drop_" + Guid.NewGuid().ToString("N"));
        var launcher = Path.Combine(root, "FusionRpg");
        var bep381 = Path.Combine(launcher, "DropIntoGame", "pvzrh-3.8.1", "BepInEx");
        var legacy = Path.Combine(launcher, "DropIntoGame", "BepInEx");
        Directory.CreateDirectory(bep381);
        Directory.CreateDirectory(legacy);
        File.Copy(FindCatalog(), Path.Combine(launcher, GameProfileCatalog.CatalogFileName));
        File.WriteAllText(Path.Combine(bep381, "FusionRpg.Injector.dll"), "bep381");
        File.WriteAllText(Path.Combine(legacy, "FusionRpg.Injector.dll"), "legacy");
        try
        {
            var host = ModLoaderHosts.BepInEx;
            Assert.True(host.HasDropPayload(launcher, GameProfileCatalog.DefaultProfileId));
            Assert.False(host.HasDropPayload(launcher, GameProfileCatalog.Profile39));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Install_melon_drop_does_not_copy_bep_dll()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgW10_mlinst_" + Guid.NewGuid().ToString("N"));
        var melonDrop = Path.Combine(root, "FusionRpg", "DropIntoGame", "pvzrh-3.8.1", "MelonLoader");
        var bepDrop = Path.Combine(root, "FusionRpg", "DropIntoGame", "pvzrh-3.8.1", "BepInEx");
        var mods = Path.Combine(root, "game", "Mods");
        Directory.CreateDirectory(melonDrop);
        Directory.CreateDirectory(bepDrop);
        File.WriteAllText(Path.Combine(melonDrop, "FusionRpg.Injector.MelonLoader.dll"), "ml");
        File.WriteAllText(Path.Combine(bepDrop, "FusionRpg.Injector.dll"), "bep");
        try
        {
            var n = new PluginInstaller().Install(melonDrop, mods);
            Assert.True(n >= 1);
            Assert.True(File.Exists(Path.Combine(mods, "FusionRpg.Injector.MelonLoader.dll")));
            Assert.False(File.Exists(Path.Combine(mods, "FusionRpg.Injector.dll")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Install_bep_drop_does_not_copy_melon_dll()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgW10_inst_" + Guid.NewGuid().ToString("N"));
        var launcher = Path.Combine(root, "FusionRpg");
        var bepDrop = Path.Combine(launcher, "DropIntoGame", "pvzrh-3.8.1", "BepInEx");
        var melonDrop = Path.Combine(launcher, "DropIntoGame", "pvzrh-3.8.1", "MelonLoader");
        var plugin = Path.Combine(root, "game", "BepInEx", "plugins", "FusionRpg");
        Directory.CreateDirectory(bepDrop);
        Directory.CreateDirectory(melonDrop);
        File.WriteAllText(Path.Combine(bepDrop, "FusionRpg.Injector.dll"), "bep");
        File.WriteAllText(Path.Combine(melonDrop, "FusionRpg.Injector.MelonLoader.dll"), "ml");
        try
        {
            var n = new PluginInstaller().Install(bepDrop, plugin);
            Assert.True(n >= 1);
            Assert.True(File.Exists(Path.Combine(plugin, "FusionRpg.Injector.dll")));
            Assert.False(File.Exists(Path.Combine(plugin, "FusionRpg.Injector.MelonLoader.dll")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static JsonElement FindProfile(JsonElement profiles, string id)
    {
        foreach (var p in profiles.EnumerateArray())
        {
            if (string.Equals(p.GetProperty("id").GetString(), id, StringComparison.OrdinalIgnoreCase))
                return p;
        }

        throw new InvalidOperationException("profile missing: " + id);
    }

    static string FindCatalog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(dir.FullName, GameProfileCatalog.CatalogFileName);
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(GameProfileCatalog.CatalogFileName);
    }
}
