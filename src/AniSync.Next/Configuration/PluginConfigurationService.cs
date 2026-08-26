using AniSync.Next.Domain;
using Shoko.Abstractions.Config;
using System.Security.Cryptography;

namespace AniSync.Next.Configuration;

public interface IPluginConfigurationService
{
    PluginConfiguration Read();
    UserSyncSettings GetUserSettings(string username);
    ProviderClientConfiguration GetClient(ProviderKey provider);
    ProviderAuthorization? GetAuthorization(string username, ProviderKey provider);
    byte[] GetOrCreateStateSigningKey();
    void SaveUserSettings(string username, UserSyncSettings settings);
    void SaveClientSettings(ProviderKey provider, string? clientId, SecretUpdate secret);
    void SaveAuthorization(string username, ProviderKey provider, ProviderAuthorization authorization);
    void RemoveAuthorization(string username, ProviderKey provider);
}

public sealed record SecretUpdate(bool IsSpecified, bool Clear, string? Value)
{
    public static SecretUpdate Preserve() => new(false, false, null);
    public static SecretUpdate Replace(string value) => new(true, false, value);
    public static SecretUpdate Remove() => new(true, true, null);
}

public sealed class PluginConfigurationService(ConfigurationProvider<PluginConfiguration> provider)
    : IPluginConfigurationService
{
    private readonly object _gate = new();

    public PluginConfiguration Read()
    {
        lock (_gate)
            return provider.Load();
    }

    public UserSyncSettings GetUserSettings(string username)
    {
        lock (_gate)
        {
            var config = provider.Load();
            return config.Users.TryGetValue(username, out var user)
                ? Clone(user.Settings)
                : new UserSyncSettings();
        }
    }

    public ProviderClientConfiguration GetClient(ProviderKey providerKey)
    {
        lock (_gate)
        {
            var config = provider.Load();
            var value = providerKey == ProviderKey.MyAnimeList ? config.MyAnimeList : config.AniList;
            return new ProviderClientConfiguration { ClientId = value.ClientId, ClientSecret = value.ClientSecret };
        }
    }

    public ProviderAuthorization? GetAuthorization(string username, ProviderKey providerKey)
    {
        lock (_gate)
        {
            var config = provider.Load();
            if (!config.Users.TryGetValue(username, out var user) ||
                !user.Providers.TryGetValue(providerKey.ToString(), out var auth))
                return null;
            return Clone(auth);
        }
    }

    public byte[] GetOrCreateStateSigningKey()
    {
        lock (_gate)
        {
            var config = provider.Load();
            if (string.IsNullOrWhiteSpace(config.OAuthStateSigningKey))
            {
                config.OAuthStateSigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                provider.Save(config);
            }
            return Convert.FromBase64String(config.OAuthStateSigningKey);
        }
    }

    public void SaveUserSettings(string username, UserSyncSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        lock (_gate)
        {
            var config = provider.Load();
            var user = config.Users.GetOrAdd(username, _ => new UserConfiguration());
            user.Settings = Clone(settings);
            provider.Save(config);
        }
    }

    public void SaveClientSettings(ProviderKey providerKey, string? clientId, SecretUpdate secret)
    {
        lock (_gate)
        {
            var config = provider.Load();
            var client = providerKey == ProviderKey.MyAnimeList ? config.MyAnimeList : config.AniList;
            client.ClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();
            if (secret.IsSpecified)
                client.ClientSecret = secret.Clear ? null : secret.Value;
            provider.Save(config);
        }
    }

    public void SaveAuthorization(string username, ProviderKey providerKey, ProviderAuthorization authorization)
    {
        lock (_gate)
        {
            var config = provider.Load();
            var user = config.Users.GetOrAdd(username, _ => new UserConfiguration());
            user.Providers[providerKey.ToString()] = Clone(authorization);
            provider.Save(config);
        }
    }

    public void RemoveAuthorization(string username, ProviderKey providerKey)
    {
        lock (_gate)
        {
            var config = provider.Load();
            if (config.Users.TryGetValue(username, out var user) &&
                user.Providers.Remove(providerKey.ToString()))
                provider.Save(config);
        }
    }

    private static UserSyncSettings Clone(UserSyncSettings value) => new()
    {
        AutoSync = value.AutoSync,
        SyncOnlyOnCompletion = value.SyncOnlyOnCompletion,
        SyncRatings = value.SyncRatings,
        IncludeAdultSearch = value.IncludeAdultSearch,
    };

    private static ProviderAuthorization Clone(ProviderAuthorization value) => new()
    {
        AccountId = value.AccountId,
        Username = value.Username,
        AccessToken = value.AccessToken,
        RefreshToken = value.RefreshToken,
        ExpiresAt = value.ExpiresAt,
    };
}

