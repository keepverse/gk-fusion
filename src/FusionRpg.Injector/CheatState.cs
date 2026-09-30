using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using FusionRpg.CheatCore;
using FusionRpg.Contracts;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;

using FusionRpg.Injector.Host;
using FusionRpg.Injector.Lawn;
using FusionRpg.Injector.Match;
using FusionRpg.Bridge.Stats;

namespace FusionRpg.Injector;

/// <summary>Session cheat registry keyed by coverage ids from cheat-menu-coverage.md.</summary>
public static class CheatState
{
    static readonly object Gate = new();
    static readonly Dictionary<string, CheatEntry> Entries = new(StringComparer.Ordinal);
    static string? _persistPath;
    public static bool MenuOpen;
    /// <summary>Always false — in-game F8 menu retired; web FE is SSOT.</summary>
    public static bool MenuEnabled;
    public static bool EmitProof = true;
    public static bool PersistEnabled;
    public static bool LocalStatsOverride;
    public static bool BoardConfigLocked;
    public static StatsConfig LocalStats { get; } = new();
    /// <summary>Shared StatSystem — plugins compose Y0 + Xi → Y. Cheats feed cheat.scale / cheat.absolute only.</summary>
    public static StatSystem Stats { get; } = StatSystemBootstrap.CreateDefault();
    static ActorHub? _actorHub;
    /// <summary>Derived snapshot compose — wraps Stats; Writer uses AppliedCombat. class-system-todo.md
    /// P1.10 (2026-08-26): now fed by <see cref="PowerIndex"/> — was constructed via a field
    /// initializer, which forced <c>PowerTuningHub.Tuning</c> (throws before
    /// <c>RpgHost.Initialize</c>'s <c>Configure</c> call) the moment ANY static member of
    /// <see cref="CheatState"/> was first touched. Lazy, same pattern as <see cref="PowerIndex"/>
    /// itself, so first evaluation happens on first actual stat resolve rather than at class-load.</summary>
    /// <summary>class-system-todo.md P2.4 (2026-08-27): now also passes AptitudeTuningHub.Tuning, so
    /// the overlay's aptitude resolve is actually live. aura-skill T5 (W1, 2026-08-30): the stale half
    /// of this comment — "allocation defaults to Empty, P6's AllocationStore doesn't exist yet" — is
    /// corrected here rather than left to rot: AllocationStore (`RpgStore.LoadAllocation`) has existed
    /// and been tested since point-economy landed, it simply had zero production callers until
    /// <see cref="CommanderAllocationSource"/> below became the first one. Exercised through the real
    /// ActorHub.Register seam, exactly as spec-aptitude-resolve.md §2 requires ("via
    /// ActorHub.Register", not merely capable of it). Both hosts already call
    /// AptitudeTuningHub.Configure at startup (P2.3), so this read is safe by the time anything
    /// touches ActorHub.</summary>
    public static ActorHub ActorHub => _actorHub ??= ActorHubBootstrap.CreateDefault(
        Stats, powerIndex: PowerIndex, aptitudeTuning: FusionRpg.Core.Stats.Aptitudes.AptitudeTuningHub.Tuning,
        // species-build `allocation-transport` (module 6): was CommanderAllocation.Resolve directly;
        // now routed through SpeciesAllocation, which builds ONE AptitudeAllocation object carrying
        // the SAME cached commander allocation alongside whichever species this ctx's (Side, TypeId)
        // resolves to. species-progression step 6.1 (spec-species-layer-delivery.md, R2/R16) rewrites
        // what happens next: AptitudeResolver.Resolve no longer treats this as "one merged share" --
        // it resolves EACH AllocationScope this object holds points in ALONE (ShareWithinScope), then
        // concatenates every scope's own contributions. The object built here is still one merge; the
        // resolve of it is now per-layer, never a single combined share.
        aptitudeAllocation: SpeciesAllocation.Resolve,
        // The lawn's `stat.derived` consumer (decisions.md "Derived-write lawn executor", 2026-08-30).
        // Registering it here is what lets the AtomKindRegistry Lawn cell be `Full` without recreating
        // D6's "binds accepted, nothing applied" state.
        // Fully qualified on purpose: a bare `Stats.` here is ambiguous with this class's own
        // `Stats` StatSystem property.
        //
        // lawn-tree-hydrate (T13): merges in the cached commander-scope shared-tree atoms alongside
        // the live-grant reader above — two independent Hub combat writers, one delegate, neither
        // shadowing the other (a node granting the same channel as a live grant folds via the SAME
        // DerivedComposer both already feed, never a second private sum).
        boundDerivedAtoms: ctx =>
            FusionRpg.Injector.Stats.GrantedDerivedAtoms.For(ctx)
                .Concat(FusionRpg.Injector.Stats.TreeBoundAtomsCache.For(ctx))
                .ToList(),
        // mechanism-wiring G1's injector half (spec-mechanism-wiring.md §4.1): registers the fourth
        // IActorStatSubsystem so a status's own `stat.<combat.*|status.*>.<op>` writes reach the
        // composed value instead of landing in the primary bag no subsystem reads. Additive next to
        // boundDerivedAtoms above — same opt-in shape, same fully-qualified-on-purpose reason.
        statusDerivedMods: FusionRpg.Injector.Stats.StatusDerivedMods.For,
        // lawn-combat-wire T12a (spec-basic-attack-cost.md wire 2): the injector's Hub never opted
        // into ResourceBaselineSubsystem before this line, so `resource.max.*`/`resource.regen.*`
        // resolved to 0 for every lawn actor -- indistinguishable from this whole feature's own bug.
        // Sole prior `true` caller was UniqueActorHubCompose.cs (Server's cold /sheet compose); this
        // is the injector's own opt-in, from the SAME already-shipped ResourceBaselineSubsystem/
        // BattleRuleset.BaseResourceMax/BaseResourceRegen -- never a second seed mechanism.
        seedResourceBaseline: true,
        // species-progression step 6.2 (SP6.2 built the subsystem; this wiring is SP6.4): delivers 1a
        // (species-passive core) + 1b (a fusion pick) rows through the ONE registered
        // SpeciesLayerSubsystem, replacing nothing today (the old aptitude-allocation species term
        // this same ctx also resolves via SpeciesAllocation above is untouched until step 6.3).
        speciesLayers: SpeciesLayer.Resolve);

    /// <summary>aura-skill T5 (W1): cached commander-scope allocation — <see cref="ActorHub"/>'s
    /// hot-path <c>aptitudeAllocation</c> delegate reads only this cache, never the server
    /// (`CommanderAllocationSource`'s own doc comment). Populated by
    /// <see cref="ApplyCommanderAllocation"/>, called from <c>RpgClient.RefreshCommanderAllocationAsync</c>
    /// at session start and on the server's <c>"AptitudesUpdated"</c> SignalR broadcast — never on a
    /// per-hit poll.</summary>
    public static readonly FusionRpg.Core.Stats.Aptitudes.CommanderAllocationSource CommanderAllocation =
        new(() => FusionRpg.Core.Commanders.MatchCommanderSnapshotHolder.ResolveAllocation(_fetchedCommanderAllocation));
    static FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation _fetchedCommanderAllocation =
        FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Empty;

    /// <summary>Latest commander allocation from server poll — used when building match snapshot cache.</summary>
    internal static FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation FetchedCommanderAllocation =>
        _fetchedCommanderAllocation;

    /// <summary>Called from the transport (<c>RpgClient.RefreshCommanderAllocationAsync</c>) after a
    /// successful fetch. Stores the value and immediately refreshes the cache on this same call —
    /// never touched from a hot-path stat resolve.</summary>
    public static void ApplyCommanderAllocation(FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation allocation)
    {
        _fetchedCommanderAllocation = allocation;
        RefreshCommanderAllocationCache();
        // A commander reallocation changes what every living entity resolves to, so it is a stat
        // invalidation like any other. Without this the new allocation only reached entities spawned
        // AFTER it (owner-observed live 2026-08-30: reallocating mid-match changed nothing until the
        // injector reconnected). Invalidate is edge-triggered -- ConsumeDirty clears it -- so this
        // costs one reapply per real allocation change, not per frame.
        Stats.Invalidate();
    }

