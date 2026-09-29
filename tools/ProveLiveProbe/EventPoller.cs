using System.Text.Json;
using FusionRpg.Contracts;

namespace FusionRpg.Tools.ProveLiveProbe;

/// <summary>
/// This tool's own bounded ack/poll wait, mirroring the ALGORITHM <c>DebugEndpoints.cs</c>'s own
/// <c>PollForKind</c> uses server-side (deadline + fixed delay between checks, never a single fixed
/// sleep) — but over the plain, non-debug-scoped <c>GET /api/events</c>
/// (<c>Program.cs</c>'s <c>app.MapGet("/api/events", ...)</c>), never
/// <c>/api/debug/events</c>. That is a deliberate substitution, not an oversight: this tool's own
/// boundary rule (spec-live-probe-tool.md, tasks/live-probe-todo.md Task 8) caps it at exactly two
/// debug-shaped routes — the identity-only <c>spawn-unique-actor</c> shortcut (which turns out to live
/// under <c>/api/creatures/debug/*</c>, not <c>/api/debug/*</c> — see <c>LiveProbeClient</c>'s own
/// note) and <c>POST /api/debug/board-stats</c> itself — polling the read side through the generic,
/// non-debug events feed keeps that count at two while still reading the same underlying event log
/// (<c>RpgStore.ListEvents</c> backs both routes identically).
/// </summary>
public static class EventPoller
{
    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    const int PageSize = 500;

    /// <summary>
    /// <c>GET /api/events</c> only ever answers "events with id &gt; afterId, oldest-first, capped at
    /// 500" (<c>RpgStore.ListEvents</c>) — there is no HTTP-exposed "give me the current max id"
    /// (that's an in-process-only call, <c>RpgStore.GetMaxEventId</c>, used by
    /// <c>DebugEndpoints.cs</c>'s own handlers). This tool finds "now" by asking whether any event exists after a given id
    /// (one-row pages): doubling until none does, then bisecting — about 40 requests on a log of millions. Run ONCE, right
    /// before sending the <c>debug.board-stats</c> command, never inside the poll loop itself.
    ///
    /// <para>live-probe Task 16: this used to walk forward one full page at a time and give up after 100,000 events, which
    /// on the live server (about 650,000 events) returned an id far behind "now"; every later poll then read stale
    /// history and timed out whatever the game did.</para>
    /// </summary>
    public static async Task<long> FindCurrentMaxEventIdAsync(LiveProbeClient client)
    {
        async Task<bool> AnyAfter(long afterId)
        {
            var (ok, _, body, _) = await client.GetAsync<EventPage>($"/api/events?limit=1&afterId={afterId}");
            return ok && body?.Items is { Count: > 0 };
        }

        if (!await AnyAfter(0)) return 0;
        long known = 0;   // an event exists after this id
        long step = 1;
        while (await AnyAfter(known + step))
        {
            known += step;
            step = checked(step * 2);
        }
        long none = known + step; // no event exists after this id
        while (none - known > 1)
        {
            var mid = known + (none - known) / 2;
            if (await AnyAfter(mid)) known = mid;
            else none = mid;
        }
        return none;
    }

    /// <summary>
    /// Polls <c>GET /api/events</c> for the first <paramref name="kind"/> event, appearing strictly
    /// after <paramref name="afterId"/>, whose payload carries <paramref name="tag"/> (this tool's own
    /// correlation stamp — see <c>LiveProbeClient.SendBoardStatsAsync</c>) — never the first event of
    /// that kind at all, which could be a stale one still sitting in the log from an earlier run.
    /// Bounded by <paramref name="timeout"/>, checked on a fixed short delay, exactly
    /// <c>DebugEndpoints.PollForKind</c>'s own shape. Returns <c>null</c> on timeout — the caller reports
    /// that as its own distinct "timed out" outcome, never as a mismatch.
    /// </summary>
    public static async Task<EventEnvelope?> PollForTaggedKindAsync(
        LiveProbeClient client, long afterId, string kind, string tag, TimeSpan timeout)
    {
        // live-probe Task 16: the cursor advances past every page read, so a busy board that writes more than one page between
        // checks is read in full instead of the same first 500 events being re-read until the deadline.
        var deadline = DateTime.UtcNow + timeout;
        var cursor = afterId;
        while (DateTime.UtcNow < deadline)
        {
            while (DateTime.UtcNow < deadline)
            {
                var (ok, _, body, _) = await client.GetAsync<EventPage>($"/api/events?limit={PageSize}&afterId={cursor}");
                if (!ok || body?.Items is not { Count: > 0 } items) break;
                foreach (var e in items)
                {
                    if (string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase) && PayloadHasTag(e.Payload, tag)) return e;
                }
                cursor = items[^1].Id ?? cursor;
                if (items.Count < PageSize) break; // caught up with the log
            }
            await Task.Delay(300);
        }
        return null;
    }

    static bool PayloadHasTag(object? payload, string tag)
    {
        if (payload is not JsonElement el || el.ValueKind != JsonValueKind.Object) return false;
        return el.TryGetProperty("tag", out var t) && t.ValueKind == JsonValueKind.String &&
               string.Equals(t.GetString(), tag, StringComparison.Ordinal);
    }

    public static BoardStatsPayload? ParseBoardStats(EventEnvelope evt)
    {
        if (evt.Payload is not JsonElement el) return null;
        return JsonSerializer.Deserialize<BoardStatsPayload>(el.GetRawText(), JsonOpts);
    }

    sealed class EventPage
    {
        public List<EventEnvelope> Items { get; set; } = new();
    }
}
