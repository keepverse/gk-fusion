using UnityEngine;

namespace FusionRpg.Injector;

/// <summary>
/// <c>debug.leave-board</c> — leave a live lawn the way a player does: open the in-game menu, press
/// 主菜单 (main menu), press 确定 (confirm). Owner-specified 2026-09-15 after <c>debug.ui-nav back-to-menu</c>
/// (a direct <c>UIMgr.BackToMenu</c> call) was shown by screenshot to leave the Board alive — lawnmowers
/// drawn over the main menu and every later <c>debug.enter-level</c> refused with "board already live".
///
/// <para>Run as a pending operation ticked from <see cref="Host.InjectorLoop"/>: each press waits until its
/// control is live (the menu and the confirm dialog animate in), and the ack
/// (<c>debug.leave-board</c>) is emitted once — when the Board has been destroyed
/// (<see cref="GameHooks.Board"/> cleared by <c>Board.OnDestroy</c>), or with the stage that failed.
/// Controls and their press methods were read live: the menu button is a <c>UIButton</c>
/// (<c>OnMouseUpAsButton</c>), the two dialog buttons are <c>PauseMenu_Btn</c> (<c>OnMouseDown</c>/<c>OnMouseUp</c>).
/// On the lose screen the single press is the lose menu's own back-to-menu button.</para>
/// </summary>
public static class DebugLeaveBoard
{
    const string MenuButtonPath = "CanvasUp/CanvasUp/LeftButtons/BackToMainMenu";
    const string MainMenuPath = "CanvasUp/PauseMenu(Clone)/mainmenu";
    const string ConfirmPath = "CanvasUp/PauseMenu_checkQuit(Clone)/Confirm";
    // On the seed-picker the in-battle menu button does not exist and the picker's own 主菜单 ignores a
    // synthetic press (live 2026-09-15), so leave-board starts the battle first with the picker's start button.
    const string PickerStartPath = "CanvasUp/InGameUI(Clone)/Bottom/SeedLibrary/StartGameButton";
    // After a loss the in-battle menu is gone and the lose screen carries its own back-to-menu button
    // (a LoseMenuBtn, pressed with OnMouseDown/OnMouseUp like the dialog buttons) — lawn-combat-wire L-N32.
    const string LoseBackToMenuPath = "CanvasUp/LoseMenu(Clone)/backtomenu";

    // Structural (not tunable): acknowledgement and animation bounds for a debug command.
    const long PressSettleMs = 600;
    const long TimeoutMs = 20_000;

    sealed class Pending
    {
        public long StartedMs;
        public int Stage; // 0 open menu, 1 press main menu, 2 confirm, 3 wait for board destroyed
        public long StageSinceMs;
        public bool PickerStarted;
        public readonly Dictionary<string, object> Dump = new();
    }

    static Pending? _pending;

    public static void Start()
    {
        var now = Environment.TickCount64;
        if (GameHooks.Board == null && Board.Instance == null)
        {
            DebugRuntime.Emit("debug.leave-board", new Dictionary<string, object>
            {
                ["ok"] = true,
                ["stage"] = "no-board",
                ["waitedMs"] = 0
            });
            return;
        }
        _pending = new Pending { StartedMs = now, StageSinceMs = now };
        CheatState.Note("debug.leave-board pending (menu → main menu → confirm)");
    }

    public static void Tick()
    {
        var p = _pending;
        if (p == null) return;
        var now = Environment.TickCount64;
        try
        {
            if (now - p.StartedMs > TimeoutMs)
            {
                Finish(p, false, StageName(p.Stage), $"stage '{StageName(p.Stage)}' did not complete within {TimeoutMs / 1000}s", now);
                return;
            }
            if (now - p.StageSinceMs < PressSettleMs) return;

            switch (p.Stage)
            {
                case 0:
                    if (Press(LoseBackToMenuPath, "OnMouseDown", "OnMouseUp"))
                    {
                        p.Dump["loseMenuMs"] = now - p.StartedMs;
                        p.Stage = 3;
                        p.StageSinceMs = now;
                        return;
                    }
                    if (Press(MenuButtonPath, "OnMouseUpAsButton"))
                    {
                        Advance(p, now);
                        return;
                    }
                    if (!p.PickerStarted && Press(PickerStartPath, "OnMouseDown", "OnMouseUp"))
                    {
                        p.PickerStarted = true;
                        p.Dump["pickerStartedMs"] = now - p.StartedMs;
                        p.StageSinceMs = now;
                    }
                    return;
                case 1:
                    if (!Press(MainMenuPath, "OnMouseDown", "OnMouseUp")) return;
                    Advance(p, now);
                    return;
                case 2:
                    if (!Press(ConfirmPath, "OnMouseDown", "OnMouseUp")) return;
                    Advance(p, now);
                    return;
                default:
                    if (GameHooks.Board == null && Board.Instance == null)
                        Finish(p, true, "left", null, now);
                    return;
            }
        }
        catch (Exception ex)
        {
            Finish(p, false, StageName(p.Stage), ex.Message, now);
        }
    }

    static void Advance(Pending p, long now)
    {
        p.Dump[StageName(p.Stage) + "Ms"] = now - p.StartedMs;
        p.Stage++;
        p.StageSinceMs = now;
    }

    static string StageName(int stage) => stage switch
    {
        0 => "open-menu",
        1 => "press-main-menu",
        2 => "confirm",
        _ => "board-destroyed"
    };

    /// <summary>Presses the active control at <paramref name="path"/>; false while it is not live yet.</summary>
    static bool Press(string path, params string[] messages)
    {
        GameObject? go;
        try { go = GameObject.Find(path); } catch { go = null; }
        if (go == null || !go.activeInHierarchy) return false;
        foreach (var m in messages) go.SendMessage(m);
        return true;
    }

    static void Finish(Pending p, bool ok, string stage, string? error, long now)
    {
        if (!ReferenceEquals(_pending, p)) return;
        _pending = null;
        p.Dump["ok"] = ok;
        p.Dump["stage"] = stage;
        p.Dump["waitedMs"] = now - p.StartedMs;
        if (error != null) p.Dump["error"] = error;
        DebugRuntime.Emit("debug.leave-board", p.Dump);
        if (ok) CheatState.Note("debug.leave-board left after " + p.Dump["waitedMs"] + "ms");
        else CheatState.Error("debug.leave-board: " + error);
    }
}
