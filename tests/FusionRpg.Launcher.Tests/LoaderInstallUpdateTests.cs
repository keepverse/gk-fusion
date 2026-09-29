using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FusionRpg.Launcher.Services;

namespace FusionRpg.Launcher.Tests;

[Trait("VerificationId", "launcher.loader-install")]
public class LoaderInstallUpdateTests
{
    [Theory]
    [InlineData("BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip", true)]
    [InlineData("BepInEx-Unity.Mono-win-x64-6.0.0-pre.2.zip", false)]
    [InlineData("MelonLoader.x64.zip", true)]
    [InlineData("MelonLoader.Windows.x64.zip", true)]
    [InlineData("MelonLoader.x86.zip", false)]
    [InlineData("FusionRpg-win-x64.zip", true)]
    [InlineData("FusionRpg-win-x86.zip", false)]
    public void AssetMatches_uses_manifest_regexes(string name, bool expected)
    {
        var m = LoaderManifest.Default;
        var regex = name.StartsWith("BepInEx", StringComparison.Ordinal)
            ? m.BepInEx.AssetRegex
            : name.StartsWith("Melon", StringComparison.Ordinal)
                ? m.MelonLoader.AssetRegex
                : m.FusionRpg.AssetRegex;
        Assert.Equal(expected, LoaderManifest.AssetMatches(name, regex));
    }

    [Fact]
    public async Task OfficialReleaseDownloader_picks_matching_asset()
    {
        var json = """
            {
              "tag_name": "v6.0.0-pre.2",
              "html_url": "https://example.test/r",
              "assets": [
                { "name": "BepInEx-Unity.Mono-win-x64-6.0.0-pre.2.zip", "browser_download_url": "https://example.test/mono.zip" },
                { "name": "BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip", "browser_download_url": "https://example.test/il2cpp.zip" }
              ]
            }
            """;
        using var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        using var http = new HttpClient(handler);
        var dl = new OfficialReleaseDownloader(http);
        var asset = await dl.ResolveAssetAsync("BepInEx", "BepInEx", "v6.0.0-pre.2",
            LoaderManifest.Default.BepInEx.AssetRegex);
        Assert.Equal("BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip", asset.Name);
        Assert.Equal("https://example.test/il2cpp.zip", asset.DownloadUrl);
        Assert.Equal("v6.0.0-pre.2", asset.TagName);
    }

