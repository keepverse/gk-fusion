using System.Text;
using System.Text.Json;
using FusionRpg.Contracts;

namespace FusionRpg.Tools.ProveLiveProbe;

/// <summary>Where one kill-earned ledger row's victim came from, read from its activity fact's own
/// <c>spawnOrigin</c> (stamped by the injector on <c>zombie.die</c>/<c>plant.die</c>).</summary>
public enum KillOrigin
{
    /// <summary>Spawned by the game itself (level waves, real placement).</summary>
    Game,
    /// <summary>Spawned by a Game Injector Debug command (`debug.spawn-zombie`/`debug.spawn-plant`).</summary>
    Debug,
    /// <summary>Spawned from the cheat panel (<c>CheatActions.SpawnPlant/SpawnZombie</c>).</summary>
    Cheat,
    /// <summary>The fact exists but carries no <c>spawnOrigin</c> — captured before origin stamping
    /// existed. Provenance unknown; never counted as clean.</summary>
    Unrecorded,
    /// <summary>The ledger row points at an activity fact the tool could not read back.</summary>
    FactNotFound,
}

public sealed record SoulProvenanceSummary(
    long Balance,
    int LedgerRowsScanned,
    bool LedgerTruncated,
    IReadOnlyDictionary<string, long> DeltaByReason,
    IReadOnlyDictionary<KillOrigin, long> KillSoulsByOrigin)
{
    /// <summary>Kill souls from entities an Injector command created (debug or cheat spawns).</summary>
    public long DebugKillSouls =>
        KillSoulsByOrigin.GetValueOrDefault(KillOrigin.Debug) + KillSoulsByOrigin.GetValueOrDefault(KillOrigin.Cheat);

    /// <summary>Kill souls whose origin the tool could not prove to be the game's own.</summary>
    public long UnprovenKillSouls =>
        KillSoulsByOrigin.GetValueOrDefault(KillOrigin.Unrecorded) + KillSoulsByOrigin.GetValueOrDefault(KillOrigin.FactNotFound);

    /// <summary>live-probe-standard: a balance fed by debug-spawned kills is debug-funded, so an acquire
    /// spending it is not proof of the real economy.</summary>
    public bool DebugFunded => DebugKillSouls > 0;
}

/// <summary>
/// live-probe Task 18: make the souls behind a Mode B acquire visible. Pure over the three reads the
/// client makes (balance, ledger pages, the kill facts those ledger rows reference) so it is testable
/// offline.
/// </summary>
public static class SoulProvenance
{
    public const string KillReason = "kill";
    public const string ActivityFactRefKind = "activity_fact";

    public static SoulProvenanceSummary Summarize(
        long balance,
        IReadOnlyList<SoulLedgerEntryDto> ledger,
        bool ledgerTruncated,
        IReadOnlyList<PvzActivityFactDto> facts)
    {
        var byReason = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var byOrigin = new Dictionary<KillOrigin, long>();
        var factsById = new Dictionary<long, PvzActivityFactDto>();
        foreach (var f in facts) factsById[f.Id] = f;

        foreach (var row in ledger)
        {
            byReason[row.Reason] = checked(byReason.GetValueOrDefault(row.Reason) + row.Delta);
            if (!IsKillEarn(row)) continue;

            var origin = long.TryParse(row.RefId, out var factId) && factsById.TryGetValue(factId, out var fact)
                ? OriginOf(fact.PayloadJson)
                : KillOrigin.FactNotFound;
            byOrigin[origin] = checked(byOrigin.GetValueOrDefault(origin) + row.Delta);
        }

        return new SoulProvenanceSummary(balance, ledger.Count, ledgerTruncated, byReason, byOrigin);
    }

    public static bool IsKillEarn(SoulLedgerEntryDto row) =>
        row.Delta > 0
        && string.Equals(row.Reason, KillReason, StringComparison.Ordinal)
        && string.Equals(row.RefKind, ActivityFactRefKind, StringComparison.Ordinal);

    public static KillOrigin OriginOf(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return KillOrigin.Unrecorded;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("spawnOrigin", out var el)
                || el.ValueKind != JsonValueKind.String)
                return KillOrigin.Unrecorded;
            return el.GetString() switch
            {
                "debug" => KillOrigin.Debug,
                "game" => KillOrigin.Game,
                "cheat" => KillOrigin.Cheat,
                _ => KillOrigin.Unrecorded,
            };
        }
        catch (JsonException)
        {
            return KillOrigin.Unrecorded;
        }
    }

    /// <summary>The report step. Always <see cref="StepOutcome.Ok"/> unless debug-funded, which is a
    /// <see cref="StepOutcome.Mismatch"/> — the server answered, but the balance is not what a real-economy
    /// proof requires. Unproven (pre-stamping) souls are reported, never silently counted as clean.</summary>
    public static StepResult ToStep(SoulProvenanceSummary s)
    {
        var sb = new StringBuilder();
        sb.Append($"balance={s.Balance} ledgerRows={s.LedgerRowsScanned}");
        if (s.LedgerTruncated) sb.Append(" (TRUNCATED — older rows not scanned)");
        sb.Append(" byReason{");
        sb.Append(string.Join(", ", s.DeltaByReason.Select(kv => $"{kv.Key}:{kv.Value}")));
        sb.Append("} killSoulsByOrigin{");
        sb.Append(string.Join(", ", Enum.GetValues<KillOrigin>().Select(o => $"{o}:{s.KillSoulsByOrigin.GetValueOrDefault(o)}")));
        sb.Append('}');

        if (s.DebugFunded)
            return new StepResult("0-soul-provenance", StepOutcome.Mismatch,
                $"DEBUG-FUNDED: {s.DebugKillSouls} souls came from kills of debug- or cheat-spawned entities — an acquire " +
                $"spending this balance is not real-economy evidence. {sb}", s);
        if (s.UnprovenKillSouls > 0)
            sb.Append($" — WARNING: {s.UnprovenKillSouls} kill souls have unproven origin");
        return new StepResult("0-soul-provenance", StepOutcome.Ok, sb.ToString(), s);
    }
}