    /// <summary>Edge-triggered sync of <see cref="CommanderAllocation"/> hot-path cache — match
    /// start/end and allocation poll, never per stat resolve.
    ///
    /// <para>`species-build` T0.1/T0.2: `AptitudeSubsystem`'s per-entity memo needs no explicit
    /// invalidation from here — it self-corrects by checking the allocation's own object reference on
    /// every read, so `CommanderAllocation.Refresh()` replacing `_cached` with a new instance is
    /// already sufficient (see that type's own doc comment for why an earlier draft's explicit-bump
    /// design was wrong: it could not cover a Core-only caller that never goes through
    /// `CheatState`).</para></summary>
    internal static void RefreshCommanderAllocationCache() => CommanderAllocation.Refresh();

    // ---- species-build `allocation-transport` (module 6) --------------------------------------

    /// <summary>The injector-side cache `spec-allocation-transport.md`'s own "Injector side" section
    /// describes: keyed by speciesId, holding each species' EFFECTIVE allocation exactly as the server
    /// computed it (baseline composed with any override — this cache never needs the plan, the level,
    /// or the budget rule, matching the spec's own "it receives points" framing). Replaced wholesale on
    /// each refresh, at exactly the existing commander-cache cadence (StartAsync, reconnect,
    /// AptitudesUpdated, match edges) — never a per-entity fetch, never a poll of its own.</summary>
    static IReadOnlyDictionary<string, FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation> _speciesAllocations =
        new Dictionary<string, FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation>(StringComparer.Ordinal);

    /// <summary>
    /// `ai-empire-species` EP4.18 (R23) — each empire's OWN commander pool, keyed by `EmpireId.Value`, fed
    /// by the SAME `GET /api/aptitudes/{playerId}` fetch that already carries `shares` and `speciesLayers`
    /// (one fetch, another cache, never a second round trip). Wholesale replace on each refresh, exactly
    /// like every other cache here.
    ///
    /// <para><b>The human empire is deliberately NOT in this map's routing.</b> `_humanEmpireForPools`
    /// names the save's own human empire, so the human's ask still resolves through
    /// <see cref="CommanderAllocation"/> — the match-scoped cache whose answer changes on a match edge
    /// with no fetch at all (a pre-existing behaviour that must not be taken over here). Every OTHER
    /// empire's ask reads this map.</para>
    /// </summary>
    static IReadOnlyDictionary<string, FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation> _commanderPoolsByEmpire =
        new Dictionary<string, FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation>(StringComparer.Ordinal);
    static string? _humanEmpireForPools;

    /// <summary>Called from the transport (`RpgClient.RefreshCommanderAllocationAsync`) after a successful
    /// fetch. Wholesale replace plus `Stats.Invalidate()`, matching <see cref="ApplySpeciesAllocations"/>'s
    /// contract exactly — a refresh must reach entities already spawned, not only future ones.</summary>
    public static void ApplyCommanderPools(
        string? humanEmpire,
        IReadOnlyDictionary<string, FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation> byEmpire)
    {
        _humanEmpireForPools = humanEmpire;
        _commanderPoolsByEmpire = byEmpire ?? throw new ArgumentNullException(nameof(byEmpire));
        Stats.Invalidate();
    }

    /// <summary>
    /// The ONE empire-keyed commander read the `SpeciesAllocation` source asks (R23). The human empire
    /// resolves through the match-scoped cache; every other empire comes from
    /// <see cref="_commanderPoolsByEmpire"/>, and an empire the payload does not carry answers Empty —
    /// NEVER the human's pool under another empire's name, which is the S1 defect the delegate's EMPIRE
    /// argument exists to prevent. A payload with no `humanEmpire` (an older server) keeps the pre-R23
    /// answer: every ask is the human's own match-scoped pool.
    /// </summary>
    public static FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation CommanderPoolFor(
        FusionRpg.Core.Commanders.EmpireId empire)
    {
        if (_humanEmpireForPools is null
            || string.Equals(empire.Value, _humanEmpireForPools, StringComparison.Ordinal))
            return CommanderAllocation.Resolve(DummyStatContextForCommanderRead);

        return _commanderPoolsByEmpire.TryGetValue(empire.Value, out var pool)
            ? pool
            : FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Empty;
    }

    /// <summary>`(Side, GameTypeId) → speciesId`, lazily built from <c>CreatureSpeciesCatalog.All</c> and
    /// cached for the process lifetime (`catalog-runtime`'s own "loaded once, immutable" rule — the
    /// SAME precedent <c>LawnElementResolverHost</c> already established for the element-resolve case).
    /// <see cref="FusionRpg.Core.Creatures.CreatureSpeciesCatalog.IsConfigured"/> is checked FIRST, non-
    /// throwing, so an un-configured catalog is a distinguishable <see cref="FusionRpg.Core.Stats.Aptitudes.SpeciesLookupResult.NotConfigured"/>
    /// answer rather than an exception or a silent empty-index miss (the exact bootstrap-window hazard
    /// spec-allocation-transport.md calls out by name).</summary>
    static readonly object SpeciesIndexGate = new();
    static FusionRpg.Core.Creatures.LawnElementIndex? _speciesIndex;

    static FusionRpg.Core.Stats.Aptitudes.SpeciesLookupResult ResolveSpeciesLookup(StatSide side, int typeId)
    {
        if (!FusionRpg.Core.Creatures.CreatureSpeciesCatalog.IsConfigured)
            return FusionRpg.Core.Stats.Aptitudes.SpeciesLookupResult.NotConfigured;

        FusionRpg.Core.Creatures.LawnElementIndex index;
        lock (SpeciesIndexGate)
            index = _speciesIndex ??= new FusionRpg.Core.Creatures.LawnElementIndex(FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All);

        var sideText = side == StatSide.Zombie ? "zombie" : "plant";
        return index.TryGet(sideText, typeId, out var species)
            ? FusionRpg.Core.Stats.Aptitudes.SpeciesLookupResult.Hit(species.SpeciesId)
            : FusionRpg.Core.Stats.Aptitudes.SpeciesLookupResult.NoSpecies;
    }

    /// <summary><see cref="FusionRpg.Core.Stats.Aptitudes.CommanderAllocationSource.Resolve"/> ignores
    /// its parameter entirely (it is scoped to the local injector's one active commander, not per-ctx)
    /// — this shared instance avoids allocating a throwaway <see cref="StatContext"/> on every read.
    /// Declared BEFORE <see cref="SpeciesAllocation"/> below purely to satisfy the nullable analyzer's
    /// linear, declaration-order view of static field initializers (a real build surfaced the warning:
    /// runtime behavior was always correct either way, since a lambda captures a field by reference
    /// and every static field finishes initializing before ANY of them is first used — see the
    /// reordering's own point, it's a warning fix, not a behavior fix).</summary>
    static readonly StatContext DummyStatContextForCommanderRead = new();

    /// <summary>Commander merged with whichever species this ctx resolves to — the ONE place
    /// `ActorHub`'s `aptitudeAllocation` delegate reads. A `LawnElementIndex` not yet configured is
    /// reported once per call (via <c>RpgHost.Log.Warning</c>, matching <c>LawnElementResolverHost</c>'s
    /// own reporting convention) rather than silently resolving commander-only forever.</summary>
    public static readonly FusionRpg.Core.Stats.Aptitudes.SpeciesAllocationSource SpeciesAllocation = new(
        resolveSpeciesId: ResolveSpeciesLookup,
        // solid-remediation T4.1 (S1/S3): the empire is now part of the species key, and this cache
        // holds exactly ONE empire's rows — the player's. `/api/aptitudes/{playerId}` returns the
        // player's own `species` map and nothing else, so answering a Zomboss-empire ask out of this
        // dictionary would hand a lawn zombie the human player's species progression. Zomboss's empire
        // has no species rows anywhere yet (nothing writes them), so it resolves Empty — the same
        // treatment, and for the same reason, as the commander term above.
        resolveSpeciesAllocation: (empire, speciesId) =>
            empire == FusionRpg.Core.Commanders.EmpireId.Dave
            && _speciesAllocations.TryGetValue(speciesId, out var a)
                ? a : FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Empty,
        resolveCommanderAllocation: CommanderPoolFor,
        reportUnconfigured: msg => RpgHost.Log.Warning(msg),
        resolveBoundInstanceId: ResolveBoundInstanceId,
        resolveUniqueAllocation: instanceId => _uniqueAllocations.TryGetValue(instanceId, out var u)
            ? u : FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Empty,
        // species-progression SP1.3 (layer-source-selector, rule 3 / C10): a Bound unique's commander
        // term comes from its OWNER, read from the SAME specimen-owner map save-identity SE4.28
        // already types as (EmpireId, EmpireController) -- never ctx.Side.
        resolveSpecimenOwnerEmpire: TryGetSpecimenEmpire);

