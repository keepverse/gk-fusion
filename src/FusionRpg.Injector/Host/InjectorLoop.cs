using FusionRpg.Core.Diagnostics;
using FusionRpg.Injector.Effects;
using FusionRpg.Injector.Fx;
using UnityEngine;

namespace FusionRpg.Injector.Host;

/// <summary>
/// Per-frame injector work. Called from BepInEx MonoBehaviour.Update or MelonMod.OnUpdate.
/// </summary>
public static class InjectorLoop
{
    static float _hb;
    static float _cmdPull;
    static float _poll;
    static float _cheatPush;
    static float _startDelay;
    static float _perf;
    static float _gateCounters;
    static float _liveState;
    static bool _started;

    /// <summary>Reset timers (e.g. after host reload). Normally not needed.</summary>
    public static void Reset()
    {
        _hb = 0;
        _cmdPull = 0;
        _poll = 0;
        _cheatPush = 0;
        _startDelay = 0;
        _perf = 0;
        _gateCounters = 0;
        _liveState = 0;
        _started = false;
    }

    public static void Tick(float unscaledDeltaTime)
    {
        PerfProbe.RecordFrame(unscaledDeltaTime);
        using var _perfScope = PerfProbe.Measure(PerfSection.LoopTick);
        var client = RpgHost.Client;
        if (!_started)
        {
            _startDelay += unscaledDeltaTime;
            if (_startDelay >= 2f)
            {
                _started = true;
                ApplyFpsCap();
                ApplyEventPipelineMode();
                ApplyLawnMoveMode();
                _ = client?.StartAsync();
            }
        }
        client?.TryFlush();
        try { using (PerfProbe.Measure(PerfSection.PumpMain)) GameHooks.PumpMainThread(); } catch { }
        try { using (PerfProbe.Measure(PerfSection.PollBoard)) GameHooks.PollBoard(); } catch { }
        try { CheatCommandRunner.Drain(); } catch { }
        try { ScreenshotRunner.TickFallback(); } catch { }
        try { DebugActions.TickPendingSkipSetup(); } catch { }
        try { DebugLeaveBoard.Tick(); } catch { }
        try { CheatUiActions.Drain(); }
        catch (Exception ex) { RpgHost.Log.Error("CheatUiActions: " + ex); }
        try
        {
            // Dirty means dirty. The second, source-enumerating veto that used to sit here
            // (`ShouldPushScalesOnDirty`: cheat doc revision / PvzStats revision / Tab A scales)
            // silently discarded every OTHER contributor's change -- a commander reallocation set the
            // dirty flag and was then vetoed, so living entities never re-resolved (owner-observed
            // live 2026-08-30). Deciding whether a re-resolve is WORTH writing is EntityApply's job
            // now, and it answers by comparing values (EntityFinal.DiffersFrom), so a reapply that
            // changes nothing writes nothing. `Invalidate` is edge-triggered from 6 discrete state
            // changes, never per-frame, so this is one board pass per real change.
            if (CheatState.Stats.ConsumeDirty(out _))
            {
                CheatActions.ReapplyLivingFromStats();
                CheatState.MarkAppliedRevision();
            }
        }
        catch { }
        try { using (PerfProbe.Measure(PerfSection.CheatContinuous)) CheatActions.TickContinuous(); } catch { }
        try { using (PerfProbe.Measure(PerfSection.CheatAutoCollect)) CheatActions.AutoCollectTick(); } catch { }
        try { using (PerfProbe.Measure(PerfSection.VfxTick)) VfxDirector.Tick(unscaledDeltaTime); } catch { }
        try { Hud.OverlayInput.Tick(); } catch { }
        try { Hud.OverlaySwitch.Tick(); } catch { }
#if FUSIONRPG_MELON
        // The rift gate parents to the CANVAS ROOT, not the menu, so it does not die with the menu.
        // Its clearance is this liveness read rather than a BaseMenu.OnHide/OnExit patch — those are
        // virtual and crash boot (see InjectorBootstrap.IsRiftMenuVirtualPatch).
        try { Hud.RiftMenuTombstone.Tick(); } catch { }
#endif
        // v2 drain before the kernel's DoT pulse so the pulse shares the drain's board freeze and
        // merge into the same funnel window (plan Task 10).
        try { EventDrainHost.Tick(unscaledDeltaTime); } catch { }
        // A-M2 lawn-reposition — same record-then-drain slot as EventDrainHost above. Default off
        // (spec-lawn-reposition.md §6 hazard 4, ships knowingly inert); a no-op while
        // MoveDrainHost.Enabled is false or nothing has called TryRecordMove.
        try { MoveDrainHost.Tick(unscaledDeltaTime); } catch { }
        // lawn-combat-wire T10 — same record-then-drain slot again: every plant.spawn/zombie.spawn
        // this frame queued a ptr in MatchHost.Apply; this is the one place they all get resolved and
        // bound, after every spawn hook for the frame has already fired.
        try { LawnBasicAttackGrantBinder.Tick(); } catch { }
        // combat-ai `lawn-cast-trigger` (CAI4.8): one more tick call beside the grant binder's, so an
        // order admitted by this frame's `CheatCommandRunner.Drain` (which runs earlier in Tick) is
        // visible to this frame's decision slot. Default-off: the switch releases everything it holds.
        try { LawnDecisionHost.Tick(KernelDriveHost.NowTicks, Time.frameCount, InjectorBoardSnapshot.Capture()); } catch { }
        // battle-timeline T13 — the kernel drives DoT and shield upkeep as scheduled 100 ms events,
        // in the same slot and the same order the two accumulator grids used to occupy
        // (drain -> DoT -> shields; shield-system-spec.md §2.6). Same period, same work: only the
        // scheduling moved, which is what makes this a substitution rather than a redesign. The
        // accumulators themselves are deleted (BCU8.3), so there is one scheduler, not two paths.
        //
        // The kernel clock is FULLY SCALED (decisions.md, "Battle engine open questions
        // (2026-09-04)", item 4): it stops on pause and accelerates on fast-forward, up to the 10x
        // CheatActions.cs allows. That acceleration is chosen, not overlooked -- do not "fix" a 10x
        // DoT as a bug. `unscaledDeltaTime * Time.timeScale` rather than `Time.deltaTime` because
        // Unity clamps the latter at Time.maximumDeltaTime, which would silently lose simulated time
        // after a level-load hitch -- the exact loss the carry-corrected clock exists to prevent.
        // The second argument stays REAL frame time: the drain budget bounds wall-clock work on the
        // main thread, so it must not scale with the game's clock.
        //
        // BCU8.3: the `FUSIONRPG_KERNEL_GRIDS=0` fallback that called `EffectRuntime.TickDots` /
        // `TickShields` here is gone, with B27's live run behind it. The kernel is the only DoT and
        // shield scheduler, and `KernelDriveHost.Tick` is also what advances the lawn's effect clock
        // (status expiry reads it), so this call is the whole injector timing surface.
        try { KernelDriveHost.Tick(unscaledDeltaTime * Time.timeScale, unscaledDeltaTime); } catch { }
        try { Hud.ActorHudCache.ReconcileDirty(); } catch { }
        _hb += unscaledDeltaTime;
        if (_hb >= 2f)
        {
            _hb = 0;
            _ = client?.HeartbeatAsync();
        }
        _cmdPull += unscaledDeltaTime;
        if (_cmdPull >= 0.25f)
        {
            _cmdPull = 0;
            _ = client?.PullPendingCommandsAsync();
        }
        _perf += unscaledDeltaTime;
        if (_perf >= PerfReporter.IntervalSeconds)
        {
            _perf = 0;
            try { PerfReporter.Flush(client); } catch { }
        }
        // passive-tree-todo.md G6 (spec-gate-counters.md §4.3) -- the timer half of the flush contract.
        // Rides PerfReporter's OWN already-configured cadence rather than a second tuning load: §4.3
        // names `gateCounters.flushIntervalMs` (default 5000ms) as "the window PerfProbe already uses"
        // by design, and PassiveTreeTuningHub is never configured in this process (server-only) --
        // reusing PerfReporter.IntervalSeconds gets the same number without adding one.
        _gateCounters += unscaledDeltaTime;
        if (_gateCounters >= PerfReporter.IntervalSeconds)
        {
            _gateCounters = 0;
            try { GateCounterHost.Flush(client); } catch { }
        }
        _cheatPush += unscaledDeltaTime;
        if (_cheatPush >= 3f)
        {
            _cheatPush = 0;
            try { _ = client?.PushCheatSnapshotAsync(); } catch { }
        }
        // CG-A4b: Hot sheet live bag — same slow cadence as cheat mirror; Bound UniqueActors only.
        // Path documented on ActorLiveStatePush (no prior dump ingest for GetShields-shaped layers).
        _liveState += unscaledDeltaTime;
        if (_liveState >= 3f)
        {
            _liveState = 0;
            try { ActorLiveStatePush.FlushBound(client); } catch { }
        }
        if (client is { SignalROk: false })
        {
            _poll += unscaledDeltaTime;
            if (_poll >= 5f)
            {
                _poll = 0;
                _ = client.RefreshStatsAsync();
            }
        }
    }

