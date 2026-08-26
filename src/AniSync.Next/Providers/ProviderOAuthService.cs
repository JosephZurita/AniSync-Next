using AniSync.Next.Application;
using AniSync.Next.Configuration;
using AniSync.Next.Domain;

namespace AniSync.Next.Providers;

public interface IProviderOAuthService
{
    Uri BuildAuthorizeUri(ProviderKey provider, string username, string baseUrl);
    Task CompleteAsync(string state, string code, CancellationToken cancellationToken);
}

internal sealed class ProviderOAuthService(
    IPluginConfigurationService configuration,
    IOAuthStateService stateService,
    IProviderTokenService tokenService,
    IProviderRegistry providerRegistry) : IProviderOAuthService
{
    public Uri BuildAuthorizeUri(ProviderKey provider, string username, string baseUrl)
    {
        var client = configuration.GetClient(provider);
        if (string.IsNullOrWhiteSpace(client.ClientId))
            throw new ProviderException($"Configure the {provider} client ID before connecting.", false);
        var state = stateService.Create(username, provider, baseUrl, out var codeChallenge);
        var redirectUri = Uri.EscapeDataString($"{baseUrl.TrimEnd('/')}/anisync-next/oauth/callback");
        var url = provider switch
        {
            ProviderKey.MyAnimeList =>
                $"https://myanimelist.net/v1/oauth2/authorize?response_type=code&client_id={Uri.EscapeDataString(client.ClientId)}&code_challenge={Uri.EscapeDataString(codeChallenge!)}&state={Uri.EscapeDataString(state)}&redirect_uri={redirectUri}",
            ProviderKey.AniList =>
                $"https://anilist.co/api/v2/oauth/authorize?client_id={Uri.EscapeDataString(client.ClientId)}&response_type=code&state={Uri.EscapeDataString(state)}&redirect_uri={redirectUri}",
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        return new Uri(url);
    }

    public async Task CompleteAsync(string state, string code, CancellationToken cancellationToken)
    {
        if (!stateService.TryVerify(state, out var verified) || verified is null)
            throw new ProviderException("The provider login session is invalid or expired.", false);
        await tokenService.ExchangeCodeAsync(verified.Provider, verified.Username, code,
            $"{verified.BaseUrl.TrimEnd('/')}/anisync-next/oauth/callback", verified.CodeVerifier, cancellationToken);
        var account = await providerRegistry.Get(verified.Provider).GetAccountAsync(verified.Username, cancellationToken)
            ?? throw new ProviderException($"Could not read the connected {verified.Provider} account.", false);
        var authorization = configuration.GetAuthorization(verified.Username, verified.Provider)
            ?? throw new ProviderException("The provider token was not saved.", false);
        authorization.AccountId = account.Id;
        authorization.Username = account.Username;
        configuration.SaveAuthorization(verified.Username, verified.Provider, authorization);
    }
}
