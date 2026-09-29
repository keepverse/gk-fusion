using FusionRpg.Core.Combat;

namespace FusionRpg.Injector.Hud;

/// <summary>Hot RAM set of PvZ unique-plant ptrs — from plant.unique observe, not SQLite.</summary>
public static class ActorHudUniqueFlags
{
    static readonly object Gate = new();
    static readonly HashSet<string> Ptrs = new(StringComparer.Ordinal);

    public static void Mark(string? ptr)
    {
        var key = CombatPtr.Normalize(ptr);
        if (string.IsNullOrEmpty(key)) return;
        lock (Gate)
            Ptrs.Add(key);
    }

    public static bool TryIsUnique(string? ptr)
    {
        var key = CombatPtr.Normalize(ptr);
        if (string.IsNullOrEmpty(key)) return false;
        lock (Gate)
            return Ptrs.Contains(key);
    }

    public static void Remove(string? ptr)
    {
        var key = CombatPtr.Normalize(ptr);
        if (string.IsNullOrEmpty(key)) return;
        lock (Gate)
            Ptrs.Remove(key);
    }

    public static void Clear()
    {
        lock (Gate)
            Ptrs.Clear();
    }
}
