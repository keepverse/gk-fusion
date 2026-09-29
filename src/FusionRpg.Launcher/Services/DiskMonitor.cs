namespace FusionRpg.Launcher.Services;

public sealed class DiskMonitor
{
    public const long WarnFreeBytes = 2L * 1024 * 1024 * 1024; // 2 GB
    public const long WarnDbBytes = 500L * 1024 * 1024; // 500 MB

    public sealed record DiskSnapshot(
        string DataDir,
        long FreeBytes,
        long HotBytes,
        long MediaBytes,
        long LegacyBytes,
        long DbTotalBytes,
        bool LowDisk,
        bool LargeDb);

    public DiskSnapshot Measure(string serverDir)
    {
        var dataDir = Path.Combine(serverDir, "data");
        long free = 0;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(serverDir));
            if (!string.IsNullOrEmpty(root))
            {
                var di = new DriveInfo(root);
                if (di.IsReady) free = di.AvailableFreeSpace;
            }
        }
        catch { /* ignore */ }

        long hot = SizeOf(Path.Combine(dataDir, "rpg-hot.sqlite"));
        long media = SizeOf(Path.Combine(dataDir, "rpg-media.sqlite"));
        long legacy = SizeOf(Path.Combine(dataDir, "rpg.sqlite"));
        var total = hot + media + legacy;
        return new DiskSnapshot(
            dataDir,
            free,
            hot,
            media,
            legacy,
            total,
            free > 0 && free < WarnFreeBytes,
            total > WarnDbBytes);
    }

    static long SizeOf(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch { return 0; }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double v = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
        return $"{v:0.##} {units[i]}";
    }
}
