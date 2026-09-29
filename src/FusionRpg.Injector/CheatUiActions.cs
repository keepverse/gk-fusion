using System.Collections.Concurrent;

using FusionRpg.Injector.Host;

namespace FusionRpg.Injector;

/// <summary>
/// F8 buttons must NOT run FindObjects/Die/PushScales inside OnGUI — an exception mid-ScrollView
/// permanently breaks Unity IMGUI for the session (clicks appear dead on every tab).
/// Queue work here; RpgLoop.Update drains on the main thread.
/// </summary>
public static class CheatUiActions
{
    static readonly ConcurrentQueue<Action> Pending = new();

    public static void Enqueue(Action action, string label = "")
    {
        if (action == null) return;
        Pending.Enqueue(() =>
        {
            try
            {
                if (!string.IsNullOrEmpty(label))
                    RpgHost.Log.Info("[cheat-ui] " + label);
                action();
            }
            catch (Exception ex)
            {
                CheatState.Error("ui-action " + label + ": " + ex.Message);
                RpgHost.Log.Error("[cheat-ui] " + label + ": " + ex);
            }
        });
    }

    public static void Drain()
    {
        while (Pending.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception ex)
            {
                CheatState.Error("ui-drain: " + ex.Message);
                RpgHost.Log.Error("[cheat-ui] drain: " + ex);
            }
        }
    }
}
