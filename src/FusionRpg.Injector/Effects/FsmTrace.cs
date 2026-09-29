namespace FusionRpg.Injector.Effects;

/// <summary>
/// Opt-in per-hit basic-attack pipeline trace (`fsm-trace ...` notes). Off by default because every
/// call site sits on the per-bullet / per-hit hot path; `SYS-EMIT-PROOF` is on by default and must not
/// gate this. Enable with <c>FUSIONRPG_FSM_TRACE=1</c> at process start. Read once, no lock.
/// </summary>
public static class FsmTrace
{
    public static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("FUSIONRPG_FSM_TRACE"), "1", StringComparison.Ordinal);
}
