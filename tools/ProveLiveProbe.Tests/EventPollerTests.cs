using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using FusionRpg.Contracts;
using FusionRpg.Tools.ProveLiveProbe;
using Xunit;

namespace FusionRpg.Tools.ProveLiveProbe.Tests;

/// <summary>live-probe Task 16: Task 11's live read timed out on a server holding about 650,000 events. The tail finder
/// stopped after 100,000 events and the poll re-read one fixed 500-event page, so the tagged board-stats event could not
/// be seen whatever the game did. Offline — a handler answers <c>GET /api/events</c> the way <c>RpgStore.ListEvents</c>
/// does (id &gt; afterId, oldest first, at most 500).</summary>
public class EventPollerTests
{
    [Fact]
    public async Task The_tail_is_found_on_a_log_longer_than_one_hundred_thousand_events()
    {
        var log = new FakeEventLog(250_000);
        using var client = new LiveProbeClient("http://probe.test", log);

        var tail = await EventPoller.FindCurrentMaxEventIdAsync(client);

        Assert.Equal(250_000, tail);
    }

    [Fact]
    public async Task A_tagged_event_more_than_one_page_after_the_start_is_found()
    {
        var log = new FakeEventLog(5_000, taggedAt: 2_300, tag: "probe-tag");
        using var client = new LiveProbeClient("http://probe.test", log);

        var found = await EventPoller.PollForTaggedKindAsync(client, afterId: 1_000, "debug.board-stats", "probe-tag", TimeSpan.FromSeconds(3));

        Assert.NotNull(found);
        Assert.Equal(2_300, found!.Id);
    }

    [Fact]
    public async Task An_empty_log_has_tail_zero()
    {
        using var client = new LiveProbeClient("http://probe.test", new FakeEventLog(0));
        Assert.Equal(0, await EventPoller.FindCurrentMaxEventIdAsync(client));
    }

    sealed class FakeEventLog : HttpMessageHandler
    {
        const int ServerCap = 500;
        readonly long _count;
        readonly long _taggedAt;
        readonly string? _tag;

        public FakeEventLog(long count, long taggedAt = -1, string? tag = null)
        {
            _count = count;
            _taggedAt = taggedAt;
            _tag = tag;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/api/events")
                throw new InvalidOperationException("unexpected route " + request.RequestUri.AbsolutePath);
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            var limit = Math.Min(int.Parse(query["limit"] ?? "100"), ServerCap);
            var after = long.Parse(query["afterId"] ?? "0");

            var items = new List<object>();
            for (var id = after + 1; id <= _count && items.Count < limit; id++)
            {
                items.Add(id == _taggedAt
                    ? new { id, t = "", kind = "debug.board-stats", payload = new { tag = _tag } }
                    : new { id, t = "", kind = "stat.writer", payload = new { } });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { items }), Encoding.UTF8, "application/json"),
            });
        }
    }
}
