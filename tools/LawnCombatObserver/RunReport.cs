namespace FusionRpg.Tools.LawnCombatObserver;

/// <summary>
/// The machine-readable run file this tool exists to produce (Task 0 acceptance: "a run file... so a
/// gate can diff two runs"). Every field is either an exact aggregate (never capped, never sampled) or
/// explicitly labeled as a bounded sample — never silently one or the other.
/// </summary>
public sealed class RunReport
{
    public string BaseUrl { get; set; } = "";
    public string StartedAtUtc { get; set; } = "";
    public string EndedAtUtc { get; set; } = "";
    public int RequestedDurationSec { get; set; }

    // ---- "no data" vs "zero" — the acceptance's own load-bearing distinction ----
    /// <summary>True only when NO <c>/api/perf</c> window landed during this run's own time window —
    /// meaning the injector never posted anything (not connected, not running, or the run was shorter
    /// than one reporting interval). A silent empty run must never be reported as "zero hits".</summary>
    public bool NoData { get; set; }
    public string? NoDataReason { get; set; }
    public int WindowsObserved { get; set; }

    // ---- the non-perturbation proof: EventDrainHost.Active was true throughout ----
    /// <summary>The injector's OWN <c>DebugRuntime.SessionActive</c>, read back from its own
    /// <c>debug.snapshot</c> self-report (never assumed, never merely "we didn't call session/start").
    /// <c>null</c> = could not be confirmed this run (e.g. injector never answered) — never treated as
    /// "false" by omission.</summary>
    public bool? InjectorSessionActiveEverTrue { get; set; }
    public int InjectorSessionActiveChecks { get; set; }
    /// <summary>Cross-check: the server's own in-memory session mirror (<c>GET /api/debug/session</c>,
    /// no injector relay). Corroborates, does not replace, the injector's own answer.</summary>
    public bool? ServerSessionActiveEverTrue { get; set; }
    public bool DrainEnabledObservedAtLeastOnce { get; set; }
    public bool DrainDisabledObservedAtLeastOnce { get; set; }

    /// <summary>The mechanical proof this run's own collection path never perturbed
    /// <c>EventDrainHost.Active</c> (<c>Enabled &amp;&amp; !SessionActive</c>): read, not claimed.</summary>
    public bool EventDrainActiveProvenThroughout =>
        !NoData
        && InjectorSessionActiveEverTrue == false
        && ServerSessionActiveEverTrue != true
        && DrainEnabledObservedAtLeastOnce
        && !DrainDisabledObservedAtLeastOnce;

    // ---- aggregate counters — exact, summed across every window observed this run ----
    public long TotalHits { get; set; }
    public long TotalSwings { get; set; }
    public long ActionTriggers { get; set; }
    public long StaminaSpent { get; set; }
    public long RegenAccrued { get; set; }
    public long ExhaustionEvents { get; set; }
    public long RpgDeltaMergedHits { get; set; }
    /// <summary>RPG-delta records with no vanilla record to merge into (L-N7: every one, live, until swing ids join).</summary>
    public long RpgDeltaUnmergedRecords { get; set; }
    /// <summary>Overlay outcomes that missed — their delta is 0 by construction, not a zero-damage hit.</summary>
    public long RpgMisses { get; set; }
    /// <summary>This tool's OWN ring overflow counter (bounded per-window sample) — D9-shaped: a real
    /// count, never a silent drop.</summary>
    public long ObserverDroppedRecords { get; set; }
    /// <summary>D9's own drop counters, folded in from the already-shipped
    /// <c>EventDrainHost.SnapshotStats()</c> — a SEPARATE proof from <see cref="ObserverDroppedRecords"/>.</summary>
    public long DrainDroppedOverflow { get; set; }
    public long DrainDroppedDepth { get; set; }
    public long DrainDroppedDeathBudget { get; set; }
    /// <summary>Death-budget sheds whose victim was still alive (the dying entity was the attacker) — lost RPG deltas.</summary>
    public long DrainDroppedDeathBudgetLivingTarget { get; set; }

    /// <summary>The drain's drop counters are cumulative, so the three values above are differences from a zero
    /// point: <c>last-window-before-run</c> when the ring still held one, else <c>first-window-in-run</c> (drops
    /// inside that first window are then not counted).</summary>
    public string? DrainDropsZeroPoint { get; set; }

    // ---- frame share (perf sample under load) ----
    public double DrainTickTotalMs { get; set; }
    public double WindowTotalMs { get; set; }
    public double DrainTickFrameSharePercent => WindowTotalMs > 0 ? Math.Round(DrainTickTotalMs / WindowTotalMs * 100.0, 3) : 0;

    // ---- bounded sample of individual hit records (never the whole run — see MaxHitSample) ----
    public int HitSampleCap { get; set; }
    public bool HitSampleTruncated => TotalHits > HitSample.Count;
    public List<LawnCombatHitDto> HitSample { get; set; } = new();

    // ---- headline readings, derived from the exact aggregates above (never from the sample) ----
    public bool VanillaHitsObserved => TotalHits > 0;
    /// <summary>True exactly when this run's shipped-configuration baseline holds: real vanilla hits
    /// happened, and not one of them ever had a real overlay breakdown merged in (the feature does not
    /// exist yet). Once `basic-attack-grant`/T10 lands this flips to false, on its own, with no change
    /// to this tool.</summary>
    public bool BaselineNoRpgDeltaYet => RpgDeltaMergedHits + RpgDeltaUnmergedRecords == 0;
}
