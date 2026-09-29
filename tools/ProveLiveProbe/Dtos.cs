using System.Text.Json.Serialization;
using FusionRpg.Contracts;

namespace FusionRpg.Tools.ProveLiveProbe;

// These are typed response shapes for endpoints whose SERVER-SIDE handler returns an anonymous object
// (no named DTO exists in FusionRpg.Contracts or FusionRpg.Server to reference) — read directly from
// the handler source so field names are verified, not guessed:
//   - SpawnUniqueActorResult:   CreatureEndpoints.cs MapPost("/debug/spawn-unique-actor", ...) under
//                               its own "/api/creatures" group -> real path
//                               /api/creatures/debug/spawn-unique-actor (verified live; the route name
//                               says "debug" but it is not under the server's "/api/debug" group)
//   - UniqueAptitudeState:      AptitudeEndpoints.cs ProjectUniqueState(...)
//   - SummonResult:             CreatureEndpoints.cs MapPost("/summon", ...)
//   - BoardStatsPayload/Entity: FusionRpg.Injector.DebugRuntime.BoardEntityStats()
// Every property name matches the server's own anonymous shape; JsonSerializerDefaults.Web (camelCase,
// case-insensitive) is used for every (de)serialization in this tool, matching ASP.NET Core Minimal
// API's own default wire casing, so plain PascalCase C# properties round-trip without attributes.

/// <summary>Response of <c>POST /api/creatures/debug/spawn-unique-actor</c> (Mode A's acquire, and the
/// ONLY debug-shortcut acquisition this tool ever calls).</summary>
public sealed class SpawnUniqueActorResult
{
    public string InstanceId { get; set; } = "";
    public string Ptr { get; set; } = "";
    public UniqueActorDto? Actor { get; set; }
}

/// <summary>Response of <c>POST /api/aptitudes/unique/allocate</c> (and the read-alike GET) —
/// <c>AptitudeEndpoints.ProjectUniqueState</c>'s anonymous shape.</summary>
public sealed class UniqueAptitudeState
{
    public string InstanceId { get; set; } = "";
    public long PlayerId { get; set; }
    public long SpecimenLevel { get; set; }
    public long Budget { get; set; }
    public long Spent { get; set; }
    public long Leftover { get; set; }
    public bool WithinBudget { get; set; }
    public Dictionary<string, long> Shares { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Response of <c>POST /api/creatures/summon</c> (Mode B's real acquire). Only the fields
/// this tool actually reads are modeled — <c>pity</c>/<c>balance</c> are real fields on the server's
/// anonymous shape too, just not needed here.</summary>
public sealed class SummonResult
{
    public bool Replayed { get; set; }
    public List<CreatureSpecimenDto> Specimens { get; set; } = new();
    public long DiscoverySouls { get; set; }
}

/// <summary>One living plant/zombie row inside a <c>debug.board-stats</c> event payload
/// (<c>DebugRuntime.BoardEntityStats()</c>). Field set matches the injector's own dictionary keys for
/// the fields this tool compares; extra keys the injector emits (e.g. <c>thePlantAttackInterval</c>)
/// are simply ignored by System.Text.Json rather than modeled, since this tool never reads them.</summary>
public sealed class BoardStatsEntity
{
    public string Ptr { get; set; } = "";
    public int TypeId { get; set; }
    public int Col { get; set; }
    public int Row { get; set; }
    public long Attack { get; set; }
    public long Hp { get; set; }
    public long MaxHp { get; set; }
}

/// <summary>The whole <c>debug.board-stats</c> event payload: plants + zombies, plus the <c>tag</c>
/// this tool stamps on its own request so it can tell its own answer apart from a stale/earlier one
/// still sitting in the event log.</summary>
public sealed class BoardStatsPayload
{
    public List<BoardStatsEntity> Plants { get; set; } = new();
    public List<BoardStatsEntity> Zombies { get; set; } = new();
    public string? Tag { get; set; }

    public IEnumerable<BoardStatsEntity> All => Plants.Concat(Zombies);
}
