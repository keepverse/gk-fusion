using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace FusionRpg.Launcher.Services;

/// <summary>Download FusionRpg-win-x64.zip from our GitHub Releases and apply over the install.</summary>
public sealed class FusionRpgUpdater
{
    readonly OfficialReleaseDownloader _downloader;
    readonly string _updatesDir;

    public FusionRpgUpdater(OfficialReleaseDownloader? downloader = null, string? updatesDir = null)
    {
        _downloader = downloader ?? new OfficialReleaseDownloader();
        _updatesDir = updatesDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FusionRpg", "updates");
    }

    public string UpdatesDir => _updatesDir;
    public async Task<(string ZipPath, string Tag)> DownloadLatestAsync(
        LoaderManifest.FusionRpgChannel channel,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var asset = await _downloader.ResolveAssetAsync(
            channel.Owner, channel.Repo, "latest", channel.AssetRegex, ct).ConfigureAwait(false);
        Directory.CreateDirectory(UpdatesDir);
        var zip = Path.Combine(UpdatesDir, asset.Name);
        await _downloader.DownloadAsync(asset.DownloadUrl, zip, progress, ct).ConfigureAwait(false);
        return (zip, asset.TagName);
    }

    /// <summary>
    /// Stage zip contents, preserve Server\data, write update.cmd that replaces files after launcher exits.
    /// Returns path to the bootstrap script (caller should StartProcess then hard-exit).
    /// </summary>
    public string PrepareApply(string zipPath, string launcherBaseDir, bool stopGame = true, int? launcherPid = null)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Update zip missing.", zipPath);
        if (!Directory.Exists(launcherBaseDir))
            throw new DirectoryNotFoundException(launcherBaseDir);

