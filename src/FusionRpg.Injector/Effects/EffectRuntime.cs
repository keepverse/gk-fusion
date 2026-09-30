using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Diagnostics;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Plugins;
using FusionRpg.Core.Status;
using FusionRpg.Injector.Fx;
using FusionRpg.Injector.Host;
using FusionRpg.Injector.Stats;
using FusionRpg.Bridge.Hud;
using FusionRpg.Bridge.Stats;

namespace FusionRpg.Injector.Effects;

/// <summary>Injector-hosted Foundation EffectBag + Unity action sink.</summary>
public static class EffectRuntime
{
    static readonly object Gate = new();
    static EffectBag? _bag;
    static EffectPluginHost? _plugins;
    static StatusRuntime? _status;
    static EffectEventDedupe _dedupe = new();
    static long _tick;
    /// <summary>
    /// Last OnDamageDealt tick per <c>matchKey|targetPtr</c> (A2 — skip redundant taken).
    /// Keyed by target, not target+actor: the taken-side check only asks "was this target dealt
    /// to within 8 ticks", and the newest tick is always the closest — O(1) instead of the old
    /// full-table prefix scan per damage event (~1000 events/s in heavy combat).
    /// </summary>
    static readonly Dictionary<string, long> DealtIdentity = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// solid-remediation T4.14 (D15): the lawn's time SOURCE is still the wall clock — this is seeded
    /// from it — but the SCHEDULER is now this advanced value, so a status expiry and a DoT pulse are
    /// computed from one number instead of two. Advanced from <see cref="AdvanceEffectClock"/>, driven
    /// by <c>KernelDriveHost.Tick</c> off the SAME scaled frame delta that advances the 100 ms kernel
    /// grid, so a pulse schedule and a status expiry cannot drift apart.
    /// </summary>
    // Seeded from the wall clock HERE, at the injector's own composition root — the one host that
    // legitimately reads real time. Core carries no `StartingNow()`: a Core type reading
    // `DateTimeOffset.UtcNow` is a wall-clock read inside the world-simulation purity scan, which
    // `WorldDeterminismGuardTests` refuses and is right to.
    //
    // NOT sourced from `ServerClock` (RS3 increment 4 tried it and reverted): the effect runtime's clock
    // is already injectable and correctly so, and `PlayerSpeciesMaterialiseCallerGuardTests`
    // (`gk-core/tests/FusionRpg.Guard.Tests/PlayerSpeciesMaterialiseCallerGuardTests.cs:148`) asserts this exact
    // literal as the proof that the wall clock is still the SOURCE and that no round trip was added to
    // the injector's hot path. That guard test is a pipeline-protected path, so re-pointing it at the
    // seam is not this lane's to do; the site is allowlisted with this reason in
    // `gk-core/scripts/guard-clock-seam.py` instead. Spec §7 said increment 4 MAY source it, never that it must.
    static readonly Core.Effects.AdvancedEffectClock _clock = new(DateTimeOffset.UtcNow);

    public static StatusRuntime Status
    {
        get
        {
            Ensure();
            return _status!;
        }
    }

    public static EffectBag Bag
    {
        get
        {
            Ensure();
            return _bag!;
        }
    }

