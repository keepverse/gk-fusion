using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai.Lawn;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Match.Ai;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// combat-ai `lawn-actor-view` (CAI4.1, spec-lawn-actor-view.md §"Lazy build, and what 'lazy' means
/// precisely"): the injector's composition root for the twelve-member view — it supplies the seven
/// seams <see cref="LawnBattleView"/> takes and caches at most one view per perspective per frame.
///
/// <para><b>Perspective-scoped, and the oracle is built per perspective.</b> A lawn has exactly two
/// deciding perspectives (<see cref="LawnBattleView.PerspectiveCount"/>), so the perspective is the
/// side id the <see cref="MechanicalOwnSideOracle"/> reads — <c>"plant"</c> for the player's view,
/// <c>"zombie"</c> for Zomboss's. The relation chain is constructed <b>with</b> the view rather than
/// shared across perspectives, because <see cref="IOwnSideOracle.RelationOf"/> is relative: one oracle
/// cannot answer for both sides.</para>
///
/// <para><b>Lazy, and allocation-free once warm.</b> <see cref="ViewFor"/> compares
/// <c>(perspective, frame, the census instance)</c>: a repeat call in the same frame over the same
/// snapshot returns the SAME view object and allocates nothing. The census instance matters because a
/// spawn or death inside one frame invalidates the board (<c>InjectorBoardSnapshot.Invalidate</c>) and
/// a frame number alone would then hand back a view built over a stale census. The delegate handed to
/// the view is <c>() =&gt; census</c>, so the view still materialises only on its first read — the
/// module-19 decision tick that calls this is the only caller, and it exists to be called when a
/// decision is actually due.</para>
///
/// <para><b>Six seams are wired to their production sources, and one is stated.</b>
/// <c>censusOf</c> → <see cref="InjectorBoardSnapshot.Capture"/> (already frame-cached);
/// <c>relation</c> → <see cref="SpecimenOwnershipOracle"/> over <c>CheatState.TryGetSpecimenController</c>
/// composed specimen-first with <see cref="MechanicalOwnSideOracle"/> (the shipped precedence);
/// <c>unitOf</c> → <see cref="LawnUnitViewFactory"/> over the registry's live HP pair;
/// <c>derived</c> → <c>InjectorStatusBridge.ResolveDerived</c>, which is what
/// <see cref="LawnDerivedCache"/>'s own doc names for production; <c>elementOf</c> →
/// <c>LawnElementResolverHost.Resolve</c>, the same per-ptr element read the grant binder uses</para>
///
/// <para><b>The two documented stand-ins, both because the lawn has no producer for them YET.</b>
/// <c>heldActionsOf</c> returns EMPTY until module 16's registry lands (CAI4.3) — which the spec
/// endorses in its own words ("the delegate returns an empty list until then, which `StubIntentSource`
/// already handles as 'cannot act at all'"). <c>statusMaskOf</c> reads <b>0</b>, and this is the one
/// place this host deviates from the spec's table, which names `EffectRuntime.Status`: measured
/// 2026-09-23, there is no per-ptr status-mask producer the injector can read. `SimEffectHost` declares
/// `StatusMaskOf` but it is an INSTANCE property of CORE's sim host (set by nobody anywhere in `src/`),
/// and `StatusRuntime` exposes instances rather than a mask — the mask needs the compiler's
/// status-id→bit interning, which no injector caller supplies. So <c>FactsOf.StatusMask</c> reports "no
/// statuses" until a producer exists. That is a stated absence, not a second mechanism: inventing a
/// bit-interner read here would fork the compiler's own mapping. Recorded as the row's remaining
/// dependency.</para>
/// </summary>
public static class LawnActorViewHost
{
    /// <summary>Structural on a CLOSED set — the two deciding perspectives a lawn has. Mirrors
    /// <see cref="LawnBattleView.PerspectiveCount"/> rather than re-declaring a second number.</summary>
    public const int PerspectiveCount = LawnBattleView.PerspectiveCount;

    /// <summary>The side ids <see cref="MechanicalOwnSideOracle"/> documents ("plant"/"zombie").</summary>
    public const string PlantSide = "plant";
    public const string ZombieSide = "zombie";

    static readonly object Gate = new();
    static readonly Func<string, IReadOnlyList<CompiledAction>> NoHeldActions =
        _ => Array.Empty<CompiledAction>();

    /// <summary>
    /// The board census read. Production never sets this — it IS
    /// <see cref="InjectorBoardSnapshot.Capture"/> — but it is a settable delegate for the same reason
    /// <c>ActorHudCache.Build</c> is: the real one needs a live Unity runtime
    /// (<c>UnityEngine.CoreModule</c>), so a test that wants to exercise THIS class's cache would
    /// otherwise need a running game. Setting it is the shipped idiom here, not a new seam shape.
    /// </summary>
    public static Func<BoardSnapshot> CensusOf { get; set; } = InjectorBoardSnapshot.Capture;

