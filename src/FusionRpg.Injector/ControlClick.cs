using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FusionRpg.Injector;

/// <summary>
/// Type-dispatched click (game-control module <c>control-click</c>). Resolve order per
/// call: ref table → live re-verify (ptr + type + position) → invoke → emit receipt with
/// the acted scope snapshot. The Button arm is provisional verify-or-drop (spec): menu
/// buttons measured live as custom <c>*Btn</c> types, never uGUI <c>Button</c>.
/// </summary>
public static class ControlClick
{
    public static void Run(JsonElement p)
    {
        var tag = "";
        try
        {
            if (p.TryGetProperty("tag", out var tagEl) && tagEl.ValueKind == JsonValueKind.String)
                tag = tagEl.GetString() ?? "";
            var snapshotId = Str(p, "snapshotId") ?? "";
            var refId = Str(p, "ref") ?? "";
            if (string.IsNullOrEmpty(snapshotId) || string.IsNullOrEmpty(refId))
            {
                CheatState.Error("debug.click: snapshotId and ref are required");
                return;
            }
            if (!ControlRefs.TryResolve(snapshotId, refId, out var ptr, out var typeName, out var why))
            {
                CheatState.Error("debug.click refused: " + why);
                return;
            }
            if (!ReverifyLive(ptr, typeName, snapshotId, refId, out var target, out why))
            {
                CheatState.Error("debug.click refused: " + why);
                return;
            }
            if (!Invoke(target!, typeName, out why))
            {
                CheatState.Error("debug.click refused: " + why);
                return;
            }
            DebugRuntime.Emit("debug.click.done", new Dictionary<string, object>
            {
                ["tag"] = tag,
                ["snapshotId"] = snapshotId,
                ["ref"] = refId,
                ["ptr"] = ptr,
                ["type"] = typeName,
                ["ok"] = true,
                ["snapshot"] = ControlRefs.CurrentSnapshot()
            });
            CheatState.Note($"debug click {refId} ({typeName}) ok");
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.click: " + ex.Message);
        }
    }

    static bool ReverifyLive(string ptr, string typeName, string snapshotId, string refId,
        out UObject? target, out string why)
    {
        target = null;
        why = "";
        UObject? found = null;
        string foundType = "";
        foreach (var go in SafeFindAll())
        {
            if (go == null) continue;
            try
            {
                if (PtrEq(go, ptr)) { found = go; foundType = "GameObject"; break; }
                var comps = go.GetComponents<Component>();
                if (comps == null) continue;
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    if (PtrEq(c, ptr)) { found = c; foundType = RealType(c); break; }
                }
                if (found != null) break;
            }
            catch { /* keep scanning */ }
        }
        if (found == null)
        {
            why = "ptr " + ptr + " no longer live (entity died) — take a fresh inspect snapshot";
            return false;
        }
        if (!string.Equals(foundType, typeName, StringComparison.OrdinalIgnoreCase) &&
            !foundType.EndsWith("." + typeName, StringComparison.OrdinalIgnoreCase) &&
            !foundType.EndsWith("_" + typeName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(foundType, "GameObject", StringComparison.Ordinal))
        {
            why = $"ptr reused by {foundType} (was {typeName}) — take a fresh inspect snapshot";
            return false;
        }
        if (ControlRefs.TryPosition(snapshotId, refId, out var col, out var row) && (col >= 0 || row >= 0))
        {
            if (!PositionMatches(found, col, row))
            {
                why = "entity moved since snapshot — take a fresh inspect snapshot";
                return false;
            }
        }
        target = found;
        return true;
    }

    static bool PositionMatches(UObject found, int col, int row)
    {
        try
        {
            if (found is Plant p)
            {
                if (col >= 0 && p.thePlantColumn != col) return false;
                if (row >= 0 && p.thePlantRow != row) return false;
                return true;
            }
            if (found is Zombie z)
            {
                if (row >= 0 && z.theZombieRow != row) return false;
                return true;
            }
            if (found is GridItem g)
            {
                if (col >= 0 && g.theItemColumn != col) return false;
                if (row >= 0 && g.theItemRow != row) return false;
                return true;
            }
        }
        catch { return false; }
        return true;
    }

    static bool Invoke(UObject target, string typeName, out string why)
    {
        why = "";
        try
        {
            if (target is CardUI card)
            {
                var mouse = Mouse.Instance;
                if (mouse == null) { why = "no Mouse.Instance"; return false; }
                mouse.ClickOnCard(card);
                return true;
            }
            if (target is UnityEngine.UI.Button button)
            {
                button.onClick.Invoke();
                return true;
            }
            if (target is Component comp)
            {
                var real = RealType(comp);
                if (real.IndexOf("Btn", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    comp.SendMessage("OnMouseDown");
                    comp.SendMessage("OnMouseUp");
                    return true;
                }
            }
            if (target is GameObject go)
            {
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var real = RealType(c);
                    if (real.IndexOf("Btn", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        c.SendMessage("OnMouseDown");
                        c.SendMessage("OnMouseUp");
                        return true;
                    }
                }
            }
            why = "no invoke rule for " + typeName + " (closed dispatch table)";
            return false;
        }
        catch (Exception ex)
        {
            why = "invoke threw: " + (ex.InnerException ?? ex).Message;
            return false;
        }
    }

    static List<GameObject> SafeFindAll()
    {
        try
        {
            var found = UObject.FindObjectsOfType<GameObject>();
            return found != null ? new List<GameObject>(found) : new List<GameObject>();
        }
        catch { return new List<GameObject>(); }
    }

    static bool PtrEq(UObject o, string ptr)
    {
        try { return string.Equals(GameDumps.Ptr((Il2CppObjectBase)(object)o), ptr, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static string RealType(Component c)
    {
        try
        {
            var s = c.ToString();
            if (!string.IsNullOrEmpty(s))
            {
                var open = s.LastIndexOf('(');
                var close = s.LastIndexOf(')');
                if (open >= 0 && close > open + 1)
                {
                    var full = s.Substring(open + 1, close - open - 1);
                    var dot = full.LastIndexOf('.');
                    return dot >= 0 ? full.Substring(dot + 1) : full;
                }
            }
        }
        catch { }
        try { return c.GetType().Name; } catch { return "?"; }
    }

    static string? Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;
}
