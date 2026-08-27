using AniSync.Next.Configuration;
using AniSync.Next.Domain;
using AniSync.Next.Providers;
using FluentAssertions;
using System.Net;

namespace AniSync.Next.Tests;

public sealed class ProviderTokenServiceTests
{
    [Fact]
    public async Task RefreshPreservesExistingRefreshTokenWhenProviderDoesNotRotateIt()
    {
        var configuration = new FakeConfiguration();
        configuration.SaveAuthorization("alice", ProviderKey.AniList, new ProviderAuthorization
        {
            Username = "remote",
            AccountId = 4,
            AccessToken = "expired",
            RefreshToken = "keep-me",
            ExpiresAt = DateTimeOffset.MinValue,
        });
        var handler = new JsonHandler("{\"access_token\":\"new-access\",\"expires_in\":3600}");
        var service = new ProviderTokenService(new Factory(handler), configuration, TimeProvider.System);

        var token = await service.ForceRefreshAsync("alice", ProviderKey.AniList, default);

        token.Should().Be("new-access");
        configuration.GetAuthorization("alice", ProviderKey.AniList)!.RefreshToken.Should().Be("keep-me");
    }

    [Fact]
    public async Task TokenRateLimitIsTransientAndPreservesRetryAfter()
    {
        var configuration = new FakeConfiguration();
        configuration.SaveAuthorization("alice", ProviderKey.AniList, new ProviderAuthorization
        {
            AccessToken = "expired",
            RefreshToken = "refresh",
            ExpiresAt = DateTimeOffset.MinValue,
        });
        var handler = new ResponseHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("slow down"),
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(9));
            return response;
        });
        var service = new ProviderTokenService(new Factory(handler), configuration, TimeProvider.System);

        var action = () => service.ForceRefreshAsync("alice", ProviderKey.AniList, default);

        var exception = await action.Should().ThrowAsync<AniSync.Next.Application.ProviderException>();
        exception.Which.IsTransient.Should().BeTrue();
        exception.Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task InvalidTokenRequestRemainsPermanent()
    {
        var configuration = new FakeConfiguration();
        configuration.SaveAuthorization("alice", ProviderKey.AniList, new ProviderAuthorization
        {
            AccessToken = "expired",
            RefreshToken = "refresh",
            ExpiresAt = DateTimeOffset.MinValue,
        });
        var handler = new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid grant"),
        });
        var service = new ProviderTokenService(new Factory(handler), configuration, TimeProvider.System);

        var action = () => service.ForceRefreshAsync("alice", ProviderKey.AniList, default);

        var exception = await action.Should().ThrowAsync<AniSync.Next.Application.ProviderException>();
        exception.Which.IsTransient.Should().BeFalse();
        exception.Which.RetryAfter.Should().BeNull();
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory());
    }
}

internal sealed class FakeConfiguration : IPluginConfigurationService
{
    private readonly PluginConfiguration _configuration = new();
    private readonly Dictionary<(string, ProviderKey), ProviderAuthorization> _authorizations = new();
    public PluginConfiguration Read() => _configuration;
    public UserSyncSettings GetUserSettings(string username) => _configuration.Users.TryGetValue(username, out var user) ? user.Settings : new();
    public ProviderClientConfiguration GetClient(ProviderKey provider) => new() { ClientId = "client", ClientSecret = "secret" };
    public ProviderAuthorization? GetAuthorization(string username, ProviderKey provider) => _authorizations.TryGetValue((username, provider), out var value) ? value : null;
    public byte[] GetOrCreateStateSigningKey() => Enumerable.Repeat((byte)7, 32).ToArray();
    public void SaveUserSettings(string username, UserSyncSettings settings) => _configuration.Users.GetOrAdd(username, _ => new()).Settings = settings;
    public void SaveClientSettings(ProviderKey provider, string? clientId, SecretUpdate secret) { }
    public void SaveAuthorization(string username, ProviderKey provider, ProviderAuthorization authorization) => _authorizations[(username, provider)] = authorization;
    public void RemoveAuthorization(string username, ProviderKey provider) => _authorizations.Remove((username, provider));
}
