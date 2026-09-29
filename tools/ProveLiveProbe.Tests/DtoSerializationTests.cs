using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Server;
using FusionRpg.Tools.ProveLiveProbe;
using Xunit;

namespace FusionRpg.Tools.ProveLiveProbe.Tests;

/// <summary>Offline (de)serialization coverage — no live server needed. Confirms this tool's shared
/// <see cref="LiveProbeClient.JsonOpts"/> (camelCase, matching ASP.NET Core Minimal API's own wire
/// defaults) round-trips both the real FusionRpg.Contracts/FusionRpg.Server DTOs this tool sends, and
/// the locally-declared response shapes (Dtos.cs) that stand in for the server's own anonymous
/// objects.</summary>
public class DtoSerializationTests
{
    static readonly JsonSerializerOptions Opts = LiveProbeClient.JsonOpts;

    [Fact]
    public void DeployUniqueActorRequest_round_trips_and_uses_camelCase_wire_names()
    {
        var req = new DeployUniqueActorRequest
        {
            CorrelationId = "corr-1", Col = 3, Row = 2, MatchKey = "match-1", LoadoutJson = null,
        };
        var json = JsonSerializer.Serialize(req, Opts);

        Assert.Contains("\"correlationId\":\"corr-1\"", json);
        Assert.Contains("\"col\":3", json);
        Assert.Contains("\"loadoutJson\":null", json);

        var back = JsonSerializer.Deserialize<DeployUniqueActorRequest>(json, Opts);
        Assert.NotNull(back);
        Assert.Equal("corr-1", back!.CorrelationId);
        Assert.Equal(3, back.Col);
        Assert.Null(back.LoadoutJson);
    }

    [Fact]
    public void UniqueActorDeployResultDto_round_trips()
    {
        const string json = """
            {"ok":true,"reason":"","queued":true,"correlationId":"corr-1",
             "actor":{"instanceId":"ua-1","playerId":1,"side":"plant","typeId":42,"phase":"Deploying",
                      "level":1,"xp":0,"matchKey":"m1","lastPtr":null,"deployCorrelationId":"corr-1",
                      "revision":1,"createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z"}}
            """;
        var dto = JsonSerializer.Deserialize<UniqueActorDeployResultDto>(json, Opts);
        Assert.NotNull(dto);
        Assert.True(dto!.Ok);
        Assert.True(dto.Queued);
        Assert.Equal("ua-1", dto.Actor?.InstanceId);
        Assert.Equal("Deploying", dto.Actor?.Phase);
    }

    [Fact]
    public void SpawnUniqueActorRequest_serializes_with_camelCase_gameTypeId()
    {
        var req = new CreatureEndpoints.SpawnUniqueActorRequest { PlayerId = 1, Side = "plant", GameTypeId = 42 };
        var json = JsonSerializer.Serialize(req, Opts);
        Assert.Contains("\"gameTypeId\":42", json);
        Assert.Contains("\"side\":\"plant\"", json);
    }

    [Fact]
    public void SpawnUniqueActorResult_deserializes_the_servers_anonymous_shape()
    {
        const string json = """
            {"instanceId":"ua-2","ptr":"DEBUGabc123",
             "actor":{"instanceId":"ua-2","playerId":1,"side":"plant","typeId":7,"phase":"Roster",
                      "level":1,"xp":0,"matchKey":null,"lastPtr":null,"deployCorrelationId":null,
                      "revision":1,"createdAt":"","updatedAt":""}}
            """;
        var dto = JsonSerializer.Deserialize<SpawnUniqueActorResult>(json, Opts);
        Assert.NotNull(dto);
        Assert.Equal("ua-2", dto!.InstanceId);
        Assert.Equal("DEBUGabc123", dto.Ptr);
        Assert.Equal(7, dto.Actor?.TypeId);
    }

    [Fact]
    public void UniqueAptitudeState_deserializes_shares_dictionary()
    {
        const string json = """
            {"instanceId":"ua-1","playerId":1,"specimenLevel":1,"budget":100,"spent":30,
             "leftover":70,"withinBudget":true,"shares":{"Might":30}}
            """;
        var dto = JsonSerializer.Deserialize<UniqueAptitudeState>(json, Opts);
        Assert.NotNull(dto);
        Assert.True(dto!.WithinBudget);
        Assert.Equal(30, dto.Shares["Might"]);
    }

    [Fact]
    public void EquipRequest_serializes_with_camelCase_specimenId_and_role()
    {
        var req = new ItemEquipEndpoints.EquipRequest(1, "specimen-1", "item-1", "armament-primary");
        var json = JsonSerializer.Serialize(req, Opts);
        Assert.Contains("\"specimenId\":\"specimen-1\"", json);
        Assert.Contains("\"instanceId\":\"item-1\"", json);
        Assert.Contains("\"role\":\"armament-primary\"", json);
    }

    [Fact]
    public void ItemEquipOutcomeDto_round_trips_including_refusal_reason()
    {
        const string json = """
            {"ok":false,"verb":"equip","reason":"equip.role-mismatch: nope","specimenId":"s1",
             "role":"armament-primary","refKind":"rolled","refId":"item-1","replaced":null,"assignments":[]}
            """;
        var dto = JsonSerializer.Deserialize<ItemEquipOutcomeDto>(json, Opts);
        Assert.NotNull(dto);
        Assert.False(dto!.Ok);
        Assert.Contains("role-mismatch", dto.Reason);
    }

    [Fact]
    public void EventEnvelope_payload_deserializes_as_JsonElement_for_client_side_inspection()
    {
        const string json = """
            {"id":42,"t":"2026-01-01T00:00:00Z","game":"pvzrh-3.9","kind":"debug.board-stats",
             "payload":{"tag":"abc","plants":[{"ptr":"P1","typeId":1,"col":0,"row":0,
                        "attack":10,"hp":100,"maxHp":100}],"zombies":[]}}
            """;
        var evt = JsonSerializer.Deserialize<EventEnvelope>(json, Opts);
        Assert.NotNull(evt);
        Assert.Equal("debug.board-stats", evt!.Kind);
        Assert.IsType<JsonElement>(evt.Payload);

        var payload = EventPoller.ParseBoardStats(evt);
        Assert.NotNull(payload);
        Assert.Equal("abc", payload!.Tag);
        Assert.Single(payload.Plants);
        Assert.Equal("P1", payload.Plants[0].Ptr);
        Assert.Equal(100, payload.Plants[0].MaxHp);
        Assert.Empty(payload.Zombies);
    }

    [Fact]
    public void BoardStatsPayload_All_unions_plants_and_zombies()
    {
        var payload = new BoardStatsPayload
        {
            Plants = new List<BoardStatsEntity> { new() { Ptr = "P1" } },
            Zombies = new List<BoardStatsEntity> { new() { Ptr = "Z1" } },
        };
        Assert.Equal(new[] { "P1", "Z1" }, payload.All.Select(e => e.Ptr));
    }
}
