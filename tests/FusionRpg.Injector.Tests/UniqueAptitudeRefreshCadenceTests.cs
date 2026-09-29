using System.Linq;
using FusionRpg.Injector;
using Xunit;

namespace FusionRpg.Injector.Tests;

/// <summary>
/// aptitude-sheet AS-1.1b (unique-lawn-wire fix, `spec-unique-lawn-wire.md` "Refresh cadence"):
/// <c>RpgClient.RefreshUniqueAptitudesAsync</c>'s cache is keyed by CURRENTLY-Bound instanceIds, so a
/// specimen allocated before it is deployed is absent from every fetch's key set until the bind edge
/// itself re-triggers a fetch. Before this fix only triggers 1-3 (session start, reconnect,
/// `AptitudesUpdated`) ever called it — the 4th trigger (a specimen entering `Bound`,
/// `MatchHost.ConsumeLastBound`) was missing, confirmed live 2026-09-07 as an
/// `allocate -> deploy` order silently producing an unbuffed actor (`deploy -> allocate` worked).
///
/// <para><b>Structural, not a live HTTP round trip.</b> <c>RpgClient</c> has no HTTP seam to mock, and
/// real end-to-end proof needs the live game per `live-probe-standard.md` — these tests prove the
/// WIRING exists (source-scan, same idiom as <c>LawnElementResolverTests.Trigger3_the_injector_leave_
/// board_cleanup_actually_calls_the_invalidation</c>) and that the new coalescing logic behaves
/// correctly as pure state, which is exactly the untested surface the spec named
/// ("its cache-population timing is the untested surface that let this ship").</para>
/// </summary>
[Collection("CheatState statics")]
public class UniqueAptitudeRefreshCadenceTests
{
    // MatchRuntime's constructor reads MatchTuningPolicy (CapPolicyConfig.Defaults()) -- the real
    // injector host configures it from gk-core/data/tuning/match.v1.json at startup (RpgHost.cs), which
    // never runs in this test assembly. Idempotent (a bare field assignment), so configuring it here
    // from the same real file is safe even if another test class in this assembly also does it.
    static UniqueAptitudeRefreshCadenceTests()
    {
        var path = Path.Combine(FindRepoRoot(), "data", "tuning", "match.v1.json");
        FusionRpg.Core.Match.MatchTuningPolicy.Configure(
            FusionRpg.Core.Match.MatchTuningLoader.Parse(File.ReadAllText(path)));
    }

    // ---- the 4 cadence triggers: wiring exists (source-scan) ------------------------------------

    [Fact]
    public void Trigger1_StartAsync_calls_RefreshUniqueAptitudesAsync()
    {
        var body = MethodBody(ReadInjectorFile("RpgClient.cs"), "public async Task StartAsync()");
        Assert.Contains("RefreshUniqueAptitudesAsync()", body);
    }

