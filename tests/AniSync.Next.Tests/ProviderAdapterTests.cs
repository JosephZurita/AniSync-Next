using AniSync.Next.Application;
using AniSync.Next.Domain;
using AniSync.Next.Providers;
using FluentAssertions;
using System.Net;

namespace AniSync.Next.Tests;

public sealed class ProviderAdapterTests
{
    [Fact]
    public async Task MyAnimeListReadsEveryPaginationPage()
    {
        var handler = new QueuedJsonHandler(
            "{\"data\":[{\"node\":{\"id\":1,\"title\":\"One\",\"num_episodes\":12},\"list_status\":{\"status\":\"watching\",\"score\":7,\"num_episodes_watched\":4}}],\"paging\":{\"next\":\"https://api.myanimelist.net/v2/next\"}}",
            "{\"data\":[{\"node\":{\"id\":2,\"title\":\"Two\",\"num_episodes\":1},\"list_status\":{\"status\":\"completed\",\"score\":10,\"num_episodes_watched\":1}}],\"paging\":{}}");
        var provider = new MyAnimeListProvider(Transport(handler));

        var list = await provider.GetListAsync("alice", default);

        list.Keys.Should().BeEquivalentTo([1, 2]);
        list[1].RatingRaw.Should().Be(70);
        list[2].Status.Should().Be(CanonicalListStatus.Completed);
    }

    [Fact]
    public async Task MyAnimeListReadsAnExistingListEntryDirectly()
    {
        var handler = new QueuedJsonHandler(
            "{\"id\":55888,\"title\":\"Mushoku Tensei II: Isekai Ittara Honki Dasu Part 2\",\"num_episodes\":12,\"my_list_status\":{\"status\":\"completed\",\"score\":0,\"num_episodes_watched\":12}}");
        var provider = new MyAnimeListProvider(Transport(handler));

        var entry = await provider.GetEntryAsync("alice", 55888, default);

        entry.Should().NotBeNull();
        entry!.MediaId.Should().Be(55888);
        entry.Progress.Should().Be(12);
        entry.Status.Should().Be(CanonicalListStatus.Completed);
    }

    [Fact]
    public async Task MyAnimeListConvertsCanonicalRatingToNearestTenPointScore()
    {
        var handler = new QueuedJsonHandler("{\"status\":\"watching\",\"score\":8,\"num_episodes_watched\":5}");
        var provider = new MyAnimeListProvider(Transport(handler));
        var change = Change(ProviderKey.MyAnimeList, 83);

        var result = await provider.ApplyAsync("alice", change, default);

        handler.Bodies.Should().ContainSingle().Which.Should().Contain("score=8");
        result.RatingRaw.Should().Be(80);
    }

    [Fact]
    public async Task MyAnimeListClearsAProviderRatingWhenShokoRatingWasRemoved()
    {
        var handler = new QueuedJsonHandler("{\"status\":\"watching\",\"score\":0,\"num_episodes_watched\":5}");
        var provider = new MyAnimeListProvider(Transport(handler));

        await provider.ApplyAsync("alice", Change(ProviderKey.MyAnimeList, null), default);

        handler.Bodies.Single().Should().Contain("score=0");
    }

    [Fact]
    public async Task AniListReadsEveryPaginationPageAndRawRatings()
    {
        var handler = new QueuedJsonHandler(
            "{\"data\":{\"Viewer\":{\"id\":5,\"name\":\"remote\",\"avatar\":{\"large\":null}}}}",
            "{\"data\":{\"Page\":{\"pageInfo\":{\"hasNextPage\":true},\"mediaList\":[{\"status\":\"CURRENT\",\"progress\":3,\"score\":77,\"media\":{\"id\":10,\"episodes\":12,\"title\":{\"romaji\":\"One\",\"english\":null}}}]}}}",
            "{\"data\":{\"Page\":{\"pageInfo\":{\"hasNextPage\":false},\"mediaList\":[{\"status\":\"COMPLETED\",\"progress\":1,\"score\":90,\"media\":{\"id\":11,\"episodes\":1,\"title\":{\"romaji\":\"Two\",\"english\":\"Two EN\"}}}]}}}");
        var provider = new AniListProvider(Transport(handler));

        var list = await provider.GetListAsync("alice", default);

        list.Keys.Should().BeEquivalentTo([10, 11]);
        list[10].RatingRaw.Should().Be(77);
        list[11].Title.Should().Be("Two EN");
        handler.Count.Should().Be(3);
    }

    [Fact]
    public async Task AniListGraphQlErrorsAreStructuredPermanentFailures()
    {
        var provider = new AniListProvider(Transport(new QueuedJsonHandler(
            "{\"errors\":[{\"message\":\"Validation failed\"}],\"data\":null}")));

        var action = () => provider.GetAccountAsync("alice", default);

        var exception = await action.Should().ThrowAsync<ProviderException>();
        exception.Which.IsTransient.Should().BeFalse();
        exception.Which.Message.Should().Contain("Validation failed");
    }

    [Fact]
    public async Task AniListClearsAProviderRatingWithRawZero()
    {
        var handler = new QueuedJsonHandler("{\"data\":{\"SaveMediaListEntry\":{\"status\":\"CURRENT\",\"progress\":5,\"score\":0,\"media\":{\"id\":99,\"episodes\":12,\"title\":{\"romaji\":\"Title\",\"english\":null}}}}}");
        var provider = new AniListProvider(Transport(handler));

        await provider.ApplyAsync("alice", Change(ProviderKey.AniList, null), default);

        handler.Bodies.Single().Should().Contain("\"scoreRaw\":0");
    }

    private static ProviderHttpTransport Transport(HttpMessageHandler handler) =>
        new(new StaticFactory(handler), new StaticTokens(), new NoDelay());

    private static PlannedChange Change(ProviderKey provider, int? rating) => new(
        Guid.NewGuid(), "alice", 1, 2, "Title", provider, 99, ChangeKind.Advance,
        ReviewReason.None, 4, 5, CanonicalListStatus.Watching, CanonicalListStatus.Watching,
        70, rating, "token", DateTimeOffset.UtcNow);

    private sealed class StaticFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class StaticTokens : IProviderTokenService
    {
        public Task<string> GetAccessTokenAsync(string username, ProviderKey provider, CancellationToken cancellationToken) => Task.FromResult("token");
        public Task<string> ForceRefreshAsync(string username, ProviderKey provider, CancellationToken cancellationToken) => Task.FromResult("token");
        public Task<Configuration.ProviderAuthorization> ExchangeCodeAsync(ProviderKey provider, string username, string code, string redirectUri, string? codeVerifier, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoDelay : IProviderDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class QueuedJsonHandler(params string[] responses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public int Count { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            var json = responses[Math.Min(Count, responses.Length - 1)];
            Count++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
