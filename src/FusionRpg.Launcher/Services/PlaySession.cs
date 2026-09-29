namespace FusionRpg.Launcher.Services;

/// <summary>Orchestrates install → port pick → server → game for the dashboard.</summary>
public sealed class PlaySession
{
    readonly GameLocator _games;
    readonly LoaderProbe _loader;
    readonly PluginInstaller _plugins;
    readonly PortPicker _ports;
    readonly ProcessSupervisor _procs;
    readonly HealthMonitor _health;
    readonly DiskMonitor _disk;

    public PlaySession(
        GameLocator? games = null,
        LoaderProbe? loader = null,
        PluginInstaller? plugins = null,
        PortPicker? ports = null,
        ProcessSupervisor? procs = null,
        HealthMonitor? health = null,
        DiskMonitor? disk = null)
    {
        _games = games ?? new GameLocator();
        _loader = loader ?? new LoaderProbe();
        _plugins = plugins ?? new PluginInstaller();
        _ports = ports ?? new PortPicker();
        _procs = procs ?? new ProcessSupervisor();
        _health = health ?? new HealthMonitor();
        _disk = disk ?? new DiskMonitor();
    }

    public ProcessSupervisor Processes => _procs;
    public HealthMonitor Health => _health;
    public DiskMonitor Disk => _disk;
    public PluginInstaller Plugins => _plugins;

    public int? ActivePort { get; private set; }
    public string? ActiveUrl => ActivePort is int p ? $"http://127.0.0.1:{p}" : null;

    public void RestorePort(int? port)
    {
        ActivePort = port is >= 1 and <= 65535 ? port : null;
    }

    /// <summary>
    /// Restore LastPort from settings; clear it if that port no longer answers our /health.
    /// </summary>
    public async Task RestoreFromSettingsAsync(LauncherSettings settings, CancellationToken ct = default)
    {
        if (settings.LastPort is not int port)
        {
            ActivePort = null;
            return;
        }

        RestorePort(port);
        var snap = await _health.CheckAsync(ActiveUrl!, ct).ConfigureAwait(false);
        if (snap.Reachable && snap.Ok)
            return;

        // Port dead — keep LastPort preference for next Pick, but clear ActivePort for UI.
        ActivePort = null;
    }

    public sealed record Status(
        string? GameFolder,
        LoaderProbeResult? Loader,
        bool ServerRunning,
        bool GameRunning,
        HealthMonitor.HealthSnapshot? Health,
        DiskMonitor.DiskSnapshot? Disk,
        int? Port,
        string? Message);

    public Status Snapshot(string? gameFolder, string launcherBaseDir)
    {
        LoaderProbeResult? loader = null;
        if (!string.IsNullOrWhiteSpace(gameFolder) && Directory.Exists(gameFolder))
            loader = _loader.Probe(gameFolder);

        HealthMonitor.HealthSnapshot? health = null;
        if (ActiveUrl != null)
            health = _health.CheckAsync(ActiveUrl).GetAwaiter().GetResult();

        DiskMonitor.DiskSnapshot? disk = null;
        try
        {
            disk = _disk.Measure(_procs.ResolveServerDir(launcherBaseDir));
        }
        catch { /* ignore */ }

        return new Status(
            gameFolder,
            loader,
            _procs.IsServerRunning(),
            _procs.IsGameRunning(),
            health,
            disk,
            ActivePort,
            null);
    }