    public static void Ensure()
    {
        lock (Gate)
        {
            if (_bag != null) return;
            var catalog = new InMemoryEffectCatalog();
            catalog.ReplaceAll(EffectAtomCatalog.CreateAll());
            var grants = new InMemoryEffectGrantStore();
            var proc = new EffectProcPolicy(new SystemEffectClock(), new SeededEffectRandom(Environment.TickCount));
            _status = InjectorStatusBridge.CreateRuntime();
            _status.OnResisted = ev => DebugRuntime.Emit("debug.status.resisted", new Dictionary<string, object>
            {
                ["statusId"] = ev.StatusId,
                ["hostPtr"] = ev.HostPtr,
                ["attackerPtr"] = ev.AttackerPtr ?? "",
                ["grantId"] = ev.GrantId,
                ["reason"] = ev.Reason.ToString(),
                ["delta"] = ev.Delta,
                ["at"] = ev.At.ToString("o")
            });
            // passive-tree-todo.md G6 (spec-gate-counters.md §2.1, §7 P2) -- the FIRST production
            // caller `OnFreshApplication` has had since G1 shipped it. Wired beside `OnApplied` rather
            // than folded into it: `OnApplied` is single-assignment with three OTHER assigning sites
            // (StatusRuntime.cs's own doc comment), and `OnFreshApplication` exists specifically so
            // this counter never has to touch that shared, already-fragile delegate.
            _status.OnFreshApplication = GateCounterHost.StatusCounter.Handle;
            _status.OnApplied = inst =>
            {
                try { VfxDirector.Play(Core.Vfx.StatusVfxCues.Cue(inst)); } catch { }
                // E21: StatusStatPayload.ToModifiers/SourceIdOf had zero production callers (audit
                // finding A1) — rally/expose/command/shatter created instances and changed no stat.
                // Same source-tagged path ExecModifyStat already uses for effect-granted mods
                // ("effect:" + grantId): Upsert here, WithdrawSource on end, both source-kind "status"
                // so a stack expiring cannot withdraw another stack's contribution.
                try
                {
                    if (inst.StatMods.Count > 0)
                    {
                        CheatState.Stats.Upsert(Core.Status.StatusStatPayload.ToModifiers(inst));
                        CheatActions.ReapplyLivingForOwner("entity:" + inst.HostPtr);
                    }
                }
                catch (Exception ex) { CheatState.Error("status stat apply: " + ex.Message); }
            };
            _status.OnEnded = inst =>
            {
                try { VfxDirector.Play(Core.Vfx.StatusVfxCues.ExpireCue(inst)); } catch { }
                try
                {
                    if (inst.StatMods.Count > 0)
                    {
                        CheatState.Stats.WithdrawSource("status", Core.Status.StatusStatPayload.SourceIdOf(inst));
                        CheatActions.ReapplyLivingForOwner("entity:" + inst.HostPtr);
                    }
                }
                catch (Exception ex) { CheatState.Error("status stat withdraw: " + ex.Message); }
            };
            _bag = new EffectBag(catalog, grants, proc, new InjectorEffectActionSink());
            // base-defense Gate 0 (audit C4): explicit at THIS composition root, on purpose — the
            // injector applies effects to the live, real-time PvZ match, which is the one legitimate
            // non-replayed caller. EffectBag.UtcNow no longer defaults silently (see its own doc
            // comment); every host, including this one, must now say which clock it wants.
            //
            // solid-remediation T4.14 (D15): that choice still stands, and it is still the real world
            // that supplies this host's time — `AdvancedEffectClock` is SEEDED from the wall clock and
            // ADVANCED by the frame delta this runtime already has. What changed is the ROLE. Reading
            // `DateTimeOffset.UtcNow` per question made the wall clock the SCHEDULER, while pulse
            // scheduling rode the engine's own 100 ms grid — so one tick answered "has a pulse come
            // due?" in engine time and "has this status expired?" in wall time. Now both read one
            // number. A field read, so the hot-path budget (T4.16) is untouched: no round trip exists
            // to pay for.
            _bag.UtcNow = () => _clock.UtcNow;
            _bag.Status = _status;
            // E41 (spec-ui-attach-point.md §2b): op:meter/op:banner's own collaborator, wired the same
            // way DamageFxCueAdapter.Sink is wired onto the Funnel below for op:number.
            _bag.UiPresent = InjectorUiPresentSink.Instance;
            WireCombatMath(_bag);
            _ = new EffectFunnel(_bag, DamageFxCueAdapter.Sink);
            _plugins = EffectPluginHostFactory.Create(_bag);
            _dedupe = new EffectEventDedupe();
            _tick = 0;
        }
    }

    public static void ResetForTests()
    {
        lock (Gate)
        {
            _bag = null;
            _plugins = null;
            _status = null;
            DealtIdentity.Clear();
            // E35: a stale match.modify write set must never survive into the next test/match — it is
            // per-match state, and TakeAll's own drain-and-clear is exactly what a reset needs too.
            MatchModifyWrites.TakeAll();
            Ensure();
        }
    }

