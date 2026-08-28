using AniSync.Next.Domain;
using AniSync.Next.Providers;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace AniSync.Next.Tests;

public sealed class OAuthStateServiceTests
{
    [Fact]
    public void StateIsSignedSingleUseAndBoundToUserAndProvider()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new OAuthStateService(new FakeConfiguration(), cache, TimeProvider.System);

        var state = service.Create("alice", ProviderKey.MyAnimeList, "https://shoko.test", out var verifier);

        verifier.Should().NotBeNullOrWhiteSpace();
        service.TryVerify(state, out var verified).Should().BeTrue();
        verified.Should().Be(new VerifiedOAuthState("alice", ProviderKey.MyAnimeList, "https://shoko.test", verifier));
        service.TryVerify(state, out _).Should().BeFalse("OAuth state is single-use");
    }

    [Fact]
    public void TamperedStateIsRejected()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new OAuthStateService(new FakeConfiguration(), cache, TimeProvider.System);
        var state = service.Create("alice", ProviderKey.AniList, "https://shoko.test", out _);
        var parts = state.Split('.');
        var tamperedPayload = (parts[0][0] == 'A' ? 'B' : 'A') + parts[0][1..];

        service.TryVerify($"{tamperedPayload}.{parts[1]}", out _).Should().BeFalse();
    }
}