    public async Task<(bool Ok, string Message)> PlayAsync(
        string gameFolder,
        string launcherBaseDir,
        LauncherSettings settings,
        CancellationToken ct = default)
    {
        if (!_games.LooksLikeGameFolder(gameFolder))
            return (false, "Select a folder that contains PlantsVsZombiesRH.exe.");

        var probe = _loader.Probe(gameFolder);
        if (!probe.OkForV1 || probe.PluginDir == null || probe.Host == null)
            return (false, probe.Message);

        var host = probe.Host;
        var catalog = GameProfileCatalog.LoadFromLauncherBase(launcherBaseDir);
        var profileId = catalog.Detect(gameFolder, settings.GameProfile);
        if (!catalog.SupportsLoader(profileId, host.Kind))
            return (false, $"Game profile {profileId} does not support {host.Kind}. Pick a matching pack or override GameProfile.");

        var dllName = host.InjectorDllNameFor(profileId);
        if (!host.HasDropPayload(launcherBaseDir, profileId))
        {
            return (false, host.Kind == LoaderKind.MelonLoader
                ? "Melon drop missing for " + profileId + " (need " + dllName +
                  "). Set FUSIONRPG_ML_GAMEDIR + FUSIONRPG_GAME_PROFILE and re-run publish_player.py."
                : "DropIntoGame incomplete for " + profileId + " (missing " + dllName + "). Re-run publish_player.py.");
        }

        var drop = host.DropPayloadDir(launcherBaseDir, profileId);
        if (!Directory.Exists(drop))
            return (false, "DropIntoGame payload missing for " + profileId + "/" + host.Kind + ". Re-download the player zip.");

        var injectorSrc = Path.Combine(drop, dllName);
        if (!File.Exists(injectorSrc))
            return (false, $"DropIntoGame is incomplete (missing {dllName}). Re-run publish_player.py.");

        if (_plugins.NeedsInstallOrUpdate(drop, probe.PluginDir, host, dllName))
        {
            var n = _plugins.Install(drop, probe.PluginDir);
            _procs.AppendLog($"Installed/updated {n} plugin file(s) [{profileId}] → {probe.PluginDir}");
        }

        PortPicker.Result pick;
        try
        {
            pick = _ports.Pick(settings.LastPort);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        var priorUrl = ActiveUrl;
        ActivePort = pick.Port;
        settings.LastPort = pick.Port;
        settings.GameFolder = gameFolder;
        settings.Save();

        _plugins.WriteServerUrlConfig(gameFolder, pick.Url, host);

        if (!pick.ReusedOurServer)
        {
            if (_procs.IsServerRunning())
                _procs.StopServer();
            // AV scanners can block CreateProcess for tens of seconds — keep UI responsive.
            try
            {
                await Task.Run(() => _procs.StartServer(launcherBaseDir, pick.Port), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (AntivirusGuard.LooksLikeAntivirusInterference(ex, launcherBaseDir))
            {
                return (false, AntivirusGuard.QuarantineHelpMessage(launcherBaseDir, ex.Message));
            }
            catch (Exception ex)
            {
                if (AntivirusGuard.ServerExeMissing(launcherBaseDir, out _))
                    return (false, AntivirusGuard.QuarantineHelpMessage(launcherBaseDir, ex.Message));
                return (false, "Failed to start server: " + ex.Message);
            }
        }

        var ready = await _health.WaitUntilOkAsync(pick.Url, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!ready)
        {
            if (AntivirusGuard.ServerExeMissing(launcherBaseDir, out _))
                return (false, AntivirusGuard.QuarantineHelpMessage(launcherBaseDir,
                    $"Server did not become healthy at {pick.Url} and FusionRpg.Server.exe is gone."));
            return (false, $"Server did not become healthy at {pick.Url} within 30s. Check the log pane.");
        }
        if (_procs.IsGameRunning())
        {
            if (string.Equals(priorUrl, pick.Url, StringComparison.OrdinalIgnoreCase))
                return (true, $"Playing — server {pick.Url} (game already running)");

            _procs.StopGame();
            await Task.Delay(800, ct).ConfigureAwait(false);
        }

        _procs.StartGame(_games.GameExePath(gameFolder), pick.Url);
        return (true, $"Playing — server {pick.Url}" + (pick.ReusedOurServer ? " (reused)" : ""));
    }

    public void StopAll()
    {
        _procs.StopAll();
    }

    public async Task<(bool Ok, string Message)> RestartServerAsync(
        string launcherBaseDir,
        LauncherSettings settings,
        CancellationToken ct = default)
    {
        int port;
        if (ActivePort is int active)
            port = active;
        else if (settings.LastPort is int last)
        {
            port = last;
            ActivePort = last;
        }
        else
        {
            try
            {
                var pick = _ports.Pick(settings.LastPort);
                port = pick.Port;
                ActivePort = port;
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        _procs.StopServer();
        await Task.Delay(500, ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => _procs.StartServer(launcherBaseDir, port), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (AntivirusGuard.LooksLikeAntivirusInterference(ex, launcherBaseDir))
        {
            return (false, AntivirusGuard.QuarantineHelpMessage(launcherBaseDir, ex.Message));
        }
        catch (Exception ex)
        {
            if (AntivirusGuard.ServerExeMissing(launcherBaseDir, out _))
                return (false, AntivirusGuard.QuarantineHelpMessage(launcherBaseDir, ex.Message));
            return (false, "Failed to start server: " + ex.Message);
        }
        var url = $"http://127.0.0.1:{port}";
        settings.LastPort = port;
        settings.Save();
        var ok = await _health.WaitUntilOkAsync(url, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!ok && AntivirusGuard.ServerExeMissing(launcherBaseDir, out _))
            return (false, AntivirusGuard.QuarantineHelpMessage(launcherBaseDir, "Server restart health check failed."));
        return ok ? (true, "Server restarted at " + url) : (false, "Server restart failed health check.");
    }

    public (bool Ok, string Message) RestartGame(string gameFolder, LauncherSettings? settings = null)
    {
        if (ActiveUrl == null && settings?.LastPort is int last)
            RestorePort(last);
        if (ActiveUrl == null)
            return (false, "Start Play first so a server URL is known.");
        if (!_games.LooksLikeGameFolder(gameFolder))
            return (false, "Game folder invalid.");
        var probe = _loader.Probe(gameFolder);
        if (probe.Host == null)
            return (false, "No single mod loader detected (refusing to write BepInEx config). Fix dual-load or install one loader.");
        _plugins.WriteServerUrlConfig(gameFolder, ActiveUrl, probe.Host);
        if (_procs.IsGameRunning())
            _procs.StopGame();
        _procs.StartGame(_games.GameExePath(gameFolder), ActiveUrl);
        return (true, "Game relaunched.");
    }
}
