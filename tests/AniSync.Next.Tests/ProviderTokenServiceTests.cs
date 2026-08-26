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

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
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
