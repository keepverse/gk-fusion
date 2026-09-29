using System.Text;

namespace FusionRpg.Tools.GameMetaDump;

/// <summary>
/// Recovers enum member names that the interop assembly lost.
///
/// <para><b>Why this is needed.</b> Il2CppInterop regenerates a managed assembly from the game's Il2Cpp
/// metadata, and on this game it fails for every enum whose members are named in Chinese: instead of the
/// member name it writes the literal string <c>EnumValueAsmResolver.DotNet.Serialized.SerializedConstant</c>
/// — a <c>ToString()</c> of its own internal object — into the field name. The enum's <i>values</i> survive
/// (0..N, in order); only the names are destroyed. Measured on the 3.9 pack: <b>18 enums, 628 members</b>,
/// and they are the most interesting ones in the game — `AdvBuff` (177), `TravelDebuff` (142),
/// `UltiBuff` (56), `TalentType` (47), `FruitBuffType` (25), `SynergyType` (19).</para>
///
/// <para><b>Where the real names live.</b> <c>global-metadata.dat</c>, the file Il2CppInterop itself read.
/// Its string blob holds the original UTF-8 identifiers, so the names were never lost by the game — only
/// by the regeneration step. Reading them back is a table walk, not a decompile.</para>
///
/// <para><b>Scope.</b> This reads three tables — the string blob, the field table, and the type-definition
/// table — and returns names. It decodes no IL, no constants, no string literals (the
/// <c>stringLiteral</c> tables, which hold the game's user-facing text, are deliberately never touched),
/// and it never loads or executes anything.</para>
/// </summary>
internal static class Il2CppMetadata
{
    const uint Sanity = 0xFAB11BAF;

    /// <summary>
    /// The only metadata version this parser claims to understand. Every offset below was verified
    /// against a v31 file; a different version moves them silently, which would produce confident
    /// nonsense rather than an error. So we refuse instead of guessing.
    /// </summary>
    const int SupportedVersion = 31;

    // Header: u32 sanity, i32 version, then (offset, size) u32 pairs. Index into those pairs.
    const int PairString = 2;
    const int PairFields = 11;
    const int PairTypeDefinitions = 19;

    // Il2CppTypeDefinition, v31. Verified by locating a known enum (`AdvBuff`) and checking that the
    // field_count read here equals the member count the interop assembly independently reports (178).
    const int TypeDefStride = 88;
    const int TypeDefNameIndex = 0x00;
    const int TypeDefNamespaceIndex = 0x04;
    const int TypeDefFieldStart = 0x20;
    const int TypeDefFieldCount = 0x44;  // u16

    // Il2CppFieldDefinition, v31: { i32 nameIndex; i32 typeIndex; u32 token; }
    const int FieldStride = 12;

    internal sealed record TypeMembers(string Namespace, string Name, IReadOnlyList<string> Fields);

    /// <summary>
    /// Every type's field names, keyed by <c>Namespace.Name</c>. Returns null (with a reason) rather than
    /// throwing when the file is not a metadata blob or not a version we verified against.
    /// </summary>
    internal static Dictionary<string, TypeMembers>? TryRead(string path, out string? error)
    {
        error = null;

        byte[] b;
        try { b = File.ReadAllBytes(path); }
        catch (Exception ex) { error = $"could not read metadata: {ex.Message}"; return null; }

        if (b.Length < 0x100) { error = "metadata file is too small to hold a header"; return null; }

        var sanity = BitConverter.ToUInt32(b, 0);
        if (sanity != Sanity)
        {
            error = $"not an il2cpp metadata file (sanity 0x{sanity:X8}, expected 0x{Sanity:X8})";
            return null;
        }

        var version = BitConverter.ToInt32(b, 4);
        if (version != SupportedVersion)
        {
            error = $"metadata version {version} is not supported (this parser was verified against " +
                    $"v{SupportedVersion}; the table offsets move between versions, so parsing anyway " +
                    "would produce wrong names rather than an error)";
            return null;
        }

        (int Offset, int Size) Pair(int index) => (
            BitConverter.ToInt32(b, 8 + index * 8),
            BitConverter.ToInt32(b, 8 + index * 8 + 4));

        var (strOffset, strSize) = Pair(PairString);
        var (fieldOffset, fieldSize) = Pair(PairFields);
        var (typeOffset, typeSize) = Pair(PairTypeDefinitions);

        foreach (var (o, s, name) in new[]
                 {
                     (strOffset, strSize, "string"),
                     (fieldOffset, fieldSize, "fields"),
                     (typeOffset, typeSize, "typeDefinitions"),
                 })
        {
            if (o < 0 || s < 0 || (long)o + s > b.Length)
            {
                error = $"{name} table is out of range (offset {o}, size {s}, file {b.Length})";
                return null;
            }
        }

        if (typeSize % TypeDefStride != 0)
        {
            error = $"typeDefinitions size {typeSize} is not a multiple of the v31 stride {TypeDefStride}";
            return null;
        }

        // A null-terminated UTF-8 identifier at a byte offset into the string blob.
        string? Str(int index)
        {
            if (index < 0 || index >= strSize) return null;
            var start = strOffset + index;
            var end = start;
            var limit = strOffset + strSize;
            while (end < limit && b[end] != 0) end++;
            if (end == start) return string.Empty;
            try { return Encoding.UTF8.GetString(b, start, end - start); }
            catch { return null; }
        }

        var fieldCount = fieldSize / FieldStride;
        string? FieldName(int index) =>
            index < 0 || index >= fieldCount
                ? null
                : Str(BitConverter.ToInt32(b, fieldOffset + index * FieldStride));

        var typeCount = typeSize / TypeDefStride;
        var result = new Dictionary<string, TypeMembers>(StringComparer.Ordinal);

        for (var i = 0; i < typeCount; i++)
        {
            var baseOffset = typeOffset + i * TypeDefStride;

            var name = Str(BitConverter.ToInt32(b, baseOffset + TypeDefNameIndex));
            if (string.IsNullOrEmpty(name)) continue;
            var ns = Str(BitConverter.ToInt32(b, baseOffset + TypeDefNamespaceIndex)) ?? string.Empty;

            var start = BitConverter.ToInt32(b, baseOffset + TypeDefFieldStart);
            var count = BitConverter.ToUInt16(b, baseOffset + TypeDefFieldCount);

            var members = new List<string>(count);
            if (start >= 0)
            {
                for (var k = 0; k < count; k++)
                    if (FieldName(start + k) is { } fn)
                        members.Add(fn);
            }

            // Il2Cpp namespaces several distinct types under the same simple name (three `Status` enums
            // ship in this game). Key on the full name so a recovery can never graft one type's members
            // onto another's — the failure that would be hardest to notice downstream.
            var key = ns.Length == 0 ? name : ns + "." + name;
            result[key] = new TypeMembers(ns, name, members);
        }

        return result;
    }
}