    /// <summary>
    /// Frame cap — spec decision #3: default 60 for headroom; FUSIONRPG_FPS_CAP=0 uncaps,
    /// any other value overrides.
    /// </summary>
    static void ApplyFpsCap()
    {
        try
        {
            var s = Environment.GetEnvironmentVariable("FUSIONRPG_FPS_CAP");
            var cap = 60;
            if (int.TryParse(s, out var parsed))
                cap = parsed;
            if (cap <= 0)
            {
                RpgHost.Log.Info("[perf] fps uncapped (FUSIONRPG_FPS_CAP=0)");
                return;
            }
            QualitySettings.vSyncCount = 0; // targetFrameRate is ignored while vsync is on
            Application.targetFrameRate = cap;
            RpgHost.Log.Info($"[perf] fps capped at {cap} (default 60; FUSIONRPG_FPS_CAP overrides, 0 = uncapped)");
        }
        catch { }
    }

    /// <summary>v2 record-then-drain is on by default; FUSIONRPG_EVENT_V2=0 reverts to the legacy inline pipeline.</summary>
    static void ApplyEventPipelineMode()
    {
        try
        {
            var off = string.Equals(Environment.GetEnvironmentVariable("FUSIONRPG_EVENT_V2"), "0", StringComparison.Ordinal);
            EventDrainHost.Enabled = !off;
            RpgHost.Log.Info("[perf] event pipeline v2 " + (off ? "OFF (legacy inline)" : "ON (record-then-drain)"));
        }
        catch { }
    }

