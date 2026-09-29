namespace FusionRpg.Tools.ProveLiveProbe;

/// <summary>Where step 1 (Acquire) gets its specimen from. <c>Auto</c> resolves per <see cref="Mode"/>:
/// <see cref="ProbeMode.A"/> always uses <see cref="DebugShortcut"/>, <see cref="ProbeMode.B"/> always
/// uses <see cref="RealSummon"/>. A caller can only ever land on <see cref="DebugShortcut"/> for Mode B
/// by asking for it explicitly (<c>-AcquireVia debug-shortcut</c>) — the one case
/// <see cref="Guardrails.CheckModeBAcquisition"/> refuses outright, per the spec's own correction
/// (spec-live-probe-tool.md "Two modes, not one").</summary>
public enum AcquireVia
{
    Auto,
    DebugShortcut,
    RealSummon,
}

public enum ProbeMode
{
    A,
    B,
}

/// <summary>The tool's whole CLI surface (spec-live-probe-tool.md "Commands"), plus the handful of
/// extra knobs the 6-step recipe cannot run without (base URL, deploy col/row/matchKey, timeout) that
/// the spec leaves to sensible defaults rather than naming explicitly.</summary>
public sealed class Options
{
    public ProbeMode Mode { get; set; } = ProbeMode.A;
    public string BaseUrl { get; set; } = "http://127.0.0.1:5088";

    public long? PlayerId { get; set; }
    public string Side { get; set; } = "plant";
    public int TypeId { get; set; }
    public string? BannerId { get; set; }

    /// <summary>
    /// Step 1 reuse: run the recipe against a specimen this player ALREADY owns instead of summoning a
    /// new one. It is not a shortcut around the real acquisition — the specimen still has to be a real
    /// <c>rpg_unique_actors</c> row that a real summon created, and the tool refuses anything it does
    /// not find on <c>GET /api/unique/actors/{id}</c> owned by <c>-PlayerId</c>. It exists because a
    /// Mode B run can fail for a reason that has nothing to do with the recipe — 2026-09-16: a real
    /// summon rolled <c>typeId 261</c> (<c>Synergy_蘑菇岛</c>), a species this game build has no prefab
    /// for, so <c>CreatePlant.SetPlant</c> threw and the deploy never acked — and re-rolling the species
    /// on every retry burns 100 souls a run while changing nothing the probe is trying to measure.
    /// </summary>
    public string? InstanceId { get; set; }

    public string? AptitudeId { get; set; }
    public long AptitudePoints { get; set; }

    public string? Role { get; set; }
    public string? ItemInstanceId { get; set; }

    public int? Col { get; set; }
    public int? Row { get; set; }
    public string? MatchKey { get; set; }

    public int TimeoutSec { get; set; } = 30;
    public bool NoCleanup { get; set; }

    public AcquireVia AcquireVia { get; set; } = AcquireVia.Auto;

    /// <summary>Never populated from a real recipe run — the one flag that exists only so the
    /// program's own "prove the tool would have caught the 2026-09-13 incident" test (Task 8 / todo
    /// Checkpoint 1b) can hand it a deliberately non-empty value and watch the tool refuse before any
    /// HTTP call, instead of quietly forwarding it to <c>POST /api/unique/actors/{id}/deploy</c>.</summary>
    public string? DangerousLoadoutJsonOverride { get; set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i].TrimStart('-').ToLowerInvariant())
            {
                case "mode": o.Mode = ParseMode(Next()); break;
                case "baseurl": o.BaseUrl = Next(); break;
                case "playerid": o.PlayerId = long.Parse(Next()); break;
                case "side": o.Side = Next(); break;
                case "typeid": o.TypeId = int.Parse(Next()); break;
                case "bannerid": o.BannerId = Next(); break;
                case "instanceid": o.InstanceId = Next(); break;
                case "aptitudeid": o.AptitudeId = Next(); break;
                case "aptitudepoints": o.AptitudePoints = long.Parse(Next()); break;
                case "role": o.Role = Next(); break;
                case "iteminstanceid": o.ItemInstanceId = Next(); break;
                case "col": o.Col = int.Parse(Next()); break;
                case "row": o.Row = int.Parse(Next()); break;
                case "matchkey": o.MatchKey = Next(); break;
                case "timeoutsec": o.TimeoutSec = int.Parse(Next()); break;
                case "nocleanup": o.NoCleanup = true; break;
                case "acquirevia": o.AcquireVia = ParseAcquireVia(Next()); break;
                case "dangerousloadoutjsonoverride": o.DangerousLoadoutJsonOverride = Next(); break;
                default: throw new ArgumentException($"unknown flag '{args[i]}'");
            }
        }
        return o;
    }

    static ProbeMode ParseMode(string s) => s.Trim().ToUpperInvariant() switch
    {
        "A" => ProbeMode.A,
        "B" => ProbeMode.B,
        _ => throw new ArgumentException($"-Mode must be A or B, got '{s}'"),
    };

    static AcquireVia ParseAcquireVia(string s) => s.Trim().ToLowerInvariant() switch
    {
        "auto" => AcquireVia.Auto,
        "debug-shortcut" => AcquireVia.DebugShortcut,
        "summon" or "real-summon" => AcquireVia.RealSummon,
        _ => throw new ArgumentException($"-AcquireVia must be auto|debug-shortcut|summon, got '{s}'"),
    };

    /// <summary>What step 1 actually resolves to once <see cref="AcquireVia.Auto"/> is settled against
    /// <see cref="Mode"/> — the value <see cref="Guardrails.CheckModeBAcquisition"/> checks.</summary>
    public AcquireVia ResolvedAcquireVia => AcquireVia switch
    {
        AcquireVia.Auto => Mode == ProbeMode.A ? AcquireVia.DebugShortcut : AcquireVia.RealSummon,
        var v => v,
    };
}
