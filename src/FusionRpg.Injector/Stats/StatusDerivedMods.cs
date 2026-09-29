using System;
using System.Collections.Generic;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived.Subsystems;
using FusionRpg.Core.Status;

namespace FusionRpg.Injector.Stats;

/// <summary>
/// The injector half of the STATUS `stat.derived` executor (mechanism-wiring G1's injector half,
/// spec-mechanism-wiring.md §4.1) — mirrors <see cref="GrantedDerivedAtoms"/>'s exact shape and its own
/// doc comment's reasoning verbatim.
///
/// <para><b>Why the reading logic lives in Core, not here.</b> This assembly targets net6.0 against the
/// game's BepInEx/Il2Cpp interop DLLs, so it needs a real PVZ Fusion install to build, and `ci.yml`
/// names ten test projects, none of them the injector. Everything Unity-free — walking a host's live
/// statuses, filtering to derived channels, parsing the op, source-tagging by instance — lives in
/// <see cref="StatusDerivedModReader"/>, which any of the ten CI projects can reach.</para>
///
/// <para>What is left here is exactly what is genuinely host-specific and cannot be tested off-host:
/// reaching the live <c>EffectRuntime.Status</c> static.</para>
/// </summary>
public static class StatusDerivedMods
{
    /// <summary>Every derived-channel mod this actor's currently active statuses contribute. Never
    /// null; never throws — a status runtime that is not up yet (no live match) is a normal state, not
    /// an error, exactly as <see cref="GrantedDerivedAtoms.For"/> treats an unready bag.</summary>
    public static IReadOnlyList<StatusDerivedMod> For(StatContext ctx)
    {
        if (ctx is null) return Array.Empty<StatusDerivedMod>();

        StatusRuntime status;
        try
        {
            status = Effects.EffectRuntime.Status;
        }
        catch { return Array.Empty<StatusDerivedMod>(); }

        return StatusDerivedModReader.Read(status, ctx);
    }
}
