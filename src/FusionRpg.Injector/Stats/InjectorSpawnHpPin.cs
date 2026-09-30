using FusionRpg.Core.Stats;

namespace FusionRpg.Bridge.Stats;

/// <summary>
/// Injector home of the per-ptr debug-spawn max-HP pin — the rules live in Core
/// <see cref="SpawnHpPin"/> (and its tests); this class only owns the one match-scoped instance.
///
/// <para>Why a per-ptr pin exists (lawn-combat-wire, 2026-09-15): `debug.spawn-plant`/`debug.spawn-zombie`
/// write the GLOBAL <c>P-HP</c>/<c>P-MAXHP</c> Tab-B channels, which an <c>includeAbsolute: false</c>
/// reapply (e.g. <c>cheat.pushScales</c>) drops for every entity — proven live: `hp
/// 500000/500000-&gt;300/300` one line after the spawn's own write.</para>
///
/// <para>L-N20: <see cref="EntityApply"/> feeds the pin into <c>ActorHub.Resolve</c> as an absolute
/// input (<see cref="SpawnHpPin.ApplyTo"/>), so Hub max-HP bonuses compose on top of it. The old
/// post-write re-assert replaced the composed max and erased those bonuses.</para>
///
/// <para>Lifetime: not cleared on debug-session end — the workflow is "buff HP at spawn, end the
/// session, fight on the real v2 path". Removed per ptr from <c>GameHooks.ForgetEntity</c> (IL2CPP
/// reuses addresses inside a match; confirmed live — a respawn inherited a dead zombie's 20000 HP pin)
/// and cleared at match end (<c>GameHooks.ClearMatch</c>).</para>
/// </summary>
public static class InjectorSpawnHpPin
{
    public static readonly SpawnHpPin Store = new();

    public static void Pin(string? ptr, int maxHp) => Store.Pin(ptr, maxHp);  // overflow-bounded: a debug spawn pin feeding the int cheat-absolute map, which is written into PvZ's int max-health field (host limit, docs/architecture/numeric-types.md numeric rule 4)

    public static void Remove(string? ptr) => Store.Remove(ptr);

    public static void Clear() => Store.Clear();
}
