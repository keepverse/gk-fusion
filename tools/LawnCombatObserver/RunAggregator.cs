using System.Globalization;

namespace FusionRpg.Tools.LawnCombatObserver;

/// <summary>
/// Pure aggregation logic, deliberately factored out of <c>Program.cs</c> so it can be reasoned about
/// (and exercised) without any HTTP: folds newly-seen <c>/api/perf</c> windows into a running
/// <see cref="RunReport"/>. "Newly seen" matters because <c>GET /api/perf/recent</c> answers the whole
/// ring every time (last N windows total, not "since your last poll") — polling it repeatedly would
/// double-count a window's counters unless already-folded windows are remembered by their own
/// timestamp.
/// </summary>
public sealed class RunAggregator
{
    readonly HashSet<string> _seenWindowTimestamps = new(StringComparer.Ordinal);
    readonly int _hitSampleCap;

    readonly record struct DropCounters(long Overflow, long Depth, long DeathBudget, long DeathBudgetLivingTarget)
    {
        public static DropCounters Of(DrainStatsDto d) => new(d.DroppedOverflow, d.DroppedDepth, d.DroppedDeathBudget, d.DroppedDeathBudgetLivingTarget);
    }

    DropCounters? _dropBaseline;
    DateTime? _dropBaselineT;
    DropCounters _dropFirstInRun;
    DateTime? _dropFirstInRunT;
    DropCounters _dropLast;
    DateTime? _dropLastT;
    public RunReport Report { get; }

    public RunAggregator(string baseUrl, DateTime startedAtUtc, int requestedDurationSec, int hitSampleCap)
    {
        _hitSampleCap = hitSampleCap;
        Report = new RunReport
        {
            BaseUrl = baseUrl,
            StartedAtUtc = startedAtUtc.ToString("o"),
            RequestedDurationSec = requestedDurationSec,
            HitSampleCap = hitSampleCap
        };
    }

    /// <summary>Folds every window in <paramref name="windows"/> whose own timestamp falls inside
    /// [<paramref name="runStartUtc"/> - grace, <paramref name="nowUtc"/> + grace] and has not already
    /// been folded. A small grace window absorbs clock/serialization skew between this process and the
    /// server's — it never causes a window to be folded twice (the timestamp de-dupe set is exact).</summary>
    public void FoldNewWindows(IReadOnlyList<PerfWindowDto> windows, DateTime runStartUtc, DateTime nowUtc)
    {
        var grace = TimeSpan.FromSeconds(3);
        var lower = runStartUtc - grace;
        var upper = nowUtc + grace;

        foreach (var w in windows)
        {
            if (string.IsNullOrWhiteSpace(w.T)) continue;
            if (!_seenWindowTimestamps.Add(w.T)) continue; // already folded

            // "o"/round-trip formatted timestamps (what PerfProbe.SnapshotAndReset stamps) already
            // carry a 'Z' suffix, so RoundtripKind alone yields DateTimeKind.Utc — combining it with
            // AdjustToUniversal/AssumeUniversal throws (they are mutually exclusive per DateTime's own
            // contract), a real bug this tool's own live dry run against a live server caught.
            if (!DateTime.TryParse(w.T, null, DateTimeStyles.RoundtripKind, out var t))
                continue; // unparseable timestamp — skip rather than guess which run it belongs to
            if (t.Kind != DateTimeKind.Utc) t = t.ToUniversalTime();
            if (t < lower)
            {
                // The newest window before the run is the zero point for the drain's cumulative drop counters.
                if (w.Drain is { } before && (_dropBaselineT is null || t > _dropBaselineT))
                {
                    _dropBaselineT = t;
                    _dropBaseline = DropCounters.Of(before);
                }
                continue;
            }
            if (t > upper) continue; // belongs to a different run (a long-lived server)

            Report.WindowsObserved++;
            Report.WindowTotalMs += w.WindowMs;

            if (w.Sections is not null && w.Sections.TryGetValue("drain.tick", out var drainTick))
                Report.DrainTickTotalMs += drainTick.TotalMs;

            if (w.Drain is { } drain)
            {
                // lawn-combat-wire L-N9: EventDrain's drop counters are cumulative for the drain's lifetime
                // (1623 → 1777 across one live 300z run), so summing them per window inflated the run's drops ~14x.
                // The run's drops are the last observed value minus the zero point.
                var now = DropCounters.Of(drain);
                if (_dropFirstInRunT is null || t < _dropFirstInRunT) { _dropFirstInRunT = t; _dropFirstInRun = now; }
                if (_dropLastT is null || t > _dropLastT) { _dropLastT = t; _dropLast = now; }
                var zero = _dropBaseline ?? _dropFirstInRun;
                Report.DrainDroppedOverflow = Math.Max(0, _dropLast.Overflow - zero.Overflow);
                Report.DrainDroppedDepth = Math.Max(0, _dropLast.Depth - zero.Depth);
                Report.DrainDroppedDeathBudget = Math.Max(0, _dropLast.DeathBudget - zero.DeathBudget);
                Report.DrainDroppedDeathBudgetLivingTarget = Math.Max(0, _dropLast.DeathBudgetLivingTarget - zero.DeathBudgetLivingTarget);
                Report.DrainDropsZeroPoint = _dropBaseline is null ? "first-window-in-run" : "last-window-before-run";
                if (drain.Enabled) Report.DrainEnabledObservedAtLeastOnce = true;
                else Report.DrainDisabledObservedAtLeastOnce = true;
            }

            if (w.LawnCombatObserver is { } lco)
            {
                Report.TotalHits += lco.TotalHits;
                Report.TotalSwings += lco.TotalSwings;
                Report.ActionTriggers += lco.ActionTriggers;
                Report.StaminaSpent += lco.StaminaSpent;
                Report.RegenAccrued += lco.RegenAccrued;
                Report.ExhaustionEvents += lco.ExhaustionEvents;
                Report.RpgDeltaMergedHits += lco.RpgDeltaMergedHits;
                Report.RpgDeltaUnmergedRecords += lco.RpgDeltaUnmergedRecords;
                Report.RpgMisses += lco.RpgMisses;
                Report.ObserverDroppedRecords += lco.DroppedRecords;

                if (lco.RecentHits is { } hits)
                {
                    foreach (var h in hits)
                    {
                        if (Report.HitSample.Count >= _hitSampleCap) break;
                        Report.HitSample.Add(h);
                    }
                }
            }
        }
    }

    public void RecordInjectorSessionActiveCheck(bool? sessionActive)
    {
        if (sessionActive is null) return;
        Report.InjectorSessionActiveChecks++;
        Report.InjectorSessionActiveEverTrue = (Report.InjectorSessionActiveEverTrue ?? false) || sessionActive.Value;
    }

    public void RecordServerSessionActiveCheck(bool? sessionActive)
    {
        if (sessionActive is null) return;
        Report.ServerSessionActiveEverTrue = (Report.ServerSessionActiveEverTrue ?? false) || sessionActive.Value;
    }

    public void Finish(DateTime endedAtUtc)
    {
        Report.EndedAtUtc = endedAtUtc.ToString("o");
        if (Report.WindowsObserved == 0)
        {
            Report.NoData = true;
            Report.NoDataReason =
                "no /api/perf windows landed in this run's own time window — the injector never " +
                "posted (not connected, not running, PerfProbe disabled, or the run was shorter than " +
                "one ~5s reporting interval). This is NOT the same as \"zero hits\".";
        }
    }
}
