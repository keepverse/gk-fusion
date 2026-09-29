using FusionRpg.Launcher.Services;

namespace FusionRpg.Launcher.Tests;

[Trait("VerificationId", "launcher.loader-install")]
public class LoaderManifestLoadTests
{
    [Fact]
    public void LoadFromLauncherDir_missing_file_returns_Default()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FusionRpgManifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var m = LoaderManifest.LoadFromLauncherDir(dir);
            Assert.Equal(LoaderManifest.Default.BepInEx.Tag, m.BepInEx.Tag);
            Assert.Equal(LoaderManifest.Default.FusionRpg.AssetRegex, m.FusionRpg.AssetRegex);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void LoadFromLauncherDir_corrupt_json_returns_Default()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FusionRpgManifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "loader-manifest.json"), "{ not json");
            var m = LoaderManifest.LoadFromLauncherDir(dir);
            Assert.Equal("BepInEx", m.BepInEx.Owner);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void LoadFromLauncherDir_partial_json_fills_empty_channels()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FusionRpgManifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "loader-manifest.json"),
                """{"fusionRpg":{"owner":"letuhao","repo":"plant-vs-zombie-rise-of-summoner","assetRegex":""}}""");
            var m = LoaderManifest.LoadFromLauncherDir(dir);
            Assert.False(string.IsNullOrWhiteSpace(m.BepInEx.AssetRegex));
            Assert.False(string.IsNullOrWhiteSpace(m.FusionRpg.AssetRegex));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
