using FusionRpg.Core.Time;
namespace FusionRpg.Injector;

public static class SpawnCatalog
{
    static readonly object Gate = new();
    static readonly Dictionary<(string side, int type), SpawnEntry> Entries = new();

    public static int PlantCount { get { lock (Gate) return Entries.Keys.Count(k => k.side == "plant"); } }
    public static int ZombieCount { get { lock (Gate) return Entries.Keys.Count(k => k.side == "zombie"); } }
    public static int PetCount { get { lock (Gate) return Entries.Keys.Count(k => k.side == "pet"); } }
    public static int GridCount { get { lock (Gate) return Entries.Keys.Count(k => k.side == "grid"); } }

    public static void Note(string side, int type, string? typeName, string source, string? displayName = null)
    {
        if (type < 0) return;
        if (string.Equals(typeName, "Nothing", StringComparison.Ordinal)) return;
        var key = (side, type);
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out var e))
            {
                e = new SpawnEntry
                {
                    Side = side,
                    Type = type,
                    TypeName = typeName ?? type.ToString(),
                    DisplayName = displayName,
                    FirstSeenUtc = ServerClock.UtcNowDateTime
                };
                Entries[key] = e;
                if (CheatState.EmitProof && CheatState.On("SYS-EMIT-PROOF"))
                    GameHooks.Emit("cheat.enrich", new Dictionary<string, object>
                    {
                        ["side"] = side,
                        ["type"] = type,
                        ["typeName"] = e.TypeName,
                        ["source"] = source
                    });
            }
            e.LastSeenUtc = ServerClock.UtcNowDateTime;
            if (!string.IsNullOrWhiteSpace(displayName)) e.DisplayName = displayName;
            if (!string.IsNullOrWhiteSpace(typeName)) e.TypeName = typeName!;
            if (!e.Sources.Contains(source)) e.Sources.Add(source);
        }
    }

    public static List<SpawnEntry> List(string side)
    {
        lock (Gate)
            return Entries.Values.Where(e => e.Side == side)
                .OrderByDescending(e => e.LastSeenUtc)
                .Select(Clone)
                .ToList();
    }

    public static void MarkSpawn(string side, int type, bool ok, string? error = null)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue((side, type), out var e)) return;
            e.SpawnOk = ok;
            e.LastError = error;
        }
    }

    public static void ClearFailed()
    {
        lock (Gate)
        {
            foreach (var e in Entries.Values.Where(x => x.SpawnOk == false))
            {
                e.SpawnOk = null;
                e.LastError = null;
            }
        }
    }

    static SpawnEntry Clone(SpawnEntry e) => new()
    {
        Side = e.Side,
        Type = e.Type,
        TypeName = e.TypeName,
        DisplayName = e.DisplayName,
        FirstSeenUtc = e.FirstSeenUtc,
        LastSeenUtc = e.LastSeenUtc,
        Sources = e.Sources.ToList(),
        SpawnOk = e.SpawnOk,
        LastError = e.LastError
    };
}

public sealed class SpawnEntry
{
    public string Side { get; set; } = "";
    public int Type { get; set; }
    public string TypeName { get; set; } = "";
    public string? DisplayName { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public List<string> Sources { get; set; } = new();
    public bool? SpawnOk { get; set; }
    public string? LastError { get; set; }

    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? $"{Type} {TypeName}" : $"{Type} {DisplayName}";
}
