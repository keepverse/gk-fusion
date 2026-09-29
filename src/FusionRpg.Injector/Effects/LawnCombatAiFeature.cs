namespace FusionRpg.Injector.Effects;

/// <summary>
/// combat-ai `lawn-cast-trigger` (module 19, CAI4.8, spec-lawn-cast-trigger.md §"Kill switch"): the lawn
/// combat-AI feature's switch, with <see cref="LawnBasicAttackFeature"/>'s shape EXACTLY — its own
/// <see cref="DefaultEnabled"/> const, the env var read once at process start, and an explicit debug/QA
/// toggle as the only other input, never <c>CheatState</c>'s schema fallback as the source of a default.
/// That shape exists because of the 2026-09-14 live-inert investigation this class's sibling records:
/// <c>CheatState</c>'s contract is "session cheat registry keyed by coverage ids", an ephemeral debug
/// store that never promised a durable default.
///
/// <para><b>Default OFF, and that is the shipping state rather than a placeholder.</b> The feature's
/// content feed — module 16's held sets, which need an `ActionCatalog` the injector has no feed for — is
/// still owed (`CAI4.3`), so a default-on lawn would run a decision loop with nothing to decide. Off is
/// also the zero-cost state: <see cref="LawnDecisionHost"/> drops every counter and releases every token
/// the moment this reads false.</para>
///
/// <para>The rule is a pure function (<see cref="Resolve"/>) so the two env-var wins, which are otherwise
/// untestable because the env half is read once at startup, are pinned by tests.</para>
/// </summary>
public static class LawnCombatAiFeature
{
    public const string CheatToggleId = "LAWN-COMBAT-AI";
    public const string EnvVar = "FUSIONRPG_LAWN_COMBAT_AI";

    /// <summary>This module's OWN default. FALSE — the lawn's decision loop ships off until its content
    /// feed lands (`CAI4.3`), and off is the state that costs nothing.</summary>
    public const bool DefaultEnabled = false;

    // Read once, exactly as LawnBasicAttackFeature does: this gate sits on the per-frame decision path.
    static readonly string? EnvValue = System.Environment.GetEnvironmentVariable(EnvVar);

    static bool? DebugOverride => CheatState.IsUserSet(CheatToggleId) ? CheatState.On(CheatToggleId) : null;

    public static bool Enabled => Resolve(EnvValue, DebugOverride, DefaultEnabled);

    /// <summary>The whole switch rule: the env var wins outright, then an EXPLICIT toggle, then this
    /// module's own default. Ordinal comparison — a locale that treats "0" specially must not move it.</summary>
    public static bool Resolve(string? envValue, bool? debugOverride, bool defaultEnabled)
    {
        if (string.Equals(envValue, "0", System.StringComparison.Ordinal)) return false;
        if (string.Equals(envValue, "1", System.StringComparison.Ordinal)) return true;
        return debugOverride ?? defaultEnabled;
    }
}
