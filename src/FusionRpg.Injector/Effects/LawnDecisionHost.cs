using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Actions.Ai.Lawn;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Diagnostics;
using FusionRpg.Core.Match.Ai;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// combat-ai `lawn-cast-trigger` (module 19, CAI4.8, spec-lawn-cast-trigger.md §"The frame slot"): the
/// lawn's per-frame decision slot — the due set, the budget, the cast token and the trigger state, ticked
/// from <c>InjectorLoop</c> beside <c>LawnBasicAttackGrantBinder.Tick</c>.
///
/// <para><b>What this host owns, and what arrives as a seam.</b> It owns the SCHEDULING and the STATE:
/// which actors are due (`LawnDecisionTrigger.IsDue`), the FIFO frame budget
/// (<c>LawnDecisionBudget.Take</c>), the cast token (<c>LawnCastTokenPool</c>), the swing feed, and the
/// <c>lawn.ai.decide</c> perf section. The DECISION ITSELF — view → policy → the cast plan — arrives as
/// <see cref="Decide"/>, a delegate the composition root supplies, for the same reason
/// <c>LawnBattleView</c> takes seven seams: the policy needs module 16's held sets and the cast needs the
/// ledger's held-action rows, and BOTH need an <c>ActionCatalog</c> the injector has no feed for
/// (`CAI4.3`'s unowned blocker). Wiring those here would make this file the second composition root; a
/// seam keeps it a scheduler, and lets the whole slot be tested with a fake decision.</para>
///
/// <para><b>The VIEW is this host's, though, and that is the spec's own ordering (due set → view →
/// policy → cast).</b> For each due actor the slot builds the view for the actor's OWN side — the decider
/// that owns it, since <c>LawnBattleView</c> is perspective-scoped and its `SideOf` is relative — and
/// hands it to <see cref="Decide"/>. That is what gives `LawnActorViewHost.ViewFor` a production caller:
/// before this it had none (`CAI4.1`'s open line), and a view nobody builds is not a view.</para>
///
/// <para><b>Default-off, and off means NOTHING is held.</b> Every entry point checks
/// <see cref="LawnCombatAiFeature.Enabled"/> first: a false switch drops the trigger state, the budget and
/// every leased token, so turning it off mid-match is not a pause — it is a release. That is the same
/// property <c>LawnBasicAttackFeature</c> has when it withdraws every bound grant (L-N8).</para>
///
/// <para><b>The swing feed comes from the drained record.</b> <c>EffectRuntime</c>'s drain already carries
/// each record's <c>isFirstOfSwing</c>/<c>castOrigin</c>, so <see cref="RecordSwing"/> takes exactly those
/// two flags and the trigger decides; this host never counts swings itself.</para>
///
/// <para><b>Fail closed at the tick boundary.</b> A throwing decision is caught HERE, its token released,
/// and the next frame still ticks — the spec's own rule, and the reason <c>CheatState.Error</c> exists.</para>
/// </summary>
public static class LawnDecisionHost
{
    static readonly object Gate = new();
    static readonly List<string> DueScratch = new();

    static LawnDecisionTrigger? _trigger;
    static LawnDecisionBudget? _budget;
    static LawnCastTokenPool? _pool;
    static CombatAiLawnTuning? _configured;
    static bool _missingTuningReported;

    static int _decisions;
    static int _casts;
    static int _failures;

    /// <summary>
    /// The composition root's decision step: given the due actor and the VIEW for that actor's own side,
    /// run the policy and the cast, and report whether a cast was COMMITTED (which is what starts the
    /// post-cast lock). Null means the slot has no decision source yet — it still computes the due set and
    /// the budget, and casts nothing.
    /// </summary>
    public static Func<string, LawnBattleView, bool>? Decide { get; set; }

    /// <summary>Decisions taken since the board edge. A reading, not a contract.</summary>
    public static int DecisionCount { get { lock (Gate) return _decisions; } }

    /// <summary>Casts committed since the board edge.</summary>
    public static int CastCount { get { lock (Gate) return _casts; } }

    /// <summary>Decision steps that threw and were contained at the tick boundary.</summary>
    public static int FailureCount { get { lock (Gate) return _failures; } }

    /// <summary>Actors holding trigger state right now.</summary>
    public static int TrackedCount { get { lock (Gate) return _trigger?.Count ?? 0; } }

    /// <summary>Cast tokens leased right now — 0 whenever the switch is off.</summary>
    public static int TokensInUse { get { lock (Gate) return _pool?.InUse ?? 0; } }

