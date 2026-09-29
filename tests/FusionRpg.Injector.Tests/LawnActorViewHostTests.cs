using FusionRpg.Core.Combat;
using FusionRpg.Injector.Effects;
using Xunit;

namespace FusionRpg.Injector.Tests;

/// <summary>
/// combat-ai `lawn-actor-view` module 12, CAI4.1 (spec-lawn-actor-view.md §"Lazy build, and what 'lazy'
/// means precisely"): the injector composition root's own contract — one view per perspective per frame,
/// over one census instance, and the perspective really being the oracle's side.
///
/// <para><b>Why the host and not the view.</b> <see cref="FusionRpg.Core.Actions.Ai.Lawn.LawnBattleView"/>
/// and its two siblings already ship 18 Core tests (side only from the oracle, one resolve per
/// (ptr, revision) per frame, the downed read answered rather than inherited). What had no test is the
/// WIRING and the CACHE: which oracle each perspective gets, and the (perspective, frame, census
/// instance) key that makes a second call in one frame free while a same-frame spawn or death still
/// rebuilds. The census is fed through <see cref="LawnActorViewHost.CensusOf"/> — the shipped
/// <c>ActorHudCache.Build</c> idiom — because the production read needs a live Unity runtime.</para>
///
/// <para>Not run by CI (<c>ci.yml</c> never compiles FusionRpg.Injector); build/run locally with
/// <c>$env:FUSIONRPG_GAME_DIR</c> set, the same requirement every other injector test project here
/// carries.</para>
/// </summary>
public class LawnActorViewHostTests : IDisposable
{
    static readonly Func<BoardSnapshot> ProductionCensus = LawnActorViewHost.CensusOf;
    readonly Func<BoardSnapshot> _previousCensus = LawnActorViewHost.CensusOf;

    public LawnActorViewHostTests()
    {
        LawnActorViewHost.Clear();
        // A STABLE instance: the production census is frame-cached and returns the same object, which is
        // exactly what makes the host's cache hit. A delegate that built a fresh snapshot per call would
        // test a census that cannot exist.
        var census = Census(("ptr-plant", "plant"), ("ptr-zombie", "zombie"));
        LawnActorViewHost.CensusOf = () => census;
    }

    public void Dispose()
    {
        LawnActorViewHost.Clear();
        LawnActorViewHost.CensusOf = _previousCensus;
        GC.KeepAlive(ProductionCensus);
    }

    static BoardSnapshot Census(params (string Ptr, string Side)[] rows) =>
        new(rows.Select(r => new BoardEntitySnap { Ptr = r.Ptr, Side = r.Side, TypeId = 1, Living = true }));