    public static void NotifyMatchStart(string matchKey, long playerId = 0)
    {
        Ensure();
        AtomPushReceiver.NotifyMatchStart(matchKey);
        _plugins!.NotifyMatchStart(matchKey, playerId);
    }

    public static void NotifyMatchEnd(string? matchKey)
    {
        Ensure();
        // passive-tree-todo.md G6 (spec-gate-counters.md §4.3) -- the "unconditionally at match end"
        // half of the flush contract, alongside InjectorLoop's timer-driven half. Best-effort, same as
        // every other periodic reporter this loop drives (PerfReporter.Flush): a lost window costs a
        // little progress, never correctness.
        try { GateCounterHost.Flush(RpgHost.Client); } catch { }
        _plugins!.NotifyRemoved(matchKey ?? "");

        // E35 (spec-match-modify.md §2.6): restore ONLY the E-* ids a live match.modify grant wrote
        // this match, by clearing them — never CheatActions.LoadBoardConfigIntoCheats(), which reads
        // Board.config back into EVERY E-* id unconditionally and would silently overwrite an
        // operator's own hand-set cheat values with the level's own shipped values, every match end,
        // with no log. The two existing callers of ApplyBoardConfig/LoadBoardConfigIntoCheats
        // (GameHooks.cs's Board.Awake handler, CheatCommandRunner.cs's operator command) are
        // untouched by this — this is a narrower, additional restore path for this module's own
        // writes specifically. MatchModifyRestore is the extracted, Unity-free half of this call —
        // see its own doc comment for why the logic lives there rather than inline here.
        MatchModifyRestore.Restore(MatchModifyWrites.TakeAll, id => CheatState.ClearField(id, "match-end"));

        // E36 (spec-wave-control.md §2.5): F-WAVE-FREEZE is a plain CheatState toggle, not one of
        // match.modify's own E-* fields -- MatchModifyWrites/MatchModifyRestore above is that
        // mechanism's alone. A bound `hold` op sets this toggle and nothing else clears it between
        // matches, so it gets its own, smaller clear here rather than folding into the machinery
        // above -- the same reason E35's own scoped restore exists: a toggle surviving past its match
        // leaks silently and permanently into the next one.
        CheatState.SetToggle("F-WAVE-FREEZE", false, "match-end");
    }

    public static long NextTick() => Interlocked.Increment(ref _tick);

    /// <summary>Monotonic milliseconds for the Secondary runner's ICD clocks (E19/E15).</summary>
    public static long NowMs() => Environment.TickCount64;

    public static bool HasActiveGrants() => Bag.HasAnyGrant();

    public static bool HasGrantForEffect(string effectId) => Bag.HasGrantForEffect(effectId);

    public static bool HasOnDamageDealtGrant() => Bag.HasGrantWithTrigger(EffectTriggers.OnDamageDealt);

    /// <summary>Producer gate for *.damage emission — melee OnDamageTaken effects need it even with telemetry off.</summary>
    public static bool HasOnDamageTakenGrant() => Bag.HasGrantWithTrigger(EffectTriggers.OnDamageTaken);

    /// <summary>Producer gate for bullet.init emission (OnSpawn trigger).</summary>
    public static bool HasOnSpawnGrant() => Bag.HasGrantWithTrigger(EffectTriggers.OnSpawn);

    /// <summary>
    /// Product <c>combat.hit</c> when debug/LogDamage hit-capture is on, or bag has OnDamageDealt grants.
    /// </summary>
    public static bool ShouldEmitCombatHit() =>
        DebugRuntime.ShouldEmitHit() || HasOnDamageDealtGrant();

    public static bool HasOnDeathGrant() => Bag.HasGrantWithTrigger(EffectTriggers.OnDeath);

