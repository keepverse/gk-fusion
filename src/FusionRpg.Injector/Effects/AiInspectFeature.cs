namespace FusionRpg.Injector.Effects;

/// <summary>
/// combat-ai `decision-inspector` (module 10, CAI2.5, spec-decision-inspector.md §5): the lawn
/// inspector's three-layer switch. D4 is *"see the full mechanism, hidden by default"* — so unlike
/// <see cref="LawnBasicAttackFeature"/> (<c>DefaultEnabled = true</c>, a gameplay feature), the safe
/// state here is OFF: this is an instrument, and off is also its zero-cost state (every recording site
/// is guarded, so nothing is built while it is off).
///
/// <para><b>The default is this module's own constant, never borrowed from <c>CheatState</c>.</b>
/// <c>CheatState</c>'s own contract is "session cheat registry keyed by coverage ids" — an ephemeral
/// debug/QA toggle store with no promise of a durable default. Reading its schema fallback as the
/// source of "off by default" is the exact boundary defect
/// <see cref="LawnBasicAttackFeature"/>'s 2026-09-14 correction records, so the shape is copied rather
/// than re-derived: <see cref="DefaultEnabled"/> decides, the env var overrides it at process start,
/// and an EXPLICIT user toggle (<c>CheatState.IsUserSet</c>) is the only other input — a
/// never-toggled id contributes nothing at all.</para>
///
/// <para><b>The rule is a pure function; only the ambient read is static.</b> <see cref="Resolve"/>
/// takes the three inputs explicitly so the whole rule — including the two env-var wins, which are
/// otherwise untestable because the env half is read once at process start — is pinned by its own
/// tests instead of argued from a field initialiser. <see cref="Enabled"/> is then one line: read the
/// environment once, ask <c>CheatState</c> for an explicit override, delegate.</para>
/// </summary>
public static class AiInspectFeature
{
    public const string CheatToggleId = "AI-INSPECT";
    public const string EnvVar = "FUSIONRPG_AI_INSPECT";

    /// <summary>This module's OWN default. FALSE — D4: the inspector is hidden by default, and off is
    /// the zero-cost state. Independent of CheatState/CheatSchema/CheatRegistry.</summary>
    public const bool DefaultEnabled = false;

    // Read once, exactly as LawnBasicAttackFeature does and for the same reason: this gate sits on the
    // decision path, and a per-decision Environment.GetEnvironmentVariable would be an allocation on
    // the hot path of the very thing that must cost nothing when off.
    static readonly string? EnvValue = Environment.GetEnvironmentVariable(EnvVar);

    /// <summary>An explicit debug/QA override ONLY — <c>null</c> (meaning "defer to
    /// <see cref="DefaultEnabled"/>") whenever nobody has ever toggled <see cref="CheatToggleId"/> this
    /// session. Deliberately never reads <c>CheatState.On</c>'s own schema-fallback value as a default
    /// candidate: only a real, explicit user/script toggle counts.</summary>
    static bool? DebugOverride => CheatState.IsUserSet(CheatToggleId) ? CheatState.On(CheatToggleId) : null;

    public static bool Enabled => Resolve(EnvValue, DebugOverride, DefaultEnabled);

    /// <summary>
    /// The whole switch rule, as a pure function of its three inputs — the env value, an explicit
    /// debug/QA override (<c>null</c> = never set), and the module's own default.
    ///
    /// <para>Order is the contract: the env var is read FIRST and wins outright, so
    /// <c>FUSIONRPG_AI_INSPECT=0</c> forces off even against an explicit <c>true</c> toggle (a live
    /// proof must be able to hold the instrument off for a controlled A/B run), and <c>=1</c> forces it
    /// on without any toggle. Only when the env var is neither <c>"0"</c> nor <c>"1"</c> does the
    /// explicit toggle get consulted, and only when no toggle was ever set does the module default
    /// apply. Ordinal comparison — a locale that treats <c>"0"</c> specially must not change a flag.</para>
    /// </summary>
    public static bool Resolve(string? envValue, bool? debugOverride, bool defaultEnabled)
    {
        if (string.Equals(envValue, "0", StringComparison.Ordinal)) return false;
        if (string.Equals(envValue, "1", StringComparison.Ordinal)) return true;
        return debugOverride ?? defaultEnabled;
    }
}
