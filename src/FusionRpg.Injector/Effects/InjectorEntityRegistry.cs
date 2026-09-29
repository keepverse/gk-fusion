using FusionRpg.Core.Combat;
using FusionRpg.Injector.Lawn;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// Hook-fed live Plant/Zombie registry so <see cref="InjectorBoardSnapshot"/> never pays a
/// per-event <c>FindObjectsOfType</c> scan (~10ms each, b2-live-x2 baseline). Same pattern as
/// Fx.AnchorResolver: Start/InitHealth postfixes add, die hooks remove, and a throttled full
/// resync catches units the hooks never saw. Main-thread only, like every caller.
///
/// v3 A4 — incremental snapshots: each entry caches an immutable <see cref="BoardEntitySnap"/>.
/// Plants never move → snap built once per lifetime (glove-moves heal at the next resync).
/// Zombies walk → their snap refreshes at most every <see cref="ZombieSnapRefreshMs"/> (col
/// drift over 150ms is far below one lawn column — precision note in event-pipeline-v2-ssot).
/// Snap instances are immutable and safely shared across successive frozen BoardSnapshots.
/// </summary>
public static class InjectorEntityRegistry
{
    /// <summary>Frames between full-scan resyncs (~4s at 240fps, ~17s at 60fps).</summary>
    public const int ResyncFrames = 1024;

    /// <summary>Zombie snap (col/row/mind-control) refresh throttle.</summary>
    public const int ZombieSnapRefreshMs = 150;

    sealed class PlantEntry
    {
        public Plant P = null!;
        public BoardEntitySnap? Snap;
    }

    sealed class ZombieEntry
    {
        public Zombie Z = null!;
        public string PtrHex = "";
        public int TypeId;
        public BoardEntitySnap? Snap;
        public long SnapAtMs;
    }

    static readonly Dictionary<IntPtr, PlantEntry> Plants = new();
    static readonly Dictionary<IntPtr, ZombieEntry> Zombies = new();
    static int _lastResyncFrame = int.MinValue;

    /// <summary>E28 fix #1 (spec-param-parity.md §3 row 1): the six-resource pool registry for lawn
    /// actors, keyed the same way this whole class keys everything else for a live match. Lives here
    /// rather than as a free-floating static because its lifecycle (per-actor drop on death, full
    /// clear on board reset) mirrors the shield lifecycle flush already below almost exactly.</summary>
    public static readonly FusionRpg.Core.Combat.LawnActorResourcePools ResourcePools = new();

    public static void Add(Plant? p)
    {
        try
        {
            if (p == null || p.Pointer == IntPtr.Zero) return;
            Plants[p.Pointer] = new PlantEntry { P = p };
            var typeId = -1;
            try { typeId = (int)p.thePlantType; } catch { }
            if (typeId >= 0) QueueInnateShield("plant", typeId, p.Pointer);
            // lawn-combat-wire T10, 2026-09-14 (4th defect in the live-inert investigation): a Unity
            // object Start() only ever fires ONCE per lifetime -- a REUSED/pooled Plant (confirmed
            // live: this game's own "frozen wave" lab-overlay replacement spawn reactivates one rather
            // than instantiating fresh) never re-runs PlantStart's Harmony postfix, so it never reaches
            // GameHooks.Emit("plant.spawn") and LawnBasicAttackGrantBinder.QueueSpawn was NEVER called
            // for it -- confirmed by trace: the replacement plant that landed every observed vanilla
            // hit for the rest of that match never appears in the grant binder's log at all, while the
            // ORIGINAL plant (a genuine Start()) did. This class's own doc already names Resync's full
            // rescan as the fallback for "units the [Start/InitHealth] hooks never saw" -- but nothing
            // downstream of THAT edge ever queued a grant. Queuing here, at the one place every entry
            // path (Start hook AND Resync) already funnels through, closes it. Safe to call on every
            // Add, including a resync repeat of an already-granted actor: QueueSpawn's own drain
            // (LawnBasicAttackGrantBinder.Bind) upserts by a ptr-scoped GrantId, so a repeat is a
            // harmless no-op re-grant, never a duplicate.
            LawnBasicAttackGrantBinder.QueueSpawn(p.Pointer.ToString("X"));
        }
        catch { }
    }

