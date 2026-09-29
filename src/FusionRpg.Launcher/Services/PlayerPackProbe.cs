using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FusionRpg.Launcher.Services;

public sealed record PlayerPackProbeStep(string Name, bool Ok, string Message);

public sealed class PlayerPackProbeResult
{
    public bool Ok { get; init; }
    public string PackDir { get; init; } = "";
    public IReadOnlyList<PlayerPackProbeStep> Steps { get; init; } = Array.Empty<PlayerPackProbeStep>();

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
/// Offline automation probe for an unpacked player pack (dist/FusionRpg).
/// Distinct from SIM-only HTTP /api/test/probe.
/// </summary>
public sealed class PlayerPackProbe
{
    static readonly string[] RequiredRelativePaths =
    {
        "FusionRpg.Launcher.exe",
        Path.Combine("Server", "FusionRpg.Server.exe"),
        "loader-manifest.json",
        "PLAYERS.txt",
        "LICENSE",
        Path.Combine("Server", "wwwroot", "index.html")
    };

    /// <summary>
    /// The content trees the server resolves beside its own exe at boot. Directories, not files, so
    /// they need their own check.
    ///
    /// <para><b>Why the pack layout owns this.</b> The split plan's resolver contract
    /// (<c>tasks/keepverse-split-plan.md</c>) exempts <c>src/FusionRpg.{Core,Data,Server}</c> from the
    /// content-root walk <b>because</b> it "resolves content against its build output, whose layout the
    /// <c>&lt;Content Link="data\..."&gt;</c> items preserve" — an exemption that is false the moment a
    /// pack ships without the tree. With it absent, <c>SeedImportRunner.RunSelfHealing</c> answers
    /// <c>SeedTreeNotFound</c> and <c>/health</c> reports <c>contentSource: "codeFallback"</c>: the whole
    /// content layer (atoms, containers, curves, rarities, elements, channel policies) runs on the code
    /// fallback (E46 <c>player-content-boot</c>, §1). It was deleted from the publish output by
    /// <c>gk-core/scripts/publish_player.py</c> until 2026-09-23, and nothing failed — so the requirement is
    /// asserted here, where the release smoke already runs.</para>
    /// </summary>
    static readonly string[] RequiredContentDirs =
    {
        Path.Combine("Server", "data", "seed"),
        Path.Combine("Server", "data", "tuning"),
        // Owner ruling 2026-09-23 (CS-F3): the concrete species tree ships, and the boot self-heals the
        // roster from it once (`SpeciesImportRunner.RunSelfHealing`). Without it a fresh install cannot
        // boot at all — `CreatureSpeciesCatalog.Configure` throws on an empty roster.
        Path.Combine("Server", "data", "generated", "creatures")
    };

    static bool HasInjectorDrop(string packDir)
    {
        var flat = Path.Combine(packDir, "DropIntoGame", "FusionRpg.Injector.dll");
        var nestedBep = Path.Combine(packDir, "DropIntoGame", "BepInEx", "FusionRpg.Injector.dll");
        var nestedMelon = Path.Combine(packDir, "DropIntoGame", "MelonLoader", "FusionRpg.Injector.MelonLoader.dll");
        return File.Exists(flat) || File.Exists(nestedBep) || File.Exists(nestedMelon);
    }

    readonly LoaderProbe _loader = new();
    readonly PluginInstaller _plugins = new();

    public PlayerPackProbeResult Run(string packDir)
    {
        var steps = new List<PlayerPackProbeStep>();
        if (string.IsNullOrWhiteSpace(packDir) || !Directory.Exists(packDir))
        {
            steps.Add(new PlayerPackProbeStep("layout", false, "Pack directory missing: " + packDir));
            return new PlayerPackProbeResult { Ok = false, PackDir = packDir ?? "", Steps = steps };
        }

        packDir = Path.GetFullPath(packDir);
        steps.Add(ProbeLayout(packDir));
        steps.Add(ProbeManifest(packDir));
        steps.Add(ProbeOverlayPayload(packDir));
        steps.Add(ProbeLoaderAndPlugin(packDir));
        steps.Add(ProbeDualLoadRefuse(packDir));
        steps.Add(ProbeUpdatePreserve(packDir));

        return new PlayerPackProbeResult
        {
            Ok = steps.TrueForAll(s => s.Ok),
            PackDir = packDir,
            Steps = steps
        };
    }

    PlayerPackProbeStep ProbeLayout(string packDir)
    {
        var missing = new List<string>();
        foreach (var rel in RequiredRelativePaths)
        {
            if (!File.Exists(Path.Combine(packDir, rel)))
                missing.Add(rel.Replace('/', Path.DirectorySeparatorChar));
        }
        if (!HasInjectorDrop(packDir))
            missing.Add(Path.Combine("DropIntoGame", "FusionRpg.Injector.dll") + " (or DropIntoGame\\BepInEx\\…)");
        foreach (var rel in RequiredContentDirs)
        {
            if (!Directory.Exists(Path.Combine(packDir, rel)))
                missing.Add(rel.Replace('/', Path.DirectorySeparatorChar) + " (content tree — the server boots on the code fallback without it)");
        }
        if (missing.Count > 0)
            return new PlayerPackProbeStep("layout", false, "Missing: " + string.Join(", ", missing));
        return new PlayerPackProbeStep("layout", true, "Required pack files present.");
    }

