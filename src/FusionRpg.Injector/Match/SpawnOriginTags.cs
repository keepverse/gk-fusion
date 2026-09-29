namespace FusionRpg.Injector.Match;

/// <summary>
/// live-probe Task 18: which lawn entities were created by an Injector command rather than by the
/// game, so their death can say so. <c>GameHooks</c> stamps the result as <c>spawnOrigin</c> on
/// <c>zombie.die</c> / <c>plant.die</c>; the Server stores that payload on the <c>ZombieKilled</c> /
/// <c>PlantLost</c> activity fact, and the soul ledger's kill rows point at those facts — so a soul
/// balance fed by debug-spawned kills is attributable (<c>gk-fusion/tools/ProveLiveProbe</c> step 0).
///
/// <para>Origins: <see cref="Debug"/> for Game Injector Debug commands (<c>debug.spawn-*</c>,
/// <c>debug.ice-road</c>), <see cref="Cheat"/> for the cheat-panel spawns, <see cref="Game"/> for
/// everything else — including the real <c>pvz.spawn.extra</c> deploy path.</para>
///
/// <para>A mark is consumed by the entity's death (<see cref="TakeOnDeath"/>) and dropped by
/// <c>GameHooks.ForgetEntity</c> and match end, so a native address the game reuses for a new entity
/// never inherits it. Marked after the create call returns, never cleared on a spawn hook: Unity may
/// run <c>Start</c> a frame later, which would erase the mark just written.</para>
/// </summary>
public static class SpawnOriginTags
{
    public const string Game = "game";
    public const string Debug = "debug";
    public const string Cheat = "cheat";

    static readonly object Gate = new();
    static readonly Dictionary<IntPtr, string> Marks = new();

    public static void Mark(IntPtr ptr, string origin)
    {
        if (ptr == IntPtr.Zero) return;
        lock (Gate) Marks[ptr] = origin;
    }

    /// <summary>The origin to stamp on this entity's die payload; removes the mark.</summary>
    public static string TakeOnDeath(IntPtr ptr)
    {
        lock (Gate)
            return Marks.Remove(ptr, out var origin) ? origin : Game;
    }

    public static int Count
    {
        get { lock (Gate) return Marks.Count; }
    }

    public static void Forget(IntPtr ptr)
    {
        lock (Gate) Marks.Remove(ptr);
    }

    public static void Clear()
    {
        lock (Gate) Marks.Clear();
    }
}
