using System.IO.Compression;

namespace FusionRpg.Launcher.Services;

public sealed class ModLoaderInstaller
{
    readonly OfficialReleaseDownloader _downloader;
    readonly LoaderProbe _probe = new();

    public ModLoaderInstaller(OfficialReleaseDownloader? downloader = null)
    {
        _downloader = downloader ?? new OfficialReleaseDownloader();
    }

    public async Task InstallBepInExAsync(
        string gameFolder,
        LoaderManifest.LoaderChannel channel,
        IProgress<string>? log = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            throw new DirectoryNotFoundException("Game folder does not exist: " + gameFolder);

        var probe = _probe.Probe(gameFolder);
        if (probe.BlocksBepInExInstall)
            throw new InvalidOperationException(
                "MelonLoader is already present. Do not dual-load. Remove MelonLoader first, or use a clean game folder.");

        log?.Report($"Downloading BepInEx {channel.Tag} from GitHub…");
        var asset = await _downloader.ResolveAssetAsync(
            channel.Owner, channel.Repo, channel.Tag, channel.AssetRegex, ct).ConfigureAwait(false);
        log?.Report($"Asset: {asset.Name} ({asset.TagName})");

        var zip = Path.Combine(Path.GetTempPath(), "FusionRpg-bepinex-" + Guid.NewGuid().ToString("N") + ".zip");
        var stage = Path.Combine(Path.GetTempPath(), "FusionRpg-bepinex-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            await _downloader.DownloadAsync(asset.DownloadUrl, zip, progress, ct).ConfigureAwait(false);
            log?.Report("Extracting…");
            Directory.CreateDirectory(stage);
            ZipFile.ExtractToDirectory(zip, stage);
            var root = FindContentRoot(stage, "BepInEx");
            CopyDirectory(root, gameFolder);
            var missing = new List<string>();
            if (!File.Exists(Path.Combine(gameFolder, "winhttp.dll")))
                missing.Add("winhttp.dll");
            if (!Directory.Exists(Path.Combine(gameFolder, "BepInEx", "core")))
                missing.Add("BepInEx\\core");
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    "BepInEx install incomplete (missing " + string.Join(", ", missing) + ").");
            log?.Report("BepInEx installed into " + gameFolder);
        }
        finally
        {
            TryDelete(zip);
            TryDeleteDir(stage);
        }
    }

    public async Task InstallMelonLoaderAsync(
        string gameFolder,
        LoaderManifest.LoaderChannel channel,
        IProgress<string>? log = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            throw new DirectoryNotFoundException("Game folder does not exist: " + gameFolder);

        var probe = _probe.Probe(gameFolder);
        if (probe.BlocksMelonLoaderInstall)
            throw new InvalidOperationException(
                "BepInEx is already present. Do not dual-load. Remove BepInEx first, or use a clean game folder.");

        log?.Report($"Downloading MelonLoader {channel.Tag} from GitHub…");
        var asset = await _downloader.ResolveAssetAsync(
            channel.Owner, channel.Repo, channel.Tag, channel.AssetRegex, ct).ConfigureAwait(false);
        log?.Report($"Asset: {asset.Name} ({asset.TagName})");

        var zip = Path.Combine(Path.GetTempPath(), "FusionRpg-melon-" + Guid.NewGuid().ToString("N") + ".zip");
        var stage = Path.Combine(Path.GetTempPath(), "FusionRpg-melon-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            await _downloader.DownloadAsync(asset.DownloadUrl, zip, progress, ct).ConfigureAwait(false);
            log?.Report("Extracting…");
            Directory.CreateDirectory(stage);
            ZipFile.ExtractToDirectory(zip, stage);
            var root = FindContentRoot(stage, "MelonLoader");
            CopyDirectory(root, gameFolder);
            var missing = new List<string>();
            if (!File.Exists(Path.Combine(gameFolder, "version.dll")))
                missing.Add("version.dll");
            if (!Directory.Exists(Path.Combine(gameFolder, "MelonLoader")))
                missing.Add("MelonLoader\\");
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    "MelonLoader install incomplete (missing " + string.Join(", ", missing) + ").");
            log?.Report("MelonLoader installed into " + gameFolder);
            log?.Report("Note: FusionRpg Play still needs BepInEx in this release (MelonMod dual-host is next).");
        }
        finally
        {
            TryDelete(zip);
            TryDeleteDir(stage);
        }
    }

    /// <summary>Zip may contain a single top folder or files at root.</summary>
    public static string FindContentRoot(string extractedDir, string markerFolderName)
    {
        if (Directory.Exists(Path.Combine(extractedDir, markerFolderName)))
            return extractedDir;
        foreach (var dir in Directory.GetDirectories(extractedDir))
        {
            if (Directory.Exists(Path.Combine(dir, markerFolderName)))
                return dir;
            if (string.Equals(Path.GetFileName(dir), markerFolderName, StringComparison.OrdinalIgnoreCase))
                return extractedDir;
        }
        return extractedDir;
    }

    public static void CopyDirectory(string sourceDir, string destDir)
    {
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destDir, rel));
        }
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* ignore */ }
    }
}
