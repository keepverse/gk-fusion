using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Creatures.Patron;

namespace FusionRpg.Injector.Effects;

/// <summary>Parses the server's `patron.aura` command into the runtime cache. Malformed payloads
/// clear nothing — the last good designation stands until a good one replaces it.</summary>
public static class PatronCommand
{
    public static void Apply(CommandDto cmd)
    {
        if (cmd.Payload == null) return;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(cmd.Payload));
        var root = doc.RootElement;
        var elementPrimary = root.TryGetProperty("elementPrimary", out var ep) ? ep.GetString() : null;
        if (string.IsNullOrWhiteSpace(elementPrimary)) return;

        var aura = new PatronAura(
            elementPrimary!,
            root.TryGetProperty("elementSecondary", out var es) ? es.GetString() : null,
            GetLong(root, "powerMilli"),
            GetLong(root, "defenseMilli"),
            GetLong(root, "secondaryPowerMilli"),
            GetLong(root, "secondaryDefenseMilli"));
        var playerId = root.TryGetProperty("playerId", out var pid) && pid.TryGetInt64(out var p) ? p : 0;
        PatronRuntimeState.Set(playerId, aura);
    }

    // aura-skill T22: widened from GetInt to GetLong — PatronAura's fields are `long` now (the P(Θ)
    // term makes this magnitude scale with the power ladder instead of staying int-safe forever).
    static long GetLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.TryGetInt64(out var i) ? i : 0;
}
