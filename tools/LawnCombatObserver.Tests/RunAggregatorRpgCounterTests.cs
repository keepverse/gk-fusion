using FusionRpg.Tools.LawnCombatObserver;
using Xunit;

namespace FusionRpg.Tools.LawnCombatObserver.Tests;

/// <summary>lawn-combat-wire L-N7: RPG observations that merged into no vanilla record still mean the feature produced
/// RPG deltas, and misses are summed per run.</summary>
public class RunAggregatorRpgCounterTests
{
    static readonly DateTime Start = new(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Unmerged_records_and_misses_sum_and_end_the_no_rpg_baseline()
    {
        var agg = new RunAggregator("http://x", Start, 30, 10);
        agg.FoldNewWindows(new[]
        {
            new PerfWindowDto { T = Start.AddSeconds(5).ToString("o"), WindowMs = 5000, LawnCombatObserver = new LawnCombatObserverWindowDto { TotalHits = 3, RpgDeltaUnmergedRecords = 2, RpgMisses = 1 } },
            new PerfWindowDto { T = Start.AddSeconds(10).ToString("o"), WindowMs = 5000, LawnCombatObserver = new LawnCombatObserverWindowDto { TotalHits = 1, RpgDeltaUnmergedRecords = 1 } },
        }, Start, Start.AddSeconds(12));

        Assert.Equal(0, agg.Report.RpgDeltaMergedHits);
        Assert.Equal(3, agg.Report.RpgDeltaUnmergedRecords);
        Assert.Equal(1, agg.Report.RpgMisses);
        Assert.False(agg.Report.BaselineNoRpgDeltaYet);
    }
}
