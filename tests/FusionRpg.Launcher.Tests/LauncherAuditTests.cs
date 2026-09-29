using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using FusionRpg.Launcher.Services;

namespace FusionRpg.Launcher.Tests;

public class HealthMonitorTests
{
    [Fact]
    public async Task CheckAsync_parses_ok_health()
    {
        var handler = new StubHandler(_ =>
            JsonResponse(200, """{"ok":true,"injectorConnected":true,"source":"injector"}"""));
        var mon = new HealthMonitor(new HttpClient(handler));
        var snap = await mon.CheckAsync("http://127.0.0.1:5088");
        Assert.True(snap.Reachable);
        Assert.True(snap.Ok);
        Assert.True(snap.InjectorConnected);
        Assert.Equal("injector", snap.Source);
    }

    [Fact]
    public async Task CheckAsync_404_not_reachable()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var mon = new HealthMonitor(new HttpClient(handler));
        var snap = await mon.CheckAsync("http://127.0.0.1:5088");
        Assert.False(snap.Reachable);
        Assert.False(snap.Ok);
    }

    [Fact]
    public async Task CheckAsync_bad_json_not_reachable()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not-json") });
        var mon = new HealthMonitor(new HttpClient(handler));
        var snap = await mon.CheckAsync("http://127.0.0.1:5088");
        Assert.False(snap.Reachable);
        Assert.NotNull(snap.RawError);
    }

    [Fact]
    public async Task CheckAsync_exception_not_reachable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("boom"));
        var mon = new HealthMonitor(new HttpClient(handler));
        var snap = await mon.CheckAsync("http://127.0.0.1:5088");
        Assert.False(snap.Reachable);
        Assert.Contains("boom", snap.RawError);
    }

    [Fact]
    public void LooksLikeOurServer_true_when_ok()
    {
        var handler = new StubHandler(_ => JsonResponse(200, """{"ok":true}"""));
        var mon = new HealthMonitor(new HttpClient(handler));
        Assert.True(mon.LooksLikeOurServer(5088));
    }

    [Fact]
    public void LooksLikeOurServer_false_when_ok_false()
    {
        var handler = new StubHandler(_ => JsonResponse(200, """{"ok":false}"""));
        var mon = new HealthMonitor(new HttpClient(handler));
        Assert.False(mon.LooksLikeOurServer(5088));
    }

    static HttpResponseMessage JsonResponse(int code, string json) =>
        new((HttpStatusCode)code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class StubHandler : HttpMessageHandler
{
    readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(_fn(request));
}

public class PortPickerOwnershipTests
{
    [Fact]
    public void Hops_when_stranger_holds_preferred_even_if_our_process_elsewhere()
    {
        // Regression: old code returned reuse for 5088 if ANY FusionRpg.Server existed.
        var picker = new PortPicker();
        var r = picker.Pick(
            isPortFree: p => p == 5090,
            isOwnedByOurServer: p => p == 5090); // only 5090 is ours
        Assert.Equal(5090, r.Port);
        Assert.True(r.ReusedOurServer);
    }

    [Fact]
    public void Prefers_owned_lastGood_over_free_5088()
    {
        var picker = new PortPicker();
        var r = picker.Pick(
            lastGoodPort: 5105,
            isPortFree: p => p == 5088 || p == 5105,
            isOwnedByOurServer: p => p == 5105);
        Assert.Equal(5105, r.Port);
        Assert.True(r.ReusedOurServer);
    }
}

public class PluginInstallerExtraTests
{
    [Fact]
    public void NeedsInstallOrUpdate_missing_dst()
    {
        var root = TempRoot();
        var drop = Path.Combine(root, "DropIntoGame");
        var plugin = Path.Combine(root, "plugins");
        Directory.CreateDirectory(drop);
        File.WriteAllText(Path.Combine(drop, PluginInstaller.InjectorDllName), "a");
        try
        {
            Assert.True(new PluginInstaller().NeedsInstallOrUpdate(drop, plugin));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void NeedsInstallOrUpdate_false_when_src_missing()
    {
        var root = TempRoot();
        var drop = Path.Combine(root, "DropIntoGame");
        var plugin = Path.Combine(root, "plugins");
        Directory.CreateDirectory(drop);
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, PluginInstaller.InjectorDllName), "a");
        try
        {
            Assert.False(new PluginInstaller().NeedsInstallOrUpdate(drop, plugin));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void NeedsInstallOrUpdate_true_when_src_newer_or_larger()
    {
        var root = TempRoot();
        var drop = Path.Combine(root, "DropIntoGame");
        var plugin = Path.Combine(root, "plugins");
        Directory.CreateDirectory(drop);
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, PluginInstaller.InjectorDllName), "old");
        File.WriteAllText(Path.Combine(drop, PluginInstaller.InjectorDllName), "newer-content");
        File.SetLastWriteTimeUtc(Path.Combine(drop, PluginInstaller.InjectorDllName), DateTime.UtcNow.AddMinutes(5));
        try
        {
            Assert.True(new PluginInstaller().NeedsInstallOrUpdate(drop, plugin));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void ResolveDropIntoGameDir_finds_sibling()
    {
        var root = TempRoot();
        var launcher = Path.Combine(root, "FusionRpg");
        var drop = Path.Combine(root, "DropIntoGame");
        Directory.CreateDirectory(launcher);
        Directory.CreateDirectory(drop);
        File.WriteAllText(Path.Combine(drop, PluginInstaller.InjectorDllName), "x");
        try
        {
            // parent layout when DropIntoGame is beside launcher folder (DLL required)
            Assert.Equal(drop, new PluginInstaller().ResolveDropIntoGameDir(launcher));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Uninstall_removes_files()
    {
        var root = TempRoot();
        var plugin = Path.Combine(root, "FusionRpg");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "a.dll"), "x");
        File.WriteAllText(Path.Combine(plugin, "b.dll"), "y");
        try
        {
            Assert.Equal(2, new PluginInstaller().UninstallPlugin(plugin, ModLoaderHosts.BepInEx));
            Assert.False(Directory.Exists(plugin));
        }
        finally { TryDelete(root); }
    }

    static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "FusionRpgInst_" + Guid.NewGuid().ToString("N"));

    static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); } catch { }
    }
}

