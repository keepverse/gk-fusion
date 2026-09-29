using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;

// GameMetaDump — a read-only inventory of an Il2Cpp interop assembly's type model.
//
// Why this exists: the creature corpus bakes every species' magnitudes from `pTheta * k` because the
// only stat capture path we had reads fields off a LIVE spawned entity (`GameHooks` -> `GameDumps.Zombie`),
// so a type that was never spawned has no observed stats. As of the 2026-08-23 dump that was 82 of 904
// species, and plant armour was captured 0 of 677 times. This tool exists to answer "what does the game
// actually expose" without guessing, and it found that the stats are available statically and per type:
// `PlantDataManager.GetPlantOriginalData(PlantType)` and `ZombieDataManager.GetZombieData(ZombieType)`.
//
// What it does NOT do, deliberately:
//   * It never loads or executes the assembly. `PEReader` + `MetadataReader` is a pure metadata walk,
//     so a hostile or simply broken binary cannot run code in this process.
//   * It never reads game memory and never needs the game running.
//   * It emits NAMES and SHAPES only — no IL, no constants, no assets, no resources.
//   * It records no filesystem path. The output carries the assembly's simple name and byte size, so a
//     committed dump never leaks a machine-local game directory (AGENTS.md forbids committing those).
//
// Not runnable in CI: the input is the player's own legally-installed game, which CI does not have. That
// is why there is no `--check` mode — a drift gate would fail on every machine but the owner's. Re-run it
// by hand after a game version bump and commit the diff.
//
// Usage:
//   dotnet run --project gk-fusion/tools/GameMetaDump -- --assembly <path-to-Assembly-CSharp.dll> --out <dir>
//   dotnet run --project gk-fusion/tools/GameMetaDump -- --assembly <...> --out <dir> --metadata <global-metadata.dat>
//   dotnet run --project gk-fusion/tools/GameMetaDump -- --assembly <...> --out <dir> --full

using FusionRpg.Tools.GameMetaDump;

var assemblyPath = ArgValue("--assembly");
var outPath = ArgValue("--out");
var metadataPath = ArgValue("--metadata");
var full = args.Contains("--full", StringComparer.Ordinal);

if (assemblyPath is null || outPath is null)
{
    Console.Error.WriteLine("usage: --assembly <Assembly-CSharp.dll> --out <output-dir> [--metadata <global-metadata.dat>] [--full]");
    Console.Error.WriteLine("  --metadata  recover enum member names the interop assembly destroyed (see Il2CppMetadata)");
    Console.Error.WriteLine("  --full      keep interop transport members and empty types, and also write full.json");
    return 2;
}

if (!File.Exists(assemblyPath))
{
    Console.Error.WriteLine($"assembly not found: {assemblyPath}");
    return 2;
}

var fileLength = new FileInfo(assemblyPath).Length;

using var stream = File.OpenRead(assemblyPath);
using var pe = new PEReader(stream);

if (!pe.HasMetadata)
{
    Console.Error.WriteLine("input has no CLI metadata — not a managed assembly");
    return 2;
}

var md = pe.GetMetadataReader();

// Il2CppInterop destroys the member names of every enum whose members are named in Chinese, writing a
// ToString() of its own internal object in their place. The values survive; only the names are lost.
// `global-metadata.dat` still holds the originals, so --metadata recovers them. Without it the dump is
// still correct — it just carries 628 unusable names on this game's 18 worst-affected enums.
const string LostNameMarker = "EnumValueAsmResolver";
Dictionary<string, Il2CppMetadata.TypeMembers>? recovered = null;
if (metadataPath is not null)
{
    recovered = Il2CppMetadata.TryRead(metadataPath, out var metaError);
    if (recovered is null)
    {
        Console.Error.WriteLine($"--metadata ignored: {metaError}");
    }
    else
    {
        Console.WriteLine($"metadata: {recovered.Count} types available for name recovery");
    }
}

var recoveredNames = 0;
var unrecoverable = 0;

string Text(StringHandle h) => md.GetString(h);

// Il2CppInterop emits one `NativeFieldInfoPtr_<name>` / `NativeMethodInfoPtr_<name>` static per real
// member, which is how the interop layer finds the native slot. Those prefixes are pure transport noise
// — the payload is the name after them — and stripping them is what turns a 4.8 MB blob into something a
// reader can actually scan. `--full` keeps the raw names for anyone debugging the interop layer itself.
const string FieldPrefix = "NativeFieldInfoPtr_";
const string MethodPrefix = "NativeMethodInfoPtr_";

