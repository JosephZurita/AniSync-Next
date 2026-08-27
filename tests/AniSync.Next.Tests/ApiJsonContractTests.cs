using AniSync.Next.Api;
using AniSync.Next.Configuration;
using AniSync.Next.Domain;
using AniSync.Next.Persistence;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace AniSync.Next.Tests;

public sealed class ApiJsonContractTests
{
    private static readonly JsonSerializerSettings ShokoJsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new DefaultNamingStrategy(),
        },
    };

    [Fact]
    public void SessionUsesCamelCasePropertiesAndStringProviderNamesUnderShokoSettings()
    {
        var session = new SessionResponse("alice", true,
            [new ProviderConnectionResponse(ProviderKey.AniList, true, true, "remote")], 2, 1);

        var json = JObject.Parse(JsonConvert.SerializeObject(session, ShokoJsonSettings));

        json["shokoUsername"]!.Value<string>().Should().Be("alice");
        json["providers"]![0]!["provider"]!.Value<string>().Should().Be("AniList");
        json["pendingReviewCount"]!.Value<int>().Should().Be(2);
        json["ShokoUsername"].Should().BeNull();
        json["Providers"].Should().BeNull();
    }

    [Fact]
    public void ReviewHistoryMappingJobAndSearchPayloadsMatchFrontendContract()
    {
        var change = new PlannedChange(Guid.NewGuid(), "alice", 3, 4, "Title",
            ProviderKey.MyAnimeList, 5, ChangeKind.Decrease, ReviewReason.ProgressDecrease,
            8, 4, CanonicalListStatus.Watching, CanonicalListStatus.Watching,
            70, 60, "snapshot", DateTimeOffset.UtcNow);
        var payloads = new object[]
        {
            new ReviewItem(change.Id, change, DateTimeOffset.UtcNow),
            new SyncOutcome(SyncOutcomeKind.QueuedForReview, change),
            new ProviderMapping("alice", 4, ProviderKey.MyAnimeList, 5, "Title", true, DateTimeOffset.UtcNow),
            new PersistedSyncTrigger(Guid.NewGuid(), "alice", 3, "watch", DateTimeOffset.UtcNow),
            new ProviderMediaSearchResult(ProviderKey.MyAnimeList, 5, "Title", 12, 2026, null),
            new ReviewRefreshResult(
                [new ReviewItem(change.Id, change, DateTimeOffset.UtcNow)],
                [new ProviderRefreshFailure(ProviderKey.MyAnimeList, "Reconnect required.", false)]),
        };

        foreach (var payload in payloads)
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(payload, ShokoJsonSettings));
            json.Properties().Should().OnlyContain(property => char.IsLower(property.Name[0]));
            json.ToString().Should().NotContain("\"Provider\"").And.NotContain("\"Kind\"");
        }

        var review = JObject.Parse(JsonConvert.SerializeObject(payloads[0], ShokoJsonSettings));
        review["change"]!["provider"]!.Value<string>().Should().Be("MyAnimeList");
        review["change"]!["kind"]!.Value<string>().Should().Be("Decrease");
        review["change"]!["reviewReason"]!.Value<string>().Should().Be("ProgressDecrease");

        var refresh = JObject.Parse(JsonConvert.SerializeObject(payloads[^1], ShokoJsonSettings));
        refresh["failures"]![0]!["provider"]!.Value<string>().Should().Be("MyAnimeList");
        refresh["failures"]![0]!["isTransient"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public void CamelCaseRequestsDeserializeUsingStringEnums()
    {
        const string json = """
            {"seriesId":3,"aniDbAnimeId":4,"provider":"AniList","mediaId":5,"mediaTitle":"Title"}
            """;

        var request = JsonConvert.DeserializeObject<SaveMappingRequest>(json, ShokoJsonSettings);

        request.Should().NotBeNull();
        request!.SeriesId.Should().Be(3);
        request.Provider.Should().Be(ProviderKey.AniList);
        request.MediaTitle.Should().Be("Title");
    }

    [Fact]
    public void DiagnosticLogLevelUsesTheFrontendStringContract()
    {
        var settings = new UserSyncSettings { DiagnosticLogLevel = DiagnosticLogLevel.Detailed };

        var json = JObject.Parse(JsonConvert.SerializeObject(settings, ShokoJsonSettings));

        json["diagnosticLogLevel"]!.Value<string>().Should().Be("Detailed");
    }
}
