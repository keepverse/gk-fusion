using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Injector.Tests;

/// <summary>
/// live-probe Task 22 (2026-09-16, fixed 2026-09-20): a unique-specimen deploy that fails inside the
/// engine (`SetPlant`/`SetZombie` returning null, or throwing -- a real observed case was a missing
/// prefab throwing `NullReferenceException`) was invisible to the server: `CheatActions.
/// SpawnExtraPlant`/`SpawnExtraZombieCore` only ever logged locally and cleared their own
/// pending-spawn bookkeeping, so the server's `Deploying` row sat until `UniqueActorService.
/// FailExpiredDeploys`'s multi-minute timeout cleaned it up -- "the server simply timed its deploy ack
/// out" (Task 22's own words).
///
/// <para><b>Structural, not a live HTTP round trip</b> -- same idiom as
/// <see cref="UniqueAptitudeRefreshCadenceTests"/>'s own doc comment: <c>RpgClient</c> has no HTTP
/// seam to mock here, and the real end-to-end proof needs the live game per
/// <c>live-probe-standard.md</c> (see this session's own live evidence,
/// <c>tasks/evidence-fragments/live-probe-Task22.md</c>). These tests prove the WIRING exists: all
/// four failure sites (plant-null, plant-throws, zombie-null, zombie-throws) call the new report, and
/// only when the spawn was a real unique deploy (<c>instanceId</c> present) -- a manual/debug spawn
/// with no <c>instanceId</c> has no `Deploying` row to fail and must not call an endpoint keyed on an
/// empty id.</para>
/// </summary>
public class SpawnFailDeployReportingTests
{
    [Fact]
    public void RpgClient_declares_ReportFailedDeploy_posting_to_the_real_fail_deploy_route()
    {
        var source = ReadInjectorFile("RpgClient.cs");
        Assert.Contains("public void ReportFailedDeploy(string instanceId, string reason)", source);
        Assert.Contains("/api/unique/actors/", source);
        Assert.Contains("/fail-deploy", source);
    }

    [Fact]
    public void Plant_null_case_reports_the_failed_deploy()
    {
        var body = MethodBody(ReadInjectorFile("CheatActions.cs"), "static void SpawnExtraPlant(");
        var nullCheck = body[body.IndexOf("if (plant == null)", StringComparison.Ordinal)..];
        Assert.Contains("RpgHost.Client?.ReportFailedDeploy(instanceId!,", nullCheck[..Math.Min(400, nullCheck.Length)]);
    }

    [Fact]
    public void Plant_throw_case_reports_the_failed_deploy()
    {
        var body = MethodBody(ReadInjectorFile("CheatActions.cs"), "static void SpawnExtraPlant(");
        var catchStart = body.LastIndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(catchStart >= 0, "no catch block found in SpawnExtraPlant");
        Assert.Contains("RpgHost.Client?.ReportFailedDeploy(instanceId!,", body[catchStart..]);
    }

    [Fact]
    public void Zombie_null_case_reports_the_failed_deploy()
    {
        var body = MethodBody(ReadInjectorFile("CheatActions.cs"), "static void SpawnExtraZombieCore(");
        var nullCheck = body[body.IndexOf("if (z == null)", StringComparison.Ordinal)..];
        Assert.Contains("RpgHost.Client?.ReportFailedDeploy(instanceId!,", nullCheck[..Math.Min(400, nullCheck.Length)]);
    }

    [Fact]
    public void Zombie_throw_case_reports_the_failed_deploy()
    {
        var body = MethodBody(ReadInjectorFile("CheatActions.cs"), "static void SpawnExtraZombieCore(");
        var catchStart = body.LastIndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(catchStart >= 0, "no catch block found in SpawnExtraZombieCore");
        Assert.Contains("RpgHost.Client?.ReportFailedDeploy(instanceId!,", body[catchStart..]);
    }

    // ---- helpers (deliberately duplicated, not shared, matching this test assembly's own
    // established convention of per-file independence, e.g. UniqueAptitudeRefreshCadenceTests.cs) ---

    static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing signature: " + signature);
        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, "no opening brace after: " + signature);
        return BraceBody(source, open);
    }

    static string BraceBody(string source, int openBraceIndex)
    {
        var depth = 0;
        for (var i = openBraceIndex; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source[openBraceIndex..(i + 1)];
            }
        }
        throw new InvalidOperationException("unbalanced braces from index " + openBraceIndex);
    }

    static string ReadInjectorFile(string relative) =>
        ReadRepoFile(Path.Combine("src", "FusionRpg.Injector", relative));

    static string ReadRepoFile(string relative)
    {
        var path = Path.Combine(FindRepoRoot(), relative);
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
