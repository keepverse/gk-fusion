namespace FusionRpg.Injector.Effects;

/// <summary>
/// lawn-combat-wire T10/T12's shared kill switch (lawn-combat-wire-plan.md "Kill-switch env var name":
/// fixed here so both tasks agree). Disables grant-binding (T10) AND cost-charging (T12) together —
/// with it off, the lawn is byte-identical to today: no actor ever holds an `OnDamageDealt` grant, so
/// `EffectRuntime.HasOnDamageDealtGrant()` stays false exactly as it is without this feature at all.
///
/// <para><b>Default ON — owner decision 2026-09-16.</b> It shipped default OFF on 2026-09-15
/// (lawn-combat-wire L-N1) because T13's 300-zombie wave breached the frame budget with the feature on:
/// the RPG capture pipeline took <b>28.06% of wall</b> against a ≤6% ceiling (now gk-core/data/tuning/lawn-perf-budget.v1.json), at 19.8 fps
/// (<c>_baseline-lcw-300z-env-on-a.json</c>). That ruling named its own release condition — "until a
/// measured perf pass clears it" — and the pass has now run.
///
/// <para>Three fixes landed between the two rulings: the derived fold became one pass instead of one
/// scan per channel (<c>83054adb</c>), <c>ActorHudCache</c> started honouring its dirty set
/// (<c>5a3e941c</c>), and the world-HUD walk stopped rescanning the whole scene every frame
/// (<c>b48f0e7e</c>). Measured after them at ~290 zombies, fresh process per arm, with the world-HUD
/// sync budget at its default: <c>effect.onCapture</c> is <b>3.46% of wall at 59.7 fps</b> with this
/// feature on, against 0.00% at 59.4 fps with it off
/// (<c>_baseline-lcw-300z-{hud-resync-b60,featoff-b60}.json</c>). Under the ceiling, and the feature
/// now costs about a third of one frame per second.</para>
///
/// <para>The env var still decides first and is read once at process start:</para>
/// <c>"0"</c> forces off, <c>"1"</c> forces on (the only way a live proof may enable it: a mid-match toggle on
/// never binds actors that spawned while it was off). With the env var unset, an explicit debug/QA toggle
/// may override the default; turning it off mid-match withdraws every bound basic-attack grant
/// (<c>LawnBasicAttackGrantBinder.Tick</c>, L-N8).</para>
///
/// <para><b>2026-09-14 correction (lawn-combat-wire T10/T12 live-inert investigation):</b> this used to
/// read <c>CheatState.On(CheatToggleId)</c> directly, borrowing <c>CheatSchema.EffectiveToggle</c>'s
/// own default-true fallback as this flag's ONLY source of "on by default." That is a boundary defect,
/// not a bug in <c>CheatState</c> — that class's own doc comment names its real, narrower job:
/// "Session cheat registry keyed by coverage ids from cheat-menu-coverage.md," an ephemeral debug/QA
/// toggle store. It never promised to be a durable, always-correct production feature-flag source, and
/// nothing about its contract guarantees a fresh/rehydrated/reset session reaches combat still reading
/// the fallback the way a real flag must. A real always-on-by-default production switch cannot borrow
/// its default from a system whose own contract does not promise one — the SOLID-boundary hard rule
/// this repo's AGENTS.md states ("depend on registered abstractions, not a mode-local concrete
/// composer," the identical shape one level down). <see cref="DefaultEnabled"/> below is now this module's
/// OWN default, read first; <c>CheatState</c> is consulted only as an OPTIONAL, EXPLICIT debug/QA
/// override — never as the thing that decides what "default" means. <c>OVERLAY-COMBAT</c> (this
/// class's own sibling, referenced above) has the identical shape and is left alone here — same-class
/// finding, separate task, not fixed in this pass.</para>
/// </summary>
public static class LawnBasicAttackFeature
{
    public const string CheatToggleId = "LAWN-BASIC-ATTACK";
    public const string EnvVar = "FUSIONRPG_LAWN_BASIC_ATTACK";

    /// <summary>This module's own default — true (owner decision 2026-09-16, after the perf pass
    /// L-N1's default-off was conditioned on; see the class note for the numbers), independent of
    /// CheatState/CheatSchema/CheatRegistry. The single source of truth for "on unless something
    /// explicitly disables it": <c>FUSIONRPG_LAWN_BASIC_ATTACK=0</c> at process start, or an explicit
    /// debug/QA toggle.</summary>
    public const bool DefaultEnabled = true;

    // Env is read once — this gate sits on the spawn/die path, same discipline as OverlayCombatFeature.
    static readonly string? EnvValue = Environment.GetEnvironmentVariable(EnvVar);
    static readonly bool EnvForcedOff = string.Equals(EnvValue, "0", StringComparison.Ordinal);
    static readonly bool EnvForcedOn = string.Equals(EnvValue, "1", StringComparison.Ordinal);

    /// <summary>An explicit debug/QA override ONLY — null (meaning "defer to <see cref="DefaultEnabled"/>")
    /// whenever nobody has ever toggled <see cref="CheatToggleId"/> this session
    /// (<c>CheatState.IsUserSet</c> false). Deliberately never reads <c>CheatState.On</c>'s own
    /// schema-fallback value as a default candidate — only a REAL, explicit user/script toggle counts
    /// as an override, exactly so a debug endpoint or the (currently nonexistent) web checkbox can
    /// still force this off for a controlled A/B measurement without this flag's default ever again
    /// depending on CheatState's rehydrate/reset timing.</summary>
    static bool? DebugOverride => CheatState.IsUserSet(CheatToggleId) ? CheatState.On(CheatToggleId) : null;

    public static bool Enabled => !EnvForcedOff && (EnvForcedOn || (DebugOverride ?? DefaultEnabled));
}
