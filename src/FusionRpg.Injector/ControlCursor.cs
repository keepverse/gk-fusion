using System.Text.Json;
using FusionRpg.Injector.Hud;

namespace FusionRpg.Injector;

/// <summary>
/// Drain glue for the real-cursor tier (game-control module <c>control-cursor</c>).
/// Parses and validates; all OS work lives in <see cref="Win32Input"/>.
/// </summary>
public static class ControlCursor
{
    static long _lastMs;

    public static void Run(JsonElement p)
    {
        var tag = "";
        try
        {
            if (p.TryGetProperty("tag", out var tagEl) && tagEl.ValueKind == JsonValueKind.String)
                tag = tagEl.GetString() ?? "";
            var confirmed = p.TryGetProperty("confirmedLiveCursor", out var c) &&
                c.ValueKind == JsonValueKind.True;
            var x = Int(p, "x", -1);
            var y = Int(p, "y", -1);
            var click = !(p.TryGetProperty("click", out var cl) && cl.ValueKind == JsonValueKind.False);
            var refusal = Win32Input.Check(confirmed, Environment.TickCount64, ref _lastMs);
            if (refusal != null)
            {
                CheatState.Error("debug.cursor refused: " + refusal);
                return;
            }
            if (x < 0 || y < 0)
            {
                CheatState.Error("debug.cursor refused: x/y are required client pixels");
                return;
            }
            if (!Win32Input.TryMoveClick(x, y, click, out var error))
            {
                CheatState.Error("debug.cursor refused: " + error);
                return;
            }
            Volatile.Write(ref _lastMs, Environment.TickCount64);
            DebugRuntime.Emit("debug.cursor.done", new Dictionary<string, object>
            {
                ["tag"] = tag,
                ["x"] = x,
                ["y"] = y,
                ["clicked"] = click,
                ["foreground"] = true
            });
            CheatState.Note($"debug cursor {(click ? "click" : "move")} @{x},{y}");
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.cursor: " + ex.Message);
        }
    }

    static int Int(JsonElement p, string name, int dflt) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var e) && e.TryGetInt32(out var v) ? v : dflt;
}
