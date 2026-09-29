using System.Windows;
using FusionRpg.Launcher.Services;

namespace FusionRpg.Launcher;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (IsPrepareArgs(e.Args, out var packRoot))
        {
            var ok = WindowsSecurityPrepare.TryApplyExclusion(packRoot, out _);
            // Parent launcher shows the result MessageBox after WaitForExit.
            Shutdown(ok ? 0 : 1);
            return;
        }

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    internal static bool IsPrepareArgs(string[] args, out string packRoot)
    {
        packRoot = "";
        if (args.Length < 2) return false;
        if (!string.Equals(args[0], WindowsSecurityPrepare.ArgPrepare, StringComparison.OrdinalIgnoreCase))
            return false;
        packRoot = string.Join(" ", args.Skip(1)).Trim().Trim('"');
        return packRoot.Length > 0;
    }
}