    /// <summary>E33 (spec-activation-edge.md §2.4): the fast gate a producer calls before building an
    /// `actor.activate` payload — the same shape as the four gates above, so an ungated activation
    /// emit does not repeat the per-hit-allocation/uncached-resolve shape the 2026-08 perf audit
    /// blamed. E33 ships no producer of its own; `A9 movement-actions` is the first caller.</summary>
    public static bool HasOnActivateGrant() => Bag.HasGrantWithTrigger(EffectTriggers.OnActivate);

    public static EffectGrant Grant(EffectGrantDto dto)
    {
        Ensure();
        var g = Bag.Grant(dto);
        DebugRuntime.Emit("debug.effect.granted", new Dictionary<string, object>
        {
            ["grantId"] = g.GrantId,
            ["effectId"] = g.EffectId,
            ["ownerKey"] = g.OwnerKey
        });
        return g;
    }

    /// <summary>
    /// lawn-combat-wire T10: the SAME <c>Bag.Grant</c> call <see cref="Grant"/> makes, minus its
    /// per-call <c>debug.effect.granted</c> telemetry emit. `DebugRuntime.Emit` is unconditional (no
    /// `SessionActive` gate, unlike <c>MaybeEmitCombatPacketTrace</c>) and always routes through
    /// `GameHooks.Emit` — `MatchHost.Apply` + `EffectRuntime.OnCapture` + a queued
    /// `RpgHost.Client.Enqueue` network payload. That is fine for a rare, manual cheat/debug grant
    /// (<see cref="Grant"/>'s existing callers); it is exactly the "N synchronous heavy operations on a
    /// mass spawn" this program's own acceptance criteria refuse for a grant bound once per lawn actor.
    /// Never a second grant route — still <c>Ensure</c>d, still <c>Bag.Grant</c>, still the one gate
    /// every Secondary caller shares.
    /// </summary>
    public static EffectGrant GrantQuiet(EffectGrantDto dto)
    {
        Ensure();
        return Bag.Grant(dto);
    }

    public static bool Withdraw(string grantId)
    {
        Ensure();
        // Bag.Withdraw fires OnRemoved → FA1 remove + ReapplyLivingForOwner(owner).
        var ok = Bag.Withdraw(grantId);
        if (ok)
            DebugRuntime.Emit("debug.effect.withdrawn", new Dictionary<string, object> { ["grantId"] = grantId });
        return ok;
    }

    /// <summary>
    /// Withdraw all grants owned by <c>entity:{ptr}</c> (normalized). Call on die before ForgetEntity.
    /// status-rail C1: tear down status StatMods without firing OnEnded (VFX reaps via anchor).
    /// </summary>
    public static int WithdrawEntity(string? ptrHex)
    {
        if (string.IsNullOrWhiteSpace(ptrHex)) return 0;
        Ensure();
        var n = Bag.WithdrawForOwner(null, EffectOwnerKeys.Entity(ptrHex.Trim()));
        if (_status != null)
        {
            foreach (var inst in _status.TakeHostInstances(ptrHex.Trim()))
            {
                if (inst.StatMods.Count == 0) continue;
                try
                {
                    CheatState.Stats.WithdrawSource("status", Core.Status.StatusStatPayload.SourceIdOf(inst));
                    CheatActions.ReapplyLivingForOwner("entity:" + inst.HostPtr);
                }
                catch (Exception ex) { CheatState.Error("status stat death withdraw: " + ex.Message); }
            }
        }

        if (n > 0)
        {
            DebugRuntime.Emit("debug.effect.withdrawn_entity", new Dictionary<string, object>
            {
                ["ptr"] = ptrHex.Trim(),
                ["count"] = n
            });
        }

        return n;
    }