    public static void Add(Zombie? z)
    {
        try
        {
            if (z == null || z.Pointer == IntPtr.Zero) return;
            var typeId = 0;
            try { typeId = (int)z.theZombieType; } catch { }
            Zombies[z.Pointer] = new ZombieEntry
            {
                Z = z,
                PtrHex = z.Pointer.ToString("X"),
                TypeId = typeId
            };
            QueueInnateShield("zombie", typeId, z.Pointer);
            // Same reasoning and same fix as Add(Plant?) above -- a reused/pooled Zombie never re-runs
            // ZombieStart/InitHealth's own Emit("zombie.spawn"), so it never reached QueueSpawn either.
            LawnBasicAttackGrantBinder.QueueSpawn(z.Pointer.ToString("X"));
        }
        catch { }
    }

    /// <summary>
    /// Innate content-row shields (shield-system-spec.md §2.6): queued here, applied on the
    /// first shield tick (capacity read behind the barrier). Granted-once tracking in the
    /// runtime keeps resync re-Adds from re-forming a broken innate shield.
    /// </summary>
    static void QueueInnateShield(string side, int typeId, IntPtr ptr)
    {
        try
        {
            if (FusionRpg.Core.Combat.Shield.ShieldInnateCatalog.IsEmpty) return;
            if (!FusionRpg.Core.Combat.Shield.ShieldInnateCatalog.TryGet(side, typeId, out var def)) return;
            var runtime = EffectRuntime.Bag.ShieldGate?.Runtime;
            runtime?.QueueInnate(new FusionRpg.Core.Combat.Shield.ShieldGrant
            {
                OwnerKey = FusionRpg.Contracts.EffectOwnerKeys.Entity(
                    FusionRpg.Core.Combat.CombatPtr.Normalize(ptr.ToString("X"))),
                SourceId = "innate:" + typeId,
                Element = def.Element,
                BaseHp = def.BaseHp,
                Priority = def.Priority,
                RefillOnMerge = false,
                IsInnate = true
            });
        }
        catch { }
    }

    public static void Remove(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        Plants.Remove(ptr);
        Zombies.Remove(ptr);
        // Death/lifecycle flush (shield-system-spec.md §2.6): shields, innate markers, and
        // regen carry die with the actor so a reused ptr starts clean.
        try
        {
            Effects.EffectRuntime.Bag.ShieldGate?.Runtime.RemoveAll(
                FusionRpg.Contracts.EffectOwnerKeys.Entity(
                    FusionRpg.Core.Combat.CombatPtr.Normalize(ptr.ToString("X"))));
        }
        catch { }
        // E28 fix #1: the five non-hp resource pools carry die with the actor too, same reasoning
        // as the shield flush directly above — a reused ptr must not inherit a stranger's drained pool.
        try { ResourcePools.Remove(ptr.ToString("X")); } catch { }
        // lawn LW1.3 (spec-exhaustion-event.md): the exhaustion edge detector's per-actor window is the
        // same class of state and dies on the same edge, for the same reason the pool flush directly
        // above names — a reused ptr must not inherit a stranger's exhausted window (nor emit its
        // recovery), and IL2CPP reuses addresses.
        try { Effects.LawnExhaustionLifecycle.Forget(ptr.ToString("X")); } catch { }
        // lawn LW1.6 (spec-actor-liveness-refresh.md): the liveness revision's per-actor row dies on the
        // same edge and for the same reason — a reused ptr must not read as already-invalidated (or as
        // never-invalidated) because of the actor that used to live at this address.
        try { Effects.LawnLiveness.Forget(ptr.ToString("X")); } catch { }
        // combat-ai decision-inspector (CAI2.5): the AI decision ring's last-per-actor index is
        // withdrawn on this same edge, for the same reason — IL2CPP reuses pointers, so a stale index
        // entry would attribute a NEW actor's decisions to the dead one (a fabrication by accident,
        // the failure mode the inspector is most exposed to). This is the lawn's real per-actor death
        // edge for BOTH sides (GameHooks' plant-death postfix and NoteZombieDead, which re-removes on
        // every death-animation frame because a resync can re-Add a dying zombie). ADDITIVE: nothing
        // above is removed or reordered. Entries already in the RING keep their own ptr and tick —
        // only the live index is cleared, so history is never rewritten.
        try
        {
            Effects.LawnAiDecisionObservability.ForgetActor(
                Effects.LawnAiDecisionObservability.ActorKeyOf(ptr));
        }
        catch { }
        // combat-ai `commander-direct-orders` (CAI4.9): a live direct order dies with its actor, on the
        // same edge and for the same reason (a reused ptr must not inherit a dead actor's order). The
        // queue keys by the same `entity:{ptr}` string the ring uses. ADDITIVE: nothing above changes.
        try { Effects.LawnOrderHost.Remove(Effects.LawnAiDecisionObservability.ActorKeyOf(ptr)); } catch { }
        // combat-ai `lawn-cast-trigger` (CAI4.8): the trigger counters and the cast token die with the
        // actor on the same edge, so a reused ptr starts at zero swings and no lock. ADDITIVE.
        try { Effects.LawnDecisionHost.Remove(Effects.LawnAiDecisionObservability.ActorKeyOf(ptr)); } catch { }
    }