    static string? _cachedPerspective;
    static int _cachedFrame = int.MinValue;
    static BoardSnapshot? _cachedCensus;
    static LawnBattleView? _cachedView;

    /// <summary>
    /// The view for one perspective at one frame, or <c>null</c> when the board has no census to build
    /// from. A repeat call with the same perspective, frame and census instance returns the SAME object
    /// and allocates nothing.
    /// </summary>
    public static LawnBattleView? ViewFor(string perspective, int frame)
    {
        if (string.IsNullOrWhiteSpace(perspective))
            throw new ArgumentException("perspective is required (a side id: 'plant' or 'zombie')", nameof(perspective));

        lock (Gate)
        {
            var census = CensusOf();
            if (_cachedView is not null
                && _cachedFrame == frame
                && ReferenceEquals(_cachedCensus, census)
                && string.Equals(_cachedPerspective, perspective, StringComparison.Ordinal))
                return _cachedView;

            var view = Build(perspective, census);
            _cachedPerspective = perspective;
            _cachedFrame = frame;
            _cachedCensus = census;
            _cachedView = view;
            return view;
        }
    }

    /// <summary>
    /// Drops the cached view. A match reset does NOT strictly need this — the census instance changes,
    /// so the next <see cref="ViewFor"/> rebuilds anyway — but a switch that must free the view (or a
    /// test that wants a cold start) has one place to say so.
    /// </summary>
    public static void Clear()
    {
        lock (Gate)
        {
            _cachedPerspective = null;
            _cachedFrame = int.MinValue;
            _cachedCensus = null;
            _cachedView = null;
        }
    }

    static LawnBattleView Build(string perspective, BoardSnapshot census)
    {
        // One oracle per perspective, built here rather than shared: the relation it answers is
        // relative to the side it was constructed with.
        var relation = new LawnRelationChain(
            new SpecimenOwnershipOracle(CheatState.TryGetSpecimenController),
            new MechanicalOwnSideOracle(perspective, census.FindPtr));

        return new LawnBattleView(
            relation,
            censusOf: () => census,
            unitOf: ptr => UnitOf(ptr, relation),
            heldActionsOf: NoHeldActions,
            elementOf: ElementOf,
            statusMaskOf: StatusMaskOf,
            derived: new LawnDerivedCache(
                ptr => InjectorStatusBridge.ResolveDerived(ptr, attackerLess: false),
                // lawn LW1.6: the invalidation channel's read. One revision per (player, ptr), bumped
                // by a received invalidation and compared here — so a bump costs this memo exactly one
                // further resolve for the actor that is actually read, and nothing for the others.
                LawnLiveness.RevisionOf));
    }

    /// <summary>The visible-unit half: the live HP pair from the registry, resolved through the SAME
    /// relation oracle the view reads its side from, so a unit's own `Relation` cannot disagree with
    /// `SideOf` for the same ptr.</summary>
    static ILawnUnitView? UnitOf(string ptr, IOwnSideOracle relation)
    {
        var plant = InjectorEntityRegistry.FindPlant(ptr);
        if (plant is not null)
            return LawnUnitViewFactory.Build(ptr, plant.thePlantHealth, plant.thePlantMaxHealth, relation);

        var zombie = InjectorEntityRegistry.FindZombie(ptr);
        return zombie is null
            ? null
            : LawnUnitViewFactory.Build(
                ptr, Bridges.ZombieCombatFields.GetHp(zombie), Bridges.ZombieCombatFields.GetMaxHp(zombie), relation);
    }

    /// <summary>`EntityFacts.ElementId` is an ordinal and its own doc reads "-1 when none"; the live
    /// battle path fills it `(int)ElementPrimary : 0` (`BattleRunState.cs:971`), and this mirrors that
    /// shipped shape rather than inventing a second convention for the same field.</summary>
    static int ElementOf(string ptr)
    {
        var (_, _, elements, found) = LawnElementResolverHost.Resolve(ptr);
        if (!found) return 0;
        return elements.Primary is { } primary ? (int)primary : 0;
    }

    /// <summary>The status mask. <b>0 is a stated absence, not a reading</b> — see the class doc: no
    /// per-ptr status-mask producer exists for the injector to read (`SimEffectHost.StatusMaskOf` is an
    /// instance property of Core's sim host, set by nobody; `StatusRuntime` exposes instances, not a
    /// mask). When a producer lands, this is the one line that changes.</summary>
    static ulong StatusMaskOf(string ptr) => NoStatusMask(ptr);

    static readonly Func<string, ulong> NoStatusMask = _ => 0UL;
}
