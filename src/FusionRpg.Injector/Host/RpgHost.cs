using HarmonyLib;

namespace FusionRpg.Injector.Host;

/// <summary>
/// Global facade for host services. Hooks/Writer must use this — never BepInEx or MelonLoader types.
/// </summary>
public static class RpgHost
{
    public const string HarmonyId = "com.fusionrpg.injector";
    public const string DefaultServerUrl = "http://127.0.0.1:5088";

    static IRpgLog _log = NullRpgLog.Instance;
    static IRpgConfig _config = new DefaultRpgConfig();
    static string _pluginDir = "";
    static string _serverUrl = DefaultServerUrl;
    static string _gameProfileId = FusionRpg.Contracts.RpgConstants.GameId381;
    static FusionRpg.Core.Overlay.OverlayHostMode _overlayHost = FusionRpg.Core.Overlay.OverlayHostMode.Launcher;

    public static IRpgLog Log => _log;
    public static IRpgConfig Config => _config;
    public static string PluginDir => _pluginDir;
    public static string ServerUrl => _serverUrl;
    /// <summary>Which process owns the web overlay window. Default Launcher.</summary>
    public static FusionRpg.Core.Overlay.OverlayHostMode OverlayHost => _overlayHost;
    /// <summary>Active game profile id (e.g. pvzrh-3.8.1) from compile-time bridge.</summary>
    public static string GameProfileId => _gameProfileId;
    public static RpgClient? Client { get; set; }
    /// <summary>Harmony instance (fully qualified — MelonLoader also exposes a Harmony namespace).</summary>
    public static HarmonyLib.Harmony? Harmony { get; set; }
    public static bool EnableUnsafeHitPatches { get; private set; }
    public static bool IsInitialized { get; private set; }