    public static void Clear()
    {
        Plants.Clear();
        Zombies.Clear();
        _lastResyncFrame = int.MinValue;
        // Board-start barrier only — Resync clears the dicts directly and must NOT wipe
        // shields (it re-Adds the same live actors every ResyncFrames).
        try { EffectRuntime.Bag.ShieldGate?.Runtime.Clear(); } catch { }
        try { ResourcePools.Clear(); } catch { }
        // lawn LW1.3: a new match is a fresh exhaustion window for every actor, never a carried-over one.
        try { Effects.LawnExhaustionLifecycle.Clear(); } catch { }
        // lawn LW1.6: a new match is a fresh revision window for every actor, never a carried-over one.
        try { Effects.LawnLiveness.Clear(); } catch { }
        // combat-ai decision-inspector (CAI2.5): match end / board reset clears the AI decision ring's
        // index AND its ring (the ring's own Clear, unlike ForgetActor) — a decision from the previous
        // match must not answer "why did this creature do that" in the next one. ADDITIVE.
        try { Effects.LawnAiDecisionObservability.Clear(); } catch { }
        // combat-ai `commander-direct-orders` (CAI4.9): the board edge clears every live order too —
        // an order issued in one match must not command a creature in the next. ADDITIVE.
        try { Effects.LawnOrderHost.Clear(); } catch { }
        // combat-ai `lawn-cast-trigger` (CAI4.8): the board edge drops every counter and token too.
        try { Effects.LawnDecisionHost.Clear(); } catch { }
    }

    public static bool NeedsResync(int frame) =>
        frame < 0 || frame - _lastResyncFrame >= ResyncFrames || frame < _lastResyncFrame;

    /// <summary>Full-scan resync — the only remaining <c>FindObjectsOfType</c> on the combat path.
    /// Also heals cached-snap staleness (glove-moved plants, missed hooks).</summary>
    public static void Resync(int frame)
    {
        _lastResyncFrame = frame;
        Plants.Clear();
        Zombies.Clear();
        try
        {
            foreach (var p in UObject.FindObjectsOfType<Plant>())
                Add(p);
        }
        catch { }
        try
        {
            foreach (var z in UObject.FindObjectsOfType<Zombie>())
                Add(z);
        }
        catch { }
    }

