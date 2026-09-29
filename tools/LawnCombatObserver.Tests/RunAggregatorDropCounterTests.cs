using FusionRpg.Tools.LawnCombatObserver;
using Xunit;

namespace FusionRpg.Tools.LawnCombatObserver.Tests;

/// <summary>lawn-combat-wire L-N9: <c>EventDrain</c>'s drop counters are cumulative for the drain's lifetime. The run
/// file must report the drops that happened during the run, not the sum of every window's running total (a live 300z
/// run reported 22238 death-budget drops where the counter moved 1623 → 1777).</summary>
public class RunAggregatorDropCounterTests
{
    static readonly DateTime Start = new(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);

    static PerfWindowDto Window(int secondsFromStart, long deathBudget, long overflow = 0) => new()
    {
        T = Start.AddSeconds(secondsFromStart).ToString("o"),
        WindowMs = 5000,
        Drain = new DrainStatsDto { Enabled = true, DroppedDeathBudget = deathBudget, DroppedOverflow = overflow },
    };

    [Fact]
    public void Drops_are_the_last_value_minus_the_newest_window_before_the_run()
    {
        var agg = new RunAggregator("http://x", Start, 30, 10);
        agg.FoldNewWindows(new[] { Window(-20, 1500), Window(-5, 1623), Window(5, 1623, 2), Window(10, 1700, 2), Window(15, 1777, 3) }, Start, Start.AddSeconds(20));

        Assert.Equal(3, agg.Report.WindowsObserved);
        Assert.Equal(1777 - 1623, agg.Report.DrainDroppedDeathBudget);
        Assert.Equal(3, agg.Report.DrainDroppedOverflow);
        Assert.Equal("last-window-before-run", agg.Report.DrainDropsZeroPoint);
    }

    [Fact]
    public void Without_a_window_before_the_run_the_first_window_in_the_run_is_the_zero_point()
    {
        var agg = new RunAggregator("http://x", Start, 30, 10);
        agg.FoldNewWindows(new[] { Window(5, 1623) }, Start, Start.AddSeconds(6));
        agg.FoldNewWindows(new[] { Window(5, 1623), Window(10, 1650) }, Start, Start.AddSeconds(11));

        Assert.Equal(27, agg.Report.DrainDroppedDeathBudget);
        Assert.Equal("first-window-in-run", agg.Report.DrainDropsZeroPoint);
    }

    [Fact]
    public void A_run_with_no_new_drops_reports_zero_even_when_the_counter_is_already_high()
    {
        var agg = new RunAggregator("http://x", Start, 30, 10);
        agg.FoldNewWindows(new[] { Window(-3, 1777), Window(5, 1777), Window(10, 1777) }, Start, Start.AddSeconds(12));

        Assert.Equal(0, agg.Report.DrainDroppedDeathBudget);
    }
}
