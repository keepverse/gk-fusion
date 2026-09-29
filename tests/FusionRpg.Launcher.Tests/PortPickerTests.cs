using FusionRpg.Launcher.Services;

namespace FusionRpg.Launcher.Tests;

public class PortPickerTests
{
    [Fact]
    public void Prefers_5088_when_free()
    {
        var picker = new PortPicker();
        var r = picker.Pick(
            lastGoodPort: null,
            isPortFree: p => p == 5088 || p >= 5089,
            isOwnedByOurServer: _ => false);
        Assert.Equal(5088, r.Port);
        Assert.False(r.ReusedOurServer);
        Assert.Equal("http://127.0.0.1:5088", r.Url);
    }

    [Fact]
    public void Reuses_when_our_server_owns_port()
    {
        var picker = new PortPicker();
        var r = picker.Pick(
            isPortFree: _ => false,
            isOwnedByOurServer: p => p == 5088);
        Assert.Equal(5088, r.Port);
        Assert.True(r.ReusedOurServer);
    }

    [Fact]
    public void Hops_to_next_free_when_5088_taken()
    {
        var picker = new PortPicker();
        var r = picker.Pick(
            isPortFree: p => p == 5091,
            isOwnedByOurServer: _ => false);
        Assert.Equal(5091, r.Port);
    }

    [Fact]
    public void Skips_vite_5173()
    {
        var picker = new PortPicker();
        var r = picker.Pick(
            lastGoodPort: 5173,
            isPortFree: p => p == 5173 || p == 5088,
            isOwnedByOurServer: _ => false);
        Assert.Equal(5088, r.Port);
    }

    [Fact]
    public void Prefers_last_good_when_free()
    {
        var picker = new PortPicker();
        var r = picker.Pick(
            lastGoodPort: 5100,
            isPortFree: p => p == 5100 || p == 5088,
            isOwnedByOurServer: _ => false);
        Assert.Equal(5100, r.Port);
    }

    [Fact]
    public void Throws_when_range_exhausted()
    {
        var picker = new PortPicker();
        Assert.Throws<InvalidOperationException>(() =>
            picker.Pick(isPortFree: _ => false, isOwnedByOurServer: _ => false));
    }
}