        var pid = launcherPid ?? Environment.ProcessId;
        var stage = Path.Combine(UpdatesDir, "stage-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        Directory.CreateDirectory(stage);
        ZipFile.ExtractToDirectory(zipPath, stage);

        // Zip may contain FusionRpg/ root
        var contentRoot = stage;
        if (!File.Exists(Path.Combine(stage, "FusionRpg.Launcher.exe")))
        {
            var inner = Directory.GetDirectories(stage).FirstOrDefault(d =>
                File.Exists(Path.Combine(d, "FusionRpg.Launcher.exe")));
            if (inner != null) contentRoot = inner;
        }

        if (!File.Exists(Path.Combine(contentRoot, "FusionRpg.Launcher.exe")))
            throw new InvalidOperationException("Update zip does not contain FusionRpg.Launcher.exe.");

        var dataSrc = Path.Combine(launcherBaseDir, "Server", "data");
        var dataDst = Path.Combine(contentRoot, "Server", "data");
        if (Directory.Exists(dataSrc))
        {
            Directory.CreateDirectory(Path.Combine(contentRoot, "Server"));
            if (Directory.Exists(dataDst))
                Directory.Delete(dataDst, true);
            ModLoaderInstaller.CopyDirectory(dataSrc, dataDst);
        }

        var script = Path.Combine(UpdatesDir, "apply-update.cmd");
        var logFile = Path.Combine(UpdatesDir, "apply-update.log");
        var launcherExe = Path.Combine(launcherBaseDir, "FusionRpg.Launcher.exe");
        // Escape for cmd: quotes in paths are rare; double any trailing backslash issues via quoted paths.
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal EnableExtensions");
        sb.AppendLine($"set \"LOG={logFile}\"");
        sb.AppendLine("echo FusionRpg apply-update started %DATE% %TIME% > \"%LOG%\"");
        sb.AppendLine($"echo Target PID {pid} >> \"%LOG%\"");
        sb.AppendLine($"echo Install \"{launcherBaseDir}\" >> \"%LOG%\"");
        sb.AppendLine($"echo Stage \"{contentRoot}\" >> \"%LOG%\"");

        // Unlock files first — do not wait on image name (false positives / other instances).
        if (stopGame)
        {
            sb.AppendLine("echo Stopping game/server... >> \"%LOG%\"");
            sb.AppendLine("taskkill /F /IM PlantsVsZombiesRH.exe /T >nul 2>&1");
            sb.AppendLine("taskkill /F /IM FusionRpg.Server.exe /T >nul 2>&1");
        }

        sb.AppendLine($"echo Waiting for launcher PID {pid} to exit... >> \"%LOG%\"");
        sb.AppendLine($"echo Waiting for FusionRpg Launcher (PID {pid}) to exit...");
        sb.AppendLine("set /a _n=0");
        sb.AppendLine(":wait");
        sb.AppendLine("set /a _n+=1");
        // ~90s then force-kill this PID so we never hang forever on WPF Shutdown
        sb.AppendLine("if %_n% GEQ 90 goto forcekill");
        sb.AppendLine("timeout /t 1 /nobreak >nul");
        // Exact PID check (avoids hanging on a second FusionRpg.Launcher.exe instance)
        sb.AppendLine($"powershell -NoProfile -Command \"try {{ Get-Process -Id {pid} -ErrorAction Stop | Out-Null; exit 0 }} catch {{ exit 1 }}\" >nul 2>&1");
        sb.AppendLine("if %ERRORLEVEL%==0 goto wait");
        sb.AppendLine("goto apply");
        sb.AppendLine(":forcekill");
        sb.AppendLine($"echo Force-killing launcher PID {pid} >> \"%LOG%\"");
        sb.AppendLine($"echo Launcher did not exit in time; force-closing PID {pid}...");
        sb.AppendLine($"taskkill /F /PID {pid} /T >nul 2>&1");
        sb.AppendLine("timeout /t 2 /nobreak >nul");
        sb.AppendLine(":apply");
        if (stopGame)
        {
            sb.AppendLine("taskkill /F /IM PlantsVsZombiesRH.exe /T >nul 2>&1");
            sb.AppendLine("taskkill /F /IM FusionRpg.Server.exe /T >nul 2>&1");
            sb.AppendLine("timeout /t 1 /nobreak >nul");
        }

        // Robocopy exit codes 0-7 are success; retry a few times if files still locked.
        sb.AppendLine("echo Copying update files... >> \"%LOG%\"");
        sb.AppendLine("set /a _try=0");
        sb.AppendLine(":copy");
        sb.AppendLine("set /a _try+=1");
        sb.AppendLine($"robocopy \"{contentRoot}\" \"{launcherBaseDir}\" /E /IS /IT /R:2 /W:1 /NFL /NDL /NJH /NJS /nc /ns /np");
        sb.AppendLine("set _rc=%ERRORLEVEL%");
        sb.AppendLine("if %_rc% LEQ 7 goto copied");
        sb.AppendLine("if %_try% LSS 5 (");
        sb.AppendLine("  echo robocopy retry %_try% rc=%_rc% >> \"%LOG%\"");
        sb.AppendLine("  timeout /t 2 /nobreak >nul");
        sb.AppendLine("  goto copy");
        sb.AppendLine(")");
        sb.AppendLine("echo robocopy failed rc=%_rc% >> \"%LOG%\"");
        sb.AppendLine("echo Update copy failed. See %LOG%");
        sb.AppendLine("pause");
        sb.AppendLine("exit /b 1");
        sb.AppendLine(":copied");
        sb.AppendLine("echo Copy OK rc=%_rc% >> \"%LOG%\"");
        sb.AppendLine($"start \"\" \"{launcherExe}\"");
        sb.AppendLine($"rmdir /S /Q \"{stage}\" >nul 2>&1");
        sb.AppendLine("echo Done >> \"%LOG%\"");
        sb.AppendLine("endlocal");
        File.WriteAllText(script, sb.ToString(), Encoding.ASCII);
        return script;
    }

    public static void LaunchBootstrapAndExit(string scriptPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = scriptPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!
        });
    }
}
