namespace FusionRpg.Tools.ProveLiveProbe;

/// <summary>
/// Orchestrates the 6-step live-probe recipe (spec-live-probe-tool.md) against a real running
/// <c>FusionRpg.Server</c>, in two modes: A (steps 1-5, persisted state only, no game/Injector needed)
/// and B (all 6 steps, real summon + live match/board + Injector connected required). Every refusal
/// check runs before the first HTTP call; every step reports Ok/Skipped/Refused/Mismatch/Timeout as its
/// own distinct outcome, never collapsed into one boolean; persisted state and live engine are always
/// two separately labeled report sections.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"argument error: {ex.Message}");
            return 2;
        }

        var refusal = Guardrails.PreflightRefusal(options);
        if (refusal is not null)
        {
            Console.WriteLine(refusal);
            return 1;
        }

        using var client = new LiveProbeClient(options.BaseUrl);
        var persisted = new List<StepResult>();
        var liveEngine = new List<StepResult>();
        string? instanceId = null;
        var side = options.Side;
        var typeId = options.TypeId;

        // ---- step 1: acquire ----------------------------------------------------------------------
        if (options.Mode == ProbeMode.A)
        {
            var (step, actor) = await client.SpawnUniqueActorAsync(options.PlayerId, options.Side, options.TypeId);
            persisted.Add(step);
            if (step.Outcome != StepOutcome.Ok) return await Finish(client, options, persisted, liveEngine, instanceId);
            instanceId = actor!.InstanceId;
        }
        else if (!string.IsNullOrWhiteSpace(options.InstanceId))
        {
            // Step 1 reuse. Still a real owned specimen, and still attributed: the souls that bought it
            // were spent in an earlier run, so the provenance step runs unchanged and reports the same
            // balance it always would — a reused specimen must not become a way to skip that check.
            persisted.Add(await client.GetSoulProvenanceAsync(options.PlayerId));

            var (step, actor) = await client.ReuseOwnedActorAsync(options.PlayerId, options.InstanceId!);
            persisted.Add(step);
            if (step.Outcome != StepOutcome.Ok) return await Finish(client, options, persisted, liveEngine, instanceId);
            instanceId = actor!.InstanceId;
            side = actor.Side;
            typeId = actor.TypeId;
        }
        else
        {
            // live-probe Task 18: attribute the souls the summon is about to spend, before spending them.
            // Not a hard stop — a debug-funded balance is reported (and fails the run), the recipe still runs.
            persisted.Add(await client.GetSoulProvenanceAsync(options.PlayerId));

            var (step, result) = await client.SummonAsync(options.PlayerId, options.BannerId);
            persisted.Add(step);
            if (step.Outcome != StepOutcome.Ok) return await Finish(client, options, persisted, liveEngine, instanceId);
            var actor = result!.Specimens[0].Actor;
            instanceId = actor.InstanceId;
            side = actor.Side;
            typeId = actor.TypeId;
        }

        // ---- step 2: allocate ----------------------------------------------------------------------
        // Steps 2-4 each record their own real outcome (Ok/Refused) but never hard-stop the recipe —
        // unlike step 1 (no instanceId, nothing downstream can run) a refusal here is still valuable
        // to see reflected (or not) in step 5's read-back. A live run against a fresh debug-shortcut
        // specimen found exactly this case for real: the shortcut's own TryAckUniqueSpawn already
        // leaves the actor ActiveBound, so a subsequent step 4 deploy legitimately 409s
        // ("phase.activebound") — a real, correctly-reported refusal, not a tool defect, and stopping
        // the recipe there would have hidden step 5's otherwise-informative read-back.
        var shares = Guardrails.BuildShares(options.AptitudeId, options.AptitudePoints);
        var (allocStep, _) = await client.AllocateAsync(instanceId!, shares);
        persisted.Add(allocStep);

        // ---- step 3: equip -------------------------------------------------------------------------
        var (equipStep, _) = await client.EquipAsync(options.PlayerId, instanceId!, options.ItemInstanceId, options.Role);
        persisted.Add(equipStep);

        // ---- step 4: deploy ------------------------------------------------------------------------
        var correlationId = Guid.NewGuid().ToString("N");
        var (deployStep, _) = await client.DeployAsync(instanceId!, correlationId, options.Col, options.Row, options.MatchKey);
        persisted.Add(deployStep);

        // ---- step 5: persisted-state read-back ----------------------------------------------------
        // Mode B only: the Injector's own ack (Deploying -> ActiveBound, lastPtr populated) is
        // asynchronous, so wait for it here with a bounded timeout, reported as its own distinct
        // timeout kind if it never lands (see LiveProbeClient.WaitForActiveBoundAsync doc). Still not a
        // hard stop: the read-back below runs either way and shows whatever phase was actually reached.
        if (options.Mode == ProbeMode.B)
        {
            var (waitStep, _) = await client.WaitForActiveBoundAsync(instanceId!, TimeSpan.FromSeconds(options.TimeoutSec));
            persisted.Add(waitStep);
        }

        var (actorStep, actorDto) = await client.GetActorAsync(instanceId!);
        persisted.Add(actorStep);
        var (equipGetStep, _) = await client.GetEquipmentAsync(instanceId!);
        persisted.Add(equipGetStep);

        if (options.Mode == ProbeMode.A)
            return await Finish(client, options, persisted, liveEngine, instanceId);

        // ---- step 6: live-engine read (Mode B only) -----------------------------------------------
        // Only attempted when persisted state actually carries a live ptr to look up — polling the
        // board for a ptr we already know cannot exist would just burn the whole timeout on a
        // foregone conclusion, and "skipped, no ptr yet" is a more honest report than a manufactured
        // timeout.
        if (string.IsNullOrEmpty(actorDto?.LastPtr))
        {
            liveEngine.Add(new StepResult("6-live-engine (read)", StepOutcome.Skipped,
                "no persisted lastPtr available — step 5 never reached ActiveBound with a live ptr, " +
                "so there is nothing for step 6 to look up on the board"));
            return await Finish(client, options, persisted, liveEngine, instanceId);
        }

        var beforeId = await EventPoller.FindCurrentMaxEventIdAsync(client);
        var tag = Guid.NewGuid().ToString("N");
        var sendStep = await client.SendBoardStatsAsync(tag);
        liveEngine.Add(sendStep);
        if (sendStep.Outcome != StepOutcome.Ok)
            return await Finish(client, options, persisted, liveEngine, instanceId);

        var evt = await EventPoller.PollForTaggedKindAsync(
            client, beforeId, "debug.board-stats", tag, TimeSpan.FromSeconds(options.TimeoutSec));
        if (evt is null)
        {
            liveEngine.Add(new StepResult("6-live-engine (read)", StepOutcome.Timeout,
                $"live-engine read timed out after {options.TimeoutSec}s (no live board, wrong ptr, or " +
                "injector disconnected — a DIFFERENT defect from a wrong-value mismatch)"));
            return await Finish(client, options, persisted, liveEngine, instanceId);
        }

        var payload = EventPoller.ParseBoardStats(evt);
        var candidates = string.Equals(side, "plant", StringComparison.OrdinalIgnoreCase)
            ? payload?.Plants
            : payload?.Zombies;
        var entity = candidates?.FirstOrDefault(e => string.Equals(e.Ptr, actorDto?.LastPtr, StringComparison.Ordinal));

        if (entity is null)
        {
            liveEngine.Add(new StepResult("6-live-engine (read)", StepOutcome.Mismatch,
                $"live-engine half: board-stats answered but no living {side} entity with ptr=" +
                $"{actorDto?.LastPtr ?? "(none)"} was found on the board"));
        }
        else if (entity.TypeId != typeId)
        {
            liveEngine.Add(new StepResult("6-live-engine (read)", StepOutcome.Mismatch,
                $"live-engine half: game does not reflect persisted state " +
                $"(typeId persisted={typeId} live={entity.TypeId})"));
        }
        else
        {
            liveEngine.Add(new StepResult("6-live-engine (read)", StepOutcome.Ok,
                $"ptr={entity.Ptr} typeId={entity.TypeId} attack={entity.Attack} hp={entity.Hp} " +
                $"maxHp={entity.MaxHp} col={entity.Col} row={entity.Row}"));
        }

        return await Finish(client, options, persisted, liveEngine, instanceId);
    }

    /// <summary>Mode B cleanup (default, skippable with <c>-NoCleanup</c>) + the two-halves report +
    /// the exit code. Called from every early-return point so a refusal partway through the recipe
    /// still cleans up whatever specimen was minted, and still prints what happened so far.</summary>
    static async Task<int> Finish(
        LiveProbeClient client, Options options,
        List<StepResult> persisted, List<StepResult> liveEngine, string? instanceId)
    {
        if (options.Mode == ProbeMode.B && instanceId is not null && !options.NoCleanup)
        {
            var retireStep = await client.RetireAsync(instanceId);
            Report.Section("Cleanup", new[] { retireStep });
        }

        Report.Section("Persisted state (RPG Server Debug)", persisted);
        if (options.Mode == ProbeMode.B)
            Report.Section("Live engine (Game Injector Debug read)", liveEngine);

        var allSteps = persisted.Concat(liveEngine).ToList();
        var failed = allSteps.Where(s => !s.IsOk).ToList();
        Report.Line("");
        if (failed.Count == 0)
        {
            Report.Line("RESULT: PASS");
            return 0;
        }

        Report.Line($"RESULT: FAIL ({failed.Count} step(s) not ok)");
        foreach (var f in failed)
            Report.Line($"  - [{f.Outcome}] {f.Name}: {f.Detail}");
        return 1;
    }
}
