using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Diagnostics;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// Injector side of the battle-timeline kernel — T13 / B25. Spec:
/// <c>docs/architecture/battle/spec-injector-kernel-drive.md</c>.
///
/// <para><b>This file is an adapter and deliberately nothing else.</b> All the logic worth testing
/// lives in <see cref="TimelineDrive"/> in Core, because CI runs ten test projects and never builds
/// <c>gk-fusion/src/FusionRpg.Injector</c> — anything placed here is untested by CI forever. What is left is
/// what only the injector can do: read Unity's frame delta, own the board lifecycle, and name a real
/// clock.</para>
///
/// <para><b>One kernel per board</b> (owner decision, 2026-08-31): created at <c>board.start</c>, torn
/// down at <c>board.end</c>. Nothing shipped needs a timeline that outlives a board, so this is the
/// cheap default rather than a researched conclusion — and it means a stale queue can never survive
/// into the next match.</para>
///
/// <para><b>The clock is FULLY SCALED</b> (owner decision, 2026-09-04, reversing 2026-08-31) — it
/// follows <c>Time.timeScale</c>, so it stops on pause <b>and accelerates on fast-forward</b>.
/// ⛔ <b>The acceleration is chosen, not overlooked.</b> <c>CheatActions.cs:28</c> allows up to
/// <b>10×</b>, and <c>event-pipeline-v2-ssot.md</c> records that unscaled was originally picked
/// precisely so game speed could not multiply DoTs. The owner was shown that consequence and
/// confirmed it. <b>Do not "fix" a 10× DoT as a bug</b> — see <c>decisions.md</c>, <i>Battle engine
/// open questions (2026-09-04)</i>, item (4). This is why the substitution is <i>not</i>
/// byte-identical to the two grids it replaces, and why B26 is the program's one golden-mover.</para>
///
/// <para><b>Scope of that decision is this host only</b> (item (5)). Core has no
/// <c>Time.timeScale</c> and <see cref="SimulationClock"/> cannot read a wall clock at all, so battle
/// and expedition resolution stay virtual-time and instantaneous. The two clocks are separate on
/// purpose.</para>
///
/// <para>B25 built the drive; <b>B26</b> moved the shield and DoT grids onto it; <b>BCU8.3</b> deleted
/// the legacy accumulators and the <c>FUSIONRPG_KERNEL_GRIDS</c> kill switch they sat behind, once
/// B27's live run had measured the kernel safe. There is one scheduler in the injector now — no
/// accumulator-plus-period grid survives in <c>EffectRuntime.cs</c>, which is T13's success criterion
/// 1.</para>
/// </summary>
public static class KernelDriveHost
{
    // Structural time-unit conversions, not balance values: the Core timeline stores milliseconds
    // while the injector offers measured frame time in integer microseconds.
    const long MicrosecondsPerSecond = 1_000_000;

    /// <summary>
    /// Per-frame runtime cap — structural, not a balance number and not a progression ceiling
    /// (<c>tunables-ssot.md</c> §1 lists per-frame caps as exempt, and requires saying so).
    ///
    /// <para>Shaped after <c>EventDrainHost</c>'s own budget (a fraction of measured frame time,
    /// clamped) but sized against the kernel's <b>0.15 ms</b> share from
    /// <c>spec-kernel-performance.md</c> §"Budgets", not the event drain's 2 ms. At a 16.6 ms frame,
    /// 1 % is 0.166 ms and the clamp brings it to exactly the specced slice.</para>
    /// </summary>
    const double BudgetFrameFraction = 0.01;
    const double BudgetMinSeconds = 0.00005;   // 0.05 ms — a floor so a fast frame still drains something
    const double BudgetMaxSeconds = 0.00015;   // 0.15 ms — the kernel's own share of the injector budget

    /// <summary>
    /// The upkeep period, unchanged from the two grids this replaces — structural, not tunable.
    ///
    /// <para><b>It stays 100 ms deliberately.</b> B26 is a substitution, not a redesign: shield regen
    /// accumulates in integer milli-HP, and driving it at 1 ms granularity truncates small regen
    /// rates to zero. Only the <i>scheduling</i> moves onto the kernel; the granularity does not.</para>
    /// </summary>
    const long UpkeepPeriodTicks = 100;

