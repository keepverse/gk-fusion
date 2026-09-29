using System.Diagnostics;
using System.Security.Principal;

namespace FusionRpg.Launcher.Services;

/// <summary>
/// User-consented Microsoft Defender folder exclusion via UAC elevation.
/// Elevates PowerShell only — does not relaunch FusionRpg.Launcher (avoids "app closed" confusion).
/// Does not affect Bitdefender or other third-party AV.
/// </summary>
public static class WindowsSecurityPrepare
{
    public const string ArgPrepare = "--prepare-windows-security";

    public static string NormalizePackRoot(string launcherBaseDir) =>
        Path.GetFullPath(launcherBaseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>PowerShell that adds a Defender path exclusion (must run elevated).</summary>
    public static string BuildAddExclusionScript(string packRoot)
    {
        var root = NormalizePackRoot(packRoot);
        var escaped = root.Replace("'", "''", StringComparison.Ordinal);
        return "Add-MpPreference -ExclusionPath '" + escaped + "'; exit 0";
    }

    public static bool IsProcessElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(id);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool TryApplyExclusion(string packRoot, out string message)
    {
        var root = NormalizePackRoot(packRoot);
        if (!Directory.Exists(root))
        {
            message = "Pack folder does not exist:\n" + root;
            return false;
        }

        if (!IsProcessElevated())
        {
            message = "Administrator elevation is required to change Windows Security exclusions.";
            return false;
        }

        return RunExclusionPowerShell(root, elevated: false, out message);
    }

    /// <summary>
    /// Ask UAC once and run Add-MpPreference via elevated PowerShell.
    /// The FusionRpg launcher process stays running — do not relaunch this exe.
    /// </summary>
    public static bool TryElevateAddExclusion(string packRoot, out string message)
    {
        var root = NormalizePackRoot(packRoot);
        if (!Directory.Exists(root))
        {
            message = "Pack folder does not exist:\n" + root;
            return false;
        }

        if (IsProcessElevated())
            return TryApplyExclusion(root, out message);

        return RunExclusionPowerShell(root, elevated: true, out message);
    }

    /// <summary>Obsolete name — prefer <see cref="TryElevateAddExclusion"/>.</summary>
    public static bool TryRelaunchElevated(string packRoot, out string message) =>
        TryElevateAddExclusion(packRoot, out message);

    static bool RunExclusionPowerShell(string root, bool elevated, out string message)
    {
        try
        {
            var script = BuildAddExclusionScript(root);
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            var args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;

            ProcessStartInfo psi;
            if (elevated)
            {
                // UseShellExecute + runas: UAC for powershell.exe only (launcher stays open).
                psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = args,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
            }

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                message = "Failed to start PowerShell.";
                return false;
            }

            string stdout = "";
            string stderr = "";
            if (!elevated)
            {
                stdout = proc.StandardOutput.ReadToEnd();
                stderr = proc.StandardError.ReadToEnd();
            }

            if (!proc.WaitForExit(120_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                message = "Windows Security prepare timed out.";
                return false;
            }

            if (proc.ExitCode != 0)
            {
                message =
                    "Windows Security exclusion failed (exit " + proc.ExitCode + ").\n" +
                    "This only works with Microsoft Defender.\n\n" +
                    TrimOut(stderr, stdout);
                return false;
            }

            message =
                "Added Microsoft Defender exclusion for:\n" + root + "\n\n" +
                "FusionRpg stayed open — you do not need to launch it again.\n" +
                "Other antivirus products are unchanged — configure those yourself if needed.";
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            message = "UAC was cancelled. No exclusion was added. FusionRpg is still running.";
            return false;
        }
        catch (Exception ex)
        {
            message = "Windows Security prepare failed: " + ex.Message;
            return false;
        }
    }

    public static string ConfirmDialogText(string packRoot) =>
        "Windows will ask for admin permission (UAC) for a short PowerShell step.\n" +
        "FusionRpg stays open — that UAC prompt is not a second copy of this app.\n\n" +
        "It adds a Microsoft Defender folder exclusion so Defender is less likely to " +
        "quarantine FusionRpg.Server.exe.\n\n" +
        "Folder:\n" + NormalizePackRoot(packRoot) + "\n\n" +
        "This does NOT configure Bitdefender or other third-party antivirus.\n\n" +
        "Continue?";

    static string TrimOut(string stderr, string stdout)
    {
        var s = (stderr + "\n" + stdout).Trim();
        return s.Length <= 600 ? s : s[..600] + "…";
    }
}
