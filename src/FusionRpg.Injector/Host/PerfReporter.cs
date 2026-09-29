using FusionRpg.Core.Diagnostics;

namespace FusionRpg.Injector.Host;

/// <summary>Ships PerfProbe windows every ~5s — perf-probe-plan.md §1.4. Best-effort, never throws.</summary>
public static class PerfReporter
{
    // Config-backed (tunables-ssot.md T1) — gk-core/data/tuning/net.v1.json's perfReporter.
    public static float IntervalSeconds => (float)FusionRpg.Core.Net.NetPolicy.Tuning.PerfReporter.IntervalSeconds;

    public static void Flush(RpgClient? client)
    {
        if (!PerfProbe.Enabled) return;

        var window = PerfProbe.SnapshotAndReset();

        try
        {
            var match = Match.MatchHost.Runtime.ToSnapshot();
            window["board"] = new Dictionary<string, object>
            {
                ["plants"] = match.PlantCount,
                ["zombies"] = match.ZombieCount,
                ["bullets"] = match.BulletCount
            };
        }
        catch { }

        if (client != null)
        {
            window["queue"] = new Dictionary<string, object>
            {
                ["depth"] = client.QueueCount,
                ["dropped"] = client.Dropped
            };
        }

        try { window["drain"] = Effects.EventDrainHost.SnapshotStats(); } catch { }
        // lawn-combat-observer (Task 0, lawn-combat-wire): rides this already-shipped ~5s window —
        // no new HTTP surface, no new event kind.
        try { window["lawnCombatObserver"] = Effects.LawnCombatObserverBridge.SnapshotAndReset(); } catch { }

        try { LogLine(window); } catch { }
        _ = client?.PostPerfAsync(window);
    }

    static void LogLine(Dictionary<string, object> window)
    {
        var frames = window.TryGetValue("frames", out var f) ? f as Dictionary<string, object> : null;
        var gc = window.TryGetValue("gc", out var g) ? g as Dictionary<string, object> : null;
        var sections = window.TryGetValue("sections", out var s) ? s as Dictionary<string, object> : null;
        var loop = sections != null && sections.TryGetValue("loop.tick", out var l) ? l as Dictionary<string, object> : null;
        RpgHost.Log.Info(
            $"[perf] fps={frames?["fpsAvg"]} frameMax={frames?["maxMs"]}ms " +
            $"loopMs={loop?["totalMs"]} allocKb={gc?["allocKb"]} gen0={gc?["gen0"]} gen2={gc?["gen2"]}");
    }
}
