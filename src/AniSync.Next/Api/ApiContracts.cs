using AniSync.Next.Configuration;
using AniSync.Next.Domain;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AniSync.Next.Api;

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public sealed record ProviderConnectionResponse(
    ProviderKey Provider,
    bool Configured,
    bool Connected,
    string? Username);

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public sealed record SessionResponse(
    string ShokoUsername,
    bool IsAdmin,
    IReadOnlyList<ProviderConnectionResponse> Providers,
    int PendingReviewCount,
    int PendingJobCount);

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public sealed record SettingsResponse(
    UserSyncSettings Settings,
    IReadOnlyList<ProviderConnectionResponse> Providers,
    IReadOnlyList<ProviderClientResponse>? Clients = null);

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public sealed record ProviderClientResponse(ProviderKey Provider, string? ClientId, bool SecretConfigured);

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public sealed record UpdateSettingsRequest(
    bool AutoSync,
    bool SyncOnlyOnCompletion,
    bool SyncRatings,
    bool IncludeAdultSearch);

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public sealed record UpdateProviderClientRequest(
    ProviderKey Provider,
    string? ClientId,
    bool SecretSpecified,
    bool ClearSecret,
    string? ClientSecret);

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public sealed record ApplyReviewRequest(IReadOnlyList<Guid> Ids);

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public sealed record SaveMappingRequest(
    int SeriesId,
    int AniDbAnimeId,
    ProviderKey Provider,
    int MediaId,
    string MediaTitle);

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public sealed record SearchMappingRequest(int SeriesId, ProviderKey Provider, string Query);

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public sealed record ApiError(string Error);