    /// <summary>Kinds this host schedules. Opaque ints to the queue by design — the scheduler never
    /// interprets them, which is what keeps it testable with no game attached.</summary>
    const int KindDotPulse = 1;
    const int KindShieldUpkeep = 2;
    /// <summary>`lawn-combat-wire` T12b (spec-basic-attack-cost.md, wire 3: "regen as a third kernel
    /// kind" — the inert line this wire fixes was exactly "KernelDriveHost.cs:69-70 has KindDotPulse
    /// and KindShieldUpkeep; regen is not among them"). NOT load-bearing for cost-charging
    /// correctness — <c>ActorResourcePools</c> already resolves regen lazily off
    /// <c>NowTicks</c> with no periodic tick required. This kind exists to give the lawn's own regen
    /// a real, bounded settle point on the SAME 100 ms grid DoT/shield upkeep already use, producing
    /// the observer's (T0) "regen accrued" telemetry — see <c>LawnBasicAttackCostCharger.KernelTick</c>.</summary>
    const int KindResourceTick = 3;

    static readonly object Gate = new();
    static SimulationClock? _clock;
    static EventQueue? _queue;
    static TimelineDrive? _drive;

    /// <summary>True while a board is live and the kernel is ticking.</summary>
    public static bool Active { get { lock (Gate) return _drive != null; } }

    /// <summary>
    /// The live queue, for whoever schedules onto it (B26's shield and DoT events). Null off-board —
    /// callers must handle that rather than assume a kernel exists, because a scheduling call can
    /// legitimately arrive from a hook that fired before <c>board.start</c> was processed.
    /// </summary>
    public static EventQueue? Queue { get { lock (Gate) return _queue; } }

    /// <summary>Simulated milliseconds since this board began. 0 off-board.</summary>
    public static long NowTicks { get { lock (Gate) return _clock?.Now ?? 0; } }

    /// <summary>Whole active-match milliseconds since board start. Paused time is excluded.</summary>
    public static long NowMilliseconds => NowTicks;

    /// <summary>Start a fresh timeline. Called from <c>MatchHost.Apply</c> on <c>board.start</c>.</summary>
    public static void BeginBoard()
    {
        lock (Gate)
        {
            _clock = new SimulationClock();
            _queue = new EventQueue(expectedEvents: 256);
            _drive = new TimelineDrive(_clock, _queue, Dispatch, System.Diagnostics.Stopwatch.GetTimestamp);

            // Order matters and is set here, once. The queue sorts by (DueTick, Seq), so scheduling
            // the DoT pulse FIRST makes it fire before shield upkeep at every shared tick — which is
            // exactly the frame order the deleted grids had (InjectorLoop: drain -> DoT -> shields),
            // and which shield-system-spec.md 2.6 requires so an expiring shield still absorbs its
            // final frame's damage.
            _queue.Schedule(UpkeepPeriodTicks, "match", KindDotPulse, 0);
            _queue.Schedule(UpkeepPeriodTicks, "match", KindShieldUpkeep, 0);
            // T12b: scheduled unconditionally (matching the two kinds above) -- Dispatch's own kill
            // switch check (LawnBasicAttackFeature.Enabled) is what keeps this a no-op while the
            // feature is off.
            _queue.Schedule(UpkeepPeriodTicks, "match", KindResourceTick, 0);
        }
    }

    /// <summary>Drop the timeline. Called from <c>MatchHost.Apply</c> on <c>board.end</c>/<c>match.result</c>.</summary>
    public static void EndBoard()
    {
        lock (Gate)
        {
            _drive = null;
            _queue = null;
            _clock = null;
        }
    }