// Il2CppInterop also encodes the signature into the method name after the first `_Public_`/`_Private_`
// etc. We keep the full encoded name (it carries the return type and parameter types, which is exactly
// what a caller needs to bind against) and additionally surface the bare name for searching.
static string BareName(string encoded)
{
    var cut = encoded.IndexOf("_Public_", StringComparison.Ordinal);
    if (cut < 0) cut = encoded.IndexOf("_Private_", StringComparison.Ordinal);
    if (cut < 0) cut = encoded.IndexOf("_Internal_", StringComparison.Ordinal);
    if (cut < 0) cut = encoded.IndexOf("_Protected_", StringComparison.Ordinal);
    return cut < 0 ? encoded : encoded[..cut];
}

var types = new List<(string Ns, string Name, JsonObject Node)>();

foreach (var handle in md.TypeDefinitions)
{
    var td = md.GetTypeDefinition(handle);
    var ns = Text(td.Namespace);
    var name = Text(td.Name);

    // `<Module>` is the synthetic global type every assembly carries; it is never interesting here.
    if (!full && name == "<Module>") continue;

    // Counted once: the recovery below compares it against the metadata's own field count for this
    // type, and re-enumerating per member would be quadratic on a 249-field type.
    var interopFieldCount = td.GetFields().Count;

    var fields = new JsonArray();
    foreach (var fh in td.GetFields())
    {
        var fd = md.GetFieldDefinition(fh);
        var raw = Text(fd.Name);

        string emitted;
        if (raw.StartsWith(FieldPrefix, StringComparison.Ordinal)) emitted = raw[FieldPrefix.Length..];
        else if (!full && raw.StartsWith(MethodPrefix, StringComparison.Ordinal)) continue;
        else if (!full && raw.StartsWith("NativeClassPtr", StringComparison.Ordinal)) continue;
        else emitted = raw;

        // Positional recovery: the interop field list and the metadata field list are the same list in
        // the same order (that is how the regeneration produced it), so index N here is index N there.
        // Guarded by an exact count match, because a length mismatch would mean the two lists are NOT
        // the same list and grafting by position would silently mislabel every member.
        if (emitted.Contains(LostNameMarker, StringComparison.Ordinal))
        {
            Il2CppMetadata.TypeMembers? src = null;

            if (recovered is not null)
            {
                var key = ns.Length == 0 ? name : ns + "." + name;
                if (!recovered.TryGetValue(key, out src) && ns.StartsWith("Il2Cpp", StringComparison.Ordinal))
                {
                    // The interop assembly prefixes the game's namespaces with `Il2Cpp`; the metadata
                    // does not. `Il2Cpp.AdvBuff` in the assembly is a bare `AdvBuff` in the metadata.
                    var bare = ns.Length == "Il2Cpp".Length
                        ? name
                        : ns["Il2Cpp".Length..].TrimStart('.') + "." + name;
                    recovered.TryGetValue(bare, out src);
                }
            }

            var slot = fields.Count;
            if (src is not null && src.Fields.Count == interopFieldCount && slot < src.Fields.Count)
            {
                emitted = src.Fields[slot];
                recoveredNames++;
            }
            else
            {
                unrecoverable++;
            }
        }

        fields.Add(new JsonObject
        {
            ["name"] = emitted,
            ["static"] = (fd.Attributes & FieldAttributes.Static) != 0,
            ["public"] = (fd.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public,
        });
    }

    // The interop method table lives in the FIELD list (as `NativeMethodInfoPtr_*` statics), not in the
    // method list — the real methods are thin managed wrappers. Read the pointers, because their names
    // carry the encoded signature that the wrappers do not.
    var methods = new JsonArray();
    foreach (var fh in td.GetFields())
    {
        var fd = md.GetFieldDefinition(fh);
        var raw = Text(fd.Name);
        if (!raw.StartsWith(MethodPrefix, StringComparison.Ordinal)) continue;

        var encoded = raw[MethodPrefix.Length..];
        methods.Add(new JsonObject
        {
            ["name"] = BareName(encoded),
            ["signature"] = encoded,
            ["static"] = encoded.Contains("_Static_", StringComparison.Ordinal),
            ["public"] = encoded.Contains("_Public_", StringComparison.Ordinal),
        });
    }

    // A type with neither fields nor methods after distilling is an empty marker (an interop stub for an
    // enum-like or an attribute). Keeping ~1,500 of those buries the rows that matter.
    if (!full && fields.Count == 0 && methods.Count == 0) continue;

    types.Add((ns, name, new JsonObject
    {
        ["ns"] = ns,
        ["name"] = name,
        ["fields"] = fields,
        ["methods"] = methods,
    }));
}

// Ordinal sort on (namespace, name) so a re-run against the same binary is byte-identical and a real
// diff after a game update is readable rather than a reshuffle.
types.Sort((a, b) =>
{
    var c = string.CompareOrdinal(a.Ns, b.Ns);
    return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
});

// Three artifacts, because one 2.9 MB blob is not reviewable and not greppable:
//
//   index.json      every type, names and counts only — the thing you grep to find a type
//   datamodel.json  full detail for the data-bearing types — the thing you read to bind against one
//   full.json       everything, only with --full — NOT committed; regenerate it in one command
//
// The split is not a size trick. `index` answers "does the game have a X", `datamodel` answers "what
// can I call on it", and those are the two questions this tool exists to serve.
JsonObject Envelope(string kind, int count) => new()
{
    // Deliberately the simple name and the byte size — never the path it was read from.
    ["assembly"] = Path.GetFileName(assemblyPath),
    ["assemblyBytes"] = fileLength,
    ["kind"] = kind,
    ["distilled"] = !full,
    ["typeCount"] = count,
};

// Compact, not indented: these are machine artifacts read through a query, and the indentation tripled
// the byte count without making a 3,700-row array any more readable.
var compact = new JsonSerializerOptions { WriteIndented = false };

var outDir = Path.GetFullPath(outPath);
Directory.CreateDirectory(outDir);

void Write(string fileName, JsonObject envelope, IEnumerable<JsonNode> rows)
{
    envelope["types"] = new JsonArray(rows.ToArray());
    var path = Path.Combine(outDir, fileName);
    File.WriteAllText(path, envelope.ToJsonString(compact));
    Console.WriteLine($"  {fileName,-20} {new FileInfo(path).Length / 1024,6} KB");
}

Write("index.json", Envelope("index", types.Count), types.Select(t => (JsonNode)new JsonObject
{
    ["ns"] = t.Ns,
    ["name"] = t.Name,
    ["fields"] = ((JsonArray)t.Node["fields"]!).Count,
    ["methods"] = ((JsonArray)t.Node["methods"]!).Count,
}));

// What counts as "data-bearing". Name-based rather than shape-based on purpose: the game's own naming is
// consistent (`*DataManager`, `*DataLoader`, `*Config`, `*Data`, `*Info`), and a shape heuristic would
// drag in every UI window that happens to hold an int. Enums are matched separately because an interop
// enum carries its members as fields and is exactly what a sweep iterates.
string[] dataMarkers =
[
    "DataManager", "DataLoader", "Data", "Config", "Info", "Manager", "Table", "Tree", "Type",
];

var dataModel = types
    .Where(t => dataMarkers.Any(m => t.Name.EndsWith(m, StringComparison.Ordinal)))
    .Select(t => (JsonNode)t.Node)
    .ToArray();

Write("datamodel.json", Envelope("datamodel", dataModel.Length), dataModel);

// Enums are the payload this game hides its design in: the effect, buff, status and synergy
// vocabularies are all closed enums, and they are exactly what an identity-mapping pass needs to read.
// An interop enum is a type whose members are constants and which declares no methods.
var enums = types
    .Where(t => ((JsonArray)t.Node["methods"]!).Count == 0
                && ((JsonArray)t.Node["fields"]!).Count > 1)
    .Select(t => (JsonNode)new JsonObject
    {
        ["ns"] = t.Ns,
        ["name"] = t.Name,
        // `value__` is the CLR's backing field for every enum, never a member the game declared.
        ["members"] = new JsonArray(((JsonArray)t.Node["fields"]!)
            .Where(f => (string?)f!["name"] != "value__")
            .Select(f => (JsonNode)(string)f!["name"]!)
            .ToArray()),
    })
    .ToArray();

Write("enums.json", Envelope("enums", enums.Length), enums);

if (full)
    Write("full.json", Envelope("full", types.Count), types.Select(t => (JsonNode)t.Node));

Console.WriteLine($"{types.Count} types scanned, {dataModel.Length} data-bearing, {enums.Length} enums");
if (recoveredNames > 0 || unrecoverable > 0)
    Console.WriteLine($"enum names: {recoveredNames} recovered, {unrecoverable} still lost");
return 0;

string? ArgValue(string flag)
{
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
