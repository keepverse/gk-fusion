using FusionRpg.Launcher.Services;

namespace FusionRpg.Launcher.Tests;

public class GameProfileCatalogTests
{
    [Fact]
    public void Detect_3_9_by_GameAssembly_length()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgProf39_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // Fingerprint size from game-profiles.json / BuiltIn
        using (var fs = new FileStream(Path.Combine(root, "GameAssembly.dll"), FileMode.Create, FileAccess.Write))
            fs.SetLength(57717248);
        try
        {
            var id = new GameProfileCatalog().Detect(root);
            Assert.Equal(GameProfileCatalog.Profile39, id);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Detect_3_8_1_by_GameAssembly_length()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgProf381_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using (var fs = new FileStream(Path.Combine(root, "GameAssembly.dll"), FileMode.Create, FileAccess.Write))
            fs.SetLength(47964672);
        try
        {
            var id = new GameProfileCatalog().Detect(root);
            Assert.Equal(GameProfileCatalog.DefaultProfileId, id);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Override_wins_over_fingerprint()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgProfOv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using (var fs = new FileStream(Path.Combine(root, "GameAssembly.dll"), FileMode.Create, FileAccess.Write))
            fs.SetLength(57717248);
        try
        {
            var id = new GameProfileCatalog().Detect(root, GameProfileCatalog.DefaultProfileId);
            Assert.Equal(GameProfileCatalog.DefaultProfileId, id);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Melon_39_drop_uses_scoped_dll_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgDrop39_" + Guid.NewGuid().ToString("N"));
        var launcher = Path.Combine(root, "FusionRpg");
        var drop = Path.Combine(launcher, "DropIntoGame", "pvzrh-3.9", "MelonLoader");
        Directory.CreateDirectory(drop);
        File.WriteAllText(Path.Combine(drop, "FusionRpg.Injector.MelonLoader.39.dll"), "x");
        try
        {
            var host = ModLoaderHosts.MelonLoader;
            Assert.Equal("FusionRpg.Injector.MelonLoader.39.dll", host.InjectorDllNameFor(GameProfileCatalog.Profile39));
            Assert.True(host.HasDropPayload(launcher, GameProfileCatalog.Profile39));
            Assert.Equal(drop, host.DropPayloadDir(launcher, GameProfileCatalog.Profile39));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
