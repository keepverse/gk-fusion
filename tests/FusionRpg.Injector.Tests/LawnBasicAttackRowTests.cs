using FusionRpg.Core.Actions;
using FusionRpg.Injector.Actions;
using Xunit;

namespace FusionRpg.Injector.Tests;

/// <summary>
/// `lawn-combat-wire` T8 (spec-lawn-action-bridge.md): the injector-side half of "one factory, two
/// callers". Not part of CI (this project needs interop refs and a real game dir — see this
/// project's own .csproj comment and the plan's risk table: "Injector-side tests are not in CI");
/// this class is the Core test suite's own acceptance criterion made concrete for the half that
/// genuinely cannot run in Core.Tests (<see cref="LawnBasicAttackRow"/> lives in
/// <c>FusionRpg.Injector</c>).
///
/// <para><b>`ActionTimingPolicy` is a process-global static with no "unconfigure" API</b> (by design —
/// every other `Policy`/`Tuning` hub in this codebase is configure-once-at-host-startup). Nothing else
/// in this test assembly configures it, so the very first touch in this process is genuinely
/// unconfigured — <see cref="Unconfigured_TryGet_returns_null_without_throwing_and_is_idempotent"/>
/// depends on running before <see cref="Once_configured_TryGet_returns_the_shared_row"/> ever calls
/// <see cref="ActionTimingPolicy.Configure"/>. Declared in that order; xunit runs `[Fact]`s within one
/// class in declaration order by default (no `TestCaseOrderer` overrides that here). If a future test
/// elsewhere in this assembly configures `ActionTimingPolicy` first, this class's first test stops
/// proving what it claims — that coupling is inherent to a real "configure once, never after" static
/// and is called out here rather than hidden.</para>
/// </summary>
public class LawnBasicAttackRowTests
{
    [Fact]
    public void Unconfigured_TryGet_returns_null_without_throwing_and_is_idempotent()
    {
        LawnBasicAttackRow.ResetForTest();

        CompiledAction? first = null;
        var ex = Record.Exception(() => first = LawnBasicAttackRow.TryGet());
        Assert.Null(ex); // never a per-hit throw -- the spec's explicit rejection of that posture
        Assert.Null(first); // no contribution while unconfigured -- not a fabricated/default row

        // Never retried once diagnosed -- a second call must not attempt construction again (which
        // would mean logging "loudly" on every single hit instead of once).
        var second = LawnBasicAttackRow.TryGet();
        Assert.Null(second);
    }

    [Fact]
    public void Once_configured_TryGet_returns_the_shared_row()
    {
        ActionTimingPolicy.Configure(new ActionTimingTuning(
            WindupPerPowerMilli: 20,
            WindupCapReferenceMilli: 300,
            RecoveryPerPowerMilli: 8,
            BasicAttack: new BasicAttackTimingTuning(WindupTicks: 150, RecoveryTicks: 50),
            Categories: new Dictionary<ActionCategory, ActionTimingCategoryTuning>
            {
                [ActionCategory.Attack] = new(TimeCostBaseTicks: 100, CooldownBaseTicks: 200),
                [ActionCategory.Defense] = new(TimeCostBaseTicks: 120, CooldownBaseTicks: 150),
                [ActionCategory.Support] = new(TimeCostBaseTicks: 100, CooldownBaseTicks: 250),
                [ActionCategory.Movement] = new(TimeCostBaseTicks: 80, CooldownBaseTicks: 100),
                [ActionCategory.Status] = new(TimeCostBaseTicks: 90, CooldownBaseTicks: 180),
            }));
        LawnBasicAttackRow.ResetForTest();

        var row = LawnBasicAttackRow.TryGet();

        Assert.NotNull(row);
        Assert.Equal("act.attack", row!.ActionId);
        Assert.Equal(ActionKind.Basic, row.Kind);

        // Same shared factory as BattleRunState's own call -- not a second hand-built copy.
        var againstFactory = BasicAttackFactory.Create(ActionTimingPolicy.Tuning);
        Assert.Equal(againstFactory.ActionId, row.ActionId);
        Assert.Equal(againstFactory.Envelope, row.Envelope);

        // Cached: a second call returns the SAME instance, not a fresh construction per call.
        var again = LawnBasicAttackRow.TryGet();
        Assert.Same(row, again);
    }
}