    /// <summary>Called from the transport (`RpgClient.RefreshCommanderAllocationAsync`, extended to
    /// parse the SAME response's new `species` map alongside `shares` — one fetch, both caches, never
    /// a second HTTP round trip) after a successful fetch. Replaces the whole cache — never an
    /// incremental merge, so a species the player no longer has levelled (impossible today, since a
    /// species row is never deleted, but matching the commander cache's own "wholesale replace"
    /// contract) cannot leave a stale entry behind.</summary>
    public static void ApplySpeciesAllocations(
        IReadOnlyDictionary<string, FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation> bySpeciesId)
    {
        _speciesAllocations = bySpeciesId ?? throw new ArgumentNullException(nameof(bySpeciesId));
        Stats.Invalidate();
    }

    // ---- species-progression `species-layer-delivery` step 6.2 (SP6.4) ------------------------

    /// <summary>1a rows, keyed by speciesId — `speciesLayers.base` (SP6.3's own wire shape),
    /// replaced wholesale on each refresh, at the SAME cadence as <see cref="_speciesAllocations"/>.</summary>
    static IReadOnlyDictionary<string, IReadOnlyList<FusionRpg.Core.Creatures.Layers.ProjectedLayerRow>> _speciesLayersBase =
        new Dictionary<string, IReadOnlyList<FusionRpg.Core.Creatures.Layers.ProjectedLayerRow>>(StringComparer.Ordinal);

    /// <summary>1b rows, keyed by (empireId, speciesId) — `speciesLayers.mod`. An empire with no
    /// ledger rows is simply absent, matching the wire's own "absent, not present-with-nothing"
    /// contract (SP6.3).</summary>
    static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<FusionRpg.Core.Creatures.Layers.ProjectedLayerRow>>> _speciesLayersMod =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<FusionRpg.Core.Creatures.Layers.ProjectedLayerRow>>>(StringComparer.Ordinal);

    /// <summary>Called from the transport (`RpgClient.RefreshCommanderAllocationAsync`, extended to
    /// parse the SAME response's new `speciesLayers` field alongside `species` — one fetch, another
    /// cache, never a second HTTP round trip) after a successful fetch. Replaces BOTH caches wholesale
    /// — never an incremental merge, matching <see cref="ApplySpeciesAllocations"/>'s own contract —
    /// and invalidates live stats so a refresh reaches entities already spawned, not only future ones
    /// (the 2026-08-30 owner-caught defect, in this cache's own form).</summary>
    public static void ApplySpeciesLayers(
        IReadOnlyDictionary<string, IReadOnlyList<FusionRpg.Core.Creatures.Layers.ProjectedLayerRow>> baseBySpeciesId,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<FusionRpg.Core.Creatures.Layers.ProjectedLayerRow>>> modByEmpireThenSpecies)
    {
        _speciesLayersBase = baseBySpeciesId ?? throw new ArgumentNullException(nameof(baseBySpeciesId));
        _speciesLayersMod = modByEmpireThenSpecies ?? throw new ArgumentNullException(nameof(modByEmpireThenSpecies));
        Stats.Invalidate();
    }

    /// <summary>The `ctx -> 1a+1b rows` resolution `ActorHub`'s `speciesLayers` delegate reads —
    /// mirrors <see cref="SpeciesAllocation"/>'s own construction exactly, reusing the SAME species
    /// lookup and the SAME Bound-specimen delegates (`ResolveSpeciesLookup`, `ResolveBoundInstanceId`,
    /// `TryGetSpecimenEmpire`) rather than a second copy of either.</summary>
    public static readonly FusionRpg.Core.Stats.Aptitudes.SpeciesLayerSource SpeciesLayer = new(
        resolveSpeciesId: ResolveSpeciesLookup,
        resolveBaseRows: speciesId => _speciesLayersBase.TryGetValue(speciesId, out var rows)
            ? rows : Array.Empty<FusionRpg.Core.Creatures.Layers.ProjectedLayerRow>(),
        resolveModRows: (empire, speciesId) =>
            _speciesLayersMod.TryGetValue(empire.Value, out var perSpecies)
            && perSpecies.TryGetValue(speciesId, out var rows)
                ? rows : Array.Empty<FusionRpg.Core.Creatures.Layers.ProjectedLayerRow>(),
        resolveBoundInstanceId: ResolveBoundInstanceId,
        resolveSpecimenOwnerEmpire: TryGetSpecimenEmpire);

    // ---- aptitude-sheet `unique-lawn-wire` (AS-1.1) --------------------------------------------

    /// <summary>Cache `instanceId → effective UniqueCreature allocation`, populated by
    /// <see cref="ApplyUniqueAllocations"/> from <c>RpgClient</c>'s per-Bound-id
    /// <c>GET /api/aptitudes/unique/{instanceId}</c> fetch. Mirrors <see cref="_speciesAllocations"/>'s
    /// own shape exactly — wholesale replace, no incremental merge.</summary>
    static IReadOnlyDictionary<string, FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation> _uniqueAllocations =
        new Dictionary<string, FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation>(StringComparer.Ordinal);

    /// <summary>`EntityKey` (the live ptr hex `RpgClient`/`EntityApply` already build every
    /// `StatContext` with) → the UniqueCreature `instanceId` it is Bound to, or null when this entity
    /// is not a Bound specimen. Reads the SAME `MatchHost.Runtime` ptr→binding index
    /// <see cref="FusionRpg.Injector.Match.UniqueBoundLoadout"/> already uses for the absolute-write
    /// side of a Bound specimen (W5-C) — one ptr index, two consumers, never a second lookup this
    /// class builds on its own. `Phase != Bound` (PendingSpawn / Cleared) resolves to null, same
    /// guard <c>UniqueBoundLoadout.TryApply</c> applies for the identical reason: a binding row can
    /// briefly exist before/after the specimen is actually the live entity behind this ptr.</summary>
    /// <summary>The shipped ptr→Bound-instance lookup. Public because it is the ONE implementation of
    /// that resolution and `commander-direct-orders` (CAI4.9) needs it to stamp a durable subject onto
    /// a direct order — the alternative was a second copy of these four lines in the injector.
    /// Null means "not a Bound specimen right now", which is an answer, not a failure.</summary>
    public static string? ResolveBoundInstanceId(string entityKey)
    {
        if (string.IsNullOrWhiteSpace(entityKey)) return null;
        if (!MatchHost.Runtime.TryGetBindingByPtr(entityKey, out var binding) || binding is null)
            return null;
        return binding.Phase == FusionRpg.Core.Match.UniqueBindingPhase.Bound ? binding.InstanceId : null;
    }

    /// <summary>Called from the transport (`RpgClient`'s per-Bound-id unique fetch) after a successful
    /// round of fetches. Replaces the whole cache — an instanceId no longer Bound this round (the
    /// specimen was released / died) simply stops being fetched and ages out on the next full replace,
    /// matching <see cref="ApplySpeciesAllocations"/>'s own contract.</summary>
    public static void ApplyUniqueAllocations(
        IReadOnlyDictionary<string, FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation> byInstanceId)
    {
        _uniqueAllocations = byInstanceId ?? throw new ArgumentNullException(nameof(byInstanceId));
        Stats.Invalidate();
    }
    static FusionRpg.Core.Power.IPowerIndexProvider? _powerIndex;
    /// <summary>Θ ladder index. Lazy: PowerTuningHub.Configure runs in RpgHost.Initialize, which this
    /// must not race — <c>PowerTuningHub.Tuning</c> throws (not a stale default) before Configure runs,
    /// so evaluating this eagerly at class-load would crash startup, not just read Theta=0.</summary>
    public static FusionRpg.Core.Power.IPowerIndexProvider PowerIndex =>
        _powerIndex ??= new InjectorPowerIndexProvider(FusionRpg.Core.Power.PowerTuningHub.Tuning);

