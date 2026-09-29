namespace FusionRpg.Tools.ProveLiveProbe;

/// <summary>
/// The tool's own refusal logic, factored out as pure functions so they are unit-testable without a
/// live server (spec-live-probe-tool.md "Boundaries" + tasks/live-probe-todo.md Task 8). Every check
/// here runs BEFORE the first HTTP call — a refusal must never be discovered by making the fabrication
/// call and then catching a bad response.
/// </summary>
public static class Guardrails
{
    /// <summary>
    /// Mode B's step 1 MUST be a real acquisition (<c>POST /api/creatures/summon</c>) — the identity-
    /// only debug shortcut mints a synthetic <c>"DEBUG" + Guid</c> ptr that never exists on any live
    /// board (spec-live-probe-tool.md "Two modes, not one"). Combining Mode B with that shortcut can
    /// never produce a real live-board ptr for step 6 to read, so it is refused here, before any HTTP
    /// call is attempted — never discovered later as an empty/timed-out step 6.
    /// </summary>
    /// <returns>A human-readable refusal reason, or <c>null</c> when the combination is fine.</returns>
    public static string? CheckModeBAcquisition(ProbeMode mode, AcquireVia resolvedAcquireVia)
    {
        if (mode == ProbeMode.B && resolvedAcquireVia == AcquireVia.DebugShortcut)
            return "REFUSED: -Mode B combined with the debug-shortcut acquisition path can never reach " +
                   "a real live-board ptr (the shortcut mints a synthetic \"DEBUG\"+Guid ptr that no " +
                   "board ever spawns) — Mode B's step 1 must be a real POST /api/creatures/summon. " +
                   "Refusing before any HTTP call.";
        return null;
    }

    /// <summary>
    /// <c>loadoutJson</c> at step 4 (deploy) stays empty/omitted, always — a non-empty override is the
    /// exact fabrication vector this whole program exists to close (the 2026-09-13 incident: a
    /// fabricated loadout bound straight into the Injector's debug path, then read back from that same
    /// injector's own telemetry as "proof"). The ONLY caller allowed to pass a non-empty value here is
    /// this program's own incident-catching test, and even then the tool refuses to send it — it only
    /// proves the refusal fires, never that the override reaches the server.
    /// </summary>
    public static string? CheckLoadoutOverride(string? dangerousLoadoutJsonOverride)
    {
        if (!string.IsNullOrWhiteSpace(dangerousLoadoutJsonOverride))
            return "REFUSED: a non-empty loadoutJson override was supplied. loadoutJson stays " +
                   "empty/omitted at every deploy this tool makes — this is the exact fabrication " +
                   "vector live-probe-tool exists to close. Refusing before any HTTP call.";
        return null;
    }

    /// <summary>Every refusal check the tool runs before the first HTTP call. Returns the first
    /// refusal found, or <c>null</c> when the run may proceed.</summary>
    public static string? PreflightRefusal(Options o) =>
        CheckLoadoutOverride(o.DangerousLoadoutJsonOverride) ??
        CheckModeBAcquisition(o.Mode, o.ResolvedAcquireVia);

    /// <summary>
    /// Step 2's own translation, called out by name in the spec: <c>-AptitudeId X -AptitudePoints N</c>
    /// becomes <c>Shares = { [X]: N }</c>, never a flat <c>{AptitudeId, Points}</c> body
    /// (<c>AptitudeEndpoints.AllocateUniqueAptitudesRequest.Shares</c> is a <c>Dictionary&lt;string,
    /// long&gt;</c>). A blank aptitude id yields an empty dict — the caller (Program.cs) treats that as
    /// "skip step 2", not as a zero-point allocation of an unknown id.
    /// </summary>
    public static Dictionary<string, long> BuildShares(string? aptitudeId, long points) =>
        string.IsNullOrWhiteSpace(aptitudeId)
            ? new Dictionary<string, long>(StringComparer.Ordinal)
            : new Dictionary<string, long>(StringComparer.Ordinal) { [aptitudeId.Trim()] = points };
}
