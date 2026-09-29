namespace FusionRpg.Launcher.Services;

/// <summary>
/// Zero-cost hobby trust UX: detect missing/quarantined server and short user guidance.
/// Does not claim we can whitelist third-party AV.
/// </summary>
public static class AntivirusGuard
{
    public const string PlayersDocHint =
        "See PLAYERS.txt / docs/runbook/players.md (Trust & antivirus).";

    public static string PackRootHint(string launcherBaseDir) =>
        Path.GetFullPath(launcherBaseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public static bool ServerExeMissing(string launcherBaseDir, out string expectedPath)
    {
        var procs = new ProcessSupervisor();
        expectedPath = procs.ResolveServerExe(launcherBaseDir);
        return !File.Exists(expectedPath);
    }

    public static string ConsentMessage() =>
        "FusionRpg is a free hobby overlay (AGPL open source).\n\n" +
        "Builds are not code-signed. Some antivirus products may quarantine FusionRpg.Server.exe " +
        "(false positive on unsigned self-contained .NET).\n\n" +
        "The overlay only listens on 127.0.0.1 (this PC). Source: GitHub.\n\n" +
        "Click Allow to continue. You can later use Prepare Windows Security (Microsoft Defender only) " +
        "or restore/exclude the folder in your own antivirus.";

    public static string QuarantineHelpMessage(string launcherBaseDir, string? detail = null)
    {
        var root = PackRootHint(launcherBaseDir);
        var server = Path.Combine(root, "Server", ProcessSupervisor.ServerExeName);
        var detailLine = string.IsNullOrWhiteSpace(detail)
            ? ""
            : detail.Trim() + Environment.NewLine + Environment.NewLine;

        return
            detailLine +
            "FusionRpg.Server.exe is missing or could not start. Antivirus often quarantines this " +
            "unsigned hobby build (false positive).\n\n" +
            "What you can do:\n" +
            "1. Restore the file from your antivirus quarantine (if it was removed).\n" +
            "2. If you use Microsoft Defender: Launcher → Prepare Windows Security (UAC).\n" +
            "3. Other AV (Bitdefender, etc.): add an exclusion for this folder yourself, or re-download the zip.\n\n" +
            "Folder:\n" + root + "\n" +
            "Expected:\n" + server + "\n\n" +
            PlayersDocHint;
    }

    public static bool LooksLikeAntivirusInterference(Exception ex, string launcherBaseDir)
    {
        if (ServerExeMissing(launcherBaseDir, out _))
            return true;
        var msg = ex.Message + " " + (ex.InnerException?.Message ?? "");
        if (msg.Contains("not found", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("virus", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("blocked", StringComparison.OrdinalIgnoreCase)) return true;
        if (ex is System.ComponentModel.Win32Exception w32 && w32.NativeErrorCode is 2 or 5 or 225)
            return true;
        return false;
    }
}
