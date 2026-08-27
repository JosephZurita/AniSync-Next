using AniSync.Next.Application;
using AniSync.Next.Configuration;
using AniSync.Next.Domain;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AniSync.Next.Providers;

internal interface IProviderTokenService
{
    Task<string> GetAccessTokenAsync(string username, ProviderKey provider, CancellationToken cancellationToken);
    Task<string> ForceRefreshAsync(string username, ProviderKey provider, CancellationToken cancellationToken);
    Task<ProviderAuthorization> ExchangeCodeAsync(ProviderKey provider, string username, string code, string redirectUri, string? codeVerifier, CancellationToken cancellationToken);
}

internal sealed class ProviderTokenService(
    IHttpClientFactory httpClientFactory,
    IPluginConfigurationService configuration,
    TimeProvider timeProvider) : IProviderTokenService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<string> GetAccessTokenAsync(string username, ProviderKey provider, CancellationToken cancellationToken)
    {
        var authorization = configuration.GetAuthorization(username, provider)
            ?? throw new ProviderException($"{provider} is not connected for {username}.", false);
        if (authorization.ExpiresAt is null || authorization.ExpiresAt > timeProvider.GetUtcNow().AddMinutes(2))
            return authorization.AccessToken;
        return await ForceRefreshAsync(username, provider, cancellationToken);
    }

    public async Task<string> ForceRefreshAsync(string username, ProviderKey provider, CancellationToken cancellationToken)
    {
        var key = $"{provider}:{username}";
        var gate = RefreshLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = configuration.GetAuthorization(username, provider)
                ?? throw new ProviderException($"{provider} is not connected for {username}.", false);
            if (string.IsNullOrWhiteSpace(existing.RefreshToken))
                throw new ProviderException($"{provider} must be reconnected because no refresh token is available.", false);

            var clientSettings = configuration.GetClient(provider);
            using var request = new HttpRequestMessage(HttpMethod.Post, GetTokenEndpoint(provider))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientSettings.ClientId ?? string.Empty,
                    ["client_secret"] = clientSettings.ClientSecret ?? string.Empty,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = existing.RefreshToken,
                }),
            };
            using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
            var token = await ReadTokenAsync(response, provider, cancellationToken);
            var updated = new ProviderAuthorization
            {
                AccountId = existing.AccountId,
                Username = existing.Username,
                AccessToken = token.AccessToken,
                RefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken)
                    ? existing.RefreshToken
                    : token.RefreshToken,
                ExpiresAt = token.ExpiresIn is > 0
                    ? timeProvider.GetUtcNow().AddSeconds(token.ExpiresIn.Value)
                    : existing.ExpiresAt,
            };
            configuration.SaveAuthorization(username, provider, updated);
            return updated.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ProviderAuthorization> ExchangeCodeAsync(
        ProviderKey provider,
        string username,
        string code,
        string redirectUri,
        string? codeVerifier,
        CancellationToken cancellationToken)
    {
        var clientSettings = configuration.GetClient(provider);
        var body = new Dictionary<string, string>
        {
            ["client_id"] = clientSettings.ClientId ?? string.Empty,
            ["client_secret"] = clientSettings.ClientSecret ?? string.Empty,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        };
        if (provider == ProviderKey.MyAnimeList)
            body["code_verifier"] = codeVerifier ?? throw new ProviderException("The MyAnimeList login session expired.", false);

        using var request = new HttpRequestMessage(HttpMethod.Post, GetTokenEndpoint(provider))
        {
            Content = new FormUrlEncodedContent(body),
        };
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        var token = await ReadTokenAsync(response, provider, cancellationToken);
        var authorization = new ProviderAuthorization
        {
            AccessToken = token.AccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? null : token.RefreshToken,
            ExpiresAt = token.ExpiresIn is > 0 ? timeProvider.GetUtcNow().AddSeconds(token.ExpiresIn.Value) : null,
        };
        configuration.SaveAuthorization(username, provider, authorization);
        return authorization;
    }

    private static Uri GetTokenEndpoint(ProviderKey provider) => new(provider switch
    {
        ProviderKey.MyAnimeList => "https://myanimelist.net/v1/oauth2/token",
        ProviderKey.AniList => "https://anilist.co/api/v2/oauth/token",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    });

    private static async Task<TokenResponse> ReadTokenAsync(HttpResponseMessage response, ProviderKey provider, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync(cancellationToken);
            var isTransient = response.StatusCode is HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= (int)HttpStatusCode.InternalServerError;
            var retryAfter = response.Headers.RetryAfter?.Delta;
            if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryAt)
                retryAfter = retryAt - DateTimeOffset.UtcNow;
            if (retryAfter < TimeSpan.Zero)
                retryAfter = TimeSpan.Zero;
            throw new ProviderException(
                $"{provider} token request failed ({(int)response.StatusCode}): {Truncate(message)}",
                isTransient,
                retryAfter);
        }
        return await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken)
            ?? throw new ProviderException($"{provider} returned an empty token response.", false);
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300];

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; }
    }
}
