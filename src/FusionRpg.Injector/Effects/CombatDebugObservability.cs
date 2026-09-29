using System.Collections.Generic;

namespace FusionRpg.Injector.Effects;

/// <summary>In-memory ring of recent overlay/probe dumps for debug.combat.snapshot.</summary>
public static class CombatDebugObservability
{
    // Structural (tunables-ssot.md T2) — debug ring-buffer size, not balance.
    const int Cap = 8;
    static readonly object Gate = new();
    static readonly Queue<Dictionary<string, object>> OverlayRing = new();
    static Dictionary<string, object>? _lastOverlay;
    static Dictionary<string, object>? _lastProbe;

    public static Dictionary<string, object>? LastOverlay
    {
        get { lock (Gate) return Clone(_lastOverlay); }
    }

    public static Dictionary<string, object>? LastProbe
    {
        get { lock (Gate) return Clone(_lastProbe); }
    }

    public static IReadOnlyList<Dictionary<string, object>> RecentOverlays()
    {
        lock (Gate)
            return OverlayRing.Select(Clone!).Where(d => d != null).Cast<Dictionary<string, object>>().ToList();
    }

    public static void RememberOverlay(Dictionary<string, object> dump)
    {
        lock (Gate)
        {
            var copy = Clone(dump)!;
            _lastOverlay = copy;
            OverlayRing.Enqueue(copy);
            while (OverlayRing.Count > Cap)
                OverlayRing.Dequeue();
        }
    }

    public static void RememberProbe(Dictionary<string, object> dump)
    {
        lock (Gate)
            _lastProbe = Clone(dump);
    }

    static Dictionary<string, object>? Clone(Dictionary<string, object>? src)
    {
        if (src == null) return null;
        return new Dictionary<string, object>(src);
    }
}
