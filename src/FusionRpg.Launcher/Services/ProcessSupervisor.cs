using System.Diagnostics;
using System.Text;

namespace FusionRpg.Launcher.Services;

public class ProcessSupervisor
{
    public const string ServerExeName = "FusionRpg.Server.exe";
    public const string GameProcessName = "PlantsVsZombiesRH";
    public const string ServerProcessName = "FusionRpg.Server";

    Process? _server;
    readonly StringBuilder _serverLog = new();
    readonly object _logLock = new();

    public string ServerLogTail
    {
        get
        {
            lock (_logLock)
            {
                var s = _serverLog.ToString();
                return s.Length <= 8000 ? s : s[^8000..];
            }
        }
    }

    public void AppendLog(string line)
    {
        lock (_logLock)
        {
            _serverLog.AppendLine(line);
            if (_serverLog.Length > 100_000)
                _serverLog.Remove(0, _serverLog.Length - 50_000);
        }
    }

    public string ResolveServerExe(string launcherBaseDir)
    {
        var nested = Path.Combine(launcherBaseDir, "Server", ServerExeName);
        if (File.Exists(nested)) return nested;
        var sibling = Path.Combine(launcherBaseDir, ServerExeName);
        if (File.Exists(sibling)) return sibling;
        return nested;
    }

    public string ResolveServerDir(string launcherBaseDir) =>
        Path.GetDirectoryName(ResolveServerExe(launcherBaseDir)) ?? launcherBaseDir;

    public virtual bool IsServerRunning()
    {
        if (_server is { HasExited: false }) return true;
        return Process.GetProcessesByName(ServerProcessName).Length > 0;
    }

    public virtual bool IsGameRunning() =>
        Process.GetProcessesByName(GameProcessName).Length > 0;

    public virtual Process? StartServer(string launcherBaseDir, int port)
    {
        var exe = ResolveServerExe(launcherBaseDir);
        if (!File.Exists(exe))
            throw new FileNotFoundException(
                AntivirusGuard.QuarantineHelpMessage(launcherBaseDir,
                    "FusionRpg.Server.exe is missing (often removed by antivirus quarantine)."),
                exe);

        if (IsServerRunning())
            return null;

        var url = $"http://127.0.0.1:{port}";
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.Environment["FUSIONRPG_URLS"] = url;
        psi.Environment["FUSIONRPG_NO_BROWSER"] = "1";

        Process proc;
        try
        {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) AppendLog("[out] " + e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendLog("[err] " + e.Data); };
            if (!proc.Start())
                throw new InvalidOperationException("Failed to start FusionRpg.Server.");
        }
        catch (Exception ex) when (AntivirusGuard.LooksLikeAntivirusInterference(ex, launcherBaseDir))
        {
            throw new InvalidOperationException(
                AntivirusGuard.QuarantineHelpMessage(launcherBaseDir, ex.Message), ex);
        }

        // Quarantine can delete the file during/just after start
        if (!File.Exists(exe))
            throw new InvalidOperationException(
                AntivirusGuard.QuarantineHelpMessage(launcherBaseDir,
                    "Server exe disappeared right after start (antivirus quarantine)."));

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _server = proc;
        AppendLog($"Started server pid={proc.Id} url={url}");
        return proc;
    }

    public virtual void StopServer()
    {
        try
        {
            if (_server is { HasExited: false })
            {
                _server.Kill(entireProcessTree: true);
                _server.WaitForExit(5000);
            }
        }
        catch { /* ignore */ }
        finally
        {
            _server?.Dispose();
            _server = null;
        }

        foreach (var p in Process.GetProcessesByName(ServerProcessName))
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
            catch { /* ignore */ }
            finally { p.Dispose(); }
        }
    }

    public virtual Process StartGame(string gameExe, string serverUrl)
    {
        if (!File.Exists(gameExe))
            throw new FileNotFoundException("Game exe not found.", gameExe);

        var psi = new ProcessStartInfo
        {
            FileName = gameExe,
            WorkingDirectory = Path.GetDirectoryName(gameExe)!,
            UseShellExecute = false
        };
        psi.Environment["FUSIONRPG_SERVER_URL"] = serverUrl.TrimEnd('/');
        var proc = Process.Start(psi)
                   ?? throw new InvalidOperationException("Failed to start game.");
        AppendLog($"Started game pid={proc.Id} FUSIONRPG_SERVER_URL={serverUrl}");
        return proc;
    }

    public virtual void StopGame()
    {
        foreach (var p in Process.GetProcessesByName(GameProcessName))
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch { /* ignore */ }
            finally { p.Dispose(); }
        }
    }

    public void StopAll()
    {
        StopGame();
        StopServer();
    }

    public static bool OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                return true;
            }
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
                return true;
            }
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", parent) { UseShellExecute = true });
                return true;
            }
        }
        catch { /* ignore */ }
        return false;
    }

    public static string BepInExLogPath(string gameFolder) =>
        Path.Combine(gameFolder, "BepInEx", "LogOutput.log");

    /// <summary>Loader log path for the active host (BepInEx LogOutput or MelonLoader Latest.log).</summary>
    public static string? LoaderLogPath(string gameFolder, IModLoaderHost? host = null)
    {
        if (host != null) return host.LogPath(gameFolder);
        var probe = new LoaderProbe().Probe(gameFolder);
        return probe.Host?.LogPath(gameFolder) ?? BepInExLogPath(gameFolder);
    }
}