    [Fact]
    public void Trigger2_SignalR_reconnect_calls_RefreshUniqueAptitudesAsync()
    {
        var source = ReadInjectorFile("RpgClient.cs");
        var start = source.IndexOf("_hub.Reconnected +=", StringComparison.Ordinal);
        Assert.True(start >= 0, "missing _hub.Reconnected handler");
        var end = source.IndexOf("SignalR reconnected + re-joined", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not bound the Reconnected handler body");
        Assert.Contains("RefreshUniqueAptitudesAsync()", source[start..end]);
    }

    [Fact]
    public void Trigger3_AptitudesUpdated_reload_command_calls_RefreshUniqueAptitudesAsync()
    {
        // AptitudesUpdated (SignalR) enqueues "aptitudes.allocation.reload", which CheatCommandRunner
        // handles by calling RefreshUniqueAptitudesAsync -- the two halves of trigger 3.
        var hub = ReadInjectorFile("RpgClient.cs");
        Assert.Contains("\"AptitudesUpdated\"", hub);
        Assert.Contains("aptitudes.allocation.reload", hub);

        var runner = ReadInjectorFile("CheatCommandRunner.cs");
        var caseStart = runner.IndexOf("\"aptitudes.allocation.reload\"", StringComparison.Ordinal);
        Assert.True(caseStart >= 0, "missing case \"aptitudes.allocation.reload\" in CheatCommandRunner");
        var caseBody = runner[caseStart..Math.Min(runner.Length, caseStart + 600)];
        Assert.Contains("RefreshUniqueAptitudesAsync()", caseBody);
    }

    [Fact]
    public void Trigger4_bind_edge_calls_TriggerBoundAptitudeRefresh()
    {
        // The fix this task exists for: MatchHost.ConsumeLastBound's bind edge, previously calling
        // only UniqueBoundLoadout.TryApply, now also fires the coalesced cadence trigger.
        var body = MethodBody(ReadInjectorFile("Match", "MatchHost.cs"), "var bound = _runtime.ConsumeLastBound();", isStatement: true);
        Assert.Contains("UniqueBoundLoadout.TryApply(bound)", body);
        Assert.Contains("TriggerBoundAptitudeRefresh()", body);
    }

    [Fact]
    public void Trigger4_is_fire_and_forget_never_awaited_on_the_bind_edge()
    {
        // "off the hot path" (the task's own accept criterion) -- awaiting a real HTTP fetch on the
        // bind edge would block the match tick that just bound a specimen. TriggerBoundAptitudeRefresh
        // is void, not Task, so there is nothing to await -- this asserts the call site never does.
        var body = MethodBody(ReadInjectorFile("Match", "MatchHost.cs"), "var bound = _runtime.ConsumeLastBound();", isStatement: true);
        Assert.DoesNotContain("await", body);
    }

    // ---- coalescing: real behavior, not just wiring ----------------------------------------------
    //
    // RefreshUniqueAptitudesAsync fetches once per CURRENTLY-Bound instanceId and returns
    // synchronously with zero HTTP calls when nothing is Bound -- so proving the coalescing state
    // machine for real needs at least one real Bound entry in MatchHost.Runtime (the shared static it
    // reads), or every TriggerBoundAptitudeRefresh call would complete before the next one even
    // started and there would be nothing to coalesce. `http://127.0.0.1:1` (nothing listens there)
    // still drives a genuine async connect-refused round trip per instance -- real async machinery,
    // just guaranteed to fail fast and never require a live server.

    static void SeedOneBoundSpecimen(string instanceId)
    {
        FusionRpg.Injector.Match.MatchHost.ResetRuntime();
        FusionRpg.Injector.Match.MatchHost.Apply("board.start", null);
        FusionRpg.Injector.Match.MatchHost.TryBeginUniquePending(instanceId, "corr-" + instanceId, "plant", typeId: 1);
        FusionRpg.Injector.Match.MatchHost.Apply("plant.spawn", new Dictionary<string, object>
        {
            ["correlationId"] = "corr-" + instanceId,
            ["ptr"] = "ABC" + instanceId,
        });
    }

    [Fact]
    public async Task TriggerBoundAptitudeRefresh_called_rapidly_N_times_runs_far_fewer_than_N_fetches()
    {
        SeedOneBoundSpecimen("inst-coalesce-1");
        try
        {
            var client = new RpgClient("http://127.0.0.1:1");

            for (var i = 0; i < 10; i++)
                client.TriggerBoundAptitudeRefresh();

            await client.WaitForUniqueRefreshLoopForTest();

            // Never anywhere close to the 10 calls made -- one in-flight run plus at most a small
            // number of coalesced reruns for whatever queued while it was running.
            Assert.True(client.UniqueRefreshRunCount < 10,
                $"expected far fewer than 10 runs, got {client.UniqueRefreshRunCount}");
        }
        finally
        {
            FusionRpg.Injector.Match.MatchHost.ResetRuntime();
        }
    }

    [Fact]
    public async Task TriggerBoundAptitudeRefresh_a_bind_after_the_loop_settled_starts_a_fresh_run()
    {
        SeedOneBoundSpecimen("inst-coalesce-2");
        try
        {
            var client = new RpgClient("http://127.0.0.1:1");

            client.TriggerBoundAptitudeRefresh();
            await client.WaitForUniqueRefreshLoopForTest();
            var afterFirst = client.UniqueRefreshRunCount;

            client.TriggerBoundAptitudeRefresh();
            await client.WaitForUniqueRefreshLoopForTest();

            // A later, independent bind is never silently absorbed by a run that already finished.
            Assert.True(client.UniqueRefreshRunCount > afterFirst);
        }
        finally
        {
            FusionRpg.Injector.Match.MatchHost.ResetRuntime();
        }
    }

    // ---- source reading (the ElementHubDocDriftTests / LawnElementResolverTests pattern) ---------

    static string MethodBody(string source, string signature, bool isStatement = false)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing signature: " + signature);

        if (isStatement)
        {
            // Bound to the enclosing `if (bound != null) { ... }` block that follows the statement,
            // rather than a whole method body -- MatchHost.Apply is large and this call site is a
            // few lines inside it, not the method itself.
            var openIf = source.IndexOf('{', source.IndexOf("if (bound != null)", start, StringComparison.Ordinal));
            Assert.True(openIf >= 0, "no `if (bound != null) {` block after: " + signature);
            return BraceBody(source, openIf);
        }

        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, "no body for: " + signature);
        return BraceBody(source, open);
    }

    static string BraceBody(string source, int open)
    {
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        throw new InvalidOperationException("unbalanced body starting at " + open);
    }

    static string ReadInjectorFile(params string[] relative) =>
        ReadRepoFile(new[] { "src", "FusionRpg.Injector" }.Concat(relative).ToArray());

    static string ReadRepoFile(params string[] relative)
    {
        var path = Path.Combine(new[] { FindRepoRoot() }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
