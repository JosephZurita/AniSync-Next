using AniSync.Next.Application;
using AniSync.Next.Domain;
using AniSync.Next.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

namespace AniSync.Next.Tests;

public sealed class MappingResolverTests
{
    [Fact]
    public async Task ExistingMappingWinsWithoutCallingRemoteDatabase()
    {
        using var directory = new TestDirectory();
        var store = Store(directory.Path);
        var existing = new ProviderMapping("alice", 100, ProviderKey.AniList, 50,
            "Verified", true, DateTimeOffset.UtcNow);
        await store.SaveMappingAsync(existing, default);
        var handler = new CountingHandler(_ => throw new InvalidOperationException("should not call"));
        var resolver = Resolver(store, handler);

        var result = await resolver.ResolveAsync(Source(), ProviderKey.AniList, default);

        result.Should().Be(existing);
        handler.Count.Should().Be(0);
    }

    [Theory]
    [InlineData(ProviderKey.AniList, 222)]
    [InlineData(ProviderKey.MyAnimeList, 333)]
    public async Task OfflineDatabaseMappingIsPersistedAsTrustedAutomaticMapping(ProviderKey provider, int expected)
    {
        using var directory = new TestDirectory();
        var store = Store(directory.Path);
        var resolver = Resolver(store, new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"anilist\":222,\"myanimelist\":333}"),
        }));

        var result = await resolver.ResolveAsync(Source(), provider, default);

        result.Should().NotBeNull();
        result!.MediaId.Should().Be(expected);
        result.IsUserVerified.Should().BeFalse();
        (await store.GetMappingAsync("alice", 100, provider, default)).Should().Be(result);
    }

    [Fact]
    public async Task MissingOrUnavailableOfflineMappingReturnsNullWithoutGuessing()
    {
        using var firstDirectory = new TestDirectory();
        var missing = Resolver(Store(firstDirectory.Path), new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}"),
        }));
        (await missing.ResolveAsync(Source(), ProviderKey.AniList, default)).Should().BeNull();

        using var secondDirectory = new TestDirectory();
        var unavailable = Resolver(Store(secondDirectory.Path), new CountingHandler(_ => throw new HttpRequestException("offline")));
        (await unavailable.ResolveAsync(Source(), ProviderKey.AniList, default)).Should().BeNull();
    }

    [Fact]
    public async Task ManualMappingIsMarkedVerifiedAndCanBeRemoved()
    {
        using var directory = new TestDirectory();
        var store = Store(directory.Path);
        var resolver = Resolver(store, new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var mapping = new ProviderMapping("alice", 100, ProviderKey.AniList, 12,
            "Manual", false, DateTimeOffset.MinValue);

        await resolver.SaveAsync(mapping, default);
        var saved = (await resolver.GetForUserAsync("alice", default)).Single();
        saved.IsUserVerified.Should().BeTrue();
        saved.UpdatedAt.Should().Be(FixedClock.Now);
        await resolver.RemoveAsync("alice", 100, ProviderKey.AniList, default);
        (await resolver.GetForUserAsync("alice", default)).Should().BeEmpty();
    }

    private static JsonPluginStateStore Store(string path) => new(path, NullLogger<JsonPluginStateStore>.Instance);
    private static MappingResolver Resolver(JsonPluginStateStore store, HttpMessageHandler handler) =>
        new(store, new Factory(handler), new FixedClock(), NullLogger<MappingResolver>.Instance);
    private static ShokoSeriesState Source() => new("alice", 1, 100, "Series", 3, 12, null);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://example.test/") };
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(factory(request));
        }
    }

    private sealed class FixedClock : IClock
    {
        public static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset IClock.UtcNow => Now;
    }
}
