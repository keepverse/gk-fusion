using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FusionRpg.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

using FusionRpg.Injector.Host;
using FusionRpg.Core.Time;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Injector.Effects;

namespace FusionRpg.Injector;

public sealed class RpgClient
{
    // Config-backed (tunables-ssot.md T1) — gk-core/data/tuning/net.v1.json's client.
    public static int QueueCap => FusionRpg.Core.Net.NetPolicy.Tuning.Client.QueueCap;
    public static int DrainSize => FusionRpg.Core.Net.NetPolicy.Tuning.Client.DrainSize;
    public static int FlushMs => FusionRpg.Core.Net.NetPolicy.Tuning.Client.FlushMs;

    private readonly string _base;
    private HttpClient? _http;
    private HubConnection? _hub;
    private readonly ConcurrentQueue<EventEnvelope> _queue = new();
    private long _bullets;
    private int _queued;
    private int _dropped;
    private int _inFlight;
    private long _lastSendMs = Environment.TickCount64;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public StatsConfig Stats { get; set; } = new();
    public bool SignalROk { get; private set; }
    public string LastError { get; private set; } = "";
    public int QueueCount => Volatile.Read(ref _queued);
    public int Dropped => Volatile.Read(ref _dropped);
    public bool InFlight => Volatile.Read(ref _inFlight) != 0;

    public RpgClient(string baseUrl)
    {
        _base = baseUrl.TrimEnd('/');
    }

    HttpClient Http()
    {
        if (_http != null) return _http;
        _http = CreateHttp();
        return _http;
    }

