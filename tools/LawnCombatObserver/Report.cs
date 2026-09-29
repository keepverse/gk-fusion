namespace FusionRpg.Tools.LawnCombatObserver;

/// <summary>Console summary — the run FILE (written separately, see <c>Program.cs</c>) is the
/// machine-readable artifact a gate diffs; this is only a human-readable echo of the same numbers,
/// never the other way around.</summary>
public static class Report
{
    public static void Print(RunReport r)
    {
        Console.WriteLine();
        Console.WriteLine("=== lawn-combat-observer run ===");
        Console.WriteLine($"baseUrl={r.BaseUrl} start={r.StartedAtUtc} end={r.EndedAtUtc} " +
                           $"requestedDurationSec={r.RequestedDurationSec}");
        Console.WriteLine();

        if (r.NoData)
        {
            Console.WriteLine("NO DATA — " + r.NoDataReason);
            Console.WriteLine();
            return;
        }

        Console.WriteLine($"windowsObserved={r.WindowsObserved}");
        Console.WriteLine();
        Console.WriteLine("--- non-perturbation proof (EventDrainHost.Active throughout) ---");
        Console.WriteLine($"injector sessionActive checks={r.InjectorSessionActiveChecks} " +
                           $"everTrue={Fmt(r.InjectorSessionActiveEverTrue)}");
        Console.WriteLine($"server   sessionActive everTrue={Fmt(r.ServerSessionActiveEverTrue)}");
        Console.WriteLine($"drain.enabled observed true={r.DrainEnabledObservedAtLeastOnce} " +
                           $"observed false={r.DrainDisabledObservedAtLeastOnce}");
        Console.WriteLine($"EventDrainActiveProvenThroughout={r.EventDrainActiveProvenThroughout}");
        Console.WriteLine();

        Console.WriteLine("--- aggregate (exact, never sampled) ---");
        Console.WriteLine($"totalHits={r.TotalHits} totalSwings={r.TotalSwings} " +
                           $"actionTriggers={r.ActionTriggers}");
        Console.WriteLine($"staminaSpent={r.StaminaSpent} regenAccrued={r.RegenAccrued} " +
                           $"exhaustionEvents={r.ExhaustionEvents}");
        Console.WriteLine($"rpgDeltaMergedHits={r.RpgDeltaMergedHits} rpgDeltaUnmergedRecords={r.RpgDeltaUnmergedRecords} rpgMisses={r.RpgMisses} " +
                           $"observerDroppedRecords={r.ObserverDroppedRecords}");
        Console.WriteLine($"drain droppedOverflow={r.DrainDroppedOverflow} droppedDepth={r.DrainDroppedDepth} " +
                           $"droppedDeathBudget={r.DrainDroppedDeathBudget}");
        Console.WriteLine();

        Console.WriteLine("--- frame share ---");
        Console.WriteLine($"drainTickTotalMs={r.DrainTickTotalMs:0.###} windowTotalMs={r.WindowTotalMs:0.###} " +
                           $"drainTickFrameSharePercent={r.DrainTickFrameSharePercent}%");
        Console.WriteLine();

        Console.WriteLine("--- headline ---");
        Console.WriteLine($"VanillaHitsObserved={r.VanillaHitsObserved} BaselineNoRpgDeltaYet={r.BaselineNoRpgDeltaYet}");
        Console.WriteLine();

        Console.WriteLine($"--- hit sample ({r.HitSample.Count}/{r.TotalHits}" +
                           (r.HitSampleTruncated ? ", truncated" : "") + ") ---");
        foreach (var h in r.HitSample.Take(20))
        {
            Console.WriteLine(
                $"  [{h.Seq}] swing={h.SwingId} {h.AttackerSide} {h.AttackerPtr} -> {h.VictimPtr} " +
                $"vanilla={h.VanillaAmount} rpgDelta={h.RpgDelta} rpgObserved={h.RpgDeltaObserved} " +
                $"elem={h.AttackerElement}/{h.VictimElement} matchup={h.MatchupRelation} outcome={h.RpgOutcome}");
        }
        if (r.HitSample.Count > 20)
            Console.WriteLine($"  ... ({r.HitSample.Count - 20} more in the run file)");
    }

    static string Fmt(bool? v) => v is null ? "unknown" : v.Value.ToString();
}
