using System.Threading;
using FusionRpg.Injector.Lawn;
using UnityEngine;

using FusionRpg.Injector.Host;

namespace FusionRpg.Injector.Stats;

/// <summary>
/// Sole Unity position mutator for A-M2 lawn-reposition — exactly the relationship
/// <see cref="EntityStatWriter"/> has to combat fields (spec-lawn-reposition.md §2, "The single
/// writer"). <c>EntityApply.MoveToCell</c> is its only caller; nothing else in the injector may
/// assign a Plant/Zombie transform or cell field (<c>thePlantRow</c>/<c>thePlantColumn</c>/
/// <c>theZombieRow</c>/<c>transform.position</c>/<c>.localPosition</c>) — enforced by the
/// extended <c>gk-fusion/scripts/guard-single-writer.py</c>, not by convention alone.
///
/// ⛔ Why this does NOT call <c>LawnCoords.CellCenter</c> (spec §2, "the landmine"):
/// <c>CellCenter</c>'s null-<c>Mouse</c> fallback returns <c>new Vector2(col, row)</c> — the grid
/// INDICES treated as a WORLD position (<c>LawnCoords.cs:71</c>). For its ~20 existing read-only
/// callers (VFX, probes) that is a harmless degradation — the effect lands somewhere wrong and
/// vanishes. For an actor WRITE it is a silent, permanent teleport to within nine world units of
/// the origin, off the lawn, with nothing logged and nothing thrown. So this file reads
/// <c>Mouse.Instance</c> itself, explicitly, exactly once per write, and drops the move — counted,
/// never thrown — instead of trusting that fallback. <b>Do not "fix" <c>CellCenter</c> instead</b>
/// — its behaviour is correct for its other ~20 callers; the narrowing belongs here, at the one
/// actor-write site this module adds, and nowhere else.
/// </summary>
public static class EntityPositionWriter
{
    // Visible drop/apply counters — the same "drops and counts, never silently" discipline
    // MoveQueue.Dropped already uses for ring overflow, so a lawn where Mouse is absent reads as
    // "moves dropped: N (no coordinate source)" rather than as a feature that silently did nothing
    // (spec §2).
    static long _droppedNoMouse;
    static long _applied;

    public static long DroppedNoMouse => Interlocked.Read(ref _droppedNoMouse);
    public static long Applied => Interlocked.Read(ref _applied);

    public static void WritePlantPosition(Plant p, int col, int row, string source)
    {
        if (p == null) return;
        try
        {
            if (!TryCellCenter(col, row, out var world))
            {
                Interlocked.Increment(ref _droppedNoMouse);
                ProofNote($"writer.move.dropped plant col={col} row={row} reason=no-mouse src={source}");
                return;
            }

            p.thePlantColumn = col;
            p.thePlantRow = row;
            var pos = p.transform.position;
            p.transform.position = new Vector3(world.x, world.y, pos.z);

            Interlocked.Increment(ref _applied);
            ProofWrite("plant", p.Pointer, source, col, row, world);
        }
        catch (Exception ex) { CheatState.Error("writer.move.plant: " + ex.Message); }
    }

    public static void WriteZombiePosition(Zombie z, int col, int row, string source)
    {
        if (z == null) return;
        try
        {
            if (!TryCellCenter(col, row, out var world))
            {
                Interlocked.Increment(ref _droppedNoMouse);
                ProofNote($"writer.move.dropped zombie col={col} row={row} reason=no-mouse src={source}");
                return;
            }

            z.theZombieRow = row;
            var pos = z.transform.position;
            z.transform.position = new Vector3(world.x, world.y, pos.z);

            Interlocked.Increment(ref _applied);
            ProofWrite("zombie", z.Pointer, source, col, row, world);
        }
        catch (Exception ex) { CheatState.Error("writer.move.zombie: " + ex.Message); }
    }

    /// <summary>The one fallible read this module trusts <c>CellCenter</c> NOT to hide:
    /// <c>Mouse.Instance</c>, checked explicitly so a null (or a throw reading it) drops the move
    /// instead of silently writing <c>LawnCoords.cs:71</c>'s <c>new Vector2(col, row)</c>
    /// near-origin fallback. Col/row are already clamped by the caller (EntityApply.MoveToCell via
    /// MoveDecisionPolicy) — this method trusts that and does not re-clamp.</summary>
    static bool TryCellCenter(int col, int row, out Vector2 world)
    {
        world = default;
        try
        {
            var mouse = Mouse.Instance;
            if (mouse == null) return false;
            world = new Vector2(mouse.GetBoxXFromColumn(col), mouse.GetBoxYFromRow(row));
            return true;
        }
        catch { return false; }
    }

    static void ProofWrite(string side, IntPtr ptr, string source, int col, int row, Vector2 world)
    {
        if (!(CheatState.EmitProof && CheatState.On("SYS-EMIT-PROOF"))) return;
        try
        {
            var payload = new Dictionary<string, object>
            {
                ["side"] = side,
                ["ptr"] = ptr.ToString("X"),
                ["source"] = source ?? "",
                ["col"] = col,
                ["row"] = row,
                ["worldX"] = world.x,
                ["worldY"] = world.y
            };
            CheatState.TagProbe(payload);
            GameHooks.Emit("stat.move", payload);
        }
        catch { /* never break the write for proof */ }
    }

    static void ProofNote(string msg)
    {
        if (!(CheatState.EmitProof && CheatState.On("SYS-EMIT-PROOF"))) return;
        try { CheatState.Note(msg); } catch { }
    }
}
