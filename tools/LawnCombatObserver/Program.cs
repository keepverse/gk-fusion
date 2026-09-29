using System.Text.Json;

namespace FusionRpg.Tools.LawnCombatObserver;

/// <summary>
/// Task 0 (`lawn-combat-wire`, "Phase 0 — the ruler") — watches a real running
/// <c>FusionRpg.Server</c> (+ live game/Injector) for the already-shipped ~5s <c>/api/perf</c> window
/// this program's own <c>LawnCombatObserverBridge</c> now rides, for <c>-DurationSec</c> seconds, then
/// writes a machine-readable run file a gate can diff against another run.
///
/// Deliberately real HTTP against a live server (ProveLiveProbe's own shape), never
/// <c>RpgStore.InMemory()</c>. Makes exactly one Game-Injector-Debug-shaped call
/// (<c>POST /api/debug/snapshot</c>, itself read-only) to prove the injector's own
/// <c>DebugRuntime.SessionActive</c> stayed false throughout — everything else is a plain read.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"argument error: {ex.Message}");
            return 2;
        }

        using var client = new ObserverClient(options.BaseUrl);

        var (healthOk, healthStatus, _) = await client.GetAsync<object>("/health");
        if (!healthOk)
        {
            Console.WriteLine($"REFUSED: {options.BaseUrl}/health did not answer (status={healthStatus}) — " +
                               "is FusionRpg.Server running? This run collected nothing.");
            var refused = new RunReport
            {
                BaseUrl = options.BaseUrl,
                StartedAtUtc = DateTime.UtcNow.ToString("o"),
                EndedAtUtc = DateTime.UtcNow.ToString("o"),
                RequestedDurationSec = options.DurationSec,
                NoData = true,
                NoDataReason = $"server health check failed (status={healthStatus}) before any polling began"
            };
            await WriteRunFileAsync(options.OutFile, refused);
            Report.Print(refused);
            return 1;
        }

        var startedAtUtc = DateTime.UtcNow;
        var aggregator = new RunAggregator(options.BaseUrl, startedAtUtc, options.DurationSec, options.MaxHitSample);

        var deadline = startedAtUtc.AddSeconds(options.DurationSec);
        var checkedSessionAtStart = false;
        var checkedSessionAtEnd = false;

        while (DateTime.UtcNow < deadline)
        {
            var now = DateTime.UtcNow;

            var windows = await client.GetRecentPerfWindowsAsync(limit: 48);
            aggregator.FoldNewWindows(windows, startedAtUtc, now);

            // Session-active proof: once near the start (once the injector has had a moment to be
            // reachable) and once near the end — bounded checks, never hammered every poll tick, since
            // each round-trips through the injector.
            var elapsedFrac = options.DurationSec <= 0 ? 1.0 : (now - startedAtUtc).TotalSeconds / options.DurationSec;
            if (!checkedSessionAtStart && elapsedFrac >= 0.1)
            {
                checkedSessionAtStart = true;
                await CheckSessionActiveAsync(client, aggregator);
            }
            if (!checkedSessionAtEnd && elapsedFrac >= 0.9)
            {
                checkedSessionAtEnd = true;
                await CheckSessionActiveAsync(client, aggregator);
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(options.PollIntervalSec, Math.Max(0.1, remaining.TotalSeconds))));
        }

        // Always take at least one session-active reading even for a very short run.
        if (!checkedSessionAtStart) await CheckSessionActiveAsync(client, aggregator);

        // Final poll after the loop, so a window that landed right at the deadline is not missed.
        var finalWindows = await client.GetRecentPerfWindowsAsync(limit: 48);
        aggregator.FoldNewWindows(finalWindows, startedAtUtc, DateTime.UtcNow);

        aggregator.Finish(DateTime.UtcNow);

        await WriteRunFileAsync(options.OutFile, aggregator.Report);
        Report.Print(aggregator.Report);

        Console.WriteLine($"run file written: {Path.GetFullPath(options.OutFile)}");

        if (aggregator.Report.NoData) return 1;
        return 0;
    }

    static async Task CheckSessionActiveAsync(ObserverClient client, RunAggregator aggregator)
    {
        var injectorActive = await client.TryReadInjectorSessionActiveAsync(TimeSpan.FromSeconds(5));
        aggregator.RecordInjectorSessionActiveCheck(injectorActive);
        var serverActive = await client.TryReadServerSessionActiveAsync();
        aggregator.RecordServerSessionActiveCheck(serverActive);
    }

    static async Task WriteRunFileAsync(string path, RunReport report)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
        });
        await File.WriteAllTextAsync(path, json);
    }
}