    /// <summary>aura-skill T6 (W2): the hydration source `InjectorPowerIndexProvider.Hydrate` never
    /// had — called from `RpgClient.RefreshPowerIndexAsync` at session start / on demand, never per
    /// hit. `PowerIndex` stays typed as the interface publicly (no existing consumer needs the
    /// concrete type); this is the one place that needs `Hydrate`, so it casts locally instead of
    /// widening the public property's type for a single internal caller.</summary>
    public static void ApplyPowerSnapshot(long playerId, FusionRpg.Core.Power.ActorLadderSnapshot snapshot)
    {
        if (PowerIndex is InjectorPowerIndexProvider hydratable)
            hydratable.Hydrate(new StatContext { PlayerId = playerId }, snapshot);
        CurrentPlayerId = playerId;
        // action-enrich AE2.3 (spec-lawn-action-base.md §Refresh triggers): the snapshot edge only
        // RECORDS. One lock-protected O(1) flag, no grant call — safe on whatever thread the socket's
        // snapshot arrives on. The lawn grant's baked amount depends on Θ, so the next main-thread
        // drain (LawnBasicAttackGrantBinder.Tick) rebinds every live grant to the new value.
        Effects.LawnBasicAttackGrantBinder.MarkThetaDirty();
    }

    /// <summary>Found 2026-08-30 verifying aura-skill's commander-lawn bridge against a real lawn:
    /// `EntityApply.cs`'s per-plant/zombie `ctx` used `CheatState.PvzStatsPlayerId` for `StatContext.
    /// PlayerId` — an unrelated field, only ever set when the optional PvzStats-scaling feature has
    /// content for this player, which is a legitimately common state for a player who has never
    /// touched it. `HydratedPowerIndexProvider.Key` (`IPowerIndexProvider.cs:60`) includes `PlayerId`,
    /// so a mismatch here silently hydrates Θ under key `"1:Plant:0"` (this method, above) while every
    /// spawn/apply resolve looks it up under `"0:Plant:0"` — `ActorIndex` returns Θ=0 for the miss, and
    /// `AptitudeReadFunctions.Magnitude`'s `kMilli * sharePow * pTheta` formula collapses to 0
    /// regardless of aptitude share once `pTheta` is 0. This field is set from the exact same call that
    /// hydrates Θ, so the two can never disagree about which player's ctx a resolve is for — the actual
    /// bug (a real, empirically observed live-lawn miss: 222 commander points in `Might` produced zero
    /// change in a spawned plant's written `attackDamage`) traced to this exact mismatch via the
    /// formula above, not fixed blind.</summary>
    public static long CurrentPlayerId { get; private set; }
    public static IntPtr SelectedPtr;
    public static string SelectedSide = "";
    static int _spawnCol = 3;
    static int _spawnRow = 2;
    public static int SpawnCol
    {
        get => _spawnCol;
        set => _spawnCol = LawnCoords.ClampCol(value);
    }
    public static int SpawnRow
    {
        get => _spawnRow;
        set => _spawnRow = LawnCoords.ClampRow(value);
    }
    public static int ManualTypeId;
    public static string ManualSide = "plant";
    public static int LastAlmanacType = -1;
    public static string LastAlmanacSide = "";
    public static float TimeScale = 1f;
    public static string LastError = "";
    public static string LastNote = "";
    public static int TabIndex;
    public static int SelectedCatalogIndex;

    /// <summary>Active live probe session (web pack or F8). Cleared on end or timeout.</summary>
    public static string? ActiveProbeId;
    public static string? ActivePackId;
    public static string? ActiveCorrelationId;
    public static DateTime ActiveProbeUtc;

    /// <summary>Loaded PvzStats modifiers for current player (injector hydrate; DB-free).</summary>
    public static List<StatModifier> PvzStatsMods { get; private set; } = new();
    public static long PvzStatsRevision { get; private set; }
    public static long AppliedPvzStatsRevision { get; private set; } = -1;
    public static long PvzStatsPlayerId { get; private set; }

    /// <summary>
    /// aura-skill T21b, widened by save-identity SE4.28: ptr → owning empire, for
    /// `SpecimenOwnershipOracle` (Core) and `MatchHost`'s own "find Zomboss's unit" read. Set once at
    /// spawn time from `pvz.spawn.extra`'s own `empireId`/`controller` fields
    /// (`UniqueActorService.DeployAsync` reads them off the specimen's row and sends them, Server →
    /// Injector — this is the first place anything reads them back). Never one-shot/consumed (unlike
    /// `SpawnSourceByPtr`) — ownership must answer repeatedly for the entity's whole lifetime, not just
    /// its first read.
    ///
    /// <para><b>Was a bare ptr → player id.</b> After save-identity, a specimen's `player_id` names a
    /// SAVE every empire of it shares, so a player id alone can no longer tell which empire owns it —
    /// only the pair (which empire, is it human-controlled) can.</para>
    /// </summary>
    static readonly ConcurrentDictionary<string, (EmpireId Empire, EmpireController Controller)> SpecimenOwnerByPtr = new();

    /// <summary>
    /// <paramref name="empireId"/>/<paramref name="controller"/> are the raw strings the payload
    /// carries (`"zomboss"`/`"dave"`, `"human"`/`"ai"`). Either missing or unparseable registers
    /// NOTHING for this ptr — a payload without the fields (a manual/debug spawn) must read back as
    /// genuinely unknown, never a guessed empire, so <see cref="TryGetSpecimenController"/> answers
    /// null and the mechanical side decides.
    /// </summary>
    public static void RegisterSpecimenOwner(string ptr, string? empireId, string? controller)
    {
        if (string.IsNullOrWhiteSpace(ptr) || string.IsNullOrWhiteSpace(empireId)) return;
        if (ParseController(controller) is not { } parsed) return;
        SpecimenOwnerByPtr[ptr] = (new EmpireId(empireId.Trim()), parsed);
    }

    /// <summary>Null exactly when this ptr was never registered, or was registered with an
    /// unparseable controller — the "genuinely unknown, mechanical side decides" case.</summary>
    public static EmpireController? TryGetSpecimenController(string ptr) =>
        !string.IsNullOrWhiteSpace(ptr) && SpecimenOwnerByPtr.TryGetValue(ptr, out var v) ? v.Controller : null;

    /// <summary>The registered owner's empire id — `MatchHost`'s own "is this Zomboss's unit"
    /// read (`EmpireId.Zomboss`), never an elimination guess against "not the human's".</summary>
    public static EmpireId? TryGetSpecimenEmpire(string ptr) =>
        !string.IsNullOrWhiteSpace(ptr) && SpecimenOwnerByPtr.TryGetValue(ptr, out var v) ? v.Empire : null;

    static EmpireController? ParseController(string? controller) => controller?.Trim().ToLowerInvariant() switch
    {
        "human" => EmpireController.Human,
        "ai" => EmpireController.Ai,
        _ => null,
    };

    /// <summary>One-shot dump source override for next plant/zombie spawn emit (PvzIntent).</summary>
    public static string? PendingSpawnSourceTag;
    static readonly ConcurrentDictionary<IntPtr, string> SpawnSourceByPtr = new();

    const double ProbeTimeoutMinutes = 10;

    public static void Init(string pluginDir)
    {
        _persistPath = Path.Combine(pluginDir, "cheat-state.json");
        EnsureDefaults();
        if (PersistEnabled) TryLoad();
    }

