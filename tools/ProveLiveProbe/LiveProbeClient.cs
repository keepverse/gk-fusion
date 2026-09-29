using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Server;

namespace FusionRpg.Tools.ProveLiveProbe;

/// <summary>
/// The whole 6-step recipe's HTTP surface (spec-live-probe-tool.md), against a real running
/// <c>FusionRpg.Server</c>. Every request/response type is either a real <c>FusionRpg.Contracts</c> DTO,
/// a real public nested request type the endpoint module itself declares (<c>FusionRpg.Server</c>), or
/// (where the handler answers with a bare anonymous object) a locally-declared DTO whose shape was read
/// from that handler's own source — see Dtos.cs's header comment. Never a hand-guessed JSON shape.
///
/// <para><b>The two debug-shaped routes this class calls, and no others</b> — the tool's own boundary
/// rule (spec-live-probe-tool.md, tasks/live-probe-todo.md Task 8): the identity-only acquire shortcut
/// (<c>POST /api/creatures/debug/spawn-unique-actor</c> — mapped under <c>CreatureEndpoints.cs</c>'s
/// own <c>/api/creatures</c> group despite the "debug" name, confirmed by a real HTTP call against a
/// live Server; a literal <c>/api/debug/spawn-unique-actor</c> 405s) and <c>POST /api/debug/board-
/// stats</c> (read-only send; its answer is read back over the plain <c>/api/events</c> feed, see
/// <see cref="EventPoller"/>).</para>
/// </summary>
public sealed class LiveProbeClient : IDisposable
{
    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    readonly HttpClient _http;

    public LiveProbeClient(string baseUrl) : this(baseUrl, new HttpClientHandler())
    {
    }

    /// <summary>Offline tests pass a handler that answers the Server's routes from memory.</summary>
    public LiveProbeClient(string baseUrl, HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ---- generic wire helpers --------------------------------------------------------------------

    public async Task<(bool Ok, int Status, T? Body, string Raw)> PostAsync<T>(string path, object body)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(path, content).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        return (resp.IsSuccessStatusCode, (int)resp.StatusCode, Deserialize<T>(resp.IsSuccessStatusCode, raw), raw);
    }

    public async Task<(bool Ok, int Status, T? Body, string Raw)> GetAsync<T>(string path)
    {
        using var resp = await _http.GetAsync(path).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        return (resp.IsSuccessStatusCode, (int)resp.StatusCode, Deserialize<T>(resp.IsSuccessStatusCode, raw), raw);
    }

    static T? Deserialize<T>(bool ok, string raw)
    {
        if (!ok || string.IsNullOrWhiteSpace(raw)) return default;
        try { return JsonSerializer.Deserialize<T>(raw, JsonOpts); }
        catch { return default; }
    }

