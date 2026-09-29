using System.Text.Json;

namespace FusionRpg.Injector;

/// <summary>
/// Snapshot-scoped ref table (game-control module <c>control-click</c>).
/// Refs live exactly one snapshot: keyed by the snapshot id the inspect command minted,
/// every resolve re-verifies ptr+type+position against live objects (native ptrs are
/// reused after death — <c>match-runtime.md</c> — so ptr+type alone can resolve to a
/// different entity than inspected).
/// </summary>
public static class ControlRefs
{
    static int _snapshotCounter;
    static string _currentId = "snap0";
    static List<Dictionary<string, object>> _current = new();

    /// <returns>The minted snapshot id.</returns>
    public static string Remember(List<Dictionary<string, object>> controls)
    {
        var id = "snap" + Interlocked.Increment(ref _snapshotCounter);
        Volatile.Write(ref _currentId, id);
        Volatile.Write(ref _current, controls);
        return id;
    }

    public static Dictionary<string, object> CurrentSnapshot() => new()
    {
        ["snapshotId"] = Volatile.Read(ref _currentId),
        ["controls"] = Volatile.Read(ref _current)
    };

    /// <summary>Resolve a ref to a live (ptr, typeName); re-verified, never trusted.</summary>
    /// <returns>False with <paramref name="why"/> naming the recovery (fresh snapshot).</returns>
    public static bool TryResolve(string snapshotId, string refId, out string ptr, out string typeName, out string why)
    {
        ptr = "";
        typeName = "";
        why = "";
        if (!string.Equals(snapshotId, Volatile.Read(ref _currentId), StringComparison.Ordinal))
        {
            why = "stale snapshot (have " + Volatile.Read(ref _currentId) + ") — take a fresh inspect snapshot";
            return false;
        }
        foreach (var row in Volatile.Read(ref _current))
        {
            if (row == null) continue;
            object? r;
            if (!row.TryGetValue("ref", out r) || (r as string) != refId) continue;
            object? p;
            object? t;
            row.TryGetValue("ptr", out p);
            row.TryGetValue("type", out t);
            ptr = p as string ?? "";
            typeName = t as string ?? "";
            if (string.IsNullOrEmpty(ptr))
            {
                why = "ref has no ptr — take a fresh inspect snapshot";
                return false;
            }
            return true;
        }
        why = "unknown ref " + refId + " — take a fresh inspect snapshot";
        return false;
    }

    /// <summary>Position snapshot for re-verification: col/row carried by the ref row.</summary>
    public static bool TryPosition(string snapshotId, string refId, out int col, out int row)
    {
        col = -1;
        row = -1;
        if (!string.Equals(snapshotId, Volatile.Read(ref _currentId), StringComparison.Ordinal)) return false;
        foreach (var row_ in Volatile.Read(ref _current))
        {
            if (row_ == null) continue;
            object? r;
            if (!row_.TryGetValue("ref", out r) || (r as string) != refId) continue;
            object? c;
            object? rw;
            if (row_.TryGetValue("col", out c) && c is JsonElement ce && ce.TryGetInt32(out var cv)) col = cv;
            else if (row_.TryGetValue("col", out c) && c is int ci) col = ci;
            if (row_.TryGetValue("row", out rw) && rw is JsonElement re && re.TryGetInt32(out var rv)) row = rv;
            else if (row_.TryGetValue("row", out rw) && rw is int ri) row = ri;
            return true;
        }
        return false;
    }
}
