using FusionRpg.Contracts;
using FusionRpg.Core.Match;
using FusionRpg.Core.Commanders;
using FusionRpg.Injector.Commanders;
using FusionRpg.Injector.Host;

namespace FusionRpg.Injector.Match;

/// <summary>
/// Process-singleton MatchRuntime for LIVE Injector (W2 wire + W3 pause + W5 bind). Main-thread only.
/// </summary>
public static class MatchHost
{
    static readonly object Gate = new();
    static MatchRuntime _runtime = new();
    /// <summary>zomboss-deploy-ai T3.2/T3.4 — the one board-level field <see cref="MatchSnapshot"/>
    /// never carries; see the `isStart`/`board.economy` handling in <see cref="Apply"/> for why.</summary>
    static int _currentWave;

    public static MatchRuntime Runtime
    {
        get { lock (Gate) return _runtime; }
    }

    /// <summary>species-progression `species-layer-delivery` step 6.2, trigger 6 (SP6.6) — a run is
    /// "open" from the perspective of `decisions.md`'s "Mid-match switch: open run keeps the player
    /// it started with" rule.</summary>
    public static bool IsRunOpen
    {
        get { lock (Gate) return _runtime.Phase is not MatchPhase.Idle; }
    }

    /// <summary>Set by `RpgClient`'s `AptitudesUpdated(scope: "save")` handler when a save switch
    /// notice arrives WHILE a run is open — the refresh is deferred to the NEXT `board.start` rather
    /// than applied mid-run, matching `decisions.md`'s own rule. Never set for any other scope.</summary>
    static bool _pendingSaveSwitchRefresh;

    /// <summary>Called from `RpgClient` when a save-switch notice arrives mid-run. A no-op (and
    /// harmless) if called with no run open — the caller applies the refresh immediately in that
    /// case instead of deferring, so this should only ever be reached while <see cref="IsRunOpen"/>
    /// was true at the time of the check.</summary>
    public static void RequestDeferredSaveSwitchRefresh()
    {
        lock (Gate) _pendingSaveSwitchRefresh = true;
    }

    /// <summary>Replace runtime (tests / reconnect). Prefer Reset for match edges.</summary>
    public static void ResetRuntime()
    {
        lock (Gate) _runtime = new MatchRuntime();
    }

    public static bool TryAdmitSpawn(string? side, out GateResult result)
    {
        lock (Gate)
            return _runtime.TryAdmitSpawn(side, out result);
    }

    /// <summary>
    /// Unique deploy Intent accepted → PendingSpawn (W5-A). Optional loadout JSON for Bound apply.
    /// </summary>
    public static bool TryBeginUniquePending(
        string instanceId,
        string correlationId,
        string side,
        int typeId,
        string? loadoutJson = null)
    {
        try
        {
            lock (Gate)
                return _runtime.TryBeginPending(instanceId, correlationId, side, typeId, loadoutJson);
        }
        catch (Exception ex)
        {
            try { CheatState.Error("MatchHost.TryBeginUniquePending: " + ex.Message); } catch { }
            return false;
        }
    }

    /// <summary>FailDeploy / timeout / spawn-null — GC PendingSpawn (W5-D).</summary>
    public static bool TryClearUniquePending(string? instanceId = null, string? correlationId = null)
    {
        try
        {
            lock (Gate)
                return _runtime.TryClearPending(instanceId, correlationId);
        }
        catch (Exception ex)
        {
            try { CheatState.Error("MatchHost.TryClearUniquePending: " + ex.Message); } catch { }
            return false;
        }
    }

    /// <summary>
    /// Mirror pause UI into MatchPhase (W3-A). Observe-only Emit match.pause / match.resume on transition.
    /// Never throws to caller.
    /// </summary>
    public static void NotifyPaused(bool paused)
    {
        try
        {
            MatchPhase before;
            MatchPhase after;
            lock (Gate)
            {
                before = _runtime.Phase;
                _runtime.NotifyPaused(paused);
                after = _runtime.Phase;
            }

            if (before == after) return;

            try
            {
                GameHooks.Emit(
                    paused ? "match.pause" : "match.resume",
                    new Dictionary<string, object>());
            }
            catch { }
        }
        catch (Exception ex)
        {
            try { CheatState.Error("MatchHost.NotifyPaused: " + ex.Message); } catch { }
        }
    }