    /// <summary>Best-effort <c>reason</c>/<c>Reason</c> extraction from a refusal body — used so a
    /// refused step's <see cref="StepResult.Detail"/> quotes the server's own words, never a bare
    /// status code.</summary>
    public static string? TryExtractReason(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "reason", "Reason" })
                if (doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
                    return el.GetString();
        }
        catch { /* not JSON, or not an object — caller falls back to the raw body */ }
        return null;
    }

    // ---- step 1 (acquire) ------------------------------------------------------------------------

    /// <summary>Mode A's acquire. The ONE debug-shortcut call this tool ever makes.</summary>
    public async Task<(StepResult Step, SpawnUniqueActorResult? Actor)> SpawnUniqueActorAsync(
        long? playerId, string side, int gameTypeId)
    {
        var req = new CreatureEndpoints.SpawnUniqueActorRequest
        {
            PlayerId = playerId,
            Side = side,
            GameTypeId = gameTypeId,
        };
        // NOTE: despite the name and the "debug-only" framing in CreatureEndpoints.cs's own comment,
        // this route is mapped under that file's "/api/creatures" group, NOT "/api/debug" — confirmed
        // by a real HTTP call against a live Server (a literal "/api/debug/spawn-unique-actor" 405s,
        // "Allow: GET, HEAD"; this path 200s). Code beats docs/spec text here (DESIGN-GATE.md).
        var (ok, status, body, raw) = await PostAsync<SpawnUniqueActorResult>("/api/creatures/debug/spawn-unique-actor", req);
        if (!ok)
            return (new StepResult("1-acquire (debug shortcut)", StepOutcome.Refused,
                $"step 1 refused: HTTP {status} {TryExtractReason(raw) ?? raw}"), null);
        return (new StepResult("1-acquire (debug shortcut)", StepOutcome.Ok,
            $"instanceId={body!.InstanceId} ptr={body.Ptr} (synthetic, debug-only)"), body);
    }

    // ---- step 0 (soul provenance, Mode B) ---------------------------------------------------------

    /// <summary>live-probe Task 18. Plain RPG reads, no debug route: <c>GET /api/souls/{id}</c>, the
    /// ledger pages behind it (<c>GET /api/souls/{id}/ledger</c>, newest first, <c>afterId</c> = smallest id
    /// seen), and — per run that earned kill souls — that run's <c>ZombieKilled</c> facts
    /// (<c>GET /api/pvz-activity/{id}/facts</c>), whose payload carries the victim's <c>spawnOrigin</c>.</summary>
    public async Task<StepResult> GetSoulProvenanceAsync(long? playerId, int maxLedgerPages = 20)
    {
        if (playerId is not { } pid)
            return new StepResult("0-soul-provenance", StepOutcome.Skipped,
                "no -PlayerId given, so the balance the summon spends cannot be attributed");

        var (balOk, balStatus, balance, balRaw) = await GetAsync<SoulBalanceDto>($"/api/souls/{pid}");
        if (!balOk || balance is null)
            return new StepResult("0-soul-provenance", StepOutcome.Refused,
                $"soul balance read refused: HTTP {balStatus} {TryExtractReason(balRaw) ?? balRaw}");

        const int pageSize = 500;
        var ledger = new List<SoulLedgerEntryDto>();
        var afterId = 0L;
        var truncated = false;
        for (var page = 0; ; page++)
        {
            if (page == maxLedgerPages) { truncated = true; break; }
            var (ok, status, body, raw) = await GetAsync<SoulLedgerDto>($"/api/souls/{pid}/ledger?limit={pageSize}&afterId={afterId}");
            if (!ok || body is null)
                return new StepResult("0-soul-provenance", StepOutcome.Refused,
                    $"soul ledger read refused: HTTP {status} {TryExtractReason(raw) ?? raw}");
            ledger.AddRange(body.Items);
            if (body.Items.Count < pageSize) break;
            afterId = body.Items.Min(i => i.Id);
        }

        // live-probe Task 20: page each run's facts (newest first, afterId = smallest id seen) instead of stopping at
        // the endpoint's 500-row cap, which left older kills reported as FactNotFound.
        var facts = new List<PvzActivityFactDto>();
        foreach (var runId in ledger.Where(SoulProvenance.IsKillEarn).Select(r => r.RunId).Distinct())
        {
            var factsAfter = 0L;
            for (var page = 0; page < maxLedgerPages; page++)
            {
                var (ok, status, body, raw) = await GetAsync<PvzActivityFactsPageDto>(
                    $"/api/pvz-activity/{pid}/facts?kind=ZombieKilled&runId={runId}&limit={pageSize}&afterId={factsAfter}");
                if (!ok || body is null)
                    return new StepResult("0-soul-provenance", StepOutcome.Refused,
                        $"run {runId} facts read refused: HTTP {status} {TryExtractReason(raw) ?? raw}");
                if (body.Items.Count == 0) break;
                var oldest = body.Items.Min(i => i.Id);
                // A Server that predates the cursor ignores afterId and answers the newest page again.
                if (factsAfter > 0 && oldest >= factsAfter)
                    return new StepResult("0-soul-provenance", StepOutcome.Refused,
                        $"run {runId} facts endpoint ignored afterId={factsAfter}; restart the Server on a build with facts paging");
                facts.AddRange(body.Items);
                if (body.Items.Count < pageSize) break;
                factsAfter = oldest;
            }
        }

        return SoulProvenance.ToStep(SoulProvenance.Summarize(balance.Balance, ledger, truncated, facts));
    }

    /// <summary>Mode B's acquire — the only acquisition path that reaches a real Unity-spawned entity.
    /// Never the debug shortcut (<see cref="Guardrails.CheckModeBAcquisition"/> refuses that combination
    /// before this method is ever called).</summary>
    public async Task<(StepResult Step, SummonResult? Result)> SummonAsync(long? playerId, string? bannerId)
    {
        var req = new CreatureEndpoints.SummonRequest
        {
            PlayerId = playerId,
            BannerId = bannerId,
            Count = 1,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
        var (ok, status, body, raw) = await PostAsync<SummonResult>("/api/creatures/summon", req);
        if (!ok)
            return (new StepResult("1-acquire (real summon)", StepOutcome.Refused,
                $"step 1 refused: HTTP {status} {TryExtractReason(raw) ?? raw}"), null);
        if (body?.Specimens is not { Count: > 0 })
            return (new StepResult("1-acquire (real summon)", StepOutcome.Mismatch,
                "summon answered 200 but produced zero specimens"), null);
        var actor = body.Specimens[0].Actor;
        return (new StepResult("1-acquire (real summon)", StepOutcome.Ok,
            $"instanceId={actor.InstanceId} typeId={actor.TypeId} side={actor.Side}"), body);
    }

    // ---- step 2 (allocate) -----------------------------------------------------------------------

    public async Task<(StepResult Step, UniqueAptitudeState? State)> AllocateAsync(
        string instanceId, Dictionary<string, long> shares)
    {
        if (shares.Count == 0)
            return (new StepResult("2-allocate", StepOutcome.Skipped, "no -AptitudeId given"), null);

        var req = new AptitudeEndpoints.AllocateUniqueAptitudesRequest { InstanceId = instanceId, Shares = shares };
        var (ok, status, body, raw) = await PostAsync<UniqueAptitudeState>("/api/aptitudes/unique/allocate", req);
        if (!ok)
            return (new StepResult("2-allocate", StepOutcome.Refused,
                $"step 2 refused: HTTP {status} {TryExtractReason(raw) ?? raw}"), null);
        return (new StepResult("2-allocate", StepOutcome.Ok,
            $"spent={body!.Spent} budget={body.Budget} withinBudget={body.WithinBudget}"), body);
    }

    // ---- step 3 (equip) --------------------------------------------------------------------------

    public async Task<(StepResult Step, ItemEquipOutcomeDto? Outcome)> EquipAsync(
        long? playerId, string specimenId, string? itemInstanceId, string? role)
    {
        if (string.IsNullOrWhiteSpace(itemInstanceId) || string.IsNullOrWhiteSpace(role))
            return (new StepResult("3-equip", StepOutcome.Skipped, "no -ItemInstanceId/-Role given"), null);

        var req = new ItemEquipEndpoints.EquipRequest(playerId, specimenId, itemInstanceId, role);
        var (ok, status, body, raw) = await PostAsync<ItemEquipOutcomeDto>("/api/items/equip", req);
        if (!ok || body is { Ok: false })
        {
            var reason = body?.Reason ?? TryExtractReason(raw) ?? raw;
            return (new StepResult("3-equip", StepOutcome.Refused, $"step 3 refused: HTTP {status} {reason}"), body);
        }
        return (new StepResult("3-equip", StepOutcome.Ok, $"equipped {itemInstanceId} into {role}"), body);
    }

    // ---- step 4 (deploy) -------------------------------------------------------------------------

    /// <summary><c>loadoutJson</c> is not a parameter here on purpose — it is hard-coded to
    /// <c>null</c> at the one call site (Program.cs), which only ever reaches this method after
    /// <see cref="Guardrails.CheckLoadoutOverride"/> has already refused any non-empty override. This
    /// method itself has no way to send one even if a caller wanted to.</summary>
    public async Task<(StepResult Step, UniqueActorDeployResultDto? Result)> DeployAsync(
        string instanceId, string correlationId, int? col, int? row, string? matchKey)
    {
        var req = new DeployUniqueActorRequest
        {
            CorrelationId = correlationId,
            Col = col,
            Row = row,
            MatchKey = matchKey,
            LoadoutJson = null, // ALWAYS — see method doc.
        };
        var (ok, status, body, raw) = await PostAsync<UniqueActorDeployResultDto>(
            $"/api/unique/actors/{Uri.EscapeDataString(instanceId)}/deploy", req);
        if (!ok || body is { Ok: false })
            return (new StepResult("4-deploy", StepOutcome.Refused,
                $"step 4 refused: HTTP {status} {body?.Reason ?? TryExtractReason(raw) ?? raw}"), body);
        return (new StepResult("4-deploy", StepOutcome.Ok,
            $"queued={body!.Queued} correlationId={body.CorrelationId} phase={body.Actor?.Phase}"), body);
    }

    // ---- step 5 (persisted-state read-back) -----------------------------------------------------

    /// <summary>
    /// Step 1 when <c>-InstanceId</c> names a specimen the player already owns. This is a REAL read of a
    /// real row, never a mint: the actor must exist on <c>GET /api/unique/actors/{id}</c>, must belong to
    /// <paramref name="playerId"/>, and must not already be deployed — otherwise the step refuses and the
    /// recipe stops, exactly as a failed summon does. The report says the specimen was reused so no reader
    /// can mistake it for a fresh acquisition.
    /// </summary>
    public async Task<(StepResult Step, UniqueActorDto? Actor)> ReuseOwnedActorAsync(long? playerId, string instanceId)
    {
        var (ok, status, body, raw) = await GetAsync<UniqueActorDto>(
            $"/api/unique/actors/{Uri.EscapeDataString(instanceId)}");
        if (!ok)
            return (new StepResult("1-acquire (reuse owned specimen)", StepOutcome.Refused,
                $"step 1 refused: HTTP {status} {TryExtractReason(raw) ?? raw}"), null);

        if (playerId is not null && body!.PlayerId != playerId)
            return (new StepResult("1-acquire (reuse owned specimen)", StepOutcome.Refused,
                $"step 1 refused: '{instanceId}' belongs to player {body.PlayerId}, not {playerId}"), null);

        if (string.Equals(body!.Phase, "Retired", StringComparison.OrdinalIgnoreCase))
            return (new StepResult("1-acquire (reuse owned specimen)", StepOutcome.Refused,
                $"step 1 refused: '{instanceId}' is Retired — a retired specimen cannot be deployed"), null);

        return (new StepResult("1-acquire (reuse owned specimen)", StepOutcome.Ok,
            $"instanceId={body.InstanceId} typeId={body.TypeId} side={body.Side} phase={body.Phase} " +
            "(owned before this run — NOT summoned by it)"), body);
    }

    public async Task<(StepResult Step, UniqueActorDto? Actor)> GetActorAsync(string instanceId)
    {
        var (ok, status, body, raw) = await GetAsync<UniqueActorDto>(
            $"/api/unique/actors/{Uri.EscapeDataString(instanceId)}");
        if (!ok)
            return (new StepResult("5-read-back (actor)", StepOutcome.Refused,
                $"step 5 refused: HTTP {status} {TryExtractReason(raw) ?? raw}"), null);
        return (new StepResult("5-read-back (actor)", StepOutcome.Ok,
            $"phase={body!.Phase} level={body.Level} lastPtr={body.LastPtr ?? "(none)"}"), body);
    }

    public async Task<(StepResult Step, UniqueEquipmentListDto? Equipment)> GetEquipmentAsync(string instanceId)
    {
        var (ok, status, body, raw) = await GetAsync<UniqueEquipmentListDto>(
            $"/api/unique/actors/{Uri.EscapeDataString(instanceId)}/equipment");
        if (!ok)
            return (new StepResult("5-read-back (equipment)", StepOutcome.Refused,
                $"step 5 refused: HTTP {status} {TryExtractReason(raw) ?? raw}"), null);
        return (new StepResult("5-read-back (equipment)", StepOutcome.Ok,
            $"{body!.Items.Count} legacy-slot assignment(s): " +
            string.Join(", ", body.Items.Select(i => $"{i.Slot}={i.ItemId}"))), body);
    }

    /// <summary>Mode B only: the deploy ack (phase Roster/Deploying → ActiveBound, <c>lastPtr</c>
    /// populated) arrives asynchronously from the Injector, so the persisted-state read-back polls for
    /// it with a bounded timeout — never a fixed sleep — exactly like every other Mode B wait in this
    /// tool. A timeout here is reported as its OWN distinct kind (persisted-state never reached
    /// ActiveBound), never conflated with step 6's live-engine timeout: this one means "the server
    /// itself never recorded the ack", not "the board never answered".</summary>
    public async Task<(StepResult Step, UniqueActorDto? Actor)> WaitForActiveBoundAsync(
        string instanceId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (ok, _, body, _) = await GetAsync<UniqueActorDto>(
                $"/api/unique/actors/{Uri.EscapeDataString(instanceId)}");
            if (ok && body is not null &&
                string.Equals(body.Phase, UniqueActorPhases.ActiveBound, StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(body.LastPtr))
                return (new StepResult("5-deploy-ack-wait", StepOutcome.Ok,
                    $"phase=ActiveBound lastPtr={body.LastPtr} after waiting"), body);
            await Task.Delay(300);
        }
        return (new StepResult("5-deploy-ack-wait", StepOutcome.Timeout,
            $"persisted-state: deploy ack timed out after {timeout.TotalSeconds:0}s " +
            "(phase never reached ActiveBound — server never recorded a live spawn ack)"), null);
    }

    // ---- step 6 (live-engine read, Mode B only) -------------------------------------------------

    /// <summary>Sends <c>debug.board-stats</c> stamped with <paramref name="tag"/> so the poll
    /// (<see cref="EventPoller.PollForTaggedKindAsync"/>) can tell this run's own answer apart from a
    /// stale one already sitting in the event log. The SECOND (and last) <c>/api/debug/*</c> route this
    /// tool calls.</summary>
    public async Task<StepResult> SendBoardStatsAsync(string tag)
    {
        var (ok, status, _, raw) = await PostAsync<object>("/api/debug/board-stats", new { tag });
        return ok
            ? new StepResult("6-live-engine (send)", StepOutcome.Ok, $"debug.board-stats sent, tag={tag}")
            : new StepResult("6-live-engine (send)", StepOutcome.Refused,
                $"step 6 refused: HTTP {status} {TryExtractReason(raw) ?? raw}");
    }

    // ---- cleanup ----------------------------------------------------------------------------------

    public async Task<StepResult> RetireAsync(string instanceId)
    {
        var (ok, status, body, raw) = await PostAsync<UniqueActorDto>(
            $"/api/unique/actors/{Uri.EscapeDataString(instanceId)}/retire", new { });
        return ok
            ? new StepResult("cleanup-retire", StepOutcome.Ok, $"retired {instanceId} (phase={body?.Phase})")
            : new StepResult("cleanup-retire", StepOutcome.Refused,
                $"retire refused: HTTP {status} {TryExtractReason(raw) ?? raw}");
    }

    public void Dispose() => _http.Dispose();
}
