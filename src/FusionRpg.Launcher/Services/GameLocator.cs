namespace FusionRpg.Launcher.Services;

public sealed class GameLocator
{
    public const string GameExeName = "PlantsVsZombiesRH.exe";

    public string? SuggestGameFolder(string? launcherBaseDir = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(launcherBaseDir))
        {
            candidates.Add(launcherBaseDir);
            var parent = Directory.GetParent(launcherBaseDir)?.FullName;
            if (parent != null) candidates.Add(parent);
            var grand = parent != null ? Directory.GetParent(parent)?.FullName : null;
            if (grand != null) candidates.Add(grand);
        }

        // Common layout: launcher zip beside game, or repo under game tree.
        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (LooksLikeGameFolder(c))
                return c;
        }

        return null;
    }

    public bool LooksLikeGameFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return false;
        return File.Exists(Path.Combine(folder, GameExeName));
    }

    public string GameExePath(string gameFolder) => Path.Combine(gameFolder, GameExeName);
}
