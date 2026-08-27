using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Plugin;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;

namespace AniSync.Next.Configuration;

[Display(Name = "AniSync Next Configuration")]
public sealed class PluginConfiguration : INewtonsoftJsonConfiguration, IConfigurationWithMigrations
{
    [JsonProperty("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonProperty("myAnimeList")]
    public ProviderClientConfiguration MyAnimeList { get; set; } = new();

    [JsonProperty("aniList")]
    public ProviderClientConfiguration AniList { get; set; } = new();

    [JsonProperty("users")]
    public ConcurrentDictionary<string, UserConfiguration> Users { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonProperty("oauthStateSigningKey")]
    public string? OAuthStateSigningKey { get; set; }

    public static string ApplyMigrations(string config, IApplicationPaths applicationPaths) => config;
}

public sealed class ProviderClientConfiguration
{
    [JsonProperty("clientId")]
    public string? ClientId { get; set; }

    [JsonProperty("clientSecret")]
    public string? ClientSecret { get; set; }
}

public sealed class UserConfiguration
{
    [JsonProperty("settings")]
    public UserSyncSettings Settings { get; set; } = new();

    [JsonProperty("providers")]
    public Dictionary<string, ProviderAuthorization> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UserSyncSettings
{
    [JsonProperty("autoSync")]
    public bool AutoSync { get; set; } = true;

    [JsonProperty("syncOnlyOnCompletion")]
    public bool SyncOnlyOnCompletion { get; set; }

    [JsonProperty("syncRatings")]
    public bool SyncRatings { get; set; } = true;

    [JsonProperty("includeAdultSearch")]
    public bool IncludeAdultSearch { get; set; }

    [JsonProperty("diagnosticLogLevel")]
    public DiagnosticLogLevel DiagnosticLogLevel { get; set; } = DiagnosticLogLevel.Basic;
}

[JsonConverter(typeof(StringEnumConverter))]
public enum DiagnosticLogLevel
{
    Off,
    Basic,
    Detailed,
    Trace,
}

public sealed class ProviderAuthorization
{
    [JsonProperty("accountId")]
    public int AccountId { get; set; }

    [JsonProperty("username")]
    public string Username { get; set; } = string.Empty;

    [JsonProperty("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonProperty("refreshToken")]
    public string? RefreshToken { get; set; }

    [JsonProperty("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }
}