public class LauncherSettingsTests
{
    [Fact]
    public void RoundTrip_via_path()
    {
        var path = Path.Combine(Path.GetTempPath(), "FusionRpgSettings_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new LauncherSettings { GameFolder = @"C:\Games\PVZ", LastPort = 5099, MinimizeToTray = false };
            s.Save(path);
            var loaded = LauncherSettings.Load(path);
            Assert.Equal(@"C:\Games\PVZ", loaded.GameFolder);
            Assert.Equal(5099, loaded.LastPort);
            Assert.False(loaded.MinimizeToTray);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Load_missing_returns_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "no-such-fusionrpg-" + Guid.NewGuid().ToString("N") + ".json");
        var s = LauncherSettings.Load(path);
        Assert.Null(s.GameFolder);
        Assert.Null(s.LastPort);
        Assert.True(s.MinimizeToTray);
        Assert.False(s.TrustAcknowledged);
        Assert.False(s.WindowsSecurityPrepared);
    }

    [Fact]
    public void RoundTrip_trust_flags()
    {
        var path = Path.Combine(Path.GetTempPath(), "FusionRpgSettingsTrust_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new LauncherSettings { TrustAcknowledged = true, WindowsSecurityPrepared = true };
            s.Save(path);
            var loaded = LauncherSettings.Load(path);
            Assert.True(loaded.TrustAcknowledged);
            Assert.True(loaded.WindowsSecurityPrepared);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Load_user_store_ignores_temp_FusionRpg_paths()
    {
        var path = Path.Combine(Path.GetTempPath(), "FusionRpgSettingsEphemeral_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var junk = Path.Combine(Path.GetTempPath(), "FusionRpgPlay2_deadbeef", "game");
            var s = new LauncherSettings { GameFolder = junk, PersistToUserStore = false };
            s.Save(path);
            var loaded = LauncherSettings.Load(path, persistToUserStore: true);
            Assert.Null(loaded.GameFolder);
            Assert.True(loaded.PersistToUserStore);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Save_without_PersistToUserStore_does_not_write_AppData()
    {
        var before = File.Exists(LauncherSettings.SettingsPath)
            ? File.ReadAllText(LauncherSettings.SettingsPath)
            : null;
        try
        {
            var s = new LauncherSettings
            {
                GameFolder = @"C:\Games\ShouldNotPersist",
                PersistToUserStore = false
            };
            s.Save(); // must no-op
            var after = File.Exists(LauncherSettings.SettingsPath)
                ? File.ReadAllText(LauncherSettings.SettingsPath)
                : null;
            Assert.Equal(before, after);
        }
        finally
        {
            // leave AppData as we found it
        }
    }

    [Fact]
    public void IsEphemeralTestPath_detects_temp_FusionRpg()
    {
        var p = Path.Combine(Path.GetTempPath(), "FusionRpgPlay2_abc", "game");
        Assert.True(LauncherSettings.IsEphemeralTestPath(p));
        Assert.False(LauncherSettings.IsEphemeralTestPath(@"C:\Games\PVZ-Fusion-3.9"));
    }
}

public class ProcessSupervisorPathTests
{
    [Fact]
    public void ResolveServerExe_prefers_nested_Server()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgProc_" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "Server");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, ProcessSupervisor.ServerExeName), "x");
        File.WriteAllText(Path.Combine(root, ProcessSupervisor.ServerExeName), "y");
        try
        {
            var exe = new ProcessSupervisor().ResolveServerExe(root);
            Assert.Equal(Path.Combine(nested, ProcessSupervisor.ServerExeName), exe);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ResolveServerExe_falls_back_to_sibling()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgProc2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ProcessSupervisor.ServerExeName), "y");
        try
        {
            var exe = new ProcessSupervisor().ResolveServerExe(root);
            Assert.Equal(Path.Combine(root, ProcessSupervisor.ServerExeName), exe);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void BepInExLogPath_joins()
    {
        Assert.Equal(
            Path.Combine("G", "BepInEx", "LogOutput.log"),
            ProcessSupervisor.BepInExLogPath("G"));
    }
}

public class GitHubReleaseHttpTests
{
    [Fact]
    public async Task GetLatest_404_means_not_found()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new GitHubReleaseClient(new HttpClient(handler));
        var rel = await client.GetLatestAsync();
        Assert.False(rel.Found);
    }

    [Fact]
    public async Task GetLatest_200_parses_tag()
    {
        var json = """{"tag_name":"v1.2.3","html_url":"https://example/r","name":"Release","body":"notes"}""";
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        var client = new GitHubReleaseClient(new HttpClient(handler));
        var rel = await client.GetLatestAsync();
        Assert.True(rel.Found);
        Assert.Equal("v1.2.3", rel.TagName);
        Assert.Equal("https://example/r", rel.HtmlUrl);
        Assert.False(rel.HasPreferredZip);
    }

    [Fact]
    public async Task GetLatest_200_picks_FusionRpg_zip_asset()
    {
        var json = """
            {
              "tag_name":"v1.2.3",
              "html_url":"https://example/r",
              "assets":[
                {"name":"notes.txt","browser_download_url":"https://example/notes.txt"},
                {"name":"FusionRpg-win-x64.zip","browser_download_url":"https://example/FusionRpg-win-x64.zip"}
              ]
            }
            """;
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        var client = new GitHubReleaseClient(new HttpClient(handler));
        var rel = await client.GetLatestAsync();
        Assert.True(rel.HasPreferredZip);
        Assert.Equal("FusionRpg-win-x64.zip", rel.AssetName);
        Assert.Equal("https://example/FusionRpg-win-x64.zip", rel.DownloadUrl);
    }
}

sealed class FixedPortPicker : PortPicker
{
    readonly int _port;
    readonly bool _reused;
    public FixedPortPicker(int port, bool reused) { _port = port; _reused = reused; }
    public override Result Pick(int? lastGoodPort = null, Func<int, bool>? isPortFree = null, Func<int, bool>? isOwnedByOurServer = null) =>
        Result.For(_port, _reused);
}

sealed class FakeProcs : ProcessSupervisor
{
    public bool GameUp;
    public bool ServerUp;
    public int StartServerCalls;
    public int StartGameCalls;
    public int StopGameCalls;
    public string? LastGameUrl;

    public override bool IsGameRunning() => GameUp;
    public override bool IsServerRunning() => ServerUp;

    public override Process? StartServer(string launcherBaseDir, int port)
    {
        StartServerCalls++;
        ServerUp = true;
        return null;
    }

    public override Process StartGame(string gameExe, string serverUrl)
    {
        StartGameCalls++;
        LastGameUrl = serverUrl;
        GameUp = true;
        // Return a dummy exited process is hard; throw if caller uses the Process.
        // PlaySession ignores the return value.
        return Process.GetCurrentProcess();
    }

    public override void StopGame()
    {
        StopGameCalls++;
        GameUp = false;
    }

    public override void StopServer()
    {
        ServerUp = false;
    }
}

public class PlaySessionTests
{
    [Fact]
    public void RestorePort_sets_and_clears()
    {
        var s = new PlaySession();
        s.RestorePort(5091);
        Assert.Equal(5091, s.ActivePort);
        Assert.Equal("http://127.0.0.1:5091", s.ActiveUrl);
        s.RestorePort(null);
        Assert.Null(s.ActivePort);
    }

    [Fact]
    public async Task RestoreFromSettings_keeps_port_when_health_ok()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
            });
        var session = new PlaySession(health: new HealthMonitor(new HttpClient(handler)));
        var settings = new LauncherSettings { LastPort = 5088 };
        await session.RestoreFromSettingsAsync(settings);
        Assert.Equal(5088, session.ActivePort);
    }

