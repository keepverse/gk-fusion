using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Injector.Tests;

/// <summary>
/// action-enrich AE2.2 (spec-lawn-action-base.md §Design): the lawn grant's baked amount depends on an
/// owner Θ the binder reads from <c>CheatState.PowerIndex</c>. That read must be THE Hub's own value for
/// the same ctx — one contributor, no private fold that could drift from it.
///
/// <para><b>The last test is the falsifier, not a description.</b> It plants a SECOND contributor to
/// <c>progression.power</c> and shows the identity the binder depends on breaks. The plant has to win
/// the channel's own tie-break to bite at all: <c>progression.power</c> composes as
/// <c>FlatReplace</c> (<c>DerivedStatRegistry.cs:82</c>), whose winner among <c>Replace</c> modifiers is
/// <c>OrderByDescending(Priority).ThenBy(SourceId)</c> (<c>DerivedComposer.ComposeFlatReplace</c>) — so
/// a same-priority plant loses to the real subsystem on the source-id tie-break and would prove
/// nothing. <see cref="SecondProgressionContributor"/> therefore carries a higher Priority.</para>
///
/// <para>The hub here is a fixture, never <c>CheatState.ActorHub</c>: registering a planted subsystem on
/// the shared static would leak into every other test in this assembly.</para>
/// </summary>
public class LawnGrantThetaParityTests
{
    /// <summary>The Θ these tests drive the fixture hub with — a real, non-zero ladder index, so the
    /// identity below is never the vacuous 0 == 0 it started as.</summary>
    const int Theta = 37;

    static LawnGrantThetaParityTests()
    {
        // CheatState.PowerIndex constructs InjectorPowerIndexProvider(PowerTuningHub.Tuning), which
        // throws before Configure has run — the real hosts do it at RpgHost/Program startup, neither of
        // which runs in this assembly. v2 is the version production pins (spec-green-baseline.md).
        PowerTuningHub.Configure(PowerTuningLoader.Parse(ReadTuning("power-scale.v2.json")));
        // `new ActorHub(StatSystemBootstrap.CreateDefault())` composes a DerivedStatRegistry, whose
        // caps read DerivedStatPolicy — the same host-startup configure, for the same reason.
        DerivedStatPolicy.Configure(DerivedStatTuningLoader.Parse(ReadTuning("derived-stats.v2.json")));
        // The progression subsystem's second modifier reads StatusPolicy's stub default.
        FusionRpg.Core.Status.StatusPolicy.Configure(
            FusionRpg.Core.Status.StatusTuningLoader.Parse(ReadTuning("status.v1.json")));
    }

    static string ReadTuning(string file) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", file));

    static StatContext Ctx() => new() { PlayerId = CheatState.CurrentPlayerId };

    /// <summary>A fixture hub with exactly the contributor a real one has for this channel.</summary>
    static ActorHub FixtureHub(IPowerIndexProvider index)
    {
        var hub = new ActorHub(StatSystemBootstrap.CreateDefault());
        hub.Register(new RpgProgressionSubsystem(index));
        return hub;
    }

    static double HubTheta(ActorHub hub) =>
        hub.ResolveDerived(Ctx()).Get(DerivedStatChannels.ProgressionPower);

    /// <summary>The channel carries the provider's Θ — a real value, so this is not 0 == 0.</summary>
    [Fact]
    public void The_Hub_channel_carries_the_power_providers_Theta()
    {
        Assert.Equal((double)Theta, HubTheta(FixtureHub(new FixedPowerIndexProvider(Theta))), precision: 6);
    }

    /// <summary>The binder reads <c>CheatState.PowerIndex</c>; the production hub is built with that
    /// same instance, so the channel it must agree with is written by the very provider it reads.</summary>
    [Fact]
    public void The_binders_Theta_source_is_the_provider_that_writes_that_channel()
    {
        var lawnTheta = CheatState.PowerIndex.ActorIndex(Ctx());
        Assert.Equal((double)lawnTheta, HubTheta(FixtureHub(CheatState.PowerIndex)), precision: 6);
    }

    [Fact]
    public void A_planted_second_contributor_to_progression_power_breaks_that_parity()
    {
        Assert.Equal((double)Theta, HubTheta(FixtureHub(new FixedPowerIndexProvider(Theta))), precision: 6);

        var planted = FixtureHub(new FixedPowerIndexProvider(Theta));
        planted.Register(new SecondProgressionContributor());

        Assert.True(planted.Subsystems.Count == 2, "the planted subsystem must register");
        Assert.NotEqual((double)Theta, HubTheta(planted));
    }

    /// <summary>A SECOND contributor to the channel the real subsystem owns — the planted violation.
    /// <c>Replace</c> at a higher Priority than the real subsystem's (default 0) wins
    /// <c>ComposeFlatReplace</c>, which is what makes the channel stop carrying the provider's Θ.</summary>
    sealed class SecondProgressionContributor : IActorStatSubsystem
    {
        public string SubsystemId => "test.planted.progression";
        public int Order => 101;

        public void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods) =>
            mods.Add(new DerivedModifier(
                DerivedStatChannels.ProgressionPower,
                DerivedModifierOp.Replace,
                Theta + 7,
                Priority: 10,
                SourceId: "test.planted.progression"));
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