    /// <summary>Withdraw all grants, clear proc/dedupe, strip session effect and status mods.</summary>
    public static void ClearAll(string reason = "clear")
    {
        Ensure();
        lock (Gate)
        {
            Bag.ClearAll();
            // Compiled output is match-scoped, like the grant session it arrived with (E19).
            AtomPushReceiver.Clear();
            _dedupe.Clear();
            DealtIdentity.Clear();
        }

        try { CheatState.Stats.WithdrawAllBySourceKind("effect"); } catch { }
        // status-rail C1: Bag.ClearAll → Status.Clear() skips OnEnded; strip status session mods.
        try { CheatState.Stats.WithdrawAllBySourceKind("status"); } catch { }
        try { CheatActions.ReapplyAllLiving(); } catch { }
        InjectorDerivedOverride.Clear();
        InjectorElementOverride.Clear();
        // E41: an atom-authored meter is per-match state, the same reason match.modify's own
        // MatchModifyWrites drains on this same path — leaving one set would leak silently into the
        // next match's HUD.
        Hud.ActorHudMeterOverride.Clear();
        try { FusionRpg.Bridge.Hud.ActorHudCache.Clear(); } catch { }

        DebugRuntime.Emit("debug.effect.cleared", new Dictionary<string, object>
        {
            ["reason"] = reason,
            ["contractVersion"] = FoundationContractVersion.Current
        });
    }

    public static EffectCatalogSnapshotDto Snapshot()
    {
        Ensure();
        return Bag.Snapshot();
    }

    public static void ReplaceCatalog(IEnumerable<EffectDef> defs)
    {
        ClearAll("reload");
        Ensure();
        Bag.Catalog.ReplaceAll(defs);
        DebugRuntime.Emit("debug.effect.reload", new Dictionary<string, object>
        {
            ["contractVersion"] = FoundationContractVersion.Current,
            ["count"] = Bag.Catalog.All().Count
        });
    }

    /// <summary>
    /// v2 drain entry (event-pipeline-v2 plan Task 9) — the DTO is already mapped and
    /// dealt/taken pairing already resolved by the coalescer, so this skips OnCapture's
    /// mapping, DealtIdentity, and per-event board freeze (the host freezes once per drain).
    /// </summary>
    public static void OnDrained(EffectEventDto ev)
    {
        using var _perf = PerfProbe.Measure(PerfSection.EffectOnCapture);
        Ensure();
        if (!Bag.HasAnyGrant() && !(Bag.Funnel?.HasPending ?? false)) return;
        // lawn-combat-wire T12 (spec-basic-attack-cost.md, D2/D6: "no resource, no [RPG] trigger"):
        // untouched passthrough for every trigger but OnDamageDealt, and for OnDamageDealt whenever the
        // feature's kill switch is off -- see LawnBasicAttackCostCharger.ShouldApplyRider's own doc.
        if (!LawnBasicAttackCostCharger.ShouldApplyRider(ev)) return;
        var fsmTraceOn = FsmTrace.Enabled;
        if (fsmTraceOn)
            CheatState.Note($"fsm-trace EffectRuntime.OnDrained ev trigger={ev.Trigger} actorPtr={ev.ActorPtr} targetPtr={ev.TargetPtr} damage={ev.Damage} swingId={ev.SwingId} isFirstOfSwing={ev.IsFirstOfSwing}");
        try
        {
            // BEFORE the bag: EffectBag.OnEvent flushes the Funnel inside itself, so a Secondary
            // dispatch enqueued afterwards would wait for the next event (E19).
            AtomPushReceiver.OnEvent(ev, Bag.BoardSnapshot);
            var plan = Bag.OnEvent(ev);
            if (fsmTraceOn)
                CheatState.Note($"fsm-trace EffectRuntime.OnDrained plan trigger={plan.Trigger} actions={plan.Actions.Count} skipped={plan.Skipped.Count}");
            MaybeEmitCombatPacketTrace(plan, "drain");
        }
        catch (Exception ex)
        {
            CheatState.Error("effect OnDrained: " + ex.Message);
        }
    }