    static HttpClient CreateHttp()
    {
        try { HttpClient.DefaultProxy = new WebProxy(); } catch { /* IL2CPP WinHTTP */ }
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3)
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
    }

    /// <summary>Hello with the injector's debug-session state, so a restarted server's session mirror matches
    /// the live game (2026-09-15: the mirror read inactive while a lab session was still running).</summary>
    static HelloDto BuildHello() => new()
    {
        Game = RpgHost.GameProfileId,
        Version = "1.0.0",
        DebugSessionActive = DebugRuntime.SessionActive,
        DebugScenarioId = DebugRuntime.SessionActive ? DebugRuntime.ScenarioId : null
    };

    public async Task StartAsync()
    {
        await RefreshStatsAsync().ConfigureAwait(false);
        await RefreshPvzStatsAsync().ConfigureAwait(false);
        await RefreshCommanderAllocationAsync().ConfigureAwait(false);
        await RefreshUniqueAptitudesAsync().ConfigureAwait(false);
        await RefreshTreeBoundAtomsAsync().ConfigureAwait(false);
        await RefreshCommanderSnapshotCacheAsync().ConfigureAwait(false);
        await RefreshLawnDeployRosterCacheAsync().ConfigureAwait(false);
        await RefreshPowerIndexAsync().ConfigureAwait(false);
        try
        {
            _hub = new HubConnectionBuilder()
                .WithUrl(_base + "/hub/rpg", o =>
                {
                    o.HttpMessageHandlerFactory = inner =>
                    {
                        if (inner is SocketsHttpHandler sockets)
                        {
                            sockets.UseProxy = false;
                            return sockets;
                        }
                        return new SocketsHttpHandler { UseProxy = false };
                    };
                })
                .WithAutomaticReconnect()
                .Build();
            _hub.On<StatsConfig>("StatsUpdated", s =>
            {
                Stats = s;
                CheatCommandRunner.Enqueue(new CommandDto { Name = "reload-stats" });
            });
            _hub.On<object>("PvzStatsUpdated", _ =>
            {
                CheatCommandRunner.Enqueue(new CommandDto { Name = "pvz.stats.reload" });
            });
            _hub.On<AptitudesUpdatedScopeDto>("AptitudesUpdated", dto =>
            {
                // lawn LW1.6 (spec-actor-liveness-refresh.md): the ONE typed invalidation, received
                // here and applied as a revision BUMP only -- the derived memo re-resolves lazily on
                // its own next read (rule 3's bound, asserted in Core). A kind this build does not know
                // is REFUSED AND REPORTED, never skipped (rule 5: it means the server is newer than
                // this injector). A null kind is every pre-LW1.5 sender -- not a refusal, not logged.
                var applied = LawnLiveness.Apply(dto?.Kind, dto?.PlayerId ?? 0, dto?.InstanceId, out var livenessKind, out var refusal);
                if (!applied && !string.IsNullOrWhiteSpace(dto?.Kind))
                    RpgHost.Log.Warning($"liveness invalidation refused ({refusal}) for kind '{dto!.Kind}'");

                // A Ladder kind means the player's Θ inputs moved. The server's own `power.index.reload`
                // command is the human-player path for this; the kind covers the same change arriving on
                // this transport, and re-hydrating Θ is one line because RefreshPowerIndexAsync already
                // does it (it sets CheatState.CurrentPlayerId and calls ApplyPowerSnapshot).
                if (applied && livenessKind == LivenessInvalidationKind.Ladder)
                    _ = RefreshPowerIndexAsync();

                // species-progression `species-layer-delivery` step 6.2, trigger 6 (SP6.6) — the
                // save-switch key-set edge: a save switch replaces every row for every cache this ONE
                // broadcast already fans out to (commander, species, speciesLayers), but a mid-run
                // switch must not apply until the run ends (decisions.md "Mid-match switch: open run
                // keeps the player it started with"). Every OTHER scope (commander/unique/species/
                // preset-activate) keeps its existing unconditional-reload behavior unchanged.
                if (string.Equals(dto?.Scope, "save", StringComparison.OrdinalIgnoreCase))
                {
                    if (FusionRpg.Injector.Match.MatchHost.IsRunOpen)
                        FusionRpg.Injector.Match.MatchHost.RequestDeferredSaveSwitchRefresh();
                    else
                        CheatCommandRunner.Enqueue(new CommandDto { Name = "aptitudes.allocation.reload" });
                    return;
                }
                CheatCommandRunner.Enqueue(new CommandDto { Name = "aptitudes.allocation.reload" });
            });
            // lawn-tree-hydrate (T13): PassiveTreeEndpoints.cs already broadcasts this to BOTH groups
            // (line ~150-152) -- the injector simply never listened. A tree spend changes the SAME
            // commander-scope shared-tree atoms TreeBoundAtomsCache below caches, so it is a Hub
            // invalidation like AptitudesUpdated, not merely a UI refresh signal.
            _hub.On<object>("PassiveTreeUpdated", _ =>
            {
                CheatCommandRunner.Enqueue(new CommandDto { Name = "passive-tree.bound-atoms.reload" });
            });
            _hub.On<object>("CommandersUpdated", _ =>
            {
                CheatCommandRunner.Enqueue(new CommandDto { Name = "commander.snapshot.reload" });
            });
            // creature-lawn-deploy T2.1: CreaturesUpdated previously reached only WebGroup — a new specimen
            // (summon/fusion) changes the plant-side deploy roster, so the injector's own session cache
            // needs to hear it too (CreatureEndpoints.cs/FusionEndpoints.cs now also send to InjectorGroup).
            _hub.On<object>("CreaturesUpdated", _ =>
            {
                CheatCommandRunner.Enqueue(new CommandDto { Name = "lawn-deploy.roster.reload" });
            });
            _hub.On<CommandDto>("Command", cmd =>
            {
                try { RpgHost.Log.Info("[cheat-cmd] signalr " + (cmd?.Name ?? "?")); } catch { }
                // Patron designation is state, not a cheat action — cache it here and keep it
                // out of the cheat runner (spec-patron-creature.md; applies from the NEXT match).
                if (string.Equals(cmd?.Name, "patron.aura", StringComparison.OrdinalIgnoreCase))
                {
                    try { Effects.PatronCommand.Apply(cmd!); } catch (Exception ex) { RpgHost.Log.Warning("patron.aura: " + ex.Message); }
                    // creature-lawn-deploy T2.1: a patron reassignment changes WHO is excluded from the
                    // deploy roster — reuse this already-pushed signal instead of adding a second one.
                    CheatCommandRunner.Enqueue(new CommandDto { Name = "lawn-deploy.roster.reload" });
                    return;
                }

                CheatCommandRunner.Enqueue(cmd!);
            });
            _hub.Reconnected += async _ =>
            {
                try
                {
                    await _hub.InvokeAsync("Join", RpgConstants.InjectorGroup).ConfigureAwait(false);
                    await _hub.InvokeAsync("Hello", BuildHello()).ConfigureAwait(false);
                    // Found 2026-08-30 alongside the AptitudesUpdated group-mismatch fix
                    // (AptitudeEndpoints.cs): a reconnect (e.g. a server restart) re-joins the group
                    // but never re-syncs the two caches StartAsync populates at first connect, so any
                    // allocation/Θ change made during the disconnected window was silently lost until
                    // the next full injector process restart, not just the next reconnect.
                    await RefreshCommanderAllocationAsync().ConfigureAwait(false);
                    await RefreshUniqueAptitudesAsync().ConfigureAwait(false);
                    await RefreshTreeBoundAtomsAsync().ConfigureAwait(false);
                    await RefreshCommanderSnapshotCacheAsync().ConfigureAwait(false);
                    await RefreshLawnDeployRosterCacheAsync().ConfigureAwait(false);
                    await RefreshPowerIndexAsync().ConfigureAwait(false);
                    RpgHost.Log.Info("SignalR reconnected + re-joined + Hello (grant rehydrate)");
                }
                catch (Exception ex)
                {
                    RpgHost.Log.Warning("SignalR re-join/Hello failed: " + ex.Message);
                }
            };
            await _hub.StartAsync().ConfigureAwait(false);
            await _hub.InvokeAsync("Join", RpgConstants.InjectorGroup).ConfigureAwait(false);
            await _hub.InvokeAsync("Hello", BuildHello()).ConfigureAwait(false);
            SignalROk = true;
            RpgHost.Log.Info("SignalR connected");
        }
        catch (Exception ex)
        {
            SignalROk = false;
            LastError = ex.Message;
            RpgHost.Log.Warning("SignalR failed, using HTTP fallback: " + ex.Message);
        }
        GameHooks.RequestTypeCatalog();
    }

    /// <summary>One-shot guard for <see cref="ApplyUserSettingsOnceAsync"/>.</summary>
    int _userSettingsApplied;

    /// <summary>
    /// Applies the player's saved user settings once per injector session (solid-remediation
    /// 2026-09-17).
    ///
    /// <para><b>Why a pull and not a push.</b> The server has no injector-connect event to hang a push
    /// on — <c>InjectorConnected</c> is a five-second heartbeat window, not an edge. Without this, a
    /// player who enabled the world HUD would lose it on every game restart and the saved preference
    /// would look broken. The injector therefore asks for its own configuration once the server is
    /// first reachable, which is also the honest direction: the setting belongs to the player, and the
    /// injector is the thing that needs telling.</para>
    ///
    /// <para>Hung off the first successful command pull rather than a new bootstrap step, because that
    /// is already the moment "the server answered" is known to be true.</para>
    /// </summary>
    async Task ApplyUserSettingsOnceAsync()
    {
        if (Interlocked.Exchange(ref _userSettingsApplied, 1) != 0) return;
        try
        {
            var json = await Http().GetStringAsync(_base + "/api/settings").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return;
            foreach (var el in entries.EnumerateArray())
            {
                if (!el.TryGetProperty("key", out var k) || !el.TryGetProperty("value", out var v))
                    continue;

                var on = v.ValueKind == JsonValueKind.True
                    || (v.ValueKind == JsonValueKind.String
                        && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase));
                if (string.Equals(k.GetString(), FusionRpg.Core.Settings.UserSettingKeys.WorldHud, StringComparison.Ordinal))
                {
                    Hud.ActorHudPool.WorldHudEnabled = on;
                }
                else if (string.Equals(k.GetString(), FusionRpg.Core.Settings.UserSettingKeys.VisualEffects, StringComparison.Ordinal))
                    CheatCommandRunner.Enqueue(new CommandDto
                    {
                        Name = "vfx.enabled",
                        Payload = JsonSerializer.SerializeToElement(new { enabled = on }),
                    });
            }
        }
        catch (Exception ex)
        {
            // Leave the compiled default in place and allow a later attempt: a settings fetch failing
            // must not be what stops the injector from running.
            Interlocked.Exchange(ref _userSettingsApplied, 0);
            LastError = ex.Message;
        }
    }

    /// <summary>Pull queued cheat commands over HTTP (reliable path when SignalR group send fails).</summary>
    public async Task PullPendingCommandsAsync()
    {
        try
        {
            var json = await Http().GetStringAsync(_base + "/api/cheats/commands/pending").ConfigureAwait(false);
            await ApplyUserSettingsOnceAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return;
            foreach (var el in items.EnumerateArray())
            {
                var cmd = el.Deserialize<CommandDto>(Json);
                if (cmd != null)
                    CheatCommandRunner.Enqueue(cmd);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    public void BumpBullet() => Interlocked.Increment(ref _bullets);

    /// <summary>zomboss-deploy-ai T3.4 — fire-and-forget, matching `EnqueueAlmanacTextDump`'s own exact
    /// shape: `MatchHost.CheckZombossDeployTrigger()` runs inside its own `lock(Gate)` and must never
    /// await HTTP there (the established rule `LawnDeployRosterSessionCache`'s own doc comment already
    /// states for this same class of call), so the decision is made synchronously and the actual
    /// privileged mint+deploy is kicked off here, outside any lock, never awaited by the caller.</summary>
    public void EnqueueZombossDeploy(string speciesId, ulong matchSeed, string? matchKey)
    {
        if (string.IsNullOrWhiteSpace(speciesId)) return;
        _ = PostZombossDeployAsync(speciesId, matchSeed, matchKey);
    }

    async Task PostZombossDeployAsync(string speciesId, ulong matchSeed, string? matchKey)
    {
        try
        {
            var payload = new { speciesId, matchSeed, matchKey };
            var body = JsonSerializer.Serialize(payload, Json);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await Http().PostAsync($"{_base}/api/zomboss/deploy", content).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                RpgHost.Log.Warning($"[zomboss-deploy] {speciesId} -> {(int)resp.StatusCode}");
            else
                RpgHost.Log.Info($"[zomboss-deploy] deployed {speciesId}");
        }
        catch (Exception ex)
        {
            RpgHost.Log.Warning("[zomboss-deploy] " + ex.Message);
        }
    }

    /// <summary>Fire-and-forget almanac layer dump (not the event queue).</summary>
    public void EnqueueAlmanacTextDump(
        string side,
        int typeId,
        Dictionary<string, string?> fields,
        Dictionary<string, string> sources)
    {
        if (fields == null || fields.Count == 0) return;
        _ = UploadAlmanacTextDumpAsync(side, typeId, fields, sources);
    }

    async Task UploadAlmanacTextDumpAsync(
        string side,
        int typeId,
        Dictionary<string, string?> fields,
        Dictionary<string, string> sources)
    {
        try
        {
            try
            {
                var head = await Http().GetAsync($"{_base}/api/almanac/dump/{side}/{typeId}").ConfigureAwait(false);
                if (head.IsSuccessStatusCode)
                {
                    RpgHost.Log.Info($"[almanac-text] skip (cached) {side}/{typeId}");
                    return;
                }
            }
            catch { /* upload */ }

            var payload = new { fields, sources };
            var body = JsonSerializer.Serialize(payload, Json);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await Http().PutAsync($"{_base}/api/almanac/dump/{side}/{typeId}", content).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                RpgHost.Log.Warning($"[almanac-text] upload {side}/{typeId} -> {(int)resp.StatusCode}");
            else
                RpgHost.Log.Info($"[almanac-text] uploaded {side}/{typeId} fields={fields.Count}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            try { RpgHost.Log.Warning("[almanac-text] upload failed: " + ex.Message); } catch { }
        }
    }

    public void EnqueueIconDump(string side, int typeId, List<TypeIconCapture.Layer> layers)
    {
        if (layers == null || layers.Count == 0) return;
        _ = UploadIconDumpAsync(side, typeId, layers);
    }

    async Task UploadIconDumpAsync(string side, int typeId, List<TypeIconCapture.Layer> layers)
    {
        try
        {
            // Skip if server already has this dump (DB cache).
            try
            {
                var head = await Http().GetAsync($"{_base}/api/icons/dump/{side}/{typeId}").ConfigureAwait(false);
                if (head.IsSuccessStatusCode)
                {
                    RpgHost.Log.Info($"[icon] dump skip (cached) {side}/{typeId}");
                    return;
                }
            }
            catch { /* continue upload */ }

            var payload = new
            {
                layers = layers.Select(l => new
                {
                    name = l.Name,
                    source = l.Source,
                    width = l.Width,
                    height = l.Height,
                    pngBase64 = Convert.ToBase64String(l.Png)
                }).ToList()
            };
            var body = JsonSerializer.Serialize(payload, Json);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await Http().PutAsync($"{_base}/api/icons/dump/{side}/{typeId}", content).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                RpgHost.Log.Warning($"[icon] dump upload {side}/{typeId} -> {(int)resp.StatusCode}");
            else
                RpgHost.Log.Info($"[icon] dump uploaded {side}/{typeId} layers={layers.Count}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            try { RpgHost.Log.Warning("[icon] dump upload failed: " + ex.Message); } catch { }
        }
    }

    /// <summary>Fire-and-forget lawn screenshot upload (binary PNG, not the event queue).</summary>
    public void EnqueueScreenshot(byte[] png, string tag)
    {
        if (png == null || png.Length == 0) return;
        _ = UploadScreenshotAsync(png, ScreenshotCapture.SanitizeTag(tag));
    }

    async Task UploadScreenshotAsync(byte[] png, string tag)
    {
        try
        {
            using var content = new ByteArrayContent(png);
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            var resp = await Http().PostAsync(
                $"{_base}/api/debug/screenshot/upload?tag={Uri.EscapeDataString(tag)}", content)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                RpgHost.Log.Warning($"[screenshot] upload {tag} -> {(int)resp.StatusCode}");
            else
                RpgHost.Log.Info($"[screenshot] uploaded {tag} {png.Length}B");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            try { RpgHost.Log.Warning("[screenshot] upload failed: " + ex.Message); } catch { }
        }
    }

    /// <summary>Fire-and-forget raw scene dump upload (JSON, not the event queue).</summary>
    public void EnqueueDump(string json, string tag)
    {
        if (string.IsNullOrEmpty(json)) return;
        _ = UploadDumpAsync(json, ScreenshotCapture.SanitizeTag(tag));
    }

    async Task UploadDumpAsync(string json, string tag)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { tag, json }, Json);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var resp = await Http().PostAsync(
                $"{_base}/api/debug/dump/upload", content).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                RpgHost.Log.Warning($"[dump] upload {tag} -> {(int)resp.StatusCode}");
            else
                RpgHost.Log.Info($"[dump] uploaded {tag} {json.Length} chars");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            try { RpgHost.Log.Warning("[dump] upload failed: " + ex.Message); } catch { }
        }
    }

    public void Enqueue(string kind, object? payload, string? matchKey = null)
    {
        var n = Volatile.Read(ref _queued);
        if (n >= QueueCap && RpgConstants.IsDroppableWhenFull(kind))
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        _queue.Enqueue(new EventEnvelope
        {
            T = ServerClock.UtcNowDateTime.ToString("o"),
            Game = RpgHost.GameProfileId,
            Kind = kind,
            MatchKey = matchKey,
            Payload = payload
        });
        Interlocked.Increment(ref _queued);
    }

    public void TryFlush()
    {
        var n = Volatile.Read(ref _queued);
        if (n == 0) return;
        if (Volatile.Read(ref _inFlight) != 0) return;
        var elapsedMs = Environment.TickCount64 - _lastSendMs;
        if (n < DrainSize && elapsedMs < FlushMs) return;
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return;

        var batch = new List<EventEnvelope>(DrainSize);
        while (batch.Count < DrainSize && _queue.TryDequeue(out var e))
            batch.Add(e);
        if (batch.Count == 0)
        {
            Volatile.Write(ref _inFlight, 0);
            return;
        }
        Interlocked.Add(ref _queued, -batch.Count);
        _lastSendMs = Environment.TickCount64;
        _ = SendBatch(batch);
    }

    public async Task RefreshStatsAsync()
    {
        try
        {
            var json = await Http().GetStringAsync(_base + "/api/stats").ConfigureAwait(false);
            var s = JsonSerializer.Deserialize<StatsConfig>(json, Json);
            if (s != null) Stats = s;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    public async Task RefreshPvzStatsAsync()
    {
        try
        {
            var playerJson = await Http().GetStringAsync(_base + "/api/players/current").ConfigureAwait(false);
            using var playerDoc = JsonDocument.Parse(playerJson);
            var playerId = playerDoc.RootElement.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var pid)
                ? pid
                : 0L;
            if (playerId <= 0) return;
            var json = await Http().GetStringAsync(_base + "/api/pvz-stats/" + playerId + "/modifiers").ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<PvzStatsModifiersDto>(json, Json);
            if (dto == null) return;
            CheatState.ApplyPvzStatsModifiers(dto.PlayerId, dto.Revision, dto.Modifiers);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>aura-skill T5 (W1): the transport half of the commander allocation delegate
    /// <c>CheatState.ActorHub</c> needs. Mirrors <see cref="RefreshPvzStatsAsync"/>'s own shape exactly
    /// (same current-player lookup, same try/catch-to-LastError) — this reads
    /// <c>GET /api/aptitudes/{playerId}</c> (already shipped, <c>AptitudeEndpoints.ProjectState</c>'s
    /// <c>shares</c> map) rather than adding a new server endpoint. Called at session start
    /// (<see cref="StartAsync"/>) and on the same <c>"AptitudesUpdated"</c> SignalR broadcast
    /// <c>AptitudeEndpoints.BroadcastBestEffort</c> already sends on every save — never on a per-hit
    /// poll.
    ///
    /// <para><b>species-build `allocation-transport` (module 6).</b> The same response now also
    /// carries a `species` map (`{ speciesId: { aptitudeId: points } }`) alongside the unchanged
    /// `shares` — parsed here too, in the SAME fetch, at the SAME cadence, rather than a second HTTP
    /// round trip: `species` is additive on the wire, so it is additive here as well.</para>
    /// </summary>
    public async Task RefreshCommanderAllocationAsync()
    {
        try
        {
            var playerJson = await Http().GetStringAsync(_base + "/api/players/current").ConfigureAwait(false);
            using var playerDoc = JsonDocument.Parse(playerJson);
            var playerId = playerDoc.RootElement.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var pid)
                ? pid
                : 0L;
            if (playerId <= 0) return;
            var json = await Http().GetStringAsync(_base + "/api/aptitudes/" + playerId).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("shares", out var sharesEl) || sharesEl.ValueKind != JsonValueKind.Object)
                return;

            var allocation = FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Empty;
            foreach (var share in sharesEl.EnumerateObject())
            {
                if (!share.Value.TryGetInt64(out var points) || points == 0) continue;
                allocation += FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Single(
                    FusionRpg.Core.Stats.Aptitudes.AllocationScope.Commander, share.Name, points);
            }
            CheatState.ApplyCommanderAllocation(allocation);

            var speciesAllocations = new Dictionary<string, FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("species", out var speciesEl) && speciesEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var speciesEntry in speciesEl.EnumerateObject())
                {
                    if (speciesEntry.Value.ValueKind != JsonValueKind.Object) continue;
                    var speciesAllocation = FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Empty;
                    foreach (var share in speciesEntry.Value.EnumerateObject())
                    {
                        if (!share.Value.TryGetInt64(out var points) || points == 0) continue;
                        speciesAllocation += FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Single(
                            FusionRpg.Core.Stats.Aptitudes.AllocationScope.CreatureType, share.Name, points);
                    }
                    speciesAllocations[speciesEntry.Name] = speciesAllocation;
                }
            }
            CheatState.ApplySpeciesAllocations(speciesAllocations);

            // species-progression step 6.2, Transport (SP6.3) + injector cache (SP6.4): the SAME
            // response also carries `speciesLayers { base, mod, empire }` -- parsed here too, in the
            // SAME fetch, at the SAME cadence. `empire` (2b) is not yet read: it stays `{}` on the
            // wire until SP6.10, and this cache only ever needs base/mod today.
            var speciesLayersBase = new Dictionary<string, IReadOnlyList<FusionRpg.Core.Creatures.Layers.ProjectedLayerRow>>(StringComparer.Ordinal);
            var speciesLayersMod = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<FusionRpg.Core.Creatures.Layers.ProjectedLayerRow>>>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("speciesLayers", out var layersEl) && layersEl.ValueKind == JsonValueKind.Object)
            {
                if (layersEl.TryGetProperty("base", out var baseEl) && baseEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var speciesEntry in baseEl.EnumerateObject())
                        speciesLayersBase[speciesEntry.Name] = ParseLayerRows(speciesEntry.Value);
                }
                if (layersEl.TryGetProperty("mod", out var modEl) && modEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var empireEntry in modEl.EnumerateObject())
                    {
                        if (empireEntry.Value.ValueKind != JsonValueKind.Object) continue;
                        var perSpecies = new Dictionary<string, IReadOnlyList<FusionRpg.Core.Creatures.Layers.ProjectedLayerRow>>(StringComparer.Ordinal);
                        foreach (var speciesEntry in empireEntry.Value.EnumerateObject())
                            perSpecies[speciesEntry.Name] = ParseLayerRows(speciesEntry.Value);
                        speciesLayersMod[empireEntry.Name] = perSpecies;
                    }
                }
            }
            CheatState.ApplySpeciesLayers(speciesLayersBase, speciesLayersMod);

            // ai-empire-species EP4.18 (R23): the SAME response also carries `humanEmpire` and
            // `commanderByEmpire` -- the per-empire commander pools, keyed by EmpireId. Parsed here, in
            // the same fetch, at the same cadence; the human empire's own pool stays the `shares` cache
            // above, so this map is read only for a NON-human empire.
            var humanEmpire = doc.RootElement.TryGetProperty("humanEmpire", out var humanEl)
                && humanEl.ValueKind == JsonValueKind.String ? humanEl.GetString() : null;
            var commanderPoolsByEmpire = new Dictionary<string, FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("commanderByEmpire", out var poolsEl) && poolsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var empireEntry in poolsEl.EnumerateObject())
                {
                    if (empireEntry.Value.ValueKind != JsonValueKind.Object) continue;
                    var pool = FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Empty;
                    foreach (var share in empireEntry.Value.EnumerateObject())
                    {
                        if (!share.Value.TryGetInt64(out var points) || points == 0) continue;
                        pool += FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Single(
                            FusionRpg.Core.Stats.Aptitudes.AllocationScope.Commander, share.Name, points);
                    }
                    commanderPoolsByEmpire[empireEntry.Name] = pool;
                }
            }
            CheatState.ApplyCommanderPools(humanEmpire, commanderPoolsByEmpire);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>species-progression step 6.2, Transport (SP6.3) + injector cache (SP6.4) — parses one
    /// `speciesLayers.base.{speciesId}` or `speciesLayers.mod.{empireId}.{speciesId}` array into
    /// `ProjectedLayerRow`s via the ONE wire converter (<c>ProjectedLayerRowJson.FromWire</c>) both
    /// this fetch and the server's own emit (<c>AptitudeEndpoints.ProjectSpeciesLayers</c>) agree on.
    /// A malformed row is skipped, never thrown past this call -- one bad row from a server this
    /// injector cannot control must not drop every other row in the same refresh.</summary>
    static IReadOnlyList<FusionRpg.Core.Creatures.Layers.ProjectedLayerRow> ParseLayerRows(JsonElement arrayEl)
    {
        if (arrayEl.ValueKind != JsonValueKind.Array) return Array.Empty<FusionRpg.Core.Creatures.Layers.ProjectedLayerRow>();
        var rows = new List<FusionRpg.Core.Creatures.Layers.ProjectedLayerRow>();
        foreach (var rowEl in arrayEl.EnumerateArray())
        {
            var channel = rowEl.TryGetProperty("channel", out var chEl) ? chEl.GetString() ?? "" : "";
            var op = rowEl.TryGetProperty("op", out var opEl) ? opEl.GetString() ?? "" : "";
            var kind = rowEl.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() ?? "" : "";
            double? amount = rowEl.TryGetProperty("amount", out var amtEl) && amtEl.ValueKind == JsonValueKind.Number
                ? amtEl.GetDouble() : (double?)null;
            long? kMicro = rowEl.TryGetProperty("kMicro", out var kmEl) && kmEl.ValueKind == JsonValueKind.Number
                ? kmEl.GetInt64() : (long?)null;
            var sourceId = rowEl.TryGetProperty("sourceId", out var sidEl) ? sidEl.GetString() ?? "" : "";
            try
            {
                rows.Add(FusionRpg.Core.Creatures.Layers.ProjectedLayerRowJson.FromWire(
                    new FusionRpg.Core.Creatures.Layers.ProjectedLayerRowDto(channel, op, kind, amount, kMicro, sourceId)));
            }
            catch (ArgumentException)
            {
                // malformed row -- skip it, every other row in this refresh still applies
            }
        }
        return rows;
    }

    /// <summary>`unique-lawn-wire` (aptitude-sheet AS-1.1) — S4-locked fetch strategy: one
    /// <c>GET /api/aptitudes/unique/{instanceId}</c> per currently-Bound specimen (never a `uniques`
    /// map folded into <see cref="RefreshCommanderAllocationAsync"/>'s response, which the spec
    /// explicitly rules out). The Bound set comes from the SAME <c>MatchHost.Runtime</c> ptr↔instance
    /// index <see cref="FusionRpg.Injector.Match.UniqueBoundLoadout"/> already reads — never a second
    /// tracking structure. Replaces <c>CheatState</c>'s whole unique-allocation cache each call
    /// (matching <see cref="ApplySpeciesAllocations"/>'s own "wholesale replace" contract): a specimen
    /// no longer Bound this round simply stops appearing, so its allocation cannot go stale. One dead
    /// specimen's fetch failing (404 after it was released between snapshot and request, or a
    /// transient network error) is caught PER INSTANCE and skipped — it must never blank out every
    /// other still-Bound specimen's already-fetched allocation in the same round. Called at the same
    /// cadence as <see cref="RefreshCommanderAllocationAsync"/>: session start, reconnect, and the
    /// server's <c>"AptitudesUpdated"</c> broadcast — never a per-hit poll.</summary>
    public async Task RefreshUniqueAptitudesAsync()
    {
        try
        {
            var bound = FusionRpg.Injector.Match.MatchHost.Runtime.ToSnapshot().Bindings
                .Where(b => b.Phase == FusionRpg.Core.Match.UniqueBindingPhase.Bound)
                .Select(b => b.InstanceId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var byInstanceId = new Dictionary<string, FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation>(StringComparer.Ordinal);
            foreach (var instanceId in bound)
            {
                try
                {
                    var json = await Http().GetStringAsync(_base + "/api/aptitudes/unique/" + Uri.EscapeDataString(instanceId))
                        .ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("shares", out var sharesEl) || sharesEl.ValueKind != JsonValueKind.Object)
                        continue;

                    var allocation = FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Empty;
                    foreach (var share in sharesEl.EnumerateObject())
                    {
                        if (!share.Value.TryGetInt64(out var points) || points == 0) continue;
                        allocation += FusionRpg.Core.Stats.Aptitudes.AptitudeAllocation.Single(
                            FusionRpg.Core.Stats.Aptitudes.AllocationScope.UniqueCreature, share.Name, points);
                    }
                    byInstanceId[instanceId] = allocation;
                }
                catch (Exception ex)
                {
                    // Per-instance only -- one released/unreachable specimen must not blank the rest.
                    LastError = ex.Message;
                }
            }
            CheatState.ApplyUniqueAllocations(byInstanceId);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    bool _uniqueRefreshInFlight;
    bool _uniqueRefreshRerunQueued;
    readonly object _uniqueRefreshGate = new();
    Task? _uniqueRefreshLoopTask;

    /// <summary>Read-only, incremented once per actual <see cref="RefreshUniqueAptitudesAsync"/>
    /// attempt made from <see cref="RunBoundAptitudeRefreshLoop"/> — for a coalescing test only (same
    /// idiom as <c>LawnElementResolver.BoardLookupCount</c>): proves N rapid
    /// <see cref="TriggerBoundAptitudeRefresh"/> calls cost far fewer than N actual fetches.</summary>
    public int UniqueRefreshRunCount { get; private set; }

    /// <summary>Test seam only: awaits whatever refresh loop <see cref="TriggerBoundAptitudeRefresh"/>
    /// most recently started, so a test can assert coalescing deterministically instead of racing a
    /// fire-and-forget background task. Never called by production code — every real call site fires
    /// and forgets, exactly as before this seam existed.</summary>
    public Task WaitForUniqueRefreshLoopForTest() => _uniqueRefreshLoopTask ?? Task.CompletedTask;

    /// <summary>aptitude-sheet AS-1.1b (unique-lawn-wire fix) — the bind-edge cadence trigger
    /// `MatchHost.ConsumeLastBound` calls fire-and-forget. Coalesces concurrent binds into at most ONE
    /// extra round trip after the in-flight fetch completes, rather than one full
    /// <see cref="RefreshUniqueAptitudesAsync"/> sweep per bind: a bind that lands while a fetch is
    /// already running just sets a "run once more" flag, since the next run reads the CURRENT Bound
    /// set live (never a snapshot captured at trigger time) and so already covers every bind that
    /// happened during the in-flight fetch, however many there were. Safe to call from any thread —
    /// the injector main loop is single-threaded today, but this makes no assumption of that.</summary>
    public void TriggerBoundAptitudeRefresh()
    {
        lock (_uniqueRefreshGate)
        {
            if (_uniqueRefreshInFlight) { _uniqueRefreshRerunQueued = true; return; }
            _uniqueRefreshInFlight = true;
            _uniqueRefreshLoopTask = RunBoundAptitudeRefreshLoop();
        }
    }

    async Task RunBoundAptitudeRefreshLoop()
    {
        try
        {
            while (true)
            {
                UniqueRefreshRunCount++;
                await RefreshUniqueAptitudesAsync().ConfigureAwait(false);
                lock (_uniqueRefreshGate)
                {
                    if (!_uniqueRefreshRerunQueued)
                    {
                        _uniqueRefreshInFlight = false;
                        return;
                    }
                    _uniqueRefreshRerunQueued = false;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_uniqueRefreshGate)
            {
                _uniqueRefreshInFlight = false;
                _uniqueRefreshRerunQueued = false;
            }
            LastError = ex.Message;
        }
    }

    /// <summary>lawn-tree-hydrate (T13): the transport half of the tree bound-atoms delegate
    /// <c>CheatState.ActorHub</c> needs. Mirrors <see cref="RefreshCommanderAllocationAsync"/>'s own
    /// shape exactly (same current-player lookup, same try/catch-to-LastError) — the Injector has no
    /// SQL store, so <c>TreeBoundAtoms.ForPlayer</c> (SQL-backed) cannot run in-process; this reads
    /// the one HTTP round trip the Server exposes for it,
    /// <c>GET /api/passive-tree/bound-atoms/{playerId}</c>, rather than shipping tuning JSON and a
    /// store into the injector. Called at session start (<see cref="StartAsync"/>), on reconnect, and
    /// on the same <c>"PassiveTreeUpdated"</c> SignalR broadcast <c>PassiveTreeEndpoints.cs</c> already
    /// sends on every allocate — never on a per-hit poll.</summary>
    public async Task RefreshTreeBoundAtomsAsync()
    {
        try
        {
            var playerJson = await Http().GetStringAsync(_base + "/api/players/current").ConfigureAwait(false);
            using var playerDoc = JsonDocument.Parse(playerJson);
            var playerId = playerDoc.RootElement.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var pid)
                ? pid
                : 0L;
            if (playerId <= 0) return;
            var json = await Http().GetStringAsync(_base + "/api/passive-tree/bound-atoms/" + playerId).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            var atoms = new List<FusionRpg.Core.Stats.Derived.Subsystems.BoundDerivedAtom>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var channel = el.TryGetProperty("channel", out var cEl) ? cEl.GetString() : null;
                var opText = el.TryGetProperty("op", out var oEl) ? oEl.GetString() : null;
                var sourceId = el.TryGetProperty("sourceId", out var sEl) ? sEl.GetString() : null;
                if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(opText) || string.IsNullOrEmpty(sourceId))
                    continue;
                if (!el.TryGetProperty("amount", out var aEl) || !aEl.TryGetDouble(out var amount))
                    continue;
                if (!Enum.TryParse<FusionRpg.Core.Stats.Derived.DerivedModifierOp>(opText, ignoreCase: true, out var op))
                    continue; // an unrecognized op is skipped visibly here, never coerced to Flat
                atoms.Add(new FusionRpg.Core.Stats.Derived.Subsystems.BoundDerivedAtom(channel, op, amount, sourceId));
            }
            FusionRpg.Injector.Stats.TreeBoundAtomsCache.Apply(atoms);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>commander-surface P2: session cache for match snapshot at board.start — same cadence
    /// as <see cref="RefreshCommanderAllocationAsync"/> (StartAsync, reconnect, aptitudes reload).
    /// Never called from MatchHost.Apply.</summary>
    public async Task RefreshCommanderSnapshotCacheAsync()
    {
        try
        {
            var playerJson = await Http().GetStringAsync(_base + "/api/players/current").ConfigureAwait(false);
            using var playerDoc = JsonDocument.Parse(playerJson);
            var playerId = playerDoc.RootElement.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var pid)
                ? pid
                : 0L;
            if (playerId <= 0) return;

            var json = await Http().GetStringAsync(_base + "/api/commanders/" + playerId).ConfigureAwait(false);
            var list = JsonSerializer.Deserialize<CommanderListResponse>(json, Json);
            if (list == null || string.IsNullOrWhiteSpace(list.DefaultLawnCommanderId)) return;

            var row = list.Commanders.Find(c =>
                string.Equals(c.Id, list.DefaultLawnCommanderId, StringComparison.Ordinal))
                ?? list.Commanders.FirstOrDefault();

            FusionRpg.Core.Commanders.MatchCommanderSessionCache.Apply(
                list.DefaultLawnCommanderId,
                row?.DisplayName ?? FusionRpg.Core.Commanders.CommanderDirectoryHub.Current.DisplayName(
                    FusionRpg.Core.Commanders.CommanderDirectoryHub.Current.DefaultFor(
                        FusionRpg.Core.Commanders.EmpireId.Dave), playerName: null),
                row?.ActiveAuraId,
                row?.ActiveAuraName,
                CheatState.FetchedCommanderAllocation);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>creature-lawn-deploy T2.1: session cache for the plant-side deploy roster at
    /// board.start — same cadence and same "caller resolves, cache stores" split as
    /// <see cref="RefreshCommanderSnapshotCacheAsync"/>. Never called from MatchHost.Apply. A roster
    /// or patron read failure leaves the PREVIOUS cache standing (matching this method's own sibling)
    /// rather than clearing it to empty, so a transient hiccup doesn't wipe an otherwise-good cache the
    /// moment before board.start reads it.</summary>
    public async Task RefreshLawnDeployRosterCacheAsync()
    {
        try
        {
            var playerJson = await Http().GetStringAsync(_base + "/api/players/current").ConfigureAwait(false);
            using var playerDoc = JsonDocument.Parse(playerJson);
            var playerId = playerDoc.RootElement.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var pid)
                ? pid
                : 0L;
            if (playerId <= 0) return;

            var rosterJson = await Http().GetStringAsync(_base + "/api/creatures/" + playerId).ConfigureAwait(false);
            var roster = JsonSerializer.Deserialize<CreatureRosterDto>(rosterJson, Json);
            if (roster == null) return;

            string? patronInstanceId = null;
            try
            {
                var patronJson = await Http().GetStringAsync(_base + "/api/patron/" + playerId).ConfigureAwait(false);
                using var patronDoc = JsonDocument.Parse(patronJson);
                if (patronDoc.RootElement.TryGetProperty("patron", out var p) && p.ValueKind == JsonValueKind.Object &&
                    p.TryGetProperty("instanceId", out var pInst))
                    patronInstanceId = pInst.GetString();
            }
            catch (Exception ex)
            {
                // A patron-read failure must not accidentally OFFER the active Patron as deployable —
                // fail toward the safer (fewer options), not the more permissive, direction: if we
                // cannot confirm who is NOT the Patron, keep the previous cache instead of guessing.
                LastError = ex.Message;
                return;
            }

            // creature-lawn-deploy live-check (2026-09-07): a HypnoAlly-mode species has no deploy path
            // yet (T1.4's own refusal, `DeployAsync` returns `deploy.hypno-ally-not-implemented`) —
            // caught live by actually clicking a real fired prompt's own accept button, not guessed.
            // `CreatureSpeciesCatalog` is already `Configure`d on this process at mod load
            // (`RpgHost.Initialize`), so this is an in-process lookup against the same 829-species
            // roster the frontend's own species index resolves display info from — no new REST call.
            // Unknown-species and not-yet-configured both fail CLOSED (excluded), matching this
            // method's own patron-read-failure branch above: fewer options, never a guess.
            var eligible = roster.Items
                .Where(it => !string.Equals(it.Actor.InstanceId, patronInstanceId, StringComparison.Ordinal))
                .Where(it => FusionRpg.Core.Creatures.CreatureSpeciesCatalog.IsConfigured
                    && FusionRpg.Core.Creatures.CreatureSpeciesCatalog.IsKnown(it.Profile.SpeciesId)
                    && FusionRpg.Core.Creatures.CreatureSpeciesCatalog.Get(it.Profile.SpeciesId).DeployMode
                        != FusionRpg.Core.Creatures.CreatureDeployMode.HypnoAlly)
                .Select(it => new FusionRpg.Core.Match.LawnDeployRosterEntry(it.Actor.InstanceId, it.Profile.SpeciesId))
                .ToList();
            FusionRpg.Core.Match.LawnDeployRosterSessionCache.Apply(eligible);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>aura-skill T6 (W2): the hydration source `InjectorPowerIndexProvider.Hydrate` never
    /// had — Θ read as flat `P(0) = C` for every actor in production regardless of level
    /// (`PowerIndexHydrationTests.Magnitude_isFlatWhenThetaIsZero` pins this as the pre-T6 symptom).
    /// Reads the already-shipped `GET /api/rpg/progression/{playerId}/summary` (no new server
    /// endpoint) for `player.level` — the one field `ServerPowerIndexProvider.ReadSnapshot` itself
    /// hydrates from server-side (its own doc comment: `realmsAdvanced`/`pvzRuns` have no column
    /// anywhere yet, so both stay 0, matching the server's own honest partial hydration exactly, not
    /// a shortcut unique to this path). Called at session start and on demand — never per hit.</summary>
    /// <summary>
    /// live-probe Task 22 (2026-09-16): a unique-specimen deploy that fails inside the engine (a
    /// missing prefab throwing `NullReferenceException`, `SetPlant`/`SetZombie` returning null) was
    /// invisible to the server -- <c>CheatActions.SpawnExtraPlant</c>/<c>SpawnExtraZombieCore</c> only
    /// ever logged locally (<c>CheatState.Error</c>) and cleared their own pending-spawn bookkeeping,
    /// so the server's own `Deploying` row just sat there until `UniqueActorService.
    /// FailExpiredDeploys`'s timeout (`W5-D`) eventually cleaned it up minutes later. "The server
    /// simply timed its deploy ack out" (Task 22's own words) is exactly that gap.
    ///
    /// <para>Fire-and-forget on purpose, matching <see cref="PostZombossDeployAsync"/>: the injector
    /// already correctly aborted the LOCAL spawn attempt (`TryClearUniquePending`) regardless of
    /// whether this call lands, and this is strictly an additional, faster signal on top of the
    /// existing timeout safety net -- never a replacement for it, and never allowed to affect the
    /// local abort path if the POST itself fails.</para>
    /// </summary>
    public void ReportFailedDeploy(string instanceId, string reason)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return;
        _ = PostFailDeployAsync(instanceId, reason);
    }

    async Task PostFailDeployAsync(string instanceId, string reason)
    {
        try
        {
            var resp = await Http()
                .PostAsync($"{_base}/api/unique/actors/{Uri.EscapeDataString(instanceId)}/fail-deploy", null)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                RpgHost.Log.Warning($"[fail-deploy] {instanceId} ({reason}) -> {(int)resp.StatusCode}");
            else
                RpgHost.Log.Info($"[fail-deploy] reported {instanceId}: {reason}");
        }
        catch (Exception ex)
        {
            RpgHost.Log.Warning("[fail-deploy] " + ex.Message);
        }
    }

    public async Task RefreshPowerIndexAsync()
    {
        try
        {
            var playerJson = await Http().GetStringAsync(_base + "/api/players/current").ConfigureAwait(false);
            using var playerDoc = JsonDocument.Parse(playerJson);
            var playerId = playerDoc.RootElement.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var pid)
                ? pid
                : 0L;
            if (playerId <= 0) return;
            var json = await Http().GetStringAsync(_base + "/api/rpg/progression/" + playerId + "/summary").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var daveLevel = 0;
            if (doc.RootElement.TryGetProperty("player", out var playerEl)
                && playerEl.ValueKind == JsonValueKind.Object
                && playerEl.TryGetProperty("level", out var levelEl)
                && levelEl.TryGetInt64(out var lvl))
                daveLevel = checked((int)lvl);

            CheatState.ApplyPowerSnapshot(playerId,
                new FusionRpg.Core.Power.ActorLadderSnapshot(daveLevel, RealmsAdvanced: 0, PvzRuns: 0));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    public async Task PushCheatSnapshotAsync()
    {
        try
        {
            var snap = CheatState.Snapshot();
            snap["catalog"] = new Dictionary<string, object>
            {
                ["plants"] = SpawnCatalog.List("plant").Take(80).Select(e => new Dictionary<string, object>
                {
                    ["type"] = e.Type,
                    ["typeName"] = e.TypeName,
                    ["displayName"] = e.DisplayName ?? "",
                    ["spawnOk"] = e.SpawnOk
                }).ToList(),
                ["zombies"] = SpawnCatalog.List("zombie").Take(80).Select(e => new Dictionary<string, object>
                {
                    ["type"] = e.Type,
                    ["typeName"] = e.TypeName,
                    ["displayName"] = e.DisplayName ?? "",
                    ["spawnOk"] = e.SpawnOk
                }).ToList()
            };
            var body = JsonSerializer.Serialize(snap, Json);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            // Mirror catalog only (server endpoint must not overwrite entries or CheatsUpdated → web).
            await Http().PutAsync(_base + "/api/cheats/mirror", content).ConfigureAwait(false);
        }
        catch
        {
            /* ignore push failures */
        }
    }

    public async Task HeartbeatAsync()
    {
        try
        {
            var metrics = new List<MetricItem>
            {
                new()
                {
                    Name = "bullets_spawned",
                    Value = Interlocked.Read(ref _bullets),
                    Ts = ServerClock.UtcNowDateTime.ToString("o")
                }
            };
            if (SignalROk && _hub?.State == HubConnectionState.Connected)
            {
                await _hub.InvokeAsync("Heartbeat", new HelloDto()).ConfigureAwait(false);
                await _hub.InvokeAsync("Metrics", metrics).ConfigureAwait(false);
                return;
            }
            using var hb = new StringContent("{}", Encoding.UTF8, "application/json");
            await Http().PostAsync(_base + "/api/heartbeat", hb).ConfigureAwait(false);
            var body = JsonSerializer.Serialize(new { items = metrics }, Json);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            await Http().PostAsync(_base + "/api/metrics", content).ConfigureAwait(false);
        }
        catch
        {
            SignalROk = false;
        }
    }

    /// <summary>Ship one PerfProbe window — best-effort, off the frame path.</summary>
    public async Task PostPerfAsync(object window)
    {
        try
        {
            var body = JsonSerializer.Serialize(window, Json);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            await Http().PostAsync(_base + "/api/perf", content).ConfigureAwait(false);
        }
        catch
        {
            /* perf telemetry is never allowed to fail loudly */
        }
    }

    /// <summary>
    /// passive-tree-todo.md G6 (spec-gate-counters.md §4.3) — ship one batched gate-counter flush.
    /// Best-effort, matching <see cref="PostPerfAsync"/> exactly: a lost window costs a little
    /// progress, never correctness (§4.3), so a failed POST here is swallowed rather than retried or
    /// requeued — the accumulator has already cleared its own copy by the time this call is made
    /// (<c>GateCounterAccumulator.DrainAndClear</c>'s contract), and the next window's credits are
    /// unaffected either way.
    /// </summary>
    public async Task PostGateCounterCreditAsync(object body)
    {
        try
        {
            var json = JsonSerializer.Serialize(body, Json);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            await Http().PostAsync(_base + "/api/gate-counters/credit", content).ConfigureAwait(false);
        }
        catch
        {
            /* gate-counter credit is background progress, never allowed to fail loudly (§4.3) */
        }
    }

    private async Task SendBatch(List<EventEnvelope> batch)
    {
        try
        {
            if (SignalROk && _hub?.State == HubConnectionState.Connected)
            {
                await _hub.InvokeAsync("Events", batch).ConfigureAwait(false);
                return;
            }
            var body = JsonSerializer.Serialize(new EventBatch { Events = batch }, Json);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            await Http().PostAsync(_base + "/api/events", content).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            SignalROk = false;
        }
        finally
        {
            Volatile.Write(ref _inFlight, 0);
        }
    }

    /// <summary>
    /// CG-A4b: Hot live bag upsert for sheet projection.
    /// Server emits SignalR <c>ActorLiveStateChanged</c> on write.
    /// </summary>
    public async Task PostActorLiveStateAsync(string instanceId, ActorLiveStatePayload payload)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || payload == null) return;
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                liveStatuses = payload.LiveStatuses,
                shieldLayers = payload.ShieldLayers
            }, Json);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            await Http().PostAsync(
                    $"{_base}/api/internal/actors/{Uri.EscapeDataString(instanceId.Trim())}/live-state",
                    content)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>species-progression step 6.2, trigger 6 (SP6.6) — the ONLY field this handler reads
    /// off the shared `AptitudesUpdated` broadcast payload (`AptitudeEndpoints.AptitudesUpdatedDto`'s
    /// own `scope`). A minimal DTO on purpose: every other scope's handling is unchanged and does not
    /// need to inspect the payload at all.</summary>
    sealed class AptitudesUpdatedScopeDto
    {
        public string? Scope { get; set; }

        // lawn LW1.6: the additive liveness fields the SP6.6 transport now carries. Absent on every
        // pre-LW1.5 sender, which is why a null Kind is "no invalidation", never a refusal.
        public string? Kind { get; set; }
        public long PlayerId { get; set; }
        public string? InstanceId { get; set; }
    }
}