    /// <summary>Set to 1 to release knowingly without a MelonLoader drop.</summary>
    public const string AllowNoMelonEnv = "FUSIONRPG_ALLOW_NO_MELON";

    /// <summary>Type names that only exist in an injector carrying the in-game overlay.</summary>
    static readonly string[] OverlayMarkers = { "OverlayViewHost", "OverlaySwitchGui" };

    /// <summary>
    /// The in-game browser needs three things to line up, and each can go missing quietly:
    /// the launcher's own WebView2 (F10 overlay), an injector that actually contains the overlay
    /// code, and the WebView2 files sitting <b>beside</b> that injector. PluginInstaller copies
    /// top-level files only, so a loader one folder down reaches nobody.
    /// A cloud release builds the injector from a committed fallback drop, which is exactly how a
    /// release can ship a working launcher and a feature-less injector.
    /// </summary>
    public PlayerPackProbeStep ProbeOverlayPayload(string packDir)
    {
        var problems = new List<string>();

        foreach (var required in new[] { "Microsoft.Web.WebView2.Core.dll", "WebView2Loader.dll" })
        {
            if (!File.Exists(Path.Combine(packDir, required)))
                problems.Add($"launcher is missing {required} (the F10 overlay cannot open)");
        }

        var dropRoot = Path.Combine(packDir, "DropIntoGame");
        var drops = Directory.Exists(dropRoot)
            ? Directory.EnumerateFiles(dropRoot, "FusionRpg.Injector*.dll", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith(".deps", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : new List<string>();

        if (drops.Count == 0)
        {
            problems.Add("no injector found under DropIntoGame");
        }

        var sawMelon = false;
        foreach (var injector in drops)
        {
            var dir = Path.GetDirectoryName(injector)!;
            var where = dir.Substring(packDir.Length).TrimStart(Path.DirectorySeparatorChar);
            if (dir.Contains("MelonLoader", StringComparison.OrdinalIgnoreCase)) sawMelon = true;

            foreach (var required in new[] { "Microsoft.Web.WebView2.Core.dll", "WebView2Loader.dll" })
            {
                if (!File.Exists(Path.Combine(dir, required)))
                    problems.Add($"{where}: missing {required} beside the injector");
            }

            // A stale drop is the failure mode that ships silently, so name it plainly.
            string text;
            try { text = File.ReadAllText(injector); }
            catch (Exception ex) { problems.Add($"{where}: could not read the injector ({ex.GetType().Name})"); continue; }

            if (!OverlayMarkers.All(marker => text.Contains(marker, StringComparison.Ordinal)))
                problems.Add($"{where}: injector has no in-game overlay code — this looks like a stale drop");
        }

        if (drops.Count > 0 && !sawMelon &&
            !string.Equals(Environment.GetEnvironmentVariable(AllowNoMelonEnv), "1", StringComparison.Ordinal))
        {
            problems.Add($"no MelonLoader drop (set {AllowNoMelonEnv}=1 to ship without one deliberately)");
        }

        return problems.Count > 0
            ? new PlayerPackProbeStep("overlay-payload", false, string.Join("; ", problems))
            : new PlayerPackProbeStep("overlay-payload", true,
                $"In-game overlay shippable ({drops.Count} injector drop(s), WebView2 beside each).");
    }

    PlayerPackProbeStep ProbeManifest(string packDir)
    {
        try
        {
            var m = LoaderManifest.LoadFromLauncherDir(packDir);
            if (string.IsNullOrWhiteSpace(m.BepInEx.AssetRegex) ||
                string.IsNullOrWhiteSpace(m.MelonLoader.AssetRegex) ||
                string.IsNullOrWhiteSpace(m.FusionRpg.AssetRegex))
                return new PlayerPackProbeStep("manifest", false, "loader-manifest.json missing asset regex pins.");
            return new PlayerPackProbeStep("manifest", true,
                $"Pins OK (BepInEx {m.BepInEx.Tag}, Melon {m.MelonLoader.Tag}).");
        }
        catch (Exception ex)
        {
            return new PlayerPackProbeStep("manifest", false, ex.Message);
        }
    }

    PlayerPackProbeStep ProbeLoaderAndPlugin(string packDir)
    {
        string? game = null;
        try
        {
            game = CreateTempDir("probe-game");
            File.WriteAllText(Path.Combine(game, "PlantsVsZombiesRH.exe"), "stub");
            File.WriteAllText(Path.Combine(game, "winhttp.dll"), "stub");
            Directory.CreateDirectory(Path.Combine(game, "BepInEx", "core"));
            File.WriteAllText(Path.Combine(game, "BepInEx", "core", "core.txt"), "stub");

            var probe = _loader.Probe(game);
            if (!probe.OkForV1 || probe.PluginDir == null || probe.Host == null)
                return new PlayerPackProbeStep("loader_plugin", false, "LoaderProbe not OkForV1: " + probe.Message);

            var drop = _plugins.ResolveDropIntoGameDir(packDir, probe.Host);
            if (!File.Exists(Path.Combine(drop, probe.Host.InjectorDllName)))
                return new PlayerPackProbeStep("loader_plugin", false, "DropIntoGame missing Injector DLL.");

            var n = _plugins.Install(drop, probe.PluginDir);
            var installed = Path.Combine(probe.PluginDir, probe.Host.InjectorDllName);
            if (!File.Exists(installed))
                return new PlayerPackProbeStep("loader_plugin", false, "Install did not copy Injector DLL.");

            return new PlayerPackProbeStep("loader_plugin", true, $"Installed {n} plugin file(s) into {probe.PluginDir}.");
        }
        catch (Exception ex)
        {
            return new PlayerPackProbeStep("loader_plugin", false, ex.Message);
        }
        finally
        {
            if (game != null) TryDeleteDir(game);
        }
    }

    PlayerPackProbeStep ProbeDualLoadRefuse(string packDir)
    {
        string? game = null;
        try
        {
            game = CreateTempDir("probe-dual");
            File.WriteAllText(Path.Combine(game, "winhttp.dll"), "stub");
            Directory.CreateDirectory(Path.Combine(game, "BepInEx", "core"));
            File.WriteAllText(Path.Combine(game, "version.dll"), "stub");
            Directory.CreateDirectory(Path.Combine(game, "MelonLoader"));

            var probe = _loader.Probe(game);
            if (probe.Kind != LoaderKind.Both || !probe.BlocksBepInExInstall || !probe.BlocksMelonLoaderInstall)
                return new PlayerPackProbeStep("dual_load", false,
                    $"Expected Both with dual-load blocks; got Kind={probe.Kind}, Msg={probe.Message}");

            return new PlayerPackProbeStep("dual_load", true, "Dual-load correctly refused (Both markers).");
        }
        catch (Exception ex)
        {
            return new PlayerPackProbeStep("dual_load", false, ex.Message);
        }
        finally
        {
            if (game != null) TryDeleteDir(game);
        }
    }

    PlayerPackProbeStep ProbeUpdatePreserve(string packDir)
    {
        string? install = null;
        string? updates = null;
        string? zipStage = null;
        try
        {
            install = CreateTempDir("probe-install");
            updates = CreateTempDir("probe-updates");
            zipStage = CreateTempDir("probe-zip-src");

            // Seed "old" install with launcher + save data
            File.Copy(Path.Combine(packDir, "FusionRpg.Launcher.exe"), Path.Combine(install, "FusionRpg.Launcher.exe"));
            Directory.CreateDirectory(Path.Combine(install, "Server", "data"));
            File.WriteAllText(Path.Combine(install, "Server", "data", "rpg-hot.sqlite"), "SAVE-BYTES");

            // Zip payload looks like a release: launcher + Server with wiped data
            File.Copy(Path.Combine(packDir, "FusionRpg.Launcher.exe"), Path.Combine(zipStage, "FusionRpg.Launcher.exe"));
            Directory.CreateDirectory(Path.Combine(zipStage, "Server", "data"));
            File.WriteAllText(Path.Combine(zipStage, "Server", "FusionRpg.Server.exe"), "srv");
            File.WriteAllText(Path.Combine(zipStage, "Server", "data", "rpg-hot.sqlite"), "WIPED");

            var zipPath = Path.Combine(updates, "FusionRpg-win-x64.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(zipStage, zipPath);

            var updater = new FusionRpgUpdater(updatesDir: updates);
            var script = updater.PrepareApply(zipPath, install, stopGame: true);
            if (!File.Exists(script))
                return new PlayerPackProbeStep("update_preserve", false, "Bootstrap script missing.");

            var scriptText = File.ReadAllText(script);
            if (!scriptText.Contains("robocopy", StringComparison.OrdinalIgnoreCase))
                return new PlayerPackProbeStep("update_preserve", false, "Bootstrap missing robocopy.");

            var stages = Directory.GetDirectories(updates, "stage-*");
            if (stages.Length == 0)
                return new PlayerPackProbeStep("update_preserve", false, "No staging folder.");

            var stagedData = Directory.GetFiles(stages[0], "rpg-hot.sqlite", SearchOption.AllDirectories).FirstOrDefault();
            if (stagedData == null)
                return new PlayerPackProbeStep("update_preserve", false, "Staged data file missing.");
            if (File.ReadAllText(stagedData) != "SAVE-BYTES")
                return new PlayerPackProbeStep("update_preserve", false, "Server\\data was not preserved.");

            return new PlayerPackProbeStep("update_preserve", true, "Server\\data preserved; bootstrap has robocopy.");
        }
        catch (Exception ex)
        {
            return new PlayerPackProbeStep("update_preserve", false, ex.Message);
        }
        finally
        {
            if (install != null) TryDeleteDir(install);
            if (updates != null) TryDeleteDir(updates);
            if (zipStage != null) TryDeleteDir(zipStage);
        }
    }

    static string CreateTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), "FusionRpgPackProbe-" + prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            /* ignore */
        }
    }
}
