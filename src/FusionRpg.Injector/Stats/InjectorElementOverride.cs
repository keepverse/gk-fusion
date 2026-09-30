using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Injector.Stats;

/// <summary>Per-ptr element type pin for LIVE overlay combat prove — no SQL.</summary>
public static class InjectorElementOverride
{
    static readonly object Gate = new();
    static readonly Dictionary<string, ActorElementTypes> Pins = new(StringComparer.Ordinal);

    public static void Pin(string? ptr, ActorElementTypes types)
    {
        var key = CombatPtr.Normalize(ptr);
        if (string.IsNullOrEmpty(key) || types == null) return;
        lock (Gate)
            Pins[key] = types;
    }

    public static void PinParse(string? ptr, string? primary, string? secondary)
    {
        Pin(ptr, ActorElementTypes.Parse(primary, secondary));
    }

    public static bool TryGet(string? ptr, out ActorElementTypes types)
    {
        var key = CombatPtr.Normalize(ptr);
        lock (Gate)
            return Pins.TryGetValue(key, out types!);
    }

    public static void Clear()
    {
        lock (Gate)
            Pins.Clear();
    }
}