    /// <summary>
    /// A-M2 lawn-reposition ships default-off (spec-lawn-reposition.md §6 hazard 4, "SHIPS
    /// KNOWINGLY INERT" — the production caller does not exist yet). Unlike
    /// <see cref="ApplyEventPipelineMode"/>'s default-ON-unless-killed shape, this only ever forces
    /// the switch OFF: FUSIONRPG_LAWN_MOVE=0 is a true kill switch that wins over any future default
    /// flip or debug toggle, but its absence never turns the feature on by itself — the static
    /// default (false) is what "ships inert" means, and nothing in this method may override that.
    /// </summary>
    static void ApplyLawnMoveMode()
    {
        try
        {
            var off = string.Equals(Environment.GetEnvironmentVariable("FUSIONRPG_LAWN_MOVE"), "0", StringComparison.Ordinal);
            if (off) MoveDrainHost.Enabled = false;
            RpgHost.Log.Info("[perf] lawn move drain " + (MoveDrainHost.Enabled ? "ON" : "OFF (default; FUSIONRPG_LAWN_MOVE=0 forces off)"));
        }
        catch { }
    }

    /// <summary>Convenience when Unity Time is available (BepInEx RpgLoop).</summary>
    public static void TickFromUnity() => Tick(Time.unscaledDeltaTime);
}
