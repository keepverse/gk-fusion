using FusionRpg.Launcher.Services;

namespace FusionRpg.Launcher.Tests;

public class PlayerPackProbeTests
{
    [Fact]
    public void Run_ok_on_minimal_fake_pack()
    {
        var pack = CreateFakePack(includeInjector: true);
        try
        {
            var result = new PlayerPackProbe().Run(pack);
            Assert.True(result.Ok, result.ToJson());
            Assert.Contains(result.Steps, s => s.Name == "layout" && s.Ok);
            Assert.Contains(result.Steps, s => s.Name == "manifest" && s.Ok);
            Assert.Contains(result.Steps, s => s.Name == "loader_plugin" && s.Ok);
            Assert.Contains(result.Steps, s => s.Name == "dual_load" && s.Ok);
            Assert.Contains(result.Steps, s => s.Name == "update_preserve" && s.Ok);
        }
        finally
        {
            TryDelete(pack);
        }
    }

    [Fact]
    public void Run_fails_layout_when_injector_missing()
    {
        var pack = CreateFakePack(includeInjector: false);
        try
        {
            var result = new PlayerPackProbe().Run(pack);
            Assert.False(result.Ok);
            var layout = result.Steps.First(s => s.Name == "layout");
            Assert.False(layout.Ok);
            Assert.Contains("FusionRpg.Injector.dll", layout.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(pack);
        }
    }

    [Fact]
    public void Run_fails_layout_when_the_server_content_tree_is_missing()
    {
        // CS-F2's defect: gk-core/scripts/publish_player.py deleted Server\data after `dotnet publish` had
        // put the `<Content Link="data\...">` tree there, so a player install booted on the code
        // fallback with the entire content layer inert. Nothing failed, because no check named the
        // tree. This is that check: the same pack without it must be refused, by name.
        var pack = CreateFakePack(includeInjector: true, includeContent: false);
        try
        {
            var result = new PlayerPackProbe().Run(pack);
            Assert.False(result.Ok);
            var layout = result.Steps.First(s => s.Name == "layout");
            Assert.False(layout.Ok);
            Assert.Contains(Path.Combine("Server", "data", "seed"), layout.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Path.Combine("Server", "data", "tuning"), layout.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Path.Combine("Server", "data", "generated", "creatures"), layout.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(pack);
        }
    }

    [Fact]
    public void Run_fails_when_pack_dir_missing()
    {
        var result = new PlayerPackProbe().Run(Path.Combine(Path.GetTempPath(), "no-pack-" + Guid.NewGuid().ToString("N")));
        Assert.False(result.Ok);
        Assert.Contains(result.Steps, s => s.Name == "layout" && !s.Ok);
    }

    static string CreateFakePack(bool includeInjector, bool includeContent = true)
    {
        var pack = Path.Combine(Path.GetTempPath(), "FusionRpgFakePack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pack);
        Directory.CreateDirectory(Path.Combine(pack, "Server", "wwwroot"));
        Directory.CreateDirectory(Path.Combine(pack, "DropIntoGame"));
        if (includeContent)
        {
            // The content trees the server resolves beside its exe at boot. The pack layout
            // requires them; PlayerPackProbe.RequiredContentDirs carries the why.
            Directory.CreateDirectory(Path.Combine(pack, "Server", "data", "seed"));
            Directory.CreateDirectory(Path.Combine(pack, "Server", "data", "tuning"));
            Directory.CreateDirectory(Path.Combine(pack, "Server", "data", "generated", "creatures"));
        }

        File.WriteAllText(Path.Combine(pack, "FusionRpg.Launcher.exe"), "launcher");
        File.WriteAllText(Path.Combine(pack, "Server", "FusionRpg.Server.exe"), "server");
        File.WriteAllText(Path.Combine(pack, "Server", "wwwroot", "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(pack, "PLAYERS.txt"), "players");
        File.WriteAllText(Path.Combine(pack, "LICENSE"), "AGPL");

        // The launcher's own WebView2, without which the F10 overlay cannot open.
        File.WriteAllText(Path.Combine(pack, "Microsoft.Web.WebView2.Core.dll"), "wv2");
        File.WriteAllText(Path.Combine(pack, "WebView2Loader.dll"), "wv2native");

        if (includeInjector)
        {
            // A shippable drop: the injector, the overlay code it must contain, and WebView2 beside
            // it — PluginInstaller copies top-level files only. See PlayerPackOverlayProbeTests.
            WriteInjectorDrop(Path.Combine(pack, "DropIntoGame"));
            WriteInjectorDrop(Path.Combine(pack, "DropIntoGame", "pvzrh-3.9", "MelonLoader"));
        }

        var manifestSrc = FindLoaderManifest();
        File.Copy(manifestSrc, Path.Combine(pack, "loader-manifest.json"), overwrite: true);
        return pack;
    }

    static void WriteInjectorDrop(string dir)
    {
        Directory.CreateDirectory(dir);
        var name = dir.Contains("MelonLoader", StringComparison.OrdinalIgnoreCase)
            ? "FusionRpg.Injector.MelonLoader.39.dll"
            : "FusionRpg.Injector.dll";
        File.WriteAllText(Path.Combine(dir, name), "inj OverlayViewHost OverlaySwitchGui");
        File.WriteAllText(Path.Combine(dir, "Microsoft.Web.WebView2.Core.dll"), "wv2");
        File.WriteAllText(Path.Combine(dir, "WebView2Loader.dll"), "wv2native");
    }

    static string FindLoaderManifest()
    {
        var fromOutput = Path.Combine(AppContext.BaseDirectory, "loader-manifest.json");
        if (File.Exists(fromOutput)) return fromOutput;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "FusionRpg.Launcher", "loader-manifest.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("loader-manifest.json not found for test fixture.");
    }

    static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            /* ignore */
        }
    }
}