    /// <summary>
    /// Append current-board snaps (A4). Plant snaps build once; zombie snaps refresh at most
    /// every <see cref="ZombieSnapRefreshMs"/>. Entities that throw (destroyed native side)
    /// are dropped from the registry.
    /// </summary>
    public static void CollectSnaps(List<BoardEntitySnap> into)
    {
        var now = Environment.TickCount64;
        List<IntPtr>? dead = null;

        foreach (var kv in Plants)
        {
            var e = kv.Value;
            try
            {
                if (e.P == null) { (dead ??= new List<IntPtr>()).Add(kv.Key); continue; }
                if (e.Snap == null)
                {
                    if (e.P.thePlantType == PlantType.Nothing) continue;
                    e.Snap = new BoardEntitySnap
                    {
                        Ptr = kv.Key.ToString("X"),
                        Side = "plant",
                        TypeId = (int)e.P.thePlantType,
                        Col = e.P.thePlantColumn,
                        Row = e.P.thePlantRow
                    };
                }
                into.Add(e.Snap);
            }
            catch { (dead ??= new List<IntPtr>()).Add(kv.Key); }
        }
        if (dead != null) { foreach (var k in dead) Plants.Remove(k); dead.Clear(); }

        foreach (var kv in Zombies)
        {
            var e = kv.Value;
            try
            {
                if (e.Z == null) { (dead ??= new List<IntPtr>()).Add(kv.Key); continue; }
                if (e.Snap == null || now - e.SnapAtMs >= ZombieSnapRefreshMs)
                {
                    if (e.TypeId == (int)ZombieType.Nothing && e.Snap == null)
                    {
                        try { e.TypeId = (int)e.Z.theZombieType; } catch { }
                        if (e.TypeId == (int)ZombieType.Nothing) continue;
                    }

                    var col = -1;
                    try { col = LawnCoords.ColFromX(e.Z.transform.position.x); } catch { }
                    if (col < 0)
                    {
                        try { col = e.Z.Column; } catch { }
                    }
                    var row = 0;
                    try { row = e.Z.theZombieRow; } catch { }
                    var mc = false;
                    try { mc = e.Z.isMindControlled; } catch { }

                    e.Snap = new BoardEntitySnap
                    {
                        Ptr = e.PtrHex,
                        Side = "zombie",
                        TypeId = e.TypeId,
                        Col = col,
                        Row = row,
                        MindControlled = mc
                    };
                    e.SnapAtMs = now;
                }
                into.Add(e.Snap);
            }
            catch { (dead ??= new List<IntPtr>()).Add(kv.Key); }
        }
        if (dead != null) foreach (var k in dead) Zombies.Remove(k);
    }

    /// <summary>Iterate live plants; entities that throw (destroyed native side) are dropped.</summary>
    public static void VisitPlants(Action<Plant> visit)
    {
        List<IntPtr>? dead = null;
        foreach (var kv in Plants)
        {
            try
            {
                if (kv.Value.P == null) { (dead ??= new List<IntPtr>()).Add(kv.Key); continue; }
                visit(kv.Value.P);
            }
            catch { (dead ??= new List<IntPtr>()).Add(kv.Key); }
        }
        if (dead != null)
            foreach (var k in dead) Plants.Remove(k);
    }

    public static void VisitZombies(Action<Zombie> visit)
    {
        List<IntPtr>? dead = null;
        foreach (var kv in Zombies)
        {
            try
            {
                if (kv.Value.Z == null) { (dead ??= new List<IntPtr>()).Add(kv.Key); continue; }
                visit(kv.Value.Z);
            }
            catch { (dead ??= new List<IntPtr>()).Add(kv.Key); }
        }
        if (dead != null)
            foreach (var k in dead) Zombies.Remove(k);
    }

    public static int PlantCount => Plants.Count;
    public static int ZombieCount => Zombies.Count;

    /// <summary>O(1) ptr-string lookup. Null on miss — callers needing certainty fall back to a scan.</summary>
    public static Zombie? FindZombie(string? ptrHex) =>
        TryParsePtr(ptrHex, out var p) && Zombies.TryGetValue(p, out var e) ? e.Z : null;

    public static Plant? FindPlant(string? ptrHex) =>
        TryParsePtr(ptrHex, out var p) && Plants.TryGetValue(p, out var e) ? e.P : null;

    static bool TryParsePtr(string? s, out IntPtr ptr)
    {
        ptr = IntPtr.Zero;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
        if (!long.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v) || v == 0)
            return false;
        ptr = new IntPtr(v);
        return true;
    }
}
