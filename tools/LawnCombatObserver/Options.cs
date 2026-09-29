namespace FusionRpg.Tools.LawnCombatObserver;

/// <summary>This tool's whole CLI surface. No flag here ever changes injector/server behaviour — every
/// option only shapes how long/where this tool watches and what it writes. That is the load-bearing
/// property the acceptance list calls "no flag flipped to observe".</summary>
public sealed class Options
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:5088";

    /// <summary>How long to watch. <c>/api/perf</c> windows land roughly every 5s
    /// (<c>PerfReporter.IntervalSeconds</c>), so a run shorter than that will usually see zero windows —
    /// reported as "no data", not "zero", the whole point of this tool. Default is long enough to see
    /// at least one real window even under load.</summary>
    public int DurationSec { get; set; } = 30;

    /// <summary>How often to poll <c>/api/perf/recent</c> and the session-proof routes while watching.</summary>
    public int PollIntervalSec { get; set; } = 2;

    /// <summary>Where the machine-readable run file is written. Defaults into this tool's own scratch
    /// output directory — never a repo path a caller did not explicitly choose.</summary>
    public string OutFile { get; set; } = "lawn-combat-observer-run.json";

    /// <summary>Cap on how many individual hit records the run file embeds (the aggregate counters are
    /// always exact and never capped — only the sample list is bounded, for file size).</summary>
    public int MaxHitSample { get; set; } = 200;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i].TrimStart('-').ToLowerInvariant())
            {
                case "baseurl": o.BaseUrl = Next(); break;
                case "durationsec": o.DurationSec = int.Parse(Next()); break;
                case "pollintervalsec": o.PollIntervalSec = int.Parse(Next()); break;
                case "out": case "outfile": o.OutFile = Next(); break;
                case "maxhitsample": o.MaxHitSample = int.Parse(Next()); break;
                default: throw new ArgumentException($"unknown flag '{args[i]}'");
            }
        }
        return o;
    }
}