    /// <summary>Wire host adapters before <see cref="InjectorBootstrap.Start"/>.</summary>
    public static void Initialize(IRpgLog log, IRpgConfig config, string pluginDir)
    {
        _log = log ?? NullRpgLog.Instance;
        _config = config ?? new DefaultRpgConfig();
        _pluginDir = pluginDir ?? "";

        var envUrl = Environment.GetEnvironmentVariable("FUSIONRPG_SERVER_URL");
        var cfgUrl = _config.ServerUrl;
        _serverUrl = !string.IsNullOrWhiteSpace(envUrl)
            ? envUrl.Trim().TrimEnd('/')
            : (string.IsNullOrWhiteSpace(cfgUrl) ? DefaultServerUrl : cfgUrl.Trim().TrimEnd('/'));

        // Never silent: a game whose URL came from a DEFAULT may be talking to the owner's server when it
        // should be talking to its own pool slot's server (2026-09-22).
        if (string.IsNullOrWhiteSpace(envUrl) && _config.ServerUrlFromFallback)
        {
            var cfgAt = string.IsNullOrEmpty(_pluginDir) ? "(no plugin dir)" : System.IO.Path.Combine(_pluginDir, "fusionrpg.cfg");
            _log.Warning("ServerUrl is not configured (FUSIONRPG_SERVER_URL unset, no ServerUrl= in " + cfgAt +
                "); falling back to " + _serverUrl + ". If this game belongs to a POOL SLOT that is the OWNER's " +
                "server and the wrong one -- set ServerUrl in this install's fusionrpg.cfg or FUSIONRPG_SERVER_URL.");
        }

        _overlayHost = FusionRpg.Core.Overlay.OverlayHostSelection.Resolve(
            Environment.GetEnvironmentVariable(FusionRpg.Core.Overlay.OverlayHostSelection.EnvVar),
            _config.OverlayHost);

        EnableUnsafeHitPatches = _config.EnableUnsafeHitPatches;
        try { _gameProfileId = Bridges.ZombieCombatFields.ProfileId; }
        catch { _gameProfileId = FusionRpg.Contracts.RpgConstants.GameId381; }

        // tunables-ssot.md §7.2: the injector loads gk-core/data/tuning/ and injects it; Core never reads a
        // file. Copied next to the built plugin DLL by the host .csproj (BepInEx / MelonLoader).
        var tuningDir = System.IO.Path.Combine(_pluginDir, "data", "tuning");
        FusionRpg.Core.Creatures.Contracts.ContractPolicy.Configure(
            FusionRpg.Core.Creatures.Contracts.ContractTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "contracts.v1.json"))));
        FusionRpg.Core.World.Loam.LoamPolicy.Configure(
            FusionRpg.Core.World.Loam.LoamTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "loam.v5.json"))));
        var worldTuning = FusionRpg.Core.World.WorldTuningLoader.Parse(
            System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "world.v6.json")));
        FusionRpg.Core.World.WorldTuningHub.Configure(worldTuning);
        FusionRpg.Core.World.Loam.WorldSpawnTuningHub.Configure(
            FusionRpg.Core.World.Loam.WorldSpawnTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "world-spawn.v1.json"))));
        FusionRpg.Core.World.Growth.RecruitPolicy.Configure(worldTuning.Growth);
        FusionRpg.Core.Creatures.SoulEarnPolicy.Configure(
            FusionRpg.Core.Creatures.SoulEarnTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "souls.v1.json"))));
        FusionRpg.Core.Creatures.Patron.PatronPolicy.Configure(
            FusionRpg.Core.Creatures.Patron.PatronTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "patron.v1.json"))));
        FusionRpg.Core.Combat.Shield.ShieldPolicy.Configure(
            FusionRpg.Core.Combat.Shield.ShieldTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "shield.v1.json"))));
        FusionRpg.Core.Combat.CombatPolicy.Configure(
            FusionRpg.Core.Combat.CombatTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "combat.v1.json"))));
        // combat-ai profile-schema (CAI1.8): the injector does not load siege.v{n}.json today (siege
        // battles resolve server-side), but combat-ai.v1.json is an independent domain every place
        // eventually reads (the lawn, wave 4, runs here) -- configured now so a later module's
        // Core/Actions/Ai/ read never finds this host unconfigured.
        var combatAiJson = System.IO.File.ReadAllText(
            System.IO.Path.Combine(tuningDir, FusionRpg.Core.Actions.Ai.CombatAiTuningFiles.Current));
        FusionRpg.Core.Actions.Ai.CombatAiProfilePolicy.Configure(
            FusionRpg.Core.Actions.Ai.CombatAiTuningLoader.Parse(combatAiJson));
        // combat-ai lawn-cast-trigger (module 19, CAI4.8): the `lawn` block of the SAME document, from the
        // same read, so the two parses cannot drift onto different revisions. Parsed here rather than at
        // board.start (once a match). A stale tuning directory must not take the host down over a cadence,
        // so a parse failure is reported and the slot then reports its own absence at board.start.
        try
        {
            FusionRpg.Injector.Effects.LawnDecisionHost.Configure(
                FusionRpg.Core.Actions.Ai.CombatAiLawnTuning.Parse(combatAiJson));
        }
        catch (Exception ex)
        {
            _log.Error("lawn.ai: combat-ai lawn section failed to parse -- " + ex.GetType().Name + ": " + ex.Message);
        }
        FusionRpg.Core.Creatures.Fusion.StarPolicy.Configure(
            FusionRpg.Core.Creatures.Fusion.FusionTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "fusion.v2.json"))));
        FusionRpg.Core.Status.StatusPolicy.Configure(
            FusionRpg.Core.Status.StatusTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "status.v1.json"))));
        FusionRpg.Core.Stats.Derived.DerivedStatPolicy.Configure(
            FusionRpg.Core.Stats.Derived.DerivedStatTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "derived-stats.v2.json"))));
        FusionRpg.Core.ActorSurface.ActorSurfaceCatalogHub.ConfigureAll(
            FusionRpg.Core.ActorSurface.AptitudeSurfaceCatalogLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "aptitude-catalog.v1.json"))),
            FusionRpg.Core.ActorSurface.DerivedStatSurfaceCatalogLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "derived-stat-catalog.v3.json"))),
            FusionRpg.Core.ActorSurface.StatusSurfaceCatalogLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "status-catalog.v1.json"))),
            FusionRpg.Core.ActorSurface.ResourceSurfaceCatalogLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "resource-catalog.v1.json"))),
            FusionRpg.Core.ActorSurface.ElementSurfaceCatalogLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "element-catalog.v2.json"))),
            FusionRpg.Core.ActorSurface.ActorSheetSurfaceCatalogLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "actor-sheet.v1.json"))));
        // catalog-runtime's Injector-side flip (seed-to-concrete, 2026-09-06): both of step 5's own
        // preconditions are now satisfied and evidenced — the 829-species real classification run
        // (T2.11, 2026-09-04) and the live-lawn/diff-test proof (Checkpoint 4; re-proven this session
        // via T8.5's mint-creature check) — so this host reads the same real committed tree the Server
        // already flipped to, via the Core-only path that never needs SQL: every
        // gk-data/packs/fusion/data/generated/creatures/<SpeciesId>.json this project's own .csproj now copies alongside the
        // built mod, parsed by ConcreteSpeciesSeedReader and mapped by ConcreteSpeciesMapper — the
        // SAME mapper RpgStore.BuildCreatureSpeciesSnapshot() calls, proven field-for-field identical
        // against the real 829-species tree (ConcreteSpeciesSeedReaderTests). Throws loudly on a
        // missing/empty tree rather than silently falling back to the compiled default, matching
        // CreatureSpeciesCatalog.Configure's own established "fail loudly at load, name the fix" rule.
        {
            var speciesDir = System.IO.Path.Combine(_pluginDir, "data", "generated", "creatures");
            if (!System.IO.Directory.Exists(speciesDir))
                throw new InvalidOperationException(
                    $"Species seed tree not found at '{speciesDir}'. Rebuild this project — its own " +
                    ".csproj now copies data/generated/creatures/*.json alongside the mod — or point " +
                    "FUSIONRPG at a plugin folder that has been rebuilt since 2026-09-06.");
            var roster = System.IO.Directory.EnumerateFiles(speciesDir, "*.json")
                .Where(p => !System.IO.Path.GetFileName(p).StartsWith('_'))
                .Select(FusionRpg.Core.Creatures.Generation.ConcreteSpeciesSeedReader.ParseFile)
                .Select(FusionRpg.Core.Creatures.Generation.ConcreteSpeciesMapper.ToCreatureSpeciesDef)
                .ToList();
            FusionRpg.Core.Creatures.CreatureSpeciesCatalog.Configure(roster);
        }
            // ⚠️ The version is PINNED on purpose, and a balance pass is a TWO-step change:
            // `gk-core/tools/tuning/publish.py overlay ...` writes v{n+1} and leaves v{n} on disk, then this
            // literal is bumped in a commit of its own. That commit is the attribution tunables-ssot
            // T7 requires — "a tuning change must not be able to hide a code regression" — and it is
            // what every one of the ~97 tuning load sites in this repo does.
            //
            // An earlier version of this line resolved the highest v{n} on disk instead. That is
            // wrong: it lets a published file change the game with no code change, no commit and
            // nothing to revert to, and for a domain like power-scale it would silently decouple the
            // tuning from the RulesetVersion its own comment says must be un-bumped together.
        FusionRpg.Core.Overlay.OverlayTuningHub.Configure(
            FusionRpg.Core.Overlay.OverlayTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "overlay.v4.json"))));
        FusionRpg.Core.Stats.Derived.StatsTuningHub.Configure(
            FusionRpg.Core.Stats.Derived.StatsTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "stats.v1.json"))));
        FusionRpg.Core.Expeditions.ExpeditionTuningHub.Configure(
            FusionRpg.Core.Expeditions.ExpeditionTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "expeditions.v2.json"))));
        // party-dungeon D1.4 -- same ordering as Server/Program.cs: registries load first (pure),
        // DungeonTuningHub/EncounterTuningHub configure next (cross-checked against them at parse
        // time), DungeonRegistryHub last (RoomKindDef joins DungeonTuningHub at first read).
        var dungeonRegistryDir = System.IO.Path.Combine(_pluginDir, "data", "seed", "dungeon", "_registry");
        var dungeonRegistries = FusionRpg.Core.Dungeon.Registry.DungeonRegistryLoader.LoadAll(dungeonRegistryDir);
        FusionRpg.Core.Dungeon.Tuning.DungeonTuningHub.Configure(
            FusionRpg.Core.Dungeon.Tuning.DungeonTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "dungeon.v3.json")), dungeonRegistries));
        FusionRpg.Core.Dungeon.Tuning.EncounterTuningHub.Configure(
            FusionRpg.Core.Dungeon.Tuning.EncounterTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "encounter.v1.json")), dungeonRegistries,
                // tier-propagation-contract T-2: the threat ladder is read from its own tuning file, never restated.
                FusionRpg.Core.Creatures.Generation.CreatureThreatTuningLoader.Parse(
                    System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "creature-threat.v1.json"))).RungIds));
        FusionRpg.Core.Dungeon.Registry.DungeonRegistryHub.Configure(dungeonRegistries);
        // commander-identity SE4.3: the injector's session cache and the store's persisted-string
        // encoders resolve commanders through one hub. The plugin dir carries the authored registry
        // (each injector host csproj Content-Includes data\seed\commanders\_registry, same as
        // data\seed\dungeon\_registry above -- neither was actually wired until the live-qa B27/BP4
        // probe hit the resulting DirectoryNotFoundException on every fresh game install, 2026-09-20).
        FusionRpg.Core.Commanders.CommanderDirectoryHub.Configure(
            FusionRpg.Core.Commanders.DataCommanderDirectory.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(
                    _pluginDir, "data", "seed", "commanders", "_registry", "default-commanders.v1.json"))));
        // identity-rename T12: the commander rows may name a lead by TOKEN (`{lead_antagonist}`), and
        // DataCommanderDirectory resolves it through this hub — so the injector must configure it
        // exactly as the server does, or every DisplayName read throws at snapshot time. Same
        // plugin-dir registry the commander hub above reads, one directory over.
        FusionRpg.Core.Narrative.LeadNamesHub.Configure(
            FusionRpg.Core.Narrative.LeadNames.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(
                    _pluginDir, "data", "seed", "narrative", "_registry", "names.en.v1.json"))));
        FusionRpg.Core.Match.MatchTuningPolicy.Configure(
            FusionRpg.Core.Match.MatchTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "match.v1.json"))));
        FusionRpg.Core.Effects.EffectsTuningHub.Configure(
            FusionRpg.Core.Effects.EffectsTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "effects.v1.json"))));
        FusionRpg.Core.Net.NetPolicy.Configure(
            FusionRpg.Core.Net.NetTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "net.v1.json"))));
        FusionRpg.Core.Vfx.VfxTuningHub.Configure(
            FusionRpg.Core.Vfx.VfxTuningLoader.Parse(
                // v7 is the current Earth phase-sequence revision. The renderer skips absent
                // phase tuning rather than guessing presentation values.
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "vfx.v7.json"))));
        FusionRpg.Core.Power.PowerTuningHub.Configure(
            FusionRpg.Core.Power.PowerTuningLoader.Parse(
                // T4.2 (power-dial, 2026-08-24): v1 (bMilli=0) -> v2 (bMilli=400). v1 stays on disk --
                // reverting is pointing this back at power-scale.v1.json and un-bumping RulesetVersion.
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "power-scale.v2.json"))));
        FusionRpg.Core.Stats.Aptitudes.AptitudeTuningHub.Configure(
            FusionRpg.Core.Stats.Aptitudes.AptitudeTuningLoader.Parse(
                // class-system-todo.md P8.2/P8.3 (2026-08-27): v1 -> v2. Phase 0 six-resource coverage (2026-09-02): v2 -> v3, then v3 -> v4 (0.8: combat.heal.power generalised to resource.restore.{resource}) -- 32 edges added so every (family x resource) cell is fed, closing P7.2's poise gap. v2 stays on disk -- reverting is pointing this back at aptitudes.v2.json. passive-tree C6 (2026-09-06): v5 -> v6, pointEconomy gains skillPointsPerThetaMilliByScope (D34). D55 (2026-09-06): v6 -> v7, creatureType/aspect/uniqueCreature given real rates {15,15,22} -- v6 stays on disk. solid-enforcement retire-atk (2026-09-18, R3): v8 -> v9, removes the two reader-less Might/Ferocity -> progression.bonus.atk edges. species-progression SP6.0 (2026-09-19, R21) republished on top of it (empire-progression-20260920's first merge had spliced the same content directly into v9 by hand; superseded by this real publish): v9 -> v10, adds read.layerWeightMilliByScope (applied by step 6.1) -- v9 stays on disk.
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "aptitudes.v10.json"))));
        FusionRpg.Core.Hud.ActorHudTuningHub.Configure(
            FusionRpg.Core.Hud.ActorHudTuningLoader.Parse(
                // Screen-space silhouette anchoring (actor-hud v4): v1/v2 world offsets are retained
                // only for backwards parsing. The presenter now consumes the explicit pixel layout.
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "actor-hud.v7.json"))));
        FusionRpg.Core.Match.LawnDeployEventsTuningHub.Configure(
            FusionRpg.Core.Match.LawnDeployEventsTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "lawn-deploy-events.v1.json"))));
        // creature-lawn-deploy unique-deploy-cap (lawn LW5.1): the same limits the server's deploy
        // admission reads, loaded here so a Zomboss-side caller finds the hub already configured.
        FusionRpg.Core.Match.LawnDeployLimitsTuningHub.Configure(
            FusionRpg.Core.Match.LawnDeployLimitsTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "lawn-deploy.v1.json"))));
        FusionRpg.Core.Match.Ai.ZombossDeployTuningHub.Configure(
            FusionRpg.Core.Match.Ai.ZombossDeployTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "zomboss-deploy-ai.v1.json"))));
        // lawn-combat-wire T8 (spec-lawn-action-bridge.md): without this, the first lawn touch of the
        // hand-built basic-attack row throws -- ActionTimingDerivation.DeriveBasicAttack (called from
        // BasicAttackFactory.Create) reads ActionTimingPolicy.Tuning, which throws
        // InvalidOperationException until Configure has run. Server/Program.cs already does this for
        // the Battle system; this is the injector's own copy, from the same shipped tuning file
        // (already copied verbatim by every host .csproj's own `data\tuning\**\*.json` content rule --
        // no build change needed). Ordered here, before RpgHost.Initialize returns and therefore before
        // any lawn actor can be granted a basic attack -- never raced against host startup.
        FusionRpg.Core.Actions.ActionTimingPolicy.Configure(
            FusionRpg.Core.Actions.ActionTimingTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "action-timing.v1.json"))));
        // lawn-combat-wire T12a (spec-basic-attack-cost.md wire 2): without this, ResourceBaselineSubsystem
        // (now registered on CheatState.ActorHub) reads BattleRuleset.ResourceTuning before Configure has
        // ever run, throwing on the injector's very first resource-max resolve. Same file, same call
        // Server/Program.cs already makes for the identical reason -- one shared tuning read, no second
        // copy of the arithmetic. v2 (not v1): the real, non-zero stamina regen share T11 authored.
        FusionRpg.Core.Battle.BattleRuleset.ConfigureResources(
            FusionRpg.Core.Battle.BattleResourceTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "battle-resources.v2.json"))));
        // lawn-combat-wire T12 (spec-basic-attack-cost.md), sixth defect (2026-09-14): the same
        // "Server/Program.cs already does this, this is the injector's own copy" gap as the two
        // Configure calls above, just missed at the time -- Program.cs's own RungPolicy.Configure
        // call carries a comment ("actions are battle-mode and the injector never sees one, so the
        // rung ladder has no reason to load there") that was true until this lawn-combat-wire T12
        // module gave LawnBasicAttackCostGate/CostLedger a real injector-side RungPolicy.Table read
        // (rung 1 is RungPolicy's own shipped inert row, CostMulti=1000). Unconfigured, every
        // TryChargeForSwing call threw "RungPolicy.Configure(...) has not run", caught by
        // LawnBasicAttackCostCharger.ShouldApplyRider's own try/catch and failed closed -- silently
        // zeroing actionTriggers/staminaSpent forever, confirmed live: real combat, a real bound
        // grant, drainTickTotalMs > 0, yet actionTriggers stayed 0 until this fix.
        FusionRpg.Core.Actions.Rungs.RungPolicy.Configure(
            FusionRpg.Core.Actions.Rungs.RungTableLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "action-rungs.v4.json"))));
        // action-enrich AE2.2 (spec-lawn-action-base.md §Host configuration): the injector's own copy of
        // Server/Program.cs's configure, for the identical reason as the block above. Without it,
        // LawnBasicAttackGrantBinder's bind reads ActionBaseTuningHub.Tuning and throws
        // "Configure(...) has not run" on every lawn actor — the exact "Server does this, the injector's
        // own copy was missed" defect that silently zeroed swing charging on 2026-09-14 (see this file's
        // own comment on RungPolicy above). Same version Program.cs loads (action-skill-tiers ST5.4's
        // tuning-version agreement guard holds the two hosts together).
        FusionRpg.Core.Actions.ActionBaseTuningHub.Configure(
            FusionRpg.Core.Actions.ActionBaseTuningLoader.Parse(
                System.IO.File.ReadAllText(System.IO.Path.Combine(tuningDir, "action-base.v2.json"))));

        IsInitialized = true;
    }

    /// <summary>No-op — no in-game overlay. Telemetry goes to Log / web events.</summary>
    public static void Note(string line) { }
}

/// <summary>Defaults when host has not bound config yet.</summary>
public sealed class DefaultRpgConfig : IRpgConfig
{
    public string ServerUrl => RpgHost.DefaultServerUrl;
    public bool ServerUrlFromFallback => true;
    public string OverlayHost => "launcher";
    public bool PersistCheats => false;
    public bool EnableUnsafeHitPatches => false;
}