    /// <summary>
    /// Fold capture into MatchRuntime. On board.start/end/match.result, ClearAll effects.
    /// On Bound unique, apply ptr-only loadout. On end, clear GameHooks.MatchKey.
    /// Never throws to caller.
    /// </summary>
    public static void Apply(string kind, IReadOnlyDictionary<string, object>? payload)
    {
        try
        {
            lock (Gate)
            {
                var isStart = string.Equals(kind, "board.start", StringComparison.OrdinalIgnoreCase);
                var isEnd = IsMatchEnd(kind);
                string? endingKey = isEnd ? _runtime.MatchKey : null;

                // LIVE BoardAwake may fire without a prior board.end — align with sim overlay.
                if (isStart && _runtime.Phase is not MatchPhase.Idle)
                {
                    var priorKey = _runtime.MatchKey;
                    ClearCommanderSnapshot();
                    _runtime.Apply("board.end");
                    TryEffect("KernelEndBoard", Effects.KernelDriveHost.EndBoard);
                    TryEffect("NotifyMatchEnd", () => Effects.EffectRuntime.NotifyMatchEnd(priorKey));
                    TryEffect("ClearAll", () => Effects.EffectRuntime.ClearAll("match"));
                    GameHooks.MatchKey = null;
                }

                if (isStart)
                {
                    TryEffect("ClearAll", () => Effects.EffectRuntime.ClearAll("match"));
                }

                _runtime.Apply(kind, payload);

                // lawn-combat-wire T10 (spec-basic-attack-grant.md): "bind on the spawn edge... keyed
                // on the board fold" — right after the board-fold Apply above, for every plant/zombie
                // spawn, general creature included (no branch here excludes one for lacking a
                // binding). Recorded, not bound synchronously: LawnBasicAttackGrantBinder.Tick drains
                // once per frame so a mass-spawn wave pays for one board resolve, not N.
                if (string.Equals(kind, "plant.spawn", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kind, "zombie.spawn", StringComparison.OrdinalIgnoreCase))
                {
                    var spawnPtr = payload != null && payload.TryGetValue("ptr", out var ptrObj)
                        ? ptrObj?.ToString()
                        : null;
                    TryEffect("LawnBasicAttackGrantQueue",
                        () => Effects.LawnBasicAttackGrantBinder.QueueSpawn(spawnPtr));
                }

                var bound = _runtime.ConsumeLastBound();
                if (bound != null)
                {
                    try { UniqueBoundLoadout.TryApply(bound); } catch { }
                    // aptitude-sheet AS-1.1b (unique-lawn-wire fix, DESIGN-GATE.md §2.16 3rd instance):
                    // the bind edge is the one cadence trigger AS-1.1's original 3-trigger set missed --
                    // it is the edge where RefreshUniqueAptitudesAsync's own Bound-instanceId KEY SET
                    // moves. Without this, `allocate -> deploy` (allocate before this specimen was ever
                    // Bound) leaves the cache never keyed for it, so its shares never load -- confirmed
                    // live 2026-09-13. Fire-and-forget, off the hot path (async HTTP); TriggerBoundAptitudeRefresh
                    // coalesces concurrent binds into at most one extra round trip rather than one per bind.
                    try { RpgHost.Client?.TriggerBoundAptitudeRefresh(); } catch { }
                }

                if (isEnd)
                {
                    TryEffect("NotifyMatchEnd", () => Effects.EffectRuntime.NotifyMatchEnd(endingKey));
                    TryEffect("ClearAll", () => Effects.EffectRuntime.ClearAll("match"));
                    ClearCommanderSnapshot();
                    // T13: one kernel per board — drop it here so a stale queue can never survive
                    // into the next match.
                    TryEffect("KernelEndBoard", Effects.KernelDriveHost.EndBoard);
                }

                if (isStart)
                {
                    try { _runtime.ConfigureCaps(SpawnAdmit.Config); } catch { }
                    try
                    {
                        var key = _runtime.MatchKey;
                        if (!string.IsNullOrWhiteSpace(key))
                            GameHooks.MatchKey = key;
                    }
                    catch { }
                    var snapshot = MatchCommanderSnapshotSource.BuildFromSessionCache();
                    if (MatchCommanderSnapshotSource.LastBuildUsedFallback)
                    {
                        try { RpgHost.Log.Warning("commander snapshot: cache miss — implicit Dave"); } catch { }
                    }
                    MatchCommanderSnapshotHolder.BeginMatch(snapshot);
                    CheatState.RefreshCommanderAllocationCache();
                    // species-progression step 6.2, trigger 6 (SP6.6): the deferred save-switch
                    // refresh applies HERE, at board.start, never mid-run (decisions.md "Mid-match
                    // switch: open run keeps the player it started with"). Consumed exactly once --
                    // a save switch that never happened leaves this false and costs nothing.
                    if (_pendingSaveSwitchRefresh)
                    {
                        _pendingSaveSwitchRefresh = false;
                        try { CheatCommandRunner.Enqueue(new CommandDto { Name = "aptitudes.allocation.reload" }); } catch { }
                    }
                    // creature-lawn-deploy T2.1: same Hot/Cold fix, same board.start moment — a
                    // Cold-plane roster/patron read frozen once, never re-queried mid-match.
                    var lawnRoster = LawnDeployRosterSessionCache.BuildFromSessionCache();
                    if (LawnDeployRosterSessionCache.LastBuildWasCacheMiss)
                    {
                        try { RpgHost.Log.Warning("lawn deploy roster: cache miss — empty roster frozen for this match"); } catch { }
                    }
                    LawnDeployRosterSnapshotHolder.BeginMatch(lawnRoster);
                    // creature-lawn-deploy T2.4: a fresh per-run "already fired" tracker for the trigger
                    // evaluator, same board.start moment as everything else above.
                    LawnDeployEventRunStateHolder.BeginMatch();
                    // zomboss-deploy-ai T3.4: same board.start moment, Zomboss's own sibling tracker.
                    FusionRpg.Core.Match.Ai.ZombossDeployRunStateHolder.BeginMatch();
                    // combat-ai lawn-cast-trigger (module 19, CAI4.8): the frame slot's START edge. Until
                    // this call existed `LawnDecisionHost.BeginMatch` had NO production caller -- only
                    // tests called it -- so `Tick` (every frame, from `InjectorLoop`) returned at its own
                    // null guard and the slot never scheduled anything, while its death and board-end
                    // drops in `InjectorEntityRegistry` already ran. The seed is `MatchSeed.For` of the
                    // match key: the same pure function the Server uses when replaying (spec D5), not a
                    // second derivation.
                    try
                    {
                        Effects.LawnDecisionHost.BeginMatch(
                            FusionRpg.Core.Effects.Atoms.MatchSeed.For(_runtime.MatchKey));
                    }
                    catch (Exception ex)
                    {
                        try { CheatState.Error("lawn.ai.begin: " + ex.Message); } catch { }
                    }
                    _currentWave = 0;
                    // T13: a fresh timeline per board, alongside the commander snapshot that is
                    // already frozen at exactly this moment.
                    TryEffect("KernelBeginBoard", Effects.KernelDriveHost.BeginBoard);
                    TryEffect("NotifyMatchStart", () => Effects.EffectRuntime.NotifyMatchStart(_runtime.MatchKey ?? ""));
                }
                else if (isEnd)
                {
                    GameHooks.MatchKey = null;
                }

                // zomboss-deploy-ai T3.2: the one board-level field MatchSnapshot itself never carries
                // (confirmed by direct read — GameDumps.BoardStats's own separate "board.snapshot" dump
                // owns wave, never MatchSnapshot). `board.economy`'s own payload already carries it on
                // every wave change; cached here so CheckZombossDeployTrigger has a real number to pass
                // ZombossDeployRoster.AvailableSpeciesFor without a second live query.
                if (string.Equals(kind, "board.economy", StringComparison.OrdinalIgnoreCase) && payload != null
                    && payload.TryGetValue("wave", out var waveObj))
                {
                    try { _currentWave = Convert.ToInt32(waveObj); } catch { }
                }

                // creature-lawn-deploy T2.4: checked after every event while a match is actually live —
                // the evaluator's own gates (empty roster, per-run budget, per-case "already fired",
                // the condition itself) make this cheap and safe to call this often; PlantCount/
                // ZombieCount only ever change on a spawn/die event, so this is exactly "off
                // MatchRuntime's own event stream" per the spec's own Assumption 2.
                if (_runtime.Phase == MatchPhase.InMatch)
                {
                    CheckLawnDeployTrigger();
                    CheckZombossDeployTrigger();
                }
            }
        }
        catch (Exception ex)
        {
            try { CheatState.Error("MatchHost.Apply: " + ex.Message); } catch { }
        }
    }

