using FusionRpg.Core.Overlay;
using UnityEngine;

namespace FusionRpg.Injector.Hud;

/// <summary>
/// Holds the lawn still while the player is in the web UI, so opening the control room mid-wave
/// does not cost a run.
///
/// This class decides and remembers; it does <b>not</b> write <c>Time.timeScale</c>.
/// <c>CheatActions.TickContinuous</c> asserts the timescale every frame (G-TIMEFREEZE /
/// G-TIMESCALE), so a second writer here would simply be overwritten on the next frame whenever a
/// speed setting is active. It stays the single writer and reads <see cref="Active"/> first.
/// </summary>
public static class OverlayPause
{
    static bool _active;
    static float _captured = 1f;
    static bool _restorePending;

    /// <summary>True while the overlay is holding the lawn still.</summary>
    public static bool Active => _active;

    /// <summary>
    /// Per-frame, Unity thread. Captures the running timescale on the way in so a player's own
    /// speed setting survives the round trip.
    /// </summary>
    public static void Apply(bool shouldPause)
    {
        try
        {
            if (shouldPause == _active) return;

            if (shouldPause)
            {
                _captured = Time.timeScale;
                _active = true;
            }
            else
            {
                _active = false;
                _restorePending = true;
            }
        }
        catch { }
    }

    /// <summary>
    /// One-shot: the scale to hand back after a resume, so the writer can restore it without a
    /// second class touching <c>Time.timeScale</c>. Returns false when there is nothing to restore.
    /// </summary>
    public static bool ConsumeRestore(out float scale)
    {
        scale = 1f;
        if (!_restorePending) return false;
        _restorePending = false;
        scale = OverlayPausePolicy.ResumeScale(_captured);
        return true;
    }

    /// <summary>Board ended or the plugin is going away — never leave the lawn frozen.</summary>
    public static void Clear()
    {
        if (_active) _restorePending = true;
        _active = false;
    }
}