    [Fact]
    public async Task ModLoaderInstaller_extracts_bepinex_and_refuses_dual_load()
    {
        var game = CreateTempDir("game-bep");
        try
        {
            var zipBytes = MakeZip(root =>
            {
                WriteDummy(root, "winhttp.dll");
                WriteDummy(Path.Combine(root, "BepInEx", "core"), "core.txt");
            });

            using var handler = ReleaseZipHandler("BepInEx-Unity.IL2CPP-win-x64-test.zip", zipBytes);
            using var http = new HttpClient(handler);
            var installer = new ModLoaderInstaller(new OfficialReleaseDownloader(http));
            var channel = LoaderManifest.Default.BepInEx;

            await installer.InstallBepInExAsync(game, channel);
            Assert.True(File.Exists(Path.Combine(game, "winhttp.dll")));
            Assert.True(Directory.Exists(Path.Combine(game, "BepInEx", "core")));

            var melonGame = CreateTempDir("game-ml");
            try
            {
                WriteDummy(melonGame, "version.dll");
                Directory.CreateDirectory(Path.Combine(melonGame, "MelonLoader"));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    installer.InstallBepInExAsync(melonGame, channel));
            }
            finally
            {
                TryDeleteDir(melonGame);
            }
        }
        finally
        {
            TryDeleteDir(game);
        }
    }

    [Fact]
    public async Task ModLoaderInstaller_extracts_melonloader_and_refuses_when_bepinex_present()
    {
        var game = CreateTempDir("game-melon");
        try
        {
            var zipBytes = MakeZip(root =>
            {
                WriteDummy(root, "version.dll");
                WriteDummy(Path.Combine(root, "MelonLoader"), "ml.txt");
            });

            using var handler = ReleaseZipHandler("MelonLoader.x64.zip", zipBytes);
            using var http = new HttpClient(handler);
            var installer = new ModLoaderInstaller(new OfficialReleaseDownloader(http));
            var channel = new LoaderManifest.LoaderChannel
            {
                Owner = "LavaGang",
                Repo = "MelonLoader",
                Tag = "latest",
                AssetRegex = LoaderManifest.Default.MelonLoader.AssetRegex
            };

            await installer.InstallMelonLoaderAsync(game, channel);
            Assert.True(Directory.Exists(Path.Combine(game, "MelonLoader")));
            Assert.True(File.Exists(Path.Combine(game, "version.dll")));

            var bepGame = CreateTempDir("game-bep-block");
            try
            {
                WriteDummy(bepGame, "winhttp.dll");
                Directory.CreateDirectory(Path.Combine(bepGame, "BepInEx", "core"));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    installer.InstallMelonLoaderAsync(bepGame, channel));
            }
            finally
            {
                TryDeleteDir(bepGame);
            }
        }
        finally
        {
            TryDeleteDir(game);
        }
    }

    [Fact]
    public void FusionRpgUpdater_preserves_Server_data()
    {
        var install = CreateTempDir("frpg-install");
        var updatesDir = CreateTempDir("frpg-updates");
        try
        {
            Directory.CreateDirectory(Path.Combine(install, "Server", "data"));
            File.WriteAllText(Path.Combine(install, "Server", "data", "rpg-hot.sqlite"), "SAVE");
            File.WriteAllText(Path.Combine(install, "FusionRpg.Launcher.exe"), "old");

            var zipBytes = MakeZip(root =>
            {
                File.WriteAllText(Path.Combine(root, "FusionRpg.Launcher.exe"), "new");
                Directory.CreateDirectory(Path.Combine(root, "Server"));
                File.WriteAllText(Path.Combine(root, "Server", "FusionRpg.Server.exe"), "srv");
                Directory.CreateDirectory(Path.Combine(root, "Server", "data"));
                File.WriteAllText(Path.Combine(root, "Server", "data", "rpg-hot.sqlite"), "WIPED");
            });

            var zipPath = Path.Combine(updatesDir, "FusionRpg-win-x64.zip");
            File.WriteAllBytes(zipPath, zipBytes);

            var updater = new FusionRpgUpdater(updatesDir: updatesDir);
            var script = updater.PrepareApply(zipPath, install, stopGame: false);
            Assert.True(File.Exists(script));

            var stages = Directory.GetDirectories(updatesDir, "stage-*");
            Assert.NotEmpty(stages);
            var stagedData = Directory.GetFiles(stages[0], "rpg-hot.sqlite", SearchOption.AllDirectories).First();
            Assert.Equal("SAVE", File.ReadAllText(stagedData));
        }
        finally
        {
            TryDeleteDir(install);
            TryDeleteDir(updatesDir);
        }
    }

    [Fact]
    public void FindContentRoot_detects_nested_marker()
    {
        var stage = CreateTempDir("stage-nested");
        try
        {
            var nested = Path.Combine(stage, "BepInEx-Unity.IL2CPP-win-x64");
            Directory.CreateDirectory(Path.Combine(nested, "BepInEx"));
            var root = ModLoaderInstaller.FindContentRoot(stage, "BepInEx");
            Assert.Equal(nested, root);
        }
        finally
        {
            TryDeleteDir(stage);
        }
    }

    [Fact]
    public async Task ModLoaderInstaller_incomplete_bep_zip_throws()
    {
        var game = CreateTempDir("game-incomplete-bep");
        try
        {
            // Only core — missing winhttp.dll
            var zipBytes = MakeZip(root => WriteDummy(Path.Combine(root, "BepInEx", "core"), "core.txt"));
            using var handler = ReleaseZipHandler("BepInEx-Unity.IL2CPP-win-x64-test.zip", zipBytes);
            using var http = new HttpClient(handler);
            var installer = new ModLoaderInstaller(new OfficialReleaseDownloader(http));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                installer.InstallBepInExAsync(game, LoaderManifest.Default.BepInEx));
            Assert.Contains("winhttp.dll", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDeleteDir(game); }
    }

    [Fact]
    public async Task ModLoaderInstaller_incomplete_melon_zip_throws()
    {
        var game = CreateTempDir("game-incomplete-ml");
        try
        {
            // MelonLoader folder only — missing version.dll
            var zipBytes = MakeZip(root => WriteDummy(Path.Combine(root, "MelonLoader"), "ml.txt"));
            using var handler = ReleaseZipHandler("MelonLoader.x64.zip", zipBytes);
            using var http = new HttpClient(handler);
            var installer = new ModLoaderInstaller(new OfficialReleaseDownloader(http));
            var channel = new LoaderManifest.LoaderChannel
            {
                Owner = "LavaGang",
                Repo = "MelonLoader",
                Tag = "latest",
                AssetRegex = LoaderManifest.Default.MelonLoader.AssetRegex
            };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                installer.InstallMelonLoaderAsync(game, channel));
            Assert.Contains("version.dll", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDeleteDir(game); }
    }

    [Fact]
    public async Task ModLoaderInstaller_refuses_bep_when_partial_melon_present()
    {
        var game = CreateTempDir("game-partial-ml");
        try
        {
            File.WriteAllText(Path.Combine(game, "version.dll"), "x");
            var zipBytes = MakeZip(root =>
            {
                WriteDummy(root, "winhttp.dll");
                WriteDummy(Path.Combine(root, "BepInEx", "core"), "core.txt");
            });
            using var handler = ReleaseZipHandler("BepInEx-Unity.IL2CPP-win-x64-test.zip", zipBytes);
            using var http = new HttpClient(handler);
            var installer = new ModLoaderInstaller(new OfficialReleaseDownloader(http));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                installer.InstallBepInExAsync(game, LoaderManifest.Default.BepInEx));
        }
        finally { TryDeleteDir(game); }
    }

    [Fact]
    public void FusionRpgUpdater_nested_zip_preserves_data_and_writes_bootstrap()
    {
        var install = CreateTempDir("frpg-nested-install");
        var updatesDir = CreateTempDir("frpg-nested-updates");
        try
        {
            Directory.CreateDirectory(Path.Combine(install, "Server", "data"));
            File.WriteAllText(Path.Combine(install, "Server", "data", "rpg-hot.sqlite"), "SAVE");
            File.WriteAllText(Path.Combine(install, "FusionRpg.Launcher.exe"), "old");

            var zipBytes = MakeZip(root =>
            {
                var inner = Path.Combine(root, "FusionRpg");
                Directory.CreateDirectory(inner);
                File.WriteAllText(Path.Combine(inner, "FusionRpg.Launcher.exe"), "new");
                Directory.CreateDirectory(Path.Combine(inner, "Server", "data"));
                File.WriteAllText(Path.Combine(inner, "Server", "data", "rpg-hot.sqlite"), "WIPED");
            });

            var zipPath = Path.Combine(updatesDir, "FusionRpg-win-x64.zip");
            File.WriteAllBytes(zipPath, zipBytes);

            var updater = new FusionRpgUpdater(updatesDir: updatesDir);
            var script = updater.PrepareApply(zipPath, install, stopGame: true, launcherPid: 424242);
            var scriptText = File.ReadAllText(script);
            Assert.Contains("robocopy", scriptText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("taskkill", scriptText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Get-Process -Id 424242", scriptText, StringComparison.Ordinal);
            Assert.Contains("forcekill", scriptText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("taskkill /F /PID 424242", scriptText, StringComparison.Ordinal);

            var stages = Directory.GetDirectories(updatesDir, "stage-*");
            var stagedData = Directory.GetFiles(stages[0], "rpg-hot.sqlite", SearchOption.AllDirectories).First();
            Assert.Equal("SAVE", File.ReadAllText(stagedData));
        }
        finally
        {
            TryDeleteDir(install);
            TryDeleteDir(updatesDir);
        }
    }

    [Fact]
    public void FusionRpgUpdater_missing_launcher_exe_throws()
    {
        var install = CreateTempDir("frpg-bad-install");
        var updatesDir = CreateTempDir("frpg-bad-updates");
        try
        {
            File.WriteAllText(Path.Combine(install, "FusionRpg.Launcher.exe"), "old");
            var zipBytes = MakeZip(root => File.WriteAllText(Path.Combine(root, "README.txt"), "no exe"));
            var zipPath = Path.Combine(updatesDir, "FusionRpg-win-x64.zip");
            File.WriteAllBytes(zipPath, zipBytes);
            var updater = new FusionRpgUpdater(updatesDir: updatesDir);
            Assert.Throws<InvalidOperationException>(() => updater.PrepareApply(zipPath, install));
        }
        finally
        {
            TryDeleteDir(install);
            TryDeleteDir(updatesDir);
        }
    }

    [Fact]
    public async Task FusionRpgUpdater_DownloadLatestAsync_happy_path()
    {
        var updatesDir = CreateTempDir("frpg-dl");
        try
        {
            var zipBytes = MakeZip(root => File.WriteAllText(Path.Combine(root, "FusionRpg.Launcher.exe"), "x"));
            using var handler = ReleaseZipHandler("FusionRpg-win-x64.zip", zipBytes);
            using var http = new HttpClient(handler);
            var updater = new FusionRpgUpdater(new OfficialReleaseDownloader(http), updatesDir);
            var (zip, tag) = await updater.DownloadLatestAsync(LoaderManifest.Default.FusionRpg);
            Assert.Equal("v1.0.0-test", tag);
            Assert.True(File.Exists(zip));
            Assert.Equal("FusionRpg-win-x64.zip", Path.GetFileName(zip));
        }
        finally { TryDeleteDir(updatesDir); }
    }

    [Fact]
    public async Task OfficialReleaseDownloader_no_matching_asset_throws()
    {
        var json = """
            {
              "tag_name": "v1",
              "html_url": "https://example.test/r",
              "assets": [
                { "name": "other.zip", "browser_download_url": "https://example.test/other.zip" }
              ]
            }
            """;
        using var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        using var http = new HttpClient(handler);
        var dl = new OfficialReleaseDownloader(http);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dl.ResolveAssetAsync("o", "r", "latest", @"^FusionRpg-win-x64\.zip$"));
    }

    [Fact]
    public async Task OfficialReleaseDownloader_cancel_mid_download_throws()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new StubHandler((req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/releases/", StringComparison.Ordinal))
            {
                var releaseJson = JsonSerializer.Serialize(new
                {
                    tag_name = "v1",
                    html_url = "https://example.test/r",
                    assets = new[]
                    {
                        new { name = "FusionRpg-win-x64.zip", browser_download_url = "https://example.test/asset.zip" }
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                };
            }

            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            };
        });
        using var http = new HttpClient(handler);
        var dl = new OfficialReleaseDownloader(http);
        var asset = await dl.ResolveAssetAsync("o", "r", "latest", @"^FusionRpg-win-x64\.zip$");
        var dest = Path.Combine(Path.GetTempPath(), "frpg-cancel-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                dl.DownloadAsync(asset.DownloadUrl, dest, ct: cts.Token));
        }
        finally
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch { /* ignore */ }
            try { if (File.Exists(dest + ".partial")) File.Delete(dest + ".partial"); } catch { /* ignore */ }
        }
    }

    static StubHandler ReleaseZipHandler(string assetName, byte[] zipBytes)
    {
        var releaseJson = JsonSerializer.Serialize(new
        {
            tag_name = "v1.0.0-test",
            html_url = "https://example.test/r",
            assets = new[]
            {
                new { name = assetName, browser_download_url = "https://example.test/asset.zip" }
            }
        });

        return new StubHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/releases/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                };
            }

            if (req.RequestUri.AbsoluteUri.Contains("asset.zip", StringComparison.Ordinal))
            {
                var msg = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(zipBytes)
                };
                msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                msg.Content.Headers.ContentLength = zipBytes.Length;
                return msg;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    static byte[] MakeZip(Action<string> populate)
    {
        var dir = CreateTempDir("zip-src");
        try
        {
            populate(dir);
            var zipPath = Path.Combine(Path.GetTempPath(), "frpg-test-" + Guid.NewGuid().ToString("N") + ".zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(dir, zipPath);
            var bytes = File.ReadAllBytes(zipPath);
            File.Delete(zipPath);
            return bytes;
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    static string CreateTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), "FusionRpgTests-" + prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    static void WriteDummy(string dir, string fileName)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), "x");
    }

    static void TryDeleteDir(string path)
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

    sealed class StubHandler : HttpMessageHandler
    {
        readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _impl;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> impl) =>
            _impl = impl;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_impl(request, cancellationToken));
    }
}