    /// <summary>Must be called from inside the Gate lock — reads `_runtime` directly, matching every
    /// other Phase-1 helper in this method. Never throws to the caller.</summary>
    static void CheckLawnDeployTrigger()
    {
        try
        {
            var roster = LawnDeployRosterSnapshotHolder.ResolveOrEmpty();
            if (roster.Eligible.Count == 0) return;
            if (!LawnDeployEventsTuningHub.IsConfigured) return;

            var matchKey = _runtime.MatchKey;
            if (string.IsNullOrEmpty(matchKey)) return;

            // No new numeric match seed exists anywhere in this runtime (MatchRuntime/MatchState never
            // tracked one) -- derived from the one per-match identifier that already does exist, reusing
            // SeededRng's own hash rather than inventing a new one (WorldSeed.cs's own "ONE place this
            // is computed" discipline, adapted: DeriveStream already turns an arbitrary label into a
            // reproducible stream; a fixed base seed of 0 makes THIS derivation depend only on matchKey).
            var seed = FusionRpg.Core.Battle.SeededRng.DeriveStream(0, matchKey).NextULong();

            var result = LawnDeployEventEvaluator.Evaluate(
                _runtime.ToSnapshot(), roster, LawnDeployEventsTuningHub.Tuning,
                LawnDeployEventRunStateHolder.Current, seed);
            if (!result.Fires) return;

            LawnDeployEventRunStateHolder.RecordFired(result.CaseId!);
            GameHooks.Emit("lawn-deploy-event.fired", new Dictionary<string, object>
            {
                ["caseId"] = result.CaseId!,
                ["eligibleInstanceIds"] = roster.Eligible.Select(e => e.InstanceId).ToArray(),
            });
        }
        catch (Exception ex)
        {
            try { CheatState.Error("MatchHost.CheckLawnDeployTrigger: " + ex.Message); } catch { }
        }
    }

