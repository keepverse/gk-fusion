using FusionRpg.Injector.Host;
using FusionRpg.Launcher.Services;

namespace FusionRpg.Launcher.Tests;

public class MelonHostGapTests
{
    [Fact]
    public void Melon_uninstall_removes_only_owned_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgMelonUn_" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(root, "Mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "ForeignMod.dll"), "x");
        File.WriteAllText(Path.Combine(mods, "FusionRpg.Injector.MelonLoader.dll"), "x");
        File.WriteAllText(Path.Combine(mods, "FusionRpg.Core.dll"), "x");
        File.WriteAllText(Path.Combine(mods, MelonLoaderHost.CfgFileName), "ServerUrl=http://127.0.0.1:5088");
        try
        {
            var n = new PluginInstaller().UninstallPlugin(mods, ModLoaderHosts.MelonLoader);
            Assert.Equal(3, n);
            Assert.True(File.Exists(Path.Combine(mods, "ForeignMod.dll")));
            Assert.False(File.Exists(Path.Combine(mods, "FusionRpg.Injector.MelonLoader.dll")));
            Assert.False(File.Exists(Path.Combine(mods, MelonLoaderHost.CfgFileName)));
            Assert.True(Directory.Exists(mods));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Bep_uninstall_still_wipes_dedicated_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgBepUn_" + Guid.NewGuid().ToString("N"));
        var plugin = Path.Combine(root, "BepInEx", "plugins", "FusionRpg");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "FusionRpg.Injector.dll"), "a");
        File.WriteAllText(Path.Combine(plugin, "FusionRpg.Core.dll"), "b");
        try
        {
            var n = new PluginInstaller().UninstallPlugin(plugin, ModLoaderHosts.BepInEx);
            Assert.Equal(2, n);
            Assert.False(Directory.Exists(plugin));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Melon_DropPayloadDir_requires_injector_dll()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgMelonDrop_" + Guid.NewGuid().ToString("N"));
        var launcher = Path.Combine(root, "FusionRpg");
        var empty = Path.Combine(launcher, "DropIntoGame", "MelonLoader");
        Directory.CreateDirectory(empty);
        try
        {
            var host = ModLoaderHosts.MelonLoader;
            Assert.False(host.HasDropPayload(launcher));
            File.WriteAllText(Path.Combine(empty, host.InjectorDllName), "dll");
            Assert.True(host.HasDropPayload(launcher));
            Assert.Equal(empty, host.DropPayloadDir(launcher));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Melon_LogPath_prefers_Logs_Latest()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgMelonLog_" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "MelonLoader", "Logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "Latest.log"), "x");
        try
        {
            var path = ModLoaderHosts.MelonLoader.LogPath(root);
            Assert.Equal(Path.Combine(logs, "Latest.log"), path);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task Melon_PlayAsync_happy_path_copies_to_Mods_and_writes_cfg()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgMelonPlay_" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "game");
        var drop = Path.Combine(root, "DropIntoGame", "MelonLoader");
        var serverDir = Path.Combine(root, "Server");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(drop);
        Directory.CreateDirectory(serverDir);
        File.WriteAllText(Path.Combine(game, GameLocator.GameExeName), "x");
        File.WriteAllText(Path.Combine(game, "version.dll"), "x");
        Directory.CreateDirectory(Path.Combine(game, "MelonLoader"));
        File.WriteAllText(Path.Combine(drop, "FusionRpg.Injector.MelonLoader.dll"), "dll");
        File.WriteAllText(Path.Combine(drop, "FusionRpg.Core.dll"), "core");
        File.WriteAllText(Path.Combine(serverDir, ProcessSupervisor.ServerExeName), "exe");

        var health = new HealthMonitor(new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", System.Text.Encoding.UTF8, "application/json")
            })));
        var procs = new FakeProcs { ServerUp = true };
        var ports = new FixedPortPicker(5088, reused: true);
        var session = new PlaySession(procs: procs, health: health, ports: ports);
        session.RestorePort(5088);

        try
        {
            var (ok, msg) = await session.PlayAsync(game, root, new LauncherSettings { LastPort = 5088 });
            Assert.True(ok, msg);
            Assert.True(File.Exists(Path.Combine(game, "Mods", "FusionRpg.Injector.MelonLoader.dll")));
            var cfg = Path.Combine(game, "Mods", MelonLoaderHost.CfgFileName);
            Assert.True(File.Exists(cfg));
            Assert.Contains("ServerUrl=http://127.0.0.1:5088", File.ReadAllText(cfg));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task Melon_PlayAsync_fails_when_drop_missing()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgMelonMiss_" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, GameLocator.GameExeName), "x");
        File.WriteAllText(Path.Combine(game, "version.dll"), "x");
        Directory.CreateDirectory(Path.Combine(game, "MelonLoader"));
        Directory.CreateDirectory(Path.Combine(root, "DropIntoGame", "MelonLoader")); // empty
        try
        {
            var session = new PlaySession(procs: new FakeProcs());
            var (ok, msg) = await session.PlayAsync(game, root, new LauncherSettings());
            Assert.False(ok);
            Assert.Contains("Melon drop missing", msg, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void RestartGame_refuses_when_Host_null_Both()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgBoth_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, GameLocator.GameExeName), "x");
        File.WriteAllText(Path.Combine(root, "winhttp.dll"), "x");
        Directory.CreateDirectory(Path.Combine(root, "BepInEx", "core"));
        File.WriteAllText(Path.Combine(root, "version.dll"), "x");
        Directory.CreateDirectory(Path.Combine(root, "MelonLoader"));
        try
        {
            var session = new PlaySession(procs: new FakeProcs());
            session.RestorePort(5088);
            var (ok, msg) = session.RestartGame(root);
            Assert.False(ok);
            Assert.Contains("refusing", msg, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(root, "BepInEx", "config")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void PlayerPackProbe_accepts_Melon_nested_drop()
    {
        var pack = Path.Combine(Path.GetTempPath(), "FusionRpgPackMelon_" + Guid.NewGuid().ToString("N"));
        var drop = Path.Combine(pack, "DropIntoGame", "MelonLoader");
        Directory.CreateDirectory(drop);
        File.WriteAllText(Path.Combine(drop, "FusionRpg.Injector.MelonLoader.dll"), "x");
        // Minimal other required files for layout — only testing HasInjectorDrop via layout helper path
        File.WriteAllText(Path.Combine(pack, "FusionRpg.Launcher.exe"), "x");
        Directory.CreateDirectory(Path.Combine(pack, "Server", "wwwroot"));
        File.WriteAllText(Path.Combine(pack, "Server", "FusionRpg.Server.exe"), "x");
        File.WriteAllText(Path.Combine(pack, "Server", "wwwroot", "index.html"), "x");
        // The server's content trees are part of the layout contract (PlayerPackProbe.RequiredContentDirs,
        // CS-F2): a pack without them boots on the code fallback.
        Directory.CreateDirectory(Path.Combine(pack, "Server", "data", "seed"));
        Directory.CreateDirectory(Path.Combine(pack, "Server", "data", "tuning"));
        // CS-F3 (owner ruling 2026-09-23) added this third tree to `PlayerPackProbe.RequiredContentDirs`
        // (`Server/data/generated/creatures` — the concrete species tree ships, and the boot self-heals the
        // roster from it once). The ruling updated the sibling fixture (`PlayerPackProbeTests.cs:89`, which
        // also asserts this very message at `:61`) and missed this one, which is why the layout step below
        // reported it missing and CI ran red. One line, copied from the sibling.
        Directory.CreateDirectory(Path.Combine(pack, "Server", "data", "generated", "creatures"));
        File.WriteAllText(Path.Combine(pack, "loader-manifest.json"), "{}");
        File.WriteAllText(Path.Combine(pack, "PLAYERS.txt"), "x");
        File.WriteAllText(Path.Combine(pack, "LICENSE"), "x");
        try
        {
            var result = new PlayerPackProbe().Run(pack);
            var layout = result.Steps.First(s => s.Name == "layout");
            Assert.True(layout.Ok, layout.Message);
        }
        finally { try { Directory.Delete(pack, true); } catch { } }
    }
}

public class FileRpgConfigTests
{
    [Fact]
    public void Parses_defaults_when_missing()
    {
        var cfg = new FileRpgConfig(Path.Combine(Path.GetTempPath(), "no-such-fusionrpg-" + Guid.NewGuid() + ".cfg"));
        Assert.Equal(FileRpgConfig.FallbackServerUrl, cfg.ServerUrl);
        Assert.False(cfg.PersistCheats);
        Assert.False(cfg.EnableUnsafeHitPatches);
    }

    [Fact]
    public void Parses_keys_and_ignores_comments()
    {
        var path = Path.Combine(Path.GetTempPath(), "fusionrpg-cfg-" + Guid.NewGuid().ToString("N") + ".cfg");
        File.WriteAllText(path,
            "# comment\n" +
            "ServerUrl=http://127.0.0.1:5099\n" +
            "PersistCheats=true\n" +
            "; another\n" +
            "EnableUnsafeHitPatches=true\n");
        try
        {
            var cfg = new FileRpgConfig(path);
            Assert.Equal("http://127.0.0.1:5099", cfg.ServerUrl);
            Assert.True(cfg.PersistCheats);
            Assert.True(cfg.EnableUnsafeHitPatches);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