    [Fact]
    public async Task RestoreFromSettings_clears_ActivePort_when_health_fails()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("down"));
        var session = new PlaySession(health: new HealthMonitor(new HttpClient(handler)));
        var settings = new LauncherSettings { LastPort = 5088 };
        await session.RestoreFromSettingsAsync(settings);
        Assert.Null(session.ActivePort);
    }

    [Fact]
    public async Task PlayAsync_fails_when_injector_missing_from_drop()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgPlay_" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "game");
        var drop = Path.Combine(root, "DropIntoGame");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(drop);
        File.WriteAllText(Path.Combine(game, GameLocator.GameExeName), "x");
        File.WriteAllText(Path.Combine(game, "winhttp.dll"), "x");
        Directory.CreateDirectory(Path.Combine(game, "BepInEx", "core"));
        // drop has no injector dll
        try
        {
            var session = new PlaySession(procs: new FakeProcs());
            var (ok, msg) = await session.PlayAsync(game, root, new LauncherSettings());
            Assert.False(ok);
            Assert.Contains("incomplete", msg, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task PlayAsync_skips_game_restart_when_same_url()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgPlay2_" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "game");
        var drop = Path.Combine(root, "DropIntoGame");
        var serverDir = Path.Combine(root, "Server");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(drop);
        Directory.CreateDirectory(serverDir);
        File.WriteAllText(Path.Combine(game, GameLocator.GameExeName), "x");
        File.WriteAllText(Path.Combine(game, "winhttp.dll"), "x");
        Directory.CreateDirectory(Path.Combine(game, "BepInEx", "core"));
        File.WriteAllText(Path.Combine(drop, PluginInstaller.InjectorDllName), "dll");
        File.WriteAllText(Path.Combine(serverDir, ProcessSupervisor.ServerExeName), "exe");

        var health = new HealthMonitor(new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
            })));
        var procs = new FakeProcs { GameUp = true, ServerUp = true };
        var ports = new FixedPortPicker(5088, reused: true);
        var session = new PlaySession(procs: procs, health: health, ports: ports);
        session.RestorePort(5088);

        var settings = new LauncherSettings { LastPort = 5088 };
        try
        {
            var (ok, msg) = await session.PlayAsync(game, root, settings);
            Assert.True(ok);
            Assert.Contains("already running", msg);
            Assert.Equal(0, procs.StartGameCalls);
            Assert.Equal(0, procs.StopGameCalls);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task RestartServerAsync_uses_LastPort_when_ActivePort_null()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgPlay3_" + Guid.NewGuid().ToString("N"));
        var serverDir = Path.Combine(root, "Server");
        Directory.CreateDirectory(serverDir);
        File.WriteAllText(Path.Combine(serverDir, ProcessSupervisor.ServerExeName), "exe");

        var health = new HealthMonitor(new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
            })));
        var procs = new FakeProcs();
        var session = new PlaySession(procs: procs, health: health, ports: new FixedPortPicker(5101, reused: false));
        var settings = new LauncherSettings { LastPort = 5101 };

        try
        {
            var (ok, msg) = await session.RestartServerAsync(root, settings);
            Assert.True(ok);
            Assert.Equal(5101, session.ActivePort);
            Assert.Equal(1, procs.StartServerCalls);
            Assert.Contains("5101", msg);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

public class AntivirusGuardTests
{
    [Fact]
    public void ConsentMessage_mentions_unsigned_and_localhost()
    {
        var msg = AntivirusGuard.ConsentMessage();
        Assert.Contains("not code-signed", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("127.0.0.1", msg);
        Assert.Contains("Allow", msg);
    }

    [Fact]
    public void QuarantineHelpMessage_is_short_and_points_at_docs()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgAv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var msg = AntivirusGuard.QuarantineHelpMessage(root, "detail-line");
            Assert.Contains("detail-line", msg);
            Assert.Contains(root, msg);
            Assert.Contains("Prepare Windows Security", msg);
            Assert.Contains(AntivirusGuard.PlayersDocHint, msg);
            Assert.DoesNotContain("Bitdefender → Protection → Quarantine", msg);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ServerExeMissing_true_when_absent()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgAvMiss_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.True(AntivirusGuard.ServerExeMissing(root, out var path));
            Assert.EndsWith(ProcessSupervisor.ServerExeName, path, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void LooksLikeAntivirusInterference_when_exe_missing()
    {
        var root = Path.Combine(Path.GetTempPath(), "FusionRpgAvLook_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.True(AntivirusGuard.LooksLikeAntivirusInterference(new InvalidOperationException("nope"), root));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

public class WindowsSecurityPrepareTests
{
    [Fact]
    public void BuildAddExclusionScript_escapes_single_quotes()
    {
        var script = WindowsSecurityPrepare.BuildAddExclusionScript(@"C:\Games\O'Brien\FusionRpg");
        Assert.Contains("Add-MpPreference -ExclusionPath '", script);
        Assert.Contains("O''Brien", script);
    }

    [Fact]
    public void NormalizePackRoot_trims_trailing_slash()
    {
        var n = WindowsSecurityPrepare.NormalizePackRoot(@"C:\Games\FusionRpg\");
        Assert.False(n.EndsWith('\\') || n.EndsWith('/'));
        Assert.Contains("FusionRpg", n, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfirmDialogText_states_defender_only_and_stays_open()
    {
        var t = WindowsSecurityPrepare.ConfirmDialogText(@"C:\Games\FusionRpg");
        Assert.Contains("Microsoft Defender", t);
        Assert.Contains("does NOT configure Bitdefender", t, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UAC", t);
        Assert.Contains("stays open", t, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void App_IsPrepareArgs_parses_path_with_spaces()
    {
        Assert.True(App.IsPrepareArgs(
            new[] { WindowsSecurityPrepare.ArgPrepare, @"C:\Games\My Pack\FusionRpg" },
            out var root));
        Assert.Equal(@"C:\Games\My Pack\FusionRpg", root);
        Assert.False(App.IsPrepareArgs(new[] { "--other" }, out _));
    }
}

