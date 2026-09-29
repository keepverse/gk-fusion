using FusionRpg.Core.Diagnostics;
using FusionRpg.Core.Events;
using FusionRpg.Core.Lawn;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// Injector side of A-M2 lawn-reposition's record-then-drain pipeline — modelled directly on
/// <see cref="EventDrainHost"/> (spec-lawn-reposition.md §2, "Record-then-drain"), for the same
/// reason that file's own header gives: moving an actor inside a Harmony hook is a frame-budget
/// bug waiting to happen.
///
/// <b>Ships default-off, on purpose (spec-lawn-reposition.md §6 hazard 4, "SHIPS KNOWINGLY
/// INERT").</b> The real production trigger — a lawn-side caller that raises <c>OnActivate</c> for
/// an actor's movement action — does not exist in this tree yet; <see cref="TryRecordMove"/> has
/// no caller today. That producer is a separate, out-of-scope, named follow-up
/// (spec §6 hazard 4's own acceptance shape) — it does not block this file, and this file does not
/// build it. Flipping <see cref="Enabled"/> by hand (or via a future producer, or the existing
/// <c>debug.effect.fire-synthetic</c> entry point per spec §5 AC10) exercises the whole path end to
/// end; nothing here waits on that caller to exist first.
/// </summary>
public static class MoveDrainHost
{
    /// <summary>Master switch — default off. Env kill: FUSIONRPG_LAWN_MOVE=0 (mirrors
    /// EventDrainHost.Enabled, which mirrors FUSIONRPG_EVENT_V2=0).</summary>
    public static bool Enabled { get; set; }

    // Structural (tunables-ssot.md T2) — a frame-safety ceiling on one Tick's worth of position
    // writes, not a balance dial: a move write is a handful of field assignments, not a funnel
    // walk, so a plain count budget (unlike EventDrainHost's timestamp budget) is enough to keep
    // one frame from ever draining an unbounded backlog. A balance pass has no reason to want this
    // number different.
    const int MaxPerFrame = 64;

    static MoveQueue? _queue;
    static MoveQueue Queue => _queue ??= new MoveQueue();

    /// <summary>Resolves a recorded ptr/side back to the live Plant/Zombie and calls
    /// EntityApply.MoveToCell — the drain's only reach into the entity write path.</summary>
    sealed class Writer : IMoveWriter
    {
        public void Move(in MoveRecord record)
        {
            var ptrHex = record.Ptr.ToString("X");
            switch (record.Side)
            {
                case GameEventSide.Plant:
                    EntityApply.MoveToCell(InjectorEntityRegistry.FindPlant(ptrHex), record.Col, record.Row, record.Source);
                    break;
                case GameEventSide.Zombie:
                    EntityApply.MoveToCell(InjectorEntityRegistry.FindZombie(ptrHex), record.Col, record.Row, record.Source);
                    break;
                // Bullet/None are not actors this module moves — drop silently, same "not a target
                // this host owns" shape EventDrainHost's own side-specific branches use.
            }
        }
    }

    static readonly Writer _writer = new();

    /// <summary>
    /// Records one "move actor to cell" request. Call from a hook or an effect atom reacting to
    /// <c>OnActivate</c> — this method never writes, it only appends to the bounded ring (spec §2).
    /// Returns false while the feature is off, or when the ring is full — the drop is already
    /// counted in <c>MoveQueue.Dropped</c>, matching EventDrainHost's own overflow contract.
    /// </summary>
    public static bool TryRecordMove(IntPtr ptr, byte side, int col, int row, string source)
    {
        if (!Enabled || ptr == IntPtr.Zero) return false;
        return Queue.TryRecord(ptr, side, col, row, source ?? "");
    }

    /// <summary>Ring-overflow drop count, exposed for the same reason EventDrainHost.SnapshotStats
    /// exposes DroppedByOverflow — a lawn under heavy load should read as "dropped: N", never as
    /// silence.</summary>
    public static long Dropped => _queue?.Dropped ?? 0;

    public static int Pending => _queue?.Count ?? 0;

    /// <summary>
    /// Per-frame drain — called from InjectorLoop.Tick, the same slot EventDrainHost.Tick already
    /// occupies. Applies queued moves in recorded order via EntityApply.MoveToCell, bounded by
    /// MaxPerFrame so a deep backlog can never blow the frame budget in one call.
    /// </summary>
    public static void Tick(float unscaledDeltaTime)
    {
        if (!Enabled || _queue == null || _queue.Count == 0) return;
        using var _perf = PerfProbe.Measure(PerfSection.LawnMoveDrain);

        var applied = 0;
        Queue.Drain(_writer, () => ++applied > MaxPerFrame);
    }
}