    public static void OnCapture(string kind, Dictionary<string, object> payload)
    {
        using var _perf = PerfProbe.Measure(PerfSection.EffectOnCapture);
        Ensure();
        if (!Bag.HasAnyGrant() && !(Bag.Funnel?.HasPending ?? false)) return;

        // A2: when TakeDamage will also emit combat.hit (bullet), skip OnDamageTaken from *.damage.
        if (CombatHitEmitPolicy.WillSkipTakenFromDamage(kind, payload, ShouldEmitCombatHit()))
            return;

        var tick = NextTick();
        var ev = EffectEventAdapter.TryMap(kind, payload, tick);
        if (ev == null) return;

        if (string.Equals(ev.Trigger, EffectTriggers.OnDamageDealt, StringComparison.OrdinalIgnoreCase))
        {
            var id = (ev.MatchKey ?? "") + "|" + (ev.TargetPtr ?? "");
            DealtIdentity[id] = tick;
            if (DealtIdentity.Count > 2048) DealtIdentity.Clear();
        }
        else if (string.Equals(ev.Trigger, EffectTriggers.OnDamageTaken, StringComparison.OrdinalIgnoreCase))
        {
            var id = (ev.MatchKey ?? "") + "|" + (ev.TargetPtr ?? "");
            if (DealtIdentity.TryGetValue(id, out var dealtTick) && Math.Abs(dealtTick - tick) < 8)
                return;
        }

        // EffectEventDedupe removed from this path: with a per-event tick its window (1) made
        // it always-pass dead code (v2 audit §D2), while still costing a dict insert per event.
        try
        {
            FreezeBoard();
            AtomPushReceiver.OnEvent(ev, Bag.BoardSnapshot);
            var plan = Bag.OnEvent(ev);
            MaybeEmitCombatPacketTrace(plan, "capture");
            if (plan.Actions.Count > 0)
            {
                DebugRuntime.Emit("debug.effect.plan", new Dictionary<string, object>
                {
                    ["trigger"] = plan.Trigger,
                    ["actions"] = plan.Actions.Count,
                    ["skipped"] = plan.Skipped.Count,
                    ["contractVersion"] = plan.ContractVersion
                });
            }
        }
        catch (Exception ex)
        {
            CheatState.Error("effect OnEvent: " + ex.Message);
            DebugRuntime.Emit("debug.effect.error", new Dictionary<string, object>
            {
                ["error"] = ex.Message,
                ["kind"] = kind
            });
        }
    }

    public static IntentPlanDto FireSynthetic(EffectEventDto ev)
    {
        Ensure();
        if (ev.Tick <= 0) ev.Tick = NextTick();
        FreezeBoard();
        var plan = Bag.OnEvent(ev);
        MaybeEmitCombatPacketTrace(plan, "synthetic");
        return plan;
    }

    public static void FreezeBoard()
    {
        Ensure();
        Bag.BoardSnapshot = InjectorBoardSnapshot.Capture();
        Bag.SelectedPtr = CheatState.SelectedPtr == IntPtr.Zero
            ? null
            : CheatState.SelectedPtr.ToString("X");
    }

    /// <summary>
    /// T4.14 (D15) — advance the lawn's effect clock by a frame's elapsed simulated time. Called from
    /// <c>KernelDriveHost.Tick</c> with the SAME scaled delta the kernel's 100 ms grid advances by, so
    /// the pulse schedule and every status expiry are computed from one number.
    ///
    /// <para><b>This replaced the legacy DoT accumulator's advance (backlog-clean-up BCU8.3).</b> That
    /// advance lived in the deleted <c>TickDots</c>, which B26's kernel gate had already made
    /// unreachable on a live board — so D15's own fix was inert on every board from the day it landed.
    /// Deleting the accumulator without moving the advance here would have removed it entirely.</para>
    /// </summary>
    public static void AdvanceEffectClock(float scaledDeltaSeconds)
    {
        // Advanced BEFORE any 100 ms bucket: a status expires on real elapsed time, not only on the
        // frames that happen to complete a bucket. Negative/NaN deltas are ignored by the clock itself.
        _clock.AdvanceSeconds(scaledDeltaSeconds);
    }