    [Fact]
    public void A_repeat_call_in_the_same_frame_and_perspective_returns_the_same_view()
    {
        var first = LawnActorViewHost.ViewFor(LawnActorViewHost.PlantSide, frame: 100);
        var second = LawnActorViewHost.ViewFor(LawnActorViewHost.PlantSide, frame: 100);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void A_new_frame_builds_a_new_view()
    {
        var first = LawnActorViewHost.ViewFor(LawnActorViewHost.PlantSide, frame: 100);
        var later = LawnActorViewHost.ViewFor(LawnActorViewHost.PlantSide, frame: 101);

        Assert.NotNull(first);
        Assert.NotSame(first, later);
    }

    /// <summary>The census INSTANCE is part of the key, not just the frame number: a spawn or death in
    /// the middle of one frame invalidates the board, and a frame-only key would hand back a view built
    /// over the stale census.</summary>
    [Fact]
    public void A_new_census_in_the_same_frame_builds_a_new_view()
    {
        var first = LawnActorViewHost.ViewFor(LawnActorViewHost.PlantSide, frame: 100);

        var afterSpawn = Census(("ptr-plant", "plant"));
        LawnActorViewHost.CensusOf = () => afterSpawn;
        var rebuilt = LawnActorViewHost.ViewFor(LawnActorViewHost.PlantSide, frame: 100);

        Assert.NotNull(first);
        Assert.NotSame(first, rebuilt);
        Assert.Equal(new[] { "ptr-plant" }, rebuilt!.LiveActorKeys);
    }

    /// <summary>The perspective is part of the key: one oracle cannot answer for both sides, so sharing a
    /// view across perspectives would answer a relative question absolutely.</summary>
    [Fact]
    public void Two_perspectives_in_one_frame_get_their_own_views()
    {
        var plant = LawnActorViewHost.ViewFor(LawnActorViewHost.PlantSide, frame: 100);
        var zombie = LawnActorViewHost.ViewFor(LawnActorViewHost.ZombieSide, frame: 100);

        Assert.NotNull(plant);
        Assert.NotSame(plant, zombie);
        Assert.Same(zombie, LawnActorViewHost.ViewFor(LawnActorViewHost.ZombieSide, frame: 100));
    }

    [Fact]
    public void Clear_drops_the_cache_so_the_next_call_rebuilds()
    {
        var first = LawnActorViewHost.ViewFor(LawnActorViewHost.PlantSide, frame: 100);

        LawnActorViewHost.Clear();

        Assert.NotSame(first, LawnActorViewHost.ViewFor(LawnActorViewHost.PlantSide, frame: 100));
    }

    [Fact]
    public void A_missing_perspective_is_refused_rather_than_defaulted()
    {
        Assert.Throws<ArgumentException>(() => LawnActorViewHost.ViewFor("", frame: 1));
        Assert.Throws<ArgumentException>(() => LawnActorViewHost.ViewFor("   ", frame: 1));
    }

    /// <summary>
    /// The perspective really is the oracle's own side, and the two views are mirror images of one
    /// census: the same plant is `MySideCode` from the plant view and `OtherSideCode` from Zomboss's.
    /// This is the assertion that fails if the chain is built once and shared, or built without the
    /// perspective.
    /// </summary>
    [Fact]
    public void The_perspective_decides_which_side_is_my_side()
    {
        var plant = LawnActorViewHost.ViewFor(LawnActorViewHost.PlantSide, frame: 100)!;
        var zombie = LawnActorViewHost.ViewFor(LawnActorViewHost.ZombieSide, frame: 100)!;

        Assert.Equal(0, plant.SideOf("ptr-plant"));
        Assert.Equal(1, plant.SideOf("ptr-zombie"));

        Assert.Equal(1, zombie.SideOf("ptr-plant"));
        Assert.Equal(0, zombie.SideOf("ptr-zombie"));
    }

    /// <summary>Mind control flips which side an entity fights FOR, not which side it looks like — the
    /// mechanical oracle's own rule, and the reason hypnosis reads correctly through this chain
    /// (`MechanicalOwnSideOracle.cs`).</summary>
    [Fact]
    public void A_mind_controlled_entity_fights_for_the_other_side()
    {
        LawnActorViewHost.CensusOf = () => new BoardSnapshot(new[]
        {
            new BoardEntitySnap { Ptr = "ptr-hypno", Side = "zombie", TypeId = 1, MindControlled = true, Living = true }
        });

        var plant = LawnActorViewHost.ViewFor(LawnActorViewHost.PlantSide, frame: 200)!;

        Assert.Equal(0, plant.SideOf("ptr-hypno")); // a zombie-side unit that fights for the player
    }

    [Fact]
    public void An_unknown_ptr_reads_as_the_other_side_never_as_mine()
    {
        var plant = LawnActorViewHost.ViewFor(LawnActorViewHost.PlantSide, frame: 100)!;

        Assert.Equal(1, plant.SideOf("ptr-no-oracle-knows"));
        Assert.Null(plant.PositionOf("ptr-no-oracle-knows"));
        Assert.Empty(plant.HeldActionsOf("ptr-no-oracle-knows"));
    }

    /// <summary>
    /// The two documented stand-ins read as stated: no held actions until module 16's registry lands
    /// (CAI4.3). The status-mask half of the same statement is NOT asserted here because reading it goes
    /// through `FactsOf` → the derived memo → `InjectorStatusBridge.ResolveDerived`, whose production read
    /// needs a live Unity runtime; it is stated in `LawnActorViewHost`'s own class doc and recorded as the
    /// row's remaining dependency instead of being tested against a fake.
    /// </summary>
    [Fact]
    public void The_held_actions_stand_in_reads_empty_and_the_census_is_really_read()
    {
        var plant = LawnActorViewHost.ViewFor(LawnActorViewHost.PlantSide, frame: 100)!;

        Assert.Empty(plant.HeldActionsOf("ptr-plant"));

        // The census itself is read: the live keys are the census's own, in census order, and a lawn
        // never fogs a live actor's position.
        Assert.Equal(new[] { "ptr-plant", "ptr-zombie" }, plant.LiveActorKeys);
        Assert.NotNull(plant.PositionOf("ptr-plant"));
    }
}
