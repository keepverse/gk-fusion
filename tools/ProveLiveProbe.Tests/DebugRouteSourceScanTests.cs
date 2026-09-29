using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace FusionRpg.Tools.ProveLiveProbe.Tests;

/// <summary>
/// Guards the tool's own boundary rule (spec-live-probe-tool.md "Boundaries"; tasks/live-probe-todo.md
/// Task 8): the ONLY debug-shaped routes <c>gk-fusion/tools/ProveLiveProbe</c>'s own source may reference are the
/// identity-only <c>spawn-unique-actor</c> acquire shortcut and the read-only <c>board-stats</c> send —
/// the same class of guard <c>guard-debug-scope.py</c> applies to <c>DebugEndpoints.cs</c> itself,
/// applied here to this tool's own client code, so a future change cannot quietly add a
/// fabrication-shaped debug call with nothing catching it.
///
/// <para><b>Both real paths were verified live, not assumed from the spec's own prose</b> — a real
/// HTTP call against a running Server showed <c>spawn-unique-actor</c> is mapped under
/// <c>CreatureEndpoints.cs</c>'s own <c>/api/creatures</c> group
/// (<c>/api/creatures/debug/spawn-unique-actor</c>, 200), NOT under <c>/api/debug</c> as
/// spec-live-probe-tool.md's own text says (a literal <c>/api/debug/spawn-unique-actor</c> 405s —
/// code beats docs, DESIGN-GATE.md). The scan below therefore matches any quoted literal containing a
/// <c>/debug/</c> path segment anywhere, not only a leading <c>/api/debug/</c>, so it still catches a
/// future fabrication-shaped call regardless of which group it is mapped under.</para>
///
/// <para>Scans actual C# STRING LITERALS only (a quoted substring), never bare text — a doc comment is
/// free to discuss a route by name (e.g. explaining why <c>/api/debug/events</c> is deliberately NOT
/// called, in favor of the plain, non-debug-scoped <c>/api/events</c>) without being mistaken for a
/// real call site.</para>
/// </summary>
public class DebugRouteSourceScanTests
{
    // [^"\r\n] (not just [^"]) so the match can never bridge a doc comment's mention of a path across
    // a line break into an unrelated later string literal — a real C# string literal never contains a
    // literal newline, so restricting to one line is exact, not an approximation.
    static readonly Regex DebugRouteLiteral = new("\"([^\"\r\n]*/debug/[^\"\r\n]*)\"", RegexOptions.Compiled);

    static readonly string[] AllowedRoutes =
    {
        "/api/creatures/debug/spawn-unique-actor",
        "/api/debug/board-stats",
    };

    /// <summary><c>[CallerFilePath]</c> gives this file's own absolute path at compile time — a
    /// reliable way to locate <c>gk-fusion/tools/ProveLiveProbe</c> regardless of the test runner's working
    /// directory (never relies on the current directory or an assumed repo-root).</summary>
    static string FindProveLiveProbeSourceDir([CallerFilePath] string thisFile = "")
    {
        var testsDir = Path.GetDirectoryName(Path.GetFullPath(thisFile))!;
        var srcDir = Path.GetFullPath(Path.Combine(testsDir, "..", "ProveLiveProbe"));
        if (!Directory.Exists(srcDir))
            throw new DirectoryNotFoundException($"expected tools/ProveLiveProbe next to tools/ProveLiveProbe.Tests, found none at '{srcDir}'");
        return srcDir;
    }

    static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(FindProveLiveProbeSourceDir(), "*.cs", SearchOption.TopDirectoryOnly);

    [Fact]
    public void Source_references_exactly_the_two_allowed_debug_routes()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles())
        {
            foreach (var codeLine in CodeOnlyLines(file))
                foreach (Match m in DebugRouteLiteral.Matches(codeLine))
                    found.Add(m.Groups[1].Value);
        }

        Assert.NotEmpty(found); // sanity: the scan itself must have found the two real call sites
        Assert.Equal(new SortedSet<string>(AllowedRoutes, StringComparer.Ordinal), found);
    }

    /// <summary>
    /// Every line of a file, with anything from the first <c>//</c> onward stripped off — this file's
    /// entire source uses single-line <c>//</c>/<c>///</c> comments (no <c>/* */</c> blocks, no <c>//</c>
    /// inside a string literal such as a URL), so a per-line cut at the first <c>//</c> is exact for this
    /// codebase, not an approximation. Without this, a doc comment that quotes a route by name — e.g.
    /// explaining that a literal <c>"/api/debug/spawn-unique-actor"</c> 405s, which is exactly the kind
    /// of prose this file's own doc comments contain — would be mistaken for a real call site.
    /// </summary>
    static IEnumerable<string> CodeOnlyLines(string file)
    {
        foreach (var line in File.ReadLines(file))
        {
            var slash = line.IndexOf("//", StringComparison.Ordinal);
            yield return slash < 0 ? line : line[..slash];
        }
    }

    [Fact]
    public void Source_directory_actually_contains_the_two_call_sites_this_test_expects()
    {
        // Guards the guard: if someone deletes both real calls and the scan above finds nothing to
        // compare (an empty set trivially "equals" nothing meaningful), this fails loudly instead.
        var text = string.Concat(SourceFiles().Select(File.ReadAllText));
        Assert.Contains("\"/api/creatures/debug/spawn-unique-actor\"", text);
        Assert.Contains("\"/api/debug/board-stats\"", text);
    }
}