public class LoaderProbeTests
{
    [Fact]
    public void Detects_bepinex()
    {
        var root = CreateTempGame(bep: true, melon: false);
        try
        {
            var r = new LoaderProbe().Probe(root);
            Assert.True(r.OkForV1);
            Assert.Equal(LoaderKind.BepInEx, r.Kind);
            Assert.NotNull(r.PluginDir);
            Assert.False(r.PluginInstalled);
            Assert.Contains("MISSING", r.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Detects_fusionrpg_plugin_when_present()
    {
        var root = CreateTempGame(bep: true, melon: false);
        try
        {
            var pluginDir = Path.Combine(root, "BepInEx", "plugins", "FusionRpg");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(Path.Combine(pluginDir, PluginInstaller.InjectorDllName), "x");
            var r = new LoaderProbe().Probe(root);
            Assert.True(r.PluginInstalled);
            Assert.Contains("plugin installed", r.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Accepts_complete_melonloader_for_play()
    {
        var root = CreateTempGame(bep: false, melon: true);
        try
        {
            var r = new LoaderProbe().Probe(root);
            Assert.True(r.OkForV1);
            Assert.Equal(LoaderKind.MelonLoader, r.Kind);
            Assert.NotNull(r.Host);
            Assert.Equal(LoaderKind.MelonLoader, r.Host!.Kind);
            Assert.NotNull(r.PluginDir);
            Assert.EndsWith("Mods", r.PluginDir, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Rejects_both()
    {
        var root = CreateTempGame(bep: true, melon: true);
        try
        {
            var r = new LoaderProbe().Probe(root);
            Assert.False(r.OkForV1);
            Assert.Equal(LoaderKind.Both, r.Kind);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Partial_melon_blocks_bep_install()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgLauncherTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "version.dll"), "x");
            var r = new LoaderProbe().Probe(root);
            Assert.Equal(LoaderKind.MelonLoader, r.Kind);
            Assert.False(r.OkForV1);
            Assert.True(r.BlocksBepInExInstall);
            Assert.Contains("Incomplete MelonLoader", r.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Partial_bep_blocks_melon_install_not_ok_for_play()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgLauncherTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "BepInEx", "core"));
            var r = new LoaderProbe().Probe(root);
            Assert.Equal(LoaderKind.BepInEx, r.Kind);
            Assert.False(r.OkForV1);
            Assert.True(r.BlocksMelonLoaderInstall);
            Assert.Contains("Incomplete BepInEx", r.Message, StringComparison.Ordinal);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Mixed_partial_markers_are_Both()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgLauncherTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "winhttp.dll"), "x");
            Directory.CreateDirectory(Path.Combine(root, "MelonLoader"));
            var r = new LoaderProbe().Probe(root);
            Assert.Equal(LoaderKind.Both, r.Kind);
            Assert.True(r.BlocksBepInExInstall);
            Assert.True(r.BlocksMelonLoaderInstall);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Full_melon_message_mentions_MelonMod()
    {
        var root = CreateTempGame(bep: false, melon: true);
        try
        {
            var r = new LoaderProbe().Probe(root);
            Assert.Contains("MelonMod", r.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(root); }
    }

    static string CreateTempGame(bool bep, bool melon)
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgLauncherTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "PlantsVsZombiesRH.exe"), "stub");
        if (bep)
        {
            File.WriteAllText(Path.Combine(root, "winhttp.dll"), "stub");
            Directory.CreateDirectory(Path.Combine(root, "BepInEx", "core"));
            File.WriteAllText(Path.Combine(root, "BepInEx", "core", "BepInEx.Core.dll"), "stub");
        }
        if (melon)
        {
            File.WriteAllText(Path.Combine(root, "version.dll"), "stub");
            Directory.CreateDirectory(Path.Combine(root, "MelonLoader"));
        }
        return root;
    }

    static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); } catch { /* ignore */ }
    }
}

public class PluginInstallerTests
{
    [Fact]
    public void Install_copies_dlls()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgPlugin_" + Guid.NewGuid().ToString("N"));
        var drop = Path.Combine(root, "DropIntoGame");
        var plugin = Path.Combine(root, "BepInEx", "plugins", "FusionRpg");
        Directory.CreateDirectory(drop);
        File.WriteAllText(Path.Combine(drop, "FusionRpg.Injector.dll"), "v1");
        File.WriteAllText(Path.Combine(drop, "FusionRpg.Contracts.dll"), "v1");
        try
        {
            var n = new PluginInstaller().Install(drop, plugin);
            Assert.Equal(2, n);
            Assert.True(File.Exists(Path.Combine(plugin, "FusionRpg.Injector.dll")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void WriteServerUrlConfig_creates_and_updates()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgCfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var inst = new PluginInstaller();
        try
        {
            inst.WriteServerUrlConfig(root, "http://127.0.0.1:5099", ModLoaderHosts.BepInEx);
            var cfg = Path.Combine(root, "BepInEx", "config", "com.fusionrpg.injector.cfg");
            Assert.True(File.Exists(cfg));
            Assert.Contains("ServerUrl = http://127.0.0.1:5099", File.ReadAllText(cfg));

            inst.WriteServerUrlConfig(root, "http://127.0.0.1:5100/", ModLoaderHosts.BepInEx);
            Assert.Contains("ServerUrl = http://127.0.0.1:5100", File.ReadAllText(cfg));
            Assert.DoesNotContain("5099", File.ReadAllText(cfg));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void WriteServerUrlConfig_melon_writes_fusionrpg_cfg()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgMelonCfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var inst = new PluginInstaller();
        var host = ModLoaderHosts.MelonLoader;
        try
        {
            inst.WriteServerUrlConfig(root, "http://127.0.0.1:5111", host);
            var cfg = Path.Combine(root, "Mods", MelonLoaderHost.CfgFileName);
            Assert.True(File.Exists(cfg));
            Assert.Contains("ServerUrl=http://127.0.0.1:5111", File.ReadAllText(cfg));

            inst.WriteServerUrlConfig(root, "http://127.0.0.1:5112/", host);
            Assert.Contains("ServerUrl=http://127.0.0.1:5112", File.ReadAllText(cfg));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ResolveDropIntoGameDir_prefers_nested_host_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgDrop_" + Guid.NewGuid().ToString("N"));
        var launcher = Path.Combine(root, "FusionRpg");
        var bepDrop = Path.Combine(launcher, "DropIntoGame", "BepInEx");
        var melonDrop = Path.Combine(launcher, "DropIntoGame", "MelonLoader");
        Directory.CreateDirectory(bepDrop);
        Directory.CreateDirectory(melonDrop);
        File.WriteAllText(Path.Combine(bepDrop, "FusionRpg.Injector.dll"), "b");
        File.WriteAllText(Path.Combine(melonDrop, "FusionRpg.Injector.MelonLoader.dll"), "m");
        try
        {
            var inst = new PluginInstaller();
            Assert.Equal(bepDrop, inst.ResolveDropIntoGameDir(launcher, ModLoaderHosts.BepInEx));
            Assert.Equal(melonDrop, inst.ResolveDropIntoGameDir(launcher, ModLoaderHosts.MelonLoader));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

public class GitHubReleaseClientTests
{
    [Theory]
    [InlineData("v1.2.0", "1.1.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("v1.0.0", "1.0.0", false)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("v1.0.1", "1.0.0+deadbeef", true)]
    [InlineData("v1.0.0", "1.0.0+deadbeef", false)]
    public void IsNewerThan_compares_versions(string tag, string local, bool expected)
    {
        Assert.Equal(expected, GitHubReleaseClient.IsNewerThan(tag, local));
    }

    [Fact]
    public void NormalizeVersion_strips_metadata()
    {
        Assert.Equal("1.0.0", GitHubReleaseClient.NormalizeVersion("v1.0.0+abcdef"));
        Assert.Equal("1.2.3", GitHubReleaseClient.NormalizeVersion("1.2.3"));
    }
}

public class DiskMonitorTests
{
    [Fact]
    public void FormatBytes_works()
    {
        Assert.Equal("512 B", DiskMonitor.FormatBytes(512));
        Assert.Equal("1 KB", DiskMonitor.FormatBytes(1024));
        Assert.Equal("1.5 MB", DiskMonitor.FormatBytes((long)(1.5 * 1024 * 1024)));
    }
}

public class GameLocatorTests
{
    [Fact]
    public void LooksLikeGameFolder_requires_exe()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgGame_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var loc = new GameLocator();
            Assert.False(loc.LooksLikeGameFolder(root));
            File.WriteAllText(Path.Combine(root, GameLocator.GameExeName), "x");
            Assert.True(loc.LooksLikeGameFolder(root));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void SuggestGameFolder_finds_parent_with_exe()
    {
        var game = Path.Combine(Path.GetTempPath(), "FusionRpgSuggestGame_" + Guid.NewGuid().ToString("N"));
        var launcherDir = Path.Combine(game, "FusionRpg");
        Directory.CreateDirectory(launcherDir);
        try
        {
            File.WriteAllText(Path.Combine(game, GameLocator.GameExeName), "x");
            var loc = new GameLocator();
            Assert.Equal(game, loc.SuggestGameFolder(launcherDir));
        }
        finally { try { Directory.Delete(game, true); } catch { } }
    }
}