    /// <summary>
    /// One frame of kernel. Called from <c>InjectorLoop.Tick</c>.
    ///
    /// <para><b>Two deltas, and the split is the point.</b> <paramref name="scaledDeltaTime"/> is how
    /// much <i>simulated</i> time this frame bought — <c>unscaledDeltaTime × Time.timeScale</c>, so it
    /// is 0 while paused and 10× at maximum fast-forward. <paramref name="realFrameSeconds"/> is how
    /// long the frame actually took, and the drain budget is derived from <b>that</b>: the budget
    /// bounds wall-clock work on the main thread, so scaling it would hand a slow-motion frame a
    /// smaller budget than the real time it has, for no reason. Simulation follows the game's clock;
    /// the budget follows the machine's.</para>
    ///
    /// <para><b>The float→integer conversion happens here and only here.</b> Unity hands us a
    /// <c>float</c>; Core's clock is an integer-tick clock by design, so the conversion to whole
    /// microseconds is done once at this boundary. Rounding rather than truncating matters: a
    /// truncation biases every frame downward, which is the exact drift class the carry exists to
    /// remove.</para>
    ///
    /// <para><b>A huge delta is deliberately not clamped.</b> After a level load the frame delta can
    /// be seconds; clamping it would silently lose simulated time. Offering it whole makes a large
    /// backlog due at once, which the bounded drain then spreads across frames in unchanged order —
    /// the designed behaviour, not an edge case to defend against. This is also why the caller
    /// multiplies by <c>Time.timeScale</c> rather than passing <c>Time.deltaTime</c>, which Unity
    /// clamps at <c>Time.maximumDeltaTime</c> and would silently drop simulated time after a hitch.</para>
    /// </summary>
    public static void Tick(float scaledDeltaTime, float realFrameSeconds)
    {
        TimelineDrive? drive;
        lock (Gate) drive = _drive;
        if (drive == null) return;                       // off-board: nothing to advance

        // Paused (timeScale 0) lands here and advances nothing — which is the whole of "it stops on
        // pause". `!(x > 0)` rather than `x <= 0` so a NaN scale is refused too.
        if (!(scaledDeltaTime > 0f)) return;

        // solid-remediation T4.14 (D15): the lawn's effect clock — status expiry, status ICD and
        // `NextPulse` — advances by the SAME scaled delta this kernel advances by, so "has a pulse
        // come due?" and "has this status expired?" read one number. BCU8.3 moved it here from
        // `EffectRuntime.TickDots`, whose advance was already unreachable on a live board (B26 gated
        // that accumulator behind this kernel) — the D15 fix had been inert on every board since it
        // landed. Status instances exist only while a board does, so advancing only inside this
        // guarded tick loses nothing.
        EffectRuntime.AdvanceEffectClock(scaledDeltaTime);

        using var _perf = PerfProbe.Measure(PerfSection.KernelTick);
        var micros = (long)Math.Round((double)scaledDeltaTime * MicrosecondsPerSecond);
        drive.Tick(micros, BudgetTicks(realFrameSeconds));
    }

    static long BudgetTicks(float frameSeconds)
    {
        var seconds = Math.Clamp(frameSeconds * BudgetFrameFraction, BudgetMinSeconds, BudgetMaxSeconds);
        // Split from the cast on purpose (audit-overflow.py A4): this is a double-domain timing
        // conversion of an already-clamped tiny value (<= 0.00015s * Stopwatch.Frequency), never an
        // integer multiply that could overflow before widening — the A4 pattern the split avoids.
        var ticks = seconds * System.Diagnostics.Stopwatch.Frequency;
        return (long)ticks;
    }

    /// <summary>
    /// Fires one due event: re-arm it, then run the grid kind it names.
    /// </summary>
    static void Dispatch(ScheduledEvent e)
    {
        using var _perf = PerfProbe.Measure(PerfSection.KernelDrain);

        // Re-arm FIRST, off the event's own due tick rather than "now". Re-arming off `now` would
        // let every deferred drain push the next pulse further out, so a stuttering frame would
        // permanently slow DoT cadence instead of merely delaying it -- the drift the whole
        // carry-corrected clock exists to prevent.
        //
        // live-qa 2026-09-20 (B27 probe): PerfSection.KernelSchedule existed since T13/B25 but no
        // call site ever wrapped anything in it, so the section was permanently empty in every perf
        // report -- found while proving B27's own "the sections exist (kernel.tick/kernel.drain/
        // kernel.schedule)" checklist line against a live 200+-entity board. Nested inside the
        // KernelDrain scope above, same convention board.capture already uses nested inside
        // effect.onCapture: each section still records to its own Interlocked counters.
        var next = e.DueTick + UpkeepPeriodTicks;
        using (PerfProbe.Measure(PerfSection.KernelSchedule))
        {
            lock (Gate) _queue?.Schedule(next, e.OwnerKey, e.Kind, e.Tag);
        }

        // T12b: the regen kind has its OWN kill switch (LawnBasicAttackFeature, checked inside
        // LawnBasicAttackCostCharger.KernelTick itself), so it is dispatched separately and wrapped
        // the same way the two grid kinds are: a throwing pulse must not kill the drive.
        if (e.Kind == KindResourceTick)
        {
            try { LawnBasicAttackCostCharger.KernelTick(); } catch { }
            return;
        }

        try
        {
            switch (e.Kind)
            {
                case KindDotPulse: EffectRuntime.PulseDotsNow(); break;
                case KindShieldUpkeep: EffectRuntime.PulseShieldsNow(); break;
            }
        }
        catch
        {
            // A throwing pulse must not kill the drive. The grids it replaces were each wrapped in
            // their own try/catch at the InjectorLoop call site; keeping that behaviour is part of
            // "identical to the grids they replace".
        }
    }
}
