namespace FusionRpg.Tools.ProveLiveProbe;

/// <summary>The four things that can happen to one step, kept as distinct enum members rather than a
/// single bool — spec-live-probe-tool.md's own boundary: a real endpoint refusal (4xx) must never be
/// conflated with an assertion mismatch, and (Task 6) a live-engine poll timeout must never be conflated
/// with a live-engine mismatch. Four kinds, four different fixes.</summary>
public enum StepOutcome
{
    Ok,
    Skipped,
    /// <summary>The server itself said no (a well-formed 4xx) — "the server said no", not "the server
    /// said yes but the numbers are wrong".</summary>
    Refused,
    /// <summary>The step ran, the server answered ok, but the answer does not match what was
    /// expected.</summary>
    Mismatch,
    /// <summary>A bounded poll (step 5's ActiveBound wait, or step 6's board-stats wait) never
    /// produced an answer within the timeout — "no signal", never conflated with "wrong signal".</summary>
    Timeout,
}

public sealed record StepResult(string Name, StepOutcome Outcome, string Detail, object? Data = null)
{
    public bool IsOk => Outcome is StepOutcome.Ok or StepOutcome.Skipped;
}

/// <summary>
/// Prints the two-halves-separated report the whole tool exists to produce (spec-live-probe-tool.md
/// "Code style"): persisted state (steps 1-5, RPG Server Debug scope) and live engine (step 6, Game
/// Injector Debug scope) are always two labeled sections, never one merged boolean.
/// </summary>
public static class Report
{
    public static void Section(string title, IEnumerable<StepResult> steps)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title} ===");
        foreach (var s in steps)
        {
            var tag = s.Outcome switch
            {
                StepOutcome.Ok => "OK",
                StepOutcome.Skipped => "SKIPPED",
                StepOutcome.Refused => "REFUSED",
                StepOutcome.Mismatch => "MISMATCH",
                StepOutcome.Timeout => "TIMEOUT",
                _ => s.Outcome.ToString(),
            };
            Console.WriteLine($"[{tag,-8}] {s.Name}: {s.Detail}");
        }
    }

    public static void Line(string text) => Console.WriteLine(text);
}