    /// <summary>
    /// The DoT/status pulse itself — T13/B26's seam, and since BCU8.3 the ONLY DoT entry point. The
    /// kernel schedules it on a 100 ms event; the legacy accumulator that second-guessed that schedule
    /// is gone.
    ///
    /// <para>The <b>period stays 100 ms</b>. This is a substitution, not a redesign: shield regen
    /// carries in integer milli-HP and a 1 ms drive would truncate it toward zero, so only the
    /// <i>scheduling</i> moved onto the kernel, never the granularity.</para>
    ///
    /// <para><c>effect.tickDots</c> now measures THIS pulse (BCU8.3): it used to wrap the accumulator,
    /// and leaving it there would have left the section with no producer at all.</para>
    /// </summary>
    public static void PulseDotsNow()
    {
        using var _perf = PerfProbe.Measure(PerfSection.EffectTickDots);
        Ensure();
        if (!Status.HasAnyInstances()) return;
        FreezeBoard();
        var n = Bag.TickDots();
        if (n > 0)
        {
            var actions = Bag.Funnel?.LastFlushedActions ?? Array.Empty<EffectActionPlanItem>();
            MaybeEmitCombatPacketTrace(new IntentPlanDto
            {
                Trigger = EffectTriggers.OnTimer,
                Actions = actions.ToList(),
                Skipped = Bag.LastSkipped.ToList()
            }, "dot");
        }
    }

    public static void BindSelectedTarget(DamagePacket packet)
    {
        if (packet?.Target == null) return;
        if (!string.Equals(packet.Target.Mode, TargetModes.Selected, StringComparison.OrdinalIgnoreCase))
            return;
        if (CheatState.SelectedPtr == IntPtr.Zero)
        {
            packet.Target.Mode = TargetModes.Single;
            packet.Target.Ptr = null;
            return;
        }

        packet.Target.Mode = TargetModes.Single;
        packet.Target.Ptr = CheatState.SelectedPtr.ToString("X");
    }