    public static void EnsureDefaults()
    {
        lock (Gate)
        {
            void T(string id, bool v = false) => Put(id, new CheatEntry { Id = id, Kind = "toggle", Enabled = v, IsSet = false });
            void F(string id, double v) => Put(id, new CheatEntry { Id = id, Kind = "slider", FloatValue = v, Enabled = true, IsSet = false });
            void N(string id, double v) => Put(id, new CheatEntry { Id = id, Kind = "number", FloatValue = v, Enabled = true, IsSet = false });

            T("A-APPLY", true);
            F("A-P-HP%", 1f); N("A-P-HP+", 0);
            F("A-P-ATK%", 1f); N("A-P-ATK+", 0);
            F("A-P-DEF%", 1f); N("A-P-DEF+", 0);
            F("A-Z-HP%", 1f); N("A-Z-HP+", 0);
            F("A-Z-ATK%", 1f); N("A-Z-ATK+", 0);
            F("A-Z-DEF%", 1f); N("A-Z-DEF+", 0);

            foreach (var id in new[]
                     {
                         "P-GOD", "P-GOD-DIE", "P-DEF-REAL", "P-MOD-HP", "P-MOD-ATK",
                         "Z-GOD", "Z-DEF-BODY", "Z-DEF-APPLY", "Z-REAPPLY-RC",
                         "D-PROBE-PLANT", "D-PROBE-BULLET", "D-HOMING",
                         "F-WAVE-FREEZE", "G-TIMEFREEZE", "G-AUTOCOLLECT", "G-FREE-SET",
                         "H-ANYWHERE", "H-NOCD-CARD", "H-NOCD-GLOVE", "H-NOCD-HAMMER", "H-NOCD-WHEEL", "H-MOWER-INF",
                         "SYS-EMIT-PROOF", "SYS-DAMAGE-FX", "SYS-ELEMENT-FX",
                         "SYS-LIMHEALTH-GATE", "SYS-LIMHEALTH-OBSERVE",
                         "OVERLAY-COMBAT", "DEBUG-LEVEL-ENTRY", "LAWN-BASIC-ATTACK"
                     })
                T(id);

            F("D-DMG-%", 1f); N("D-DMG-SET", -1);
            N("D-TYPE-SWAP", -1);
            F("G-TIMESCALE", 1f);

            foreach (var id in new[] { "E-ZH", "E-ZD", "E-ZS", "E-ZC" })
                F(id, 1f);
            N("E-ZARM", 0);
            F("E-PMIN", 0.2f); F("E-PMAX", 6f);
            F("E-ZMIN", 0.1f); F("E-ZMAX", 10f);
            N("E-WAVE-I", 30); N("E-CONV-I", 6);

            N("P-HP", -1); N("P-MAXHP", -1); N("P-SHIELD", -1); N("P-ATK", -1);
            N("P-ATK-INT", -1); N("P-ATK-CD", -1); N("P-ATK-ADD", -1);
            N("P-PROD-INT", -1); N("P-PROD-CD", -1); N("P-SPEED", -1); N("P-MOVE", -1);
            N("P-LEVEL", -1); N("P-SHOOTLVL", -1); N("P-LIMDMG", -1);

            N("Z-HP", -1); N("Z-MAXHP", -1); N("Z-ARM1", -1); N("Z-ARM1MAX", -1);
            N("Z-ARM2", -1); N("Z-ARM2MAX", -1); N("Z-ATK", -1); N("Z-ARMOR-F", -1);
            N("Z-TAKEMULT", -1); N("Z-SPD-U", -1); N("Z-SPD", -1); N("Z-SPD-O", -1);
            N("Z-SLOW-FREEZE", -1); N("Z-SLOW-COLD", -1); N("Z-SLOW-BUTTER", -1);

            Get("SYS-EMIT-PROOF").Enabled = true;
            Get("SYS-DAMAGE-FX").Enabled = true;
            Get("SYS-ELEMENT-FX").Enabled = true;
            // T8: C1-C13 proved green on a real MelonLoader 3.9 lawn 2026-08-30 (docs/research/
            // effect-runtime/_prove-overlay-combat.json); promoted per spec-overlay-combat-enable.md
            // §7's own "only after the proof" rule.
            Get("OVERLAY-COMBAT").Enabled = true;
            // lawn-combat-wire T10/T12's shared kill switch — kept registered here as an EXPLICIT
            // debug/QA override surface only. 2026-09-14 correction: LawnBasicAttackFeature.Enabled no
            // longer trusts this class's own IsSet=false schema-fallback as its production default (see
            // that class's doc comment) — this Enabled=true seed is display/back-compat only.
            Get("LAWN-BASIC-ATTACK").Enabled = true;
            // Schema defaults are not user-set; Effective* applies display defaults when IsSet=false.
        }
        SyncLocalStatsFromEntries();
    }

    static void Put(string id, CheatEntry e)
    {
        if (!Entries.ContainsKey(id)) Entries[id] = e;
    }