    /// <summary>Must be called from inside the Gate lock, same discipline as
    /// <see cref="CheckLawnDeployTrigger"/>. Never throws to the caller, never awaits HTTP itself —
    /// the actual mint+deploy is a fire-and-forget call kicked off outside any lock
    /// (<see cref="RpgClient.EnqueueZombossDeploy"/>).</summary>
    static void CheckZombossDeployTrigger()
    {
        try
        {
            if (!FusionRpg.Core.Match.Ai.ZombossDeployTuningHub.IsConfigured) return;
            if (FusionRpg.Core.Match.Ai.ZombossDeployRunStateHolder.AlreadyFired) return;

            var matchKey = _runtime.MatchKey;
            if (string.IsNullOrEmpty(matchKey)) return;

            if (CheatState.CurrentPlayerId <= 0) return; // no real player id cached yet — decline, never guess

            // save-identity SE4.28 (D4 fix): Zomboss's own units are found BY EmpireId.Zomboss directly,
            // read straight off CheatState's registered ptr->empire map — never by eliminating "any
            // registered owner that is NOT this player" (the old rule, correct only because exactly two
            // parties ever existed; a third empire's specimen would have been wrongly counted as
            // Zomboss's own by that elimination). Unregistered (raw vanilla PvZ) units fall back to
            // their own mechanical side -- a vanilla zombie IS Zomboss's own army, a vanilla plant is
            // the player's.
            var snapshot = _runtime.ToSnapshot();
            var units = new List<FusionRpg.Core.Match.Ai.ILawnUnitView>(snapshot.Entities.Length);
            foreach (var e in snapshot.Entities)
            {
                // A bullet is a transient projectile, never a unit either side's own counts should
                // include -- skipped entirely, not resolved to any relation.
                if (e.Side == BoardSide.Bullet) continue;

                var empire = CheatState.TryGetSpecimenEmpire(e.Ptr);
                if (empire is null && e.Side == BoardSide.Zombie)
                    // An unregistered vanilla zombie is the ambient horde, not one of Zomboss's OWN
                    // reinforcements -- ZombossScorerTuning.MaxConcurrentOwnUnits caps how many
                    // Zomboss-DEPLOYED uniques are out at once, never the whole lane's zombie count
                    // (a real gap self-caught before this task's own live check: with the horde
                    // included, any normal wave would already sit at or past a starting cap like 2,
                    // and Zomboss would structurally never fire). Excluded, not counted as Ally.
                    continue;

                // Only reachable for an unregistered PLANT now (the zombie case is skipped above) or a
                // registered specimen of ANY empire -- a vanilla plant, or another empire's own
                // specimen, is real, meaningful opposition from Zomboss's own reading of the board.
                var relation = empire == FusionRpg.Core.Commanders.EmpireId.Zomboss
                    ? FusionRpg.Contracts.RelationKind.Ally
                    : FusionRpg.Contracts.RelationKind.Enemy;
                // T3.1's own deferred gap, unchanged here: no live per-unit HP feed exists yet
                // (MatchSnapshot/BoardEntity never carried it) -- ZombossDeployPolicy's own scorer
                // never reads Hp today, so 0/0 is honest ("not populated"), never a fabricated number.
                units.Add(new FusionRpg.Core.Match.Ai.LawnUnitSnapshot(e.Ptr, relation, 0, 0));
            }
            var board = new FusionRpg.Core.Match.Ai.LawnBoardSnapshot(_currentWave, MaxWave: 0, units);

            var tuning = FusionRpg.Core.Match.Ai.ZombossDeployTuningHub.Tuning;
            var candidates = FusionRpg.Core.Match.Ai.ZombossDeployRoster.AvailableSpeciesFor(
                _currentWave, FusionRpg.Core.Creatures.CreatureSpeciesCatalog.All, tuning.WaveRarityCeilings);

            var seed = FusionRpg.Core.Battle.SeededRng.DeriveStream(0, matchKey).NextULong();
            var decision = FusionRpg.Core.Match.Ai.ZombossDeployPolicy.Decide(
                board, candidates,
                speciesId => FusionRpg.Core.Creatures.CreatureSpeciesCatalog.Get(speciesId).BaseRarity,
                tuning.Scorer, seed, caseId: "zomboss-reinforce");
            if (!decision.Deploys) return;

            FusionRpg.Core.Match.Ai.ZombossDeployRunStateHolder.RecordFired();
            RpgHost.Client?.EnqueueZombossDeploy(decision.SpeciesId!, seed, matchKey);
        }
        catch (Exception ex)
        {
            try { CheatState.Error("MatchHost.CheckZombossDeployTrigger: " + ex.Message); } catch { }
        }
    }

    static bool IsMatchEnd(string kind) =>
        string.Equals(kind, "board.end", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, "match.result", StringComparison.OrdinalIgnoreCase);

    static void ClearCommanderSnapshot()
    {
        MatchCommanderSnapshotHolder.EndMatch();
        CheatState.RefreshCommanderAllocationCache();
        LawnDeployRosterSnapshotHolder.EndMatch();
        LawnDeployEventRunStateHolder.EndMatch();
        FusionRpg.Core.Match.Ai.ZombossDeployRunStateHolder.EndMatch();
    }

    static void TryEffect(string label, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            try { CheatState.Error("MatchHost plugin/clear: " + label + ": " + ex.Message); } catch { }
        }
    }
}