    static void MaybeEmitCombatPacketTrace(IntentPlanDto plan, string source)
    {
        // LIVE-prove trace only — in normal play this fired per hit (dict + list allocs + queue).
        if (!DebugRuntime.SessionActive) return;
        var fa = plan.Actions
            .Where(a => string.Equals(a.Action, EffectActions.ApplyResourceDelta, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (fa.Count <= 0) return;
        var ptrs = fa
            .Select(a => a.Params.TryGetValue("targetPtr", out var p) ? p?.ToString() : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .ToList();
        DebugRuntime.Emit("debug.combat.packet", new Dictionary<string, object>
        {
            ["source"] = source,
            ["fa10"] = fa.Count,
            ["skipped"] = plan.Skipped.Count,
            ["trigger"] = plan.Trigger ?? "",
            ["ptrs"] = ptrs,
            ["chainDepth"] = 0
        });
    }

    static void WireCombatMath(EffectBag bag)
    {
        var overlay = OverlayCombatMath.Create(
            InjectorCombatBridge.ResolveActor,
            ElementHub.Default,
            bag.CombatRng,
            (breakdown, packet, targetPtr) =>
            {
                InjectorCombatBridge.EmitOverlayBreakdown(breakdown, packet, targetPtr);
                // lawn-combat-observer (Task 0, lawn-combat-wire): unconditional RPG-delta capture,
                // additive alongside EmitOverlayBreakdown (which stays session-gated) — never a
                // replacement for it, never a new gate on it.
                LawnCombatObserverBridge.RecordRpgDelta(breakdown, packet, targetPtr);
            });
        bag.CombatMath = new ConditionalOverlayCombatMath(overlay)
        {
            IsEnabled = () => OverlayCombatFeature.Enabled
        };
        // Shield layer above the Funnel (shield-system-spec.md §2.2) — same resolve as combat.
        bag.ShieldGate = new FusionRpg.Core.Combat.Shield.ShieldGate(
            new FusionRpg.Core.Combat.Shield.ShieldRuntime(),
            InjectorCombatBridge.ResolveActor);
        // aura-skill T20 (audit): the SAME resolve, threaded to DispatchInstant's actorResolve
        // parameter so Retribution/reflect actually fires — it shipped with the math but no
        // production caller ever passed this argument.
        bag.ActorResolve = InjectorCombatBridge.ResolveActor;
        // passive-tree-todo.md G6 (spec-gate-counters.md §2.2, §7 P1) -- ElementMasteryCounter's real
        // production caller. Fires for every DispatchInstant call this bag makes, direct hit AND DoT
        // pulse alike (EffectBag.OnDamageApplied's own doc comment), correctly tagged by DamageOrigin
        // in each case so a DoT tick never double-credits the one application status_applied already
        // credited.
        bag.OnDamageApplied = GateCounterHost.HandleDamageApplied;
    }

    // ---- Shield tick host — own guard (NOT the DoT pulse's status guard) ----

    static long _shieldTickNo;
    static readonly Func<string, FusionRpg.Core.Stats.Derived.ActorDerivedSnapshot> ShieldOwnerResolver =
        ownerKey =>
        {
            var ptr = ownerKey.StartsWith("entity:", StringComparison.Ordinal)
                ? ownerKey.Substring("entity:".Length)
                : ownerKey;
            return InjectorCombatBridge.ResolveActor(ptr, attackerLess: false).Derived;
        };

    /// <summary>
    /// One 100 ms shield upkeep step — T13/B26's seam, and since BCU8.3 the ONLY shield-upkeep entry
    /// point. The kernel schedules it; the accumulator loop it replaced is gone. Same period as that
    /// grid, for the reason given on <see cref="PulseDotsNow"/>: regen carries in integer milli-HP and
    /// a finer drive would truncate it away. Runs AFTER the frame's drain dispatch and after the DoT
    /// pulse, so an expiring shield still absorbs its final frame's damage (shield-system-spec.md §2.6).
    ///
    /// <para>Deleting the accumulator also retires a real per-frame spike rather than documenting it:
    /// that loop was an <b>unbounded</b> <c>while</c> — after a 2 s hitch it ran 20 upkeep steps inside
    /// one Unity frame on the main thread. Now catch-up is paced by the kernel's drain budget.</para>
    /// </summary>
    public static void PulseShieldsNow()
    {
        Ensure();
        var runtime = Bag.ShieldGate?.Runtime;
        if (runtime == null || !runtime.HasPendingWork) return;
        runtime.Tick(_shieldTickNo++, 100, ShieldOwnerResolver);
        FlushShieldEvents(runtime);
    }

    /// <summary>Flush the shield event window (absorbed aggregates included) — shared by both paths.</summary>
    static void FlushShieldEvents(FusionRpg.Core.Combat.Shield.ShieldRuntime runtime)
    {
        if (runtime.DrainEvents(_shieldEventScratch) > 0)
        {
            foreach (var rec in _shieldEventScratch)
            {
                var ptr = rec.OwnerKey.StartsWith("entity:", StringComparison.Ordinal)
                    ? rec.OwnerKey.Substring("entity:".Length)
                    : rec.OwnerKey;
                try { Hud.ActorHudInvalidator.MarkDirtyFromOwnerKey(rec.OwnerKey); } catch { }
                GameHooks.Emit(rec.Kind, new Dictionary<string, object>
                {
                    ["targetPtr"] = ptr,
                    ["shieldId"] = rec.ShieldId,
                    ["element"] = rec.Element,
                    ["amount"] = rec.Amount,
                    ["hitCount"] = rec.HitCount,
                    ["hp"] = rec.Hp,
                    ["maxHp"] = rec.MaxHp
                });
                if (rec.Kind == FusionRpg.Core.Combat.Shield.ShieldEventKinds.Broken)
                {
                    try
                    {
                        VfxDirector.Play(new FusionRpg.Contracts.VfxCueDto
                        {
                            CueId = FusionRpg.Core.Vfx.VfxCueIds.ShieldBroken,
                            TargetPtr = ptr
                        });
                    }
                    catch { }
                }
            }

            _shieldEventScratch.Clear();
        }
    }

    static readonly List<FusionRpg.Core.Combat.Shield.ShieldEventRec> _shieldEventScratch = new();
}

/// <summary>Thin injector wrapper — mapping lives in <see cref="EffectEventAdapterCore"/>.</summary>
public static class EffectEventAdapter
{
    public static EffectEventDto? TryMap(string kind, Dictionary<string, object> p, long tick) =>
        EffectEventAdapterCore.TryMap(kind, p, tick, GameHooks.MatchKey);
}
