namespace FusionRpg.Injector.Effects;

/// <summary>Feature gate for overlay combat calculator — env or cheat toggle.</summary>
public static class OverlayCombatFeature
{
    public const string CheatToggleId = "OVERLAY-COMBAT";
    public const string EnvVar = "FUSIONRPG_OVERLAY_COMBAT";

    // Env is read once — GetEnvironmentVariable allocates and this gate sits on the damage path.
    static readonly bool EnvEnabled =
        string.Equals(Environment.GetEnvironmentVariable(EnvVar), "1", StringComparison.Ordinal);

    public static bool Enabled => EnvEnabled || CheatState.On(CheatToggleId);
}