    public static CheatEntry Get(string id)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(id, out var e))
            {
                e = new CheatEntry { Id = id, Kind = "toggle" };
                Entries[id] = e;
            }
            return e;
        }
    }

    public static long DocumentRevision;
    public static long AppliedRevision;

    public static bool On(string id)
    {
        var e = Get(id);
        return CheatSchema.EffectiveToggle(id, e.IsSet, e.Enabled);
    }

    public static float FVal(string id)
    {
        var e = Get(id);
        return (float)CheatSchema.EffectiveFloat(id, e.IsSet, e.FloatValue);
    }

    public static int IVal(string id)
    {
        var v = FVal(id);
        if (v >= int.MaxValue) return int.MaxValue;
        if (v <= int.MinValue) return int.MinValue;
        return (int)Math.Round(v);
    }

    /// <summary>
    /// E35 (spec-match-modify.md §2.3): the `long` channel this class had none of — <see cref="FVal"/>
    /// stores through `SetFloat` -&gt; `double` -&gt; read back as `float`, and `float` stops being
    /// integer-exact at 16,777,216 (docs/architecture/numeric-types.md's own overflow table, row 1), well inside what a cursed
    /// <c>zombieStartAmmor</c> can reach. <see cref="LVal"/> reads <see cref="CheatEntry.LongValue"/>
    /// directly — no float hop anywhere on this path. Used by <c>E-ZARM</c> only.
    /// </summary>
    public static long LVal(string id) => Get(id).LongValue;

    public static bool IsUserSet(string id)
    {
        lock (Gate) return Entries.TryGetValue(id, out var e) && e.IsSet;
    }

    public static void SetToggle(string id, bool on, string source = "web", bool emitInject = true)
    {
        var e = Get(id);
        e.Enabled = on;
        e.IsSet = true;
        if (id == "SYS-EMIT-PROOF") EmitProof = on;
        if (id.StartsWith("A-", StringComparison.Ordinal)) SyncLocalStatsFromEntries();
        if (emitInject) EmitInject(source, "toggle", id, enabled: on);
        if (id.StartsWith("A-", StringComparison.Ordinal))
            Stats.Invalidate();
        MaybeSave();
    }

    public static void SetFloat(string id, double v, string source = "web", bool emitInject = true, bool forceApplyMaster = true)
    {
        var e = Get(id);
        e.FloatValue = v;
        e.Enabled = true;
        e.IsSet = true;
        // Tab A scales only apply when A-APPLY is on — auto-enable so FE Set is not a no-op.
        if (forceApplyMaster
            && (id.StartsWith("A-P-", StringComparison.Ordinal) || id.StartsWith("A-Z-", StringComparison.Ordinal)))
            SetToggle("A-APPLY", true, source, emitInject: false);
        if (id.StartsWith("A-", StringComparison.Ordinal)) SyncLocalStatsFromEntries();
        if (id.StartsWith("E-", StringComparison.Ordinal)) BoardConfigLocked = true;
        if (emitInject) EmitInject(source, "set-float", id, value: v);
        if (id.StartsWith("A-", StringComparison.Ordinal) || id.StartsWith("P-", StringComparison.Ordinal) || id.StartsWith("Z-", StringComparison.Ordinal))
            Stats.Invalidate();
        MaybeSave();
    }

    /// <summary>
    /// E35 (spec-match-modify.md §2.3): the `long`-preserving sibling of <see cref="SetFloat"/> — used
    /// by <c>E-ZARM</c> (<c>zombieStartAmmor</c>) only, this kind's one true `long` magnitude. Mirrors
    /// <see cref="SetFloat"/>'s own shape (board-config lock, inject, save) but never touches
    /// <see cref="CheatEntry.FloatValue"/>, so nothing here can round-trip through a `float`.
    /// </summary>
    public static void SetLong(string id, long v, string source = "web", bool emitInject = true)
    {
        var e = Get(id);
        e.LongValue = v;
        e.Enabled = true;
        e.IsSet = true;
        if (id.StartsWith("E-", StringComparison.Ordinal)) BoardConfigLocked = true;
        // Telemetry only — the stored value stays the exact long in CheatEntry.LongValue regardless
        // of what this cast can represent in the injected debug payload.
        if (emitInject) EmitInject(source, "set-long", id, value: v);
        MaybeSave();
    }

    /// <summary>Pull StatsConfig without marking identity scales as user-set.</summary>
    static void PullScaleFloat(string id, double value, bool enabled)
    {
        if (!enabled || CheatSchema.IsUnsetOrIdentity(id, true, value))
        {
            // Leave unset — do not reify identity into Snapshot.
            lock (Gate)
            {
                if (Entries.TryGetValue(id, out var e))
                {
                    e.IsSet = false;
                    if (CheatSchema.TryGet(id, out var meta))
                    {
                        e.FloatValue = meta.DisplayDefault;
                        e.Enabled = meta.ToggleDefault;
                    }
                }
            }
            return;
        }
        SetFloat(id, value, "web", emitInject: false, forceApplyMaster: false);
    }

    public static void ClearField(string id, string source = "web")
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(id, out var e))
            {
                e.IsSet = false;
                if (CheatSchema.TryGet(id, out var meta))
                {
                    e.FloatValue = meta.DisplayDefault;
                    e.Enabled = meta.ToggleDefault;
                    e.Kind = meta.Kind;
                }
            }
        }
        if (id.StartsWith("A-", StringComparison.Ordinal)) SyncLocalStatsFromEntries();
        if (id.StartsWith("E-", StringComparison.Ordinal) && !HasAnySetWithPrefix("E-"))
            BoardConfigLocked = false;
        EmitInject(source, "clear-field", id);
        if (id.StartsWith("A-", StringComparison.Ordinal) || id.StartsWith("P-", StringComparison.Ordinal) || id.StartsWith("Z-", StringComparison.Ordinal))
            Stats.Invalidate();
        MaybeSave();
        Note("cleared " + id);
    }

    static bool HasAnySetWithPrefix(string prefix)
    {
        lock (Gate)
        {
            foreach (var e in Entries.Values)
            {
                if (e.IsSet && e.Id.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    public static void SetFloatQuiet(string id, double v)
    {
        var e = Get(id);
        e.FloatValue = v;
        e.Enabled = true;
        e.IsSet = true;
    }

    /// <summary>The `long`-preserving sibling of <see cref="SetFloatQuiet"/> — E35's
    /// <c>LoadBoardConfigIntoCheats</c> round-trips <c>E-ZARM</c> through this, not `SetFloatQuiet`,
    /// so the read-back-from-Unity direction never passes through a `float` either.</summary>
    public static void SetLongQuiet(string id, long v)
    {
        var e = Get(id);
        e.LongValue = v;
        e.Enabled = true;
        e.IsSet = true;
    }

    public static void BeginProbe(string probeId, string? packId = null)
    {
        ActiveProbeId = probeId;
        ActivePackId = packId;
        ActiveCorrelationId = Guid.NewGuid().ToString("N");
        ActiveProbeUtc = DateTime.UtcNow;
    }

    public static void EndProbe(string? reason = null)
    {
        var pid = ActiveProbeId;
        var pack = ActivePackId;
        ActiveProbeId = null;
        ActivePackId = null;
        ActiveCorrelationId = null;
        if (!string.IsNullOrEmpty(pid) && EmitProof && On("SYS-EMIT-PROOF"))
        {
            GameHooks.Emit("probe.end", new Dictionary<string, object>
            {
                ["probeId"] = pid,
                ["packId"] = pack ?? "",
                ["reason"] = reason ?? "end"
            });
        }
    }

    public static void RefreshProbeTimeout()
    {
        if (string.IsNullOrEmpty(ActiveProbeId)) return;
        if ((DateTime.UtcNow - ActiveProbeUtc).TotalMinutes > ProbeTimeoutMinutes)
            EndProbe("timeout");
    }

    public static void EmitInject(
        string source,
        string op,
        string? id = null,
        string? action = null,
        bool? enabled = null,
        double? value = null)
    {
        RefreshProbeTimeout();
        if (!(EmitProof && On("SYS-EMIT-PROOF"))) return;
        var corr = ActiveCorrelationId ?? Guid.NewGuid().ToString("N");
        if (string.IsNullOrEmpty(ActiveCorrelationId)) ActiveCorrelationId = corr;
        var payload = new Dictionary<string, object>
        {
            ["source"] = source,
            ["op"] = op,
            ["correlationId"] = corr
        };
        if (!string.IsNullOrEmpty(ActiveProbeId)) payload["probeId"] = ActiveProbeId!;
        if (!string.IsNullOrEmpty(ActivePackId)) payload["packId"] = ActivePackId!;
        if (!string.IsNullOrEmpty(id)) payload["id"] = id!;
        if (!string.IsNullOrEmpty(action)) payload["action"] = action!;
        if (enabled is { } en) payload["enabled"] = en;
        if (value is { } v) payload["value"] = v;
        GameHooks.Emit("cheat.inject", payload);
    }

    public static void EmitActionInject(string action, string source = "web")
    {
        EmitInject(source, "action", action: action);
    }

    /// <summary>Attach active probe ids onto outcome payloads.</summary>
    public static void TagProbe(Dictionary<string, object> payload)
    {
        RefreshProbeTimeout();
        if (string.IsNullOrEmpty(ActiveProbeId)) return;
        payload["probeId"] = ActiveProbeId!;
        if (!string.IsNullOrEmpty(ActiveCorrelationId))
            payload["correlationId"] = ActiveCorrelationId!;
        if (!string.IsNullOrEmpty(ActivePackId))
            payload["packId"] = ActivePackId!;
    }

    public static void SyncLocalStatsFromEntries()
    {
        LocalStatsOverride = true;
        LocalStats.ApplyStats = On("A-APPLY");
        // Unset scales use identity (1 / 0) — never treat missing as 0%.
        LocalStats.Plants.HpPercent = ScalePct("A-P-HP%");
        LocalStats.Plants.HpFlat = ScaleFlat("A-P-HP+");
        LocalStats.Plants.AttackPercent = ScalePct("A-P-ATK%");
        LocalStats.Plants.AttackFlat = ScaleFlat("A-P-ATK+");
        LocalStats.Plants.DefensePercent = ScalePct("A-P-DEF%");
        LocalStats.Plants.DefenseFlat = ScaleFlat("A-P-DEF+");
        LocalStats.Zombies.HpPercent = ScalePct("A-Z-HP%");
        LocalStats.Zombies.HpFlat = ScaleFlat("A-Z-HP+");
        LocalStats.Zombies.AttackPercent = ScalePct("A-Z-ATK%");
        LocalStats.Zombies.AttackFlat = ScaleFlat("A-Z-ATK+");
        LocalStats.Zombies.DefensePercent = ScalePct("A-Z-DEF%");
        LocalStats.Zombies.DefenseFlat = ScaleFlat("A-Z-DEF+");
    }

    static float ScalePct(string id) => IsUserSet(id) ? FVal(id) : 1f;
    static int ScaleFlat(string id) => IsUserSet(id) ? IVal(id) : 0;

    /// <summary>True when Tab A has at least one non-identity user-set scale for plants.</summary>
    public static bool HasPlantScaleMods()
    {
        if (!On("A-APPLY")) return false;
        return NonIdentityScale("A-P-HP%", "A-P-HP+")
               || NonIdentityScale("A-P-ATK%", "A-P-ATK+")
               || NonIdentityScale("A-P-DEF%", "A-P-DEF+");
    }

    public static bool HasZombieScaleMods()
    {
        if (!On("A-APPLY")) return false;
        return NonIdentityScale("A-Z-HP%", "A-Z-HP+")
               || NonIdentityScale("A-Z-ATK%", "A-Z-ATK+")
               || NonIdentityScale("A-Z-DEF%", "A-Z-DEF+");
    }

    static bool NonIdentityScale(string pctId, string flatId)
    {
        if (IsUserSet(pctId) && Math.Abs(FVal(pctId) - 1f) > 0.0001f) return true;
        if (IsUserSet(flatId) && IVal(flatId) != 0) return true;
        return false;
    }

    public static bool HasPlantExtrasSet()
    {
        foreach (var id in new[]
                 {
                     "P-SHIELD", "P-ATK-INT", "P-ATK-CD", "P-ATK-ADD", "P-PROD-INT", "P-PROD-CD",
                     "P-SPEED", "P-MOVE", "P-LEVEL", "P-SHOOTLVL", "P-MOD-HP", "P-MOD-ATK"
                 })
        {
            if (IsUserSet(id)) return true;
        }
        return false;
    }

    public static bool HasZombieExtrasSet()
    {
        foreach (var id in new[]
                 {
                     "Z-ARMOR-F", "Z-TAKEMULT", "Z-SPD-U", "Z-SPD", "Z-SPD-O",
                     "Z-SLOW-FREEZE", "Z-SLOW-COLD", "Z-SLOW-BUTTER"
                 })
        {
            if (IsUserSet(id)) return true;
        }
        return false;
    }

    /// <summary>Tab B plant overrides — only user-set positive values.</summary>
    public static Dictionary<string, int> BuildPlantAbsolute()
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        void Put(string channel, string id)
        {
            if (!IsUserSet(id)) return;
            var v = IVal(id);
            if (v > 0) d[channel] = v;
        }
        Put(StatChannels.Hp, "P-HP");
        Put(StatChannels.MaxHp, "P-MAXHP");
        Put(StatChannels.Atk, "P-ATK");
        return d;
    }

    /// <summary>
    /// The real-valued absolutes (E16): fire rate, sun rate, creep speed. E38 (spec-entity-fields-
    /// 12plus.md) adds eight more plant keys the same way — "E16 run a second time".
    ///
    /// <para>These used to be written straight to the Unity field from the extras path, bypassing
    /// the modifier bag — which is why no effect could ever reach them, and why "shoots faster" (and
    /// then "takes +X% damage", E38's own headline case on the zombie side) was unauthorable. They
    /// are Overrides now, the same shape <c>P-HP</c> and <c>P-ATK</c> have always had, so the
    /// operator surface is unchanged and there is one path to the field.</para>
    ///
    /// <para>Separate from <see cref="BuildPlantAbsolute"/> because these are fractions and that map
    /// is <c>int</c>: an attack interval of 1.5 seconds would truncate to 1.</para>
    ///
    /// <para><b>E38's three guard shapes (§2b), each preserved exactly:</b> P-SHIELD/P-ATK-CD/
    /// P-PROD-CD/P-LEVEL/P-SHOOTLVL accept a legal zero (<c>&gt;= 0</c> — the same class of key
    /// <see cref="BuildPlantAbsolute"/>'s own <c>&gt; 0</c> filter would have silently broken, per
    /// that method's own warning); P-SPEED/P-MOVE keep refusing one (<c>&gt; 0</c> — a zero speed
    /// freezes the plant, a structural floor, not a balance choice); P-ATK-ADD carries no value
    /// guard at all (an attack-speed adder is a signed delta by construction, so a negative value is
    /// ordinary content — <see cref="Stats.Plugins.CheatAbsoluteStatPlugin"/> no longer re-filters
    /// this map by sign for exactly this reason).</para>
    /// </summary>
    public static Dictionary<string, double> BuildPlantAbsoluteReal()
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);

        // >0: a zero interval/speed is refused today and the promotion must not start accepting it.
        void Put(string channel, string id)
        {
            if (!IsUserSet(id)) return;
            var v = FVal(id);
            if (v > 0) d[channel] = v;
        }

        // >=0: a zero is legal and must survive (P-SHIELD "no shield", P-ATK-CD "ready now",
        // Z-TAKEMULT "immune", …) — the exact shape BuildPlantAbsolute's own int map would drop.
        void PutGe0(string channel, string id, Func<string, double> read)
        {
            if (!IsUserSet(id)) return;
            var v = read(id);
            if (v >= 0) d[channel] = v;
        }

        Put(StatChannels.AttackInterval, "P-ATK-INT");
        Put(StatChannels.ProduceInterval, "P-PROD-INT");

        PutGe0(StatChannels.PlantShield, "P-SHIELD", id => IVal(id));
        PutGe0(StatChannels.AttackCountdown, "P-ATK-CD", id => FVal(id));
        PutGe0(StatChannels.ProduceCountdown, "P-PROD-CD", id => FVal(id));
        PutGe0(StatChannels.PlantLevel, "P-LEVEL", id => IVal(id));
        PutGe0(StatChannels.ShootingLevel, "P-SHOOTLVL", id => IVal(id));

        Put(StatChannels.PlantSpeed, "P-SPEED");
        Put(StatChannels.PlantMoveSpeed, "P-MOVE");

        // No guard at all (⛔ DECIDED 2026-09-03) — see this method's own doc comment. Do not add
        // one; EntityFields12PlusGuardTests.P_ATK_ADD_stays_unguarded pins the absence.
        if (IsUserSet("P-ATK-ADD"))
            d[StatChannels.AttackSpeedAdder] = FVal("P-ATK-ADD");

        return d;
    }

    public static Dictionary<string, double> BuildZombieAbsoluteReal()
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        if (IsUserSet("Z-SPD-U") && FVal("Z-SPD-U") > 0)
            d[StatChannels.ZombieSpeed] = FVal("Z-SPD-U");

        // E38: same two guard shapes as BuildPlantAbsoluteReal's own note — Z-ARMOR-F/Z-TAKEMULT
        // accept a legal zero, Z-SPD/Z-SPD-O keep refusing one (a zero speed freezes the zombie).
        if (IsUserSet("Z-ARMOR-F") && FVal("Z-ARMOR-F") >= 0)
            d[StatChannels.ArmorFlat] = FVal("Z-ARMOR-F");
        if (IsUserSet("Z-TAKEMULT") && FVal("Z-TAKEMULT") >= 0)
            d[StatChannels.TakeDmgMultiplier] = FVal("Z-TAKEMULT");
        if (IsUserSet("Z-SPD") && FVal("Z-SPD") > 0)
            d[StatChannels.ZombieSpeedCurrent] = FVal("Z-SPD");
        if (IsUserSet("Z-SPD-O") && FVal("Z-SPD-O") > 0)
            d[StatChannels.ZombieOriginSpeed] = FVal("Z-SPD-O");

        return d;
    }

    public static Dictionary<string, int> BuildZombieAbsolute()
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        void Put(string channel, string id)
        {
            if (!IsUserSet(id)) return;
            var v = IVal(id);
            if (v > 0) d[channel] = v;
        }
        Put(StatChannels.Hp, "Z-HP");
        Put(StatChannels.MaxHp, "Z-MAXHP");
        Put(StatChannels.Atk, "Z-ATK");
        Put(StatChannels.Arm1, "Z-ARM1");
        Put(StatChannels.Arm1Max, "Z-ARM1MAX");
        Put(StatChannels.Arm2, "Z-ARM2");
        Put(StatChannels.Arm2Max, "Z-ARM2MAX");
        return d;
    }

    public static void PullFromServer(StatsConfig s)
    {
        LocalStats.LogDamage = s.LogDamage;
        SetToggle("A-APPLY", s.ApplyStats, "web", emitInject: false);
        PullScaleFloat("A-P-HP%", s.Plants.HpPercent, s.ApplyStats);
        PullScaleFloat("A-P-HP+", s.Plants.HpFlat, s.ApplyStats);
        PullScaleFloat("A-P-ATK%", s.Plants.AttackPercent, s.ApplyStats);
        PullScaleFloat("A-P-ATK+", s.Plants.AttackFlat, s.ApplyStats);
        PullScaleFloat("A-P-DEF%", s.Plants.DefensePercent, s.ApplyStats);
        PullScaleFloat("A-P-DEF+", s.Plants.DefenseFlat, s.ApplyStats);
        PullScaleFloat("A-Z-HP%", s.Zombies.HpPercent, s.ApplyStats);
        PullScaleFloat("A-Z-HP+", s.Zombies.HpFlat, s.ApplyStats);
        PullScaleFloat("A-Z-ATK%", s.Zombies.AttackPercent, s.ApplyStats);
        PullScaleFloat("A-Z-ATK+", s.Zombies.AttackFlat, s.ApplyStats);
        PullScaleFloat("A-Z-DEF%", s.Zombies.DefensePercent, s.ApplyStats);
        PullScaleFloat("A-Z-DEF+", s.Zombies.DefenseFlat, s.ApplyStats);
        SyncLocalStatsFromEntries();
        Stats.Invalidate();
        Note("pulled stats from server");
        EmitInject("web", "action", action: "pull-stats");
    }

    public static void ApplyPvzStatsModifiers(long playerId, long revision, IEnumerable<PvzStatModifierDto> modifiers)
    {
        var list = new List<StatModifier>();
        foreach (var m in modifiers)
        {
            if (m == null || !m.Enabled) continue;
            if (string.IsNullOrWhiteSpace(m.Channel)) continue;
            list.Add(PvzStatsSheetComposer.ToStatModifier(
                m.PluginId, m.SourceKind, m.SourceId, m.Channel, m.Op, m.Value, m.Priority));
        }
        PvzStatsMods = list;
        PvzStatsPlayerId = playerId;
        PvzStatsRevision = revision;
        Stats.Invalidate();
        Note($"pvz.stats loaded player={playerId} rev={revision} n={list.Count}");
    }

    public static void RegisterSpawnSourceTag(IntPtr ptr, string source)
    {
        if (ptr == IntPtr.Zero || string.IsNullOrWhiteSpace(source)) return;
        SpawnSourceByPtr[ptr] = source;
    }

    public static string? ConsumeSpawnSourceTag(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero && SpawnSourceByPtr.TryRemove(ptr, out var tagged))
            return tagged;
        var pending = PendingSpawnSourceTag;
        PendingSpawnSourceTag = null;
        return string.IsNullOrWhiteSpace(pending) ? null : pending;
    }

    public static void ClearPendingSpawnSourceTag() => PendingSpawnSourceTag = null;

    public static bool HasPvzStatsMods() => PvzStatsMods.Count > 0;

    /// <summary>True when dirty reapply should run (cheat doc, PvzStats revision, or Tab A scales).</summary>
    public static bool ShouldPushScalesOnDirty() =>
        PvzStatsApplyGate.ShouldPushOnDirty(
            DocumentRevision,
            AppliedRevision,
            PvzStatsRevision,
            AppliedPvzStatsRevision,
            HasPlantScaleMods(),
            HasZombieScaleMods());

    public static void MarkAppliedRevision() => AppliedRevision = DocumentRevision;

    public static void MarkAppliedPvzStatsRevision() => AppliedPvzStatsRevision = PvzStatsRevision;

    static void CopyMod(StatMod from, StatMod to)
    {
        to.HpPercent = from.HpPercent; to.HpFlat = from.HpFlat;
        to.AttackPercent = from.AttackPercent; to.AttackFlat = from.AttackFlat;
        to.DefensePercent = from.DefensePercent; to.DefenseFlat = from.DefenseFlat;
    }

    public static StatsConfig EffectiveStats()
    {
        // Feeds cheat.scale plugin input only — apply path uses StatSystem.Resolve.
        SyncLocalStatsFromEntries();
        return LocalStats;
    }

    public static void ResetAll()
    {
        lock (Gate) Entries.Clear();
        EnsureDefaults();
        LocalStatsOverride = false;
        BoardConfigLocked = false;
        DocumentRevision++;
        AppliedRevision = DocumentRevision;
        Note("reset all cheats");
        MaybeSave();
    }

    public static void ResetGroup(string prefix)
    {
        lock (Gate)
        {
            foreach (var key in Entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                Entries.Remove(key);
        }
        EnsureDefaults();
        if (prefix.StartsWith("E-", StringComparison.Ordinal) || prefix == "E-")
            BoardConfigLocked = false;
        else if (!HasAnySetWithPrefix("E-"))
            BoardConfigLocked = false;
        DocumentRevision++;
        AppliedRevision = DocumentRevision;
        Note("reset group " + prefix);
        MaybeSave();
    }

    public static void Select(IntPtr ptr, string side)
    {
        SelectedPtr = ptr;
        SelectedSide = side;
    }

    public static void ClearSelection()
    {
        SelectedPtr = IntPtr.Zero;
        SelectedSide = "";
    }

    public static void Note(string msg)
    {
        LastNote = msg;
        try { RpgHost.Log.Info("[cheat] " + msg); } catch { }
        if (EmitProof && On("SYS-EMIT-PROOF"))
        {
            var payload = new Dictionary<string, object> { ["note"] = msg };
            TagProbe(payload);
            GameHooks.Emit("cheat.apply", payload);
        }
    }

    public static void Error(string msg)
    {
        LastError = msg;
        RpgHost.Log.Warning("[cheat] " + msg);
        Note("ERR " + msg);
    }

    public static Dictionary<string, object> Snapshot()
    {
        lock (Gate)
        {
            // SSOT: only user-set entries (absence = unset). Schema holds display defaults.
            var setEntries = Entries.Values.Where(e => e.IsSet).Select(e => new Dictionary<string, object>
            {
                ["id"] = e.Id,
                ["kind"] = e.Kind,
                ["enabled"] = e.Enabled,
                ["floatValue"] = e.FloatValue,
                ["isSet"] = true
            }).ToList();
            return new Dictionary<string, object>
            {
                ["menuEnabled"] = false,
                ["revision"] = DocumentRevision,
                ["persist"] = PersistEnabled,
                ["emitProof"] = EmitProof,
                ["localOverride"] = LocalStatsOverride,
                ["boardConfigLocked"] = BoardConfigLocked,
                ["selectedPtr"] = SelectedPtr.ToString("X"),
                ["selectedSide"] = SelectedSide,
                ["spawnCol"] = SpawnCol,
                ["spawnRow"] = SpawnRow,
                ["catalogPlants"] = SpawnCatalog.PlantCount,
                ["catalogZombies"] = SpawnCatalog.ZombieCount,
                ["note"] = LastNote,
                ["activeProbeId"] = ActiveProbeId ?? "",
                ["activePackId"] = ActivePackId ?? "",
                ["entries"] = setEntries
            };
        }
    }

    public static void ApplySnapshot(JsonElement root)
    {
        if (root.TryGetProperty("persist", out var p)) PersistEnabled = p.GetBoolean();
        if (root.TryGetProperty("emitProof", out var ep)) EmitProof = ep.GetBoolean();
        MenuEnabled = false;
        MenuOpen = false;
        if (root.TryGetProperty("revision", out var rev) && rev.TryGetInt64(out var r))
            DocumentRevision = r;

        // Replace set flags: snapshot entries are the only IsSet=true values.
        lock (Gate)
        {
            foreach (var e in Entries.Values)
                e.IsSet = false;
        }

        if (root.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString() ?? "";
                if (string.IsNullOrEmpty(id)) continue;
                var enabled = item.TryGetProperty("enabled", out var en) && en.GetBoolean();
                var fv = item.TryGetProperty("floatValue", out var fvel) && fvel.TryGetDouble(out var d) ? d : 0d;
                if (CheatSchema.ShouldStripFromDocument(id, enabled, fv,
                        item.TryGetProperty("kind", out var k) ? k.GetString() : null))
                    continue;
                var e = Get(id);
                e.Enabled = enabled;
                e.FloatValue = fv;
                e.IsSet = true;
                if (item.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String)
                    e.Kind = kind.GetString() ?? e.Kind;
            }
        }
        SyncLocalStatsFromEntries();
        LocalStatsOverride = true;
        if (root.TryGetProperty("boardConfigLocked", out var bcl))
            BoardConfigLocked = bcl.GetBoolean();
        else
            BoardConfigLocked = HasAnySetWithPrefix("E-");
        AppliedRevision = DocumentRevision;
        Stats.Invalidate();
    }

    public static void MaybeSave()
    {
        if (!PersistEnabled || string.IsNullOrEmpty(_persistPath)) return;
        try
        {
            var json = JsonSerializer.Serialize(Snapshot());
            File.WriteAllText(_persistPath!, json);
        }
        catch (Exception ex) { RpgHost.Log.Warning("cheat save: " + ex.Message); }
    }

    public static void TryLoad()
    {
        if (string.IsNullOrEmpty(_persistPath) || !File.Exists(_persistPath)) return;
        try
        {
            var json = File.ReadAllText(_persistPath!);
            using var doc = JsonDocument.Parse(json);
            ApplySnapshot(doc.RootElement);
            Note("loaded cheat-state.json");
        }
        catch (Exception ex) { RpgHost.Log.Warning("cheat load: " + ex.Message); }
    }
}

public sealed class CheatEntry
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "toggle";
    public bool Enabled { get; set; }
    public double FloatValue { get; set; }
    /// <summary>
    /// E35 (spec-match-modify.md §2.3): the `long` channel — separate from <see cref="FloatValue"/> on
    /// purpose, so a value stored here never passes through a `float`. Used by <c>E-ZARM</c> only.
    /// </summary>
    public long LongValue { get; set; }
    /// <summary>False = unset (absent for apply). True = user/web explicitly set.</summary>
    public bool IsSet { get; set; }
}