    /// <summary>
    /// The board edge: build the three structures from the lawn section of the tuning file and the match's
    /// own seed. Called at <c>board.start</c>, the same boundary <c>LawnBasicAttackCostCharger</c> and the
    /// per-match stores use.
    /// </summary>
    public static void BeginMatch(CombatAiLawnTuning tuning, ulong matchSeed)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        lock (Gate)
        {
            _trigger = new LawnDecisionTrigger(
                tuning.SwingsPerDecision, tuning.TicksPerDecision, tuning.PostCastLockTicks,
                matchSeed, tuning.OffsetStream);
            _budget = new LawnDecisionBudget();
            _pool = new LawnCastTokenPool();
            _decisions = 0;
            _casts = 0;
            _failures = 0;
        }
    }

    /// <summary>
    /// The lawn section of `data/tuning/combat-ai.v{n}.json`, handed in ONCE by the host that read the
    /// file (<c>RpgHost.Initialize</c>, the same read that configures <c>CombatAiProfilePolicy</c>).
    /// Parsed there rather than at <c>board.start</c>, which runs once a match: the file read belongs at
    /// boot with every other tuning read, not on the frame path.
    /// </summary>
    public static void Configure(CombatAiLawnTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        lock (Gate) _configured = tuning;
    }

    /// <summary>
    /// The board edge for a caller that has already <see cref="Configure"/>d the tuning — the shape
    /// <c>MatchHost</c>'s <c>board.start</c> block uses, where every other holder's <c>BeginMatch</c> is
    /// already called. An unconfigured host reports ONCE and builds nothing rather than throwing: this
    /// runs on the frame path, and module 19's cadence is not load-bearing for anything else.
    /// </summary>
    public static void BeginMatch(ulong matchSeed)
    {
        CombatAiLawnTuning? tuning;
        lock (Gate) tuning = _configured;
        if (tuning is null)
        {
            ReportMissingTuningOnce();
            return;
        }
        BeginMatch(tuning, matchSeed);
    }

    /// <summary>Tests only: drops the configured tuning, the once-only report flag AND the three
    /// structures, so one process can exercise both board edges and no case can observe a PREVIOUS
    /// case's slot. <see cref="Clear"/> deliberately empties rather than nulls them (board.end), which
    /// is why a test that only calls <see cref="Clear"/> still sees the last <c>BeginMatch</c>.</summary>
    public static void ResetForTest()
    {
        lock (Gate)
        {
            _configured = null;
            _missingTuningReported = false;
            _trigger = null;
            _budget = null;
            _pool = null;
            _decisions = 0;
            _casts = 0;
            _failures = 0;
        }
    }

    static void ReportMissingTuningOnce()
    {
        lock (Gate)
        {
            if (_missingTuningReported) return;
            _missingTuningReported = true;
        }
        try { CheatState.Error("lawn.ai: LawnDecisionHost.Configure(...) has not run -- the frame slot builds nothing"); }
        catch { }
    }

    /// <summary>Match ends / board reset: every structure goes.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            _trigger?.Clear();
            _budget?.Clear();
            _pool?.Clear();
            _decisions = 0;
            _casts = 0;
            _failures = 0;
        }
    }

    /// <summary>The death edge, and the reused-ptr answer: the actor's trigger state, its queued budget
    /// entry and its token all go, so a reused IL2CPP address starts at zero swings and no lock.</summary>
    public static void Remove(string actorKey)
    {
        if (string.IsNullOrWhiteSpace(actorKey)) return;
        lock (Gate)
        {
            _trigger?.Remove(actorKey);
            _budget?.Remove(actorKey);
            _pool?.Release(actorKey);
        }
    }

    /// <summary>One swing from the drained record. Returns whether this swing made the actor due.</summary>
    public static bool RecordSwing(string actorKey, bool isFirstOfSwing, bool castOrigin, long nowTick)
    {
        lock (Gate)
        {
            if (!LawnCombatAiFeature.Enabled || _trigger is null) return false;
            // Register on demand, because the DRAIN runs BEFORE this frame's tick: a swing recorded in
            // frame 1 would otherwise be dropped for an actor this host has not seen yet. Register is
            // idempotent and seeds the actor's seeded offset, so a second call is a no-op.
            if (!_trigger.TryState(actorKey, out _)) _trigger.Register(actorKey, nowTick);
            return _trigger.RecordSwing(actorKey, isFirstOfSwing, castOrigin);
        }
    }

    /// <summary>
    /// The frame slot. Order matters and is the spec's: the switch first (off RELEASES everything), then
    /// the perf section around the work, then the token backstop, then the due set and the budget.
    /// </summary>
    public static void Tick(long nowTick, int frame, BoardSnapshot census)
    {
        if (census is null) return;

        lock (Gate)
        {
            if (!LawnCombatAiFeature.Enabled)
            {
                // OFF IS A RELEASE, not a pause: counters dropped, every token given back.
                _trigger?.Clear();
                _budget?.Clear();
                _pool?.Clear();
                return;
            }

            if (_trigger is null || _budget is null || _pool is null) return;

            // Its own section, OUTSIDE KernelDriveHost's budget: a share of the frame is a perf-domain
            // number (gk-core/data/tuning/lawn-perf-budget.v1.json), not a count in the kernel's budget.
            using (PerfProbe.Measure(PerfSection.LawnAiDecide))
            {
                _pool.ReclaimExpired(nowTick);

                for (var i = 0; i < census.Entities.Count; i++)
                {
                    var actorKey = census.Entities[i].Ptr;
                    if (string.IsNullOrWhiteSpace(actorKey)) continue;
                    if (!_trigger.TryState(actorKey, out _)) _trigger.Register(actorKey, nowTick);
                    if (_trigger.IsDue(actorKey, nowTick)) _budget.Offer(actorKey, nowTick);
                }

                DueScratch.Clear();
                _budget.Take(DueScratch);

                for (var i = 0; i < DueScratch.Count; i++)
                {
                    var actorKey = DueScratch[i];
                    if (!_pool.TryLease(actorKey, nowTick)) continue;   // another decision holds it

                    var decide = Decide;
                    if (decide is null) { _pool.Release(actorKey); continue; }

                    // The VIEW for the actor's OWN side: `LawnBattleView` is perspective-scoped and its
                    // `SideOf` is relative, so the decider that owns an actor is the one built for that
                    // actor's own side. `ViewFor` caches one view per (perspective, frame, census), so a
                    // frame with N due actors on one side builds one view.
                    var view = LawnActorViewHost.ViewFor(PerspectiveOf(census, actorKey), frame);
                    if (view is null) { _pool.Release(actorKey); continue; }

                    _decisions++;
                    try
                    {
                        if (decide(actorKey, view))
                        {
                            _casts++;
                            _trigger.OnCommittedCast(actorKey, nowTick);
                        }
                        else
                        {
                            _trigger.OnRefusedDecision(actorKey);
                        }
                    }
                    catch
                    {
                        // Fail closed at the boundary: the frame survives, the token is given back, and the
                        // next frame ticks.
                        _failures++;
                        try { CheatState.Error("lawn.ai.decide threw for " + actorKey); } catch { }
                    }
                    finally
                    {
                        _pool.Release(actorKey);
                    }
                }
            }
        }
    }

    /// <summary>The actor's own board side, from the census — which decider owns it. The VIEW never reads
    /// the raw side (that is what the ownership oracle is for); the HOST may, because this is a routing
    /// question ("which perspective decides for this actor"), not a relation question. An actor the census
    /// does not hold falls to the player's perspective rather than being dropped.</summary>
    static string PerspectiveOf(BoardSnapshot census, string actorKey)
    {
        var snap = census.FindPtr(actorKey);
        return string.Equals(snap?.Side, LawnActorViewHost.ZombieSide, System.StringComparison.OrdinalIgnoreCase)
            ? LawnActorViewHost.ZombieSide
            : LawnActorViewHost.PlantSide;
    }

    /// <summary>
    /// combat-ai `ai-tiers-personality` (module 3) needs an actor class per actor, and on the lawn it is
    /// the deploy path that knows: a live <c>UniqueBinding</c> is a unique creature (D6's smart tier);
    /// everything else is general. Read through the ONE shipped lookup, never a second binding index.
    /// </summary>
    public static AiActorClass ClassOf(string actorKey)
    {
        try
        {
            return CheatState.ResolveBoundInstanceId(actorKey) is not null ? AiActorClass.Unique : AiActorClass.General;
        }
        catch
        {
            // The lookup reaches the live match and therefore the game assembly; an unresolvable actor is
            // GENERAL, which is the tier that runs the cheap path — never the smart one by accident.
            return AiActorClass.General;
        }
    }

    /// <summary>The recorded trigger state for one actor, for the inspector's `Trigger` field. Null when
    /// this host tracks nothing for it — never a synthesised state.</summary>
    public static bool TryTriggerState(string actorKey, out AiTriggerState trigger)
    {
        trigger = AiTriggerState.None;
        lock (Gate)
        {
            if (_trigger is null || !_trigger.TryState(actorKey, out var state)) return false;
            trigger = new AiTriggerState(state.Swings, state.NextTimerTick, state.LockUntilTick, _pool?.InUse ?? 0);
            return true;
        }
    }
}
