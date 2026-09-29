using System.Text.Json.Serialization;

namespace FusionRpg.Tools.LawnCombatObserver;

// Typed shapes for what this tool reads. Every property name matches the server/injector's own wire
// shape verbatim (PerfProbe.SnapshotAndReset / LawnCombatObserverBridge.SnapshotAndReset /
// DebugRuntime.Snapshot), read directly from their source rather than guessed —
// JsonSerializerDefaults.Web (camelCase, case-insensitive) round-trips plain PascalCase properties
// without attributes, same convention ProveLiveProbe's own DTOs use.

/// <summary>One <c>/api/perf</c> window, as posted by <c>PerfReporter.Flush</c> and returned by
/// <c>GET /api/perf/recent</c>. Only the sub-sections this tool actually reads are modeled.</summary>
public sealed class PerfWindowDto
{
    public string? T { get; set; }
    public double WindowMs { get; set; }
    public Dictionary<string, SectionStatsDto>? Sections { get; set; }
    public LawnCombatObserverWindowDto? LawnCombatObserver { get; set; }
    public DrainStatsDto? Drain { get; set; }
}

/// <summary><c>PerfProbe.SnapshotAndReset</c>'s per-section shape — only <c>drain.tick</c> is read here
/// (the frame-share sample), by key from <see cref="PerfWindowDto.Sections"/>.</summary>
public sealed class SectionStatsDto
{
    public long Count { get; set; }
    public double TotalMs { get; set; }
    public double MaxMs { get; set; }
    public double AvgUs { get; set; }
    public double PerSec { get; set; }
}

/// <summary><c>EventDrainHost.SnapshotStats()</c>'s shape — the shipped, already-unconditional
/// drop-counter proof this tool folds in beside its own.</summary>
public sealed class DrainStatsDto
{
    public bool Enabled { get; set; }
    public long Pending { get; set; }
    public long DroppedOverflow { get; set; }
    public long DroppedDepth { get; set; }
    public long DroppedDeathBudget { get; set; }
    public long DroppedDeathBudgetLivingTarget { get; set; }
    public int MaxLatencyFrames { get; set; }
    public double MaxRecordUs { get; set; }
    public long Processed { get; set; }
    public long Carried { get; set; }
    public long ExpensiveDeferred { get; set; }
}

/// <summary><c>LawnCombatObserverBridge.SnapshotAndReset()</c>'s shape — this program's own new
/// aggregate, riding the perf window.</summary>
public sealed class LawnCombatObserverWindowDto
{
    public bool Enabled { get; set; }
    public long TotalHits { get; set; }
    public long TotalSwings { get; set; }
    public long ActionTriggers { get; set; }
    public long StaminaSpent { get; set; }
    public long RegenAccrued { get; set; }
    public long ExhaustionEvents { get; set; }
    public long RpgDeltaMergedHits { get; set; }
    public long RpgDeltaUnmergedRecords { get; set; }
    public long RpgMisses { get; set; }
    public long DroppedRecords { get; set; }
    public List<LawnCombatHitDto>? RecentHits { get; set; }
}

public sealed class LawnCombatHitDto
{
    public long Seq { get; set; }
    public int Frame { get; set; }
    public string SwingId { get; set; } = "";
    public string AttackerPtr { get; set; } = "";
    public string VictimPtr { get; set; } = "";
    public string AttackerSide { get; set; } = "";
    public long VanillaAmount { get; set; }
    public long RpgDelta { get; set; }
    public bool RpgDeltaObserved { get; set; }
    public string AttackerElement { get; set; } = "";
    public string VictimElement { get; set; } = "";
    public string MatchupRelation { get; set; } = "";
    public string RpgOutcome { get; set; } = "";
}

/// <summary><c>DebugRuntime.Snapshot()</c>'s shape, as carried by the <c>debug.snapshot</c> event this
/// tool polls for — only <c>sessionActive</c> is read (the proof this run never perturbed
/// <c>EventDrainHost.Active</c>).</summary>
public sealed class DebugSnapshotPayloadDto
{
    public bool SessionActive { get; set; }
    public string? ScenarioId { get; set; }
}

/// <summary><c>GET /api/debug/session</c>'s response — the server-side mirror, read as a cheap,
/// no-relay cross-check alongside the injector's own <c>debug.snapshot</c> answer.</summary>
public sealed class ServerDebugSessionDto
{
    public bool SessionActive { get; set; }
    public string? ScenarioId { get; set; }
}
