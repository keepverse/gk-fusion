using System.Net.Http.Headers;
using System.Text.Json;
using FusionRpg.Contracts;

namespace FusionRpg.Tools.LawnCombatObserver;

/// <summary>
/// This tool's whole HTTP surface, against a real running <c>FusionRpg.Server</c>. Three routes, two
/// scopes, named:
/// <list type="bullet">
/// <item><description><c>GET /api/perf/recent</c> — not debug-scoped at all
/// (<c>PerfEndpoints.cs</c> maps it outside the <c>/api/debug</c> group). Pure read of an
/// already-shipped, unconditional telemetry ring.</description></item>
/// <item><description><c>POST /api/debug/snapshot</c> — Game Injector Debug (relays a command asking
/// the injector to report its OWN <c>DebugRuntime.Snapshot()</c>, itself read-only: it triggers no
/// game-state mutation, only a self-report). The ONE debug-shaped route this tool ever calls.</description></item>
/// <item><description><c>GET /api/events</c> / <c>GET /api/debug/session</c> — RPG Server Debug,
/// in-memory reads only, no injector relay.</description></item>
/// </list>
/// </summary>
public sealed class ObserverClient : IDisposable
{
    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    readonly HttpClient _http;

    public ObserverClient(string baseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<(bool Ok, int Status, T? Body)> GetAsync<T>(string path)
    {
        try
        {
            using var resp = await _http.GetAsync(path).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(raw))
                return (resp.IsSuccessStatusCode, (int)resp.StatusCode, default);
            return (true, (int)resp.StatusCode, JsonSerializer.Deserialize<T>(raw, JsonOpts));
        }
        catch
        {
            return (false, 0, default);
        }
    }

    /// <summary><c>GET /api/perf/recent</c> — not debug-scoped. Every window the ring currently holds
    /// (newest-last), up to <paramref name="limit"/>.</summary>
    public async Task<List<PerfWindowDto>> GetRecentPerfWindowsAsync(int limit)
    {
        var (ok, _, body) = await GetAsync<PerfWindowsPage>($"/api/perf/recent?limit={limit}");
        return ok && body?.Items is { } items ? items : new List<PerfWindowDto>();
    }

    /// <summary>Game Injector Debug: asks the injector to self-report <c>DebugRuntime.Snapshot()</c> as
    /// a <c>debug.snapshot</c> event, then reads the answer back over the plain, non-debug-scoped
    /// <c>GET /api/events</c> feed — same two-step shape ProveLiveProbe's own
    /// <c>SendBoardStatsAsync</c>/<c>EventPoller</c> pair uses for exactly the same reason (cap the
    /// debug-shaped surface at one call; poll its answer through the ordinary read path).</summary>
    public async Task<bool?> TryReadInjectorSessionActiveAsync(TimeSpan timeout)
    {
        var before = await FindCurrentMaxEventIdAsync();
        // NOTE: this route is a GET, not a POST — MapGet("/snapshot", ...) in DebugEndpoints.cs still
        // relays a command as a GET's side effect. Verified live against a running server (a POST here
        // 405s, "Allow: GET, HEAD").
        var (sent, _, _) = await GetAsync<object>("/api/debug/snapshot");
        if (!sent) return null;

        // NOTE: /api/events (the plain, non-debug route this tool polls — see class doc) does NOT
        // support a server-side `kinds` filter (only /api/debug/events does; verified live). This walks
        // forward through pages of the real event log and filters by Kind client-side, the same shape
        // ProveLiveProbe's own EventPoller.PollForTaggedKindAsync uses for exactly the same reason.
        var afterId = before;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            for (var walked = 0; walked < 20; walked++) // one poll tick walks at most 20*500 events
            {
                var (ok, _, page) = await GetAsync<EventPage>($"/api/events?limit=500&afterId={afterId}");
                if (!ok || page?.Items is not { Count: > 0 } items) break;

                DebugSnapshotPayloadDto? found = null;
                foreach (var e in items)
                {
                    if (!string.Equals(e.Kind, "debug.snapshot", StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.Payload is not JsonElement el) continue;
                    var parsed = JsonSerializer.Deserialize<DebugSnapshotPayloadDto>(el.GetRawText(), JsonOpts);
                    if (parsed is not null) found = parsed; // keep walking — last one in the page wins
                }
                afterId = items[^1].Id ?? afterId;
                if (found is not null) return found.SessionActive;
                if (items.Count < 500) break; // reached the current tail this page
            }
            await Task.Delay(300);
        }
        return null; // timed out — caller reports this as "could not confirm", never as false
    }

    /// <summary>RPG Server Debug, no injector relay: the server's own in-memory session mirror.</summary>
    public async Task<bool?> TryReadServerSessionActiveAsync()
    {
        var (ok, _, body) = await GetAsync<ServerDebugSessionDto>("/api/debug/session");
        return ok ? body?.SessionActive : null;
    }

    /// <summary>Exponential-then-binary search over <c>GET /api/events?afterId=X&amp;limit=1</c> for the
    /// current tail of the event log, in O(log n) requests regardless of table size — the same
    /// technique every PowerShell live-test/prove script's own <c>Get-MaxEventId</c> helper already
    /// uses (e.g. <c>scripts/prove-overlay-combat.ps1</c>). A linear forward page-walk (the previous
    /// shape here: 200 pages of 500 rows, i.e. capped at 100,000 events) is NOT equivalent on a
    /// long-running dev server: this repo's real events table was measured live at 300,000+ rows
    /// (confirmed 2026-09-14: <c>afterId=300000</c> still returns real rows, <c>afterId=350000</c>
    /// does not), so that walk always exhausted its budget short of the true tail — landing "before"
    /// on a stale, days-old event id. <see cref="TryReadInjectorSessionActiveAsync"/> then had no way
    /// to ever catch up to a freshly emitted event within its 5s timeout, so it silently timed out
    /// (returned null) on every call, in every session, regardless of whether the injector correctly
    /// emitted <c>debug.snapshot</c> — which it does; the injector-side handler was verified correct.
    /// This was the actual root cause of T0's `EventDrainActiveProvenThroughout` being unprovable.</summary>
    async Task<long> FindCurrentMaxEventIdAsync()
    {
        async Task<bool> HasAfterAsync(long id)
        {
            var (ok, _, page) = await GetAsync<EventPage>($"/api/events?limit=1&afterId={id}");
            return ok && page?.Items is { Count: > 0 };
        }

        if (!await HasAfterAsync(0)) return 0L;

        var lo = 0L;
        var hi = 1L;
        while (await HasAfterAsync(hi))
        {
            lo = hi;
            if (hi > long.MaxValue / 2) break;
            hi *= 2L;
        }
        while (lo + 1L < hi)
        {
            var mid = lo + (hi - lo) / 2L;
            if (await HasAfterAsync(mid)) lo = mid; else hi = mid;
        }
        return lo;
    }

    public void Dispose() => _http.Dispose();

    sealed class PerfWindowsPage
    {
        public List<PerfWindowDto> Items { get; set; } = new();
    }

    sealed class EventPage
    {
        public List<EventEnvelope> Items { get; set; } = new();
    }
}
