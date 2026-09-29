using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using FusionRpg.Contracts;
using FusionRpg.Tools.ProveLiveProbe;
using Xunit;

namespace FusionRpg.Tools.ProveLiveProbe.Tests;

/// <summary>live-probe Task 20: a run with more kill facts than one facts page (500) must be read in full,
/// so none of its kill souls reads as FactNotFound. Offline — a handler answers the three Server routes
/// from memory and honours <c>afterId</c> the way <c>RpgStore.ListPvzActivityFacts</c> does (id &lt; afterId,
/// newest first).</summary>
public class SoulProvenancePagingTests
{
    const long Player = 1;
    const long Run = 7;
    const int Kills = 700; // more than one 500-row page, less than two

    [Fact]
    public async Task Facts_past_the_first_page_are_read_so_no_kill_is_FactNotFound()
    {
        var server = new FakeServer(honourAfterId: true);
        using var client = new LiveProbeClient("http://probe.test", server);

        var step = await client.GetSoulProvenanceAsync(Player);

        Assert.Equal(StepOutcome.Ok, step.Outcome);
        Assert.Contains("FactNotFound:0", step.Detail);
        Assert.Contains($"Game:{Kills}", step.Detail);
        Assert.Equal(2, server.FactPagesServed);
    }

    [Fact]
    public async Task A_server_that_ignores_afterId_is_refused_not_counted_twice()
    {
        using var client = new LiveProbeClient("http://probe.test", new FakeServer(honourAfterId: false));

        var step = await client.GetSoulProvenanceAsync(Player);

        Assert.Equal(StepOutcome.Refused, step.Outcome);
        Assert.Contains("ignored afterId", step.Detail);
    }

    sealed class FakeServer : HttpMessageHandler
    {
        readonly bool _honourAfterId;
        readonly List<SoulLedgerEntryDto> _ledger = new();
        readonly List<PvzActivityFactDto> _facts = new();

        public int FactPagesServed { get; private set; }

        public FakeServer(bool honourAfterId)
        {
            _honourAfterId = honourAfterId;
            for (var i = 1; i <= Kills; i++)
            {
                _facts.Add(new PvzActivityFactDto { Id = 1000 + i, RunId = Run, Kind = "ZombieKilled", PayloadJson = """{"spawnOrigin":"game"}""" });
                _ledger.Add(new SoulLedgerEntryDto { Id = i, RunId = Run, Delta = 1, Reason = "kill", RefKind = "activity_fact", RefId = (1000 + i).ToString() });
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            var limit = int.Parse(query["limit"] ?? "100");
            var after = long.Parse(query["afterId"] ?? "0");

            object body = path switch
            {
                "/api/souls/1" => new SoulBalanceDto { PlayerId = Player, Balance = Kills },
                "/api/souls/1/ledger" => new SoulLedgerDto { PlayerId = Player, Items = Page(_ledger, e => e.Id, after, limit) },
                "/api/pvz-activity/1/facts" => FactsPage(after, limit),
                _ => throw new InvalidOperationException("unexpected route " + path),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, LiveProbeClient.JsonOpts), Encoding.UTF8, "application/json"),
            });
        }

        PvzActivityFactsPageDto FactsPage(long after, int limit)
        {
            FactPagesServed++;
            return new PvzActivityFactsPageDto { PlayerId = Player, Items = Page(_facts, f => f.Id, _honourAfterId ? after : 0, limit) };
        }

        static List<T> Page<T>(IEnumerable<T> rows, Func<T, long> id, long after, int limit) =>
            rows.Where(r => after <= 0 || id(r) < after).OrderByDescending(id).Take(limit).ToList();
    }
}
