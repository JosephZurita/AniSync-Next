using AniSync.Next.Configuration;
using AniSync.Next.Domain;

namespace AniSync.Next.Api;

public sealed record ProviderConnectionResponse(
    ProviderKey Provider,
    bool Configured,
    bool Connected,
    string? Username);

public sealed record SessionResponse(
    string ShokoUsername,
    bool IsAdmin,
    IReadOnlyList<ProviderConnectionResponse> Providers,
    int PendingReviewCount,
    int PendingJobCount);

public sealed record SettingsResponse(
    UserSyncSettings Settings,
    IReadOnlyList<ProviderConnectionResponse> Providers,
    IReadOnlyList<ProviderClientResponse>? Clients = null);

public sealed record ProviderClientResponse(ProviderKey Provider, string? ClientId, bool SecretConfigured);

public sealed record UpdateSettingsRequest(
    bool AutoSync,
    bool SyncOnlyOnCompletion,
    bool SyncRatings,
    bool IncludeAdultSearch);

public sealed record UpdateProviderClientRequest(
    ProviderKey Provider,
    string? ClientId,
    bool SecretSpecified,
    bool ClearSecret,
    string? ClientSecret);

public sealed record ApplyReviewRequest(IReadOnlyList<Guid> Ids);

public sealed record SaveMappingRequest(
    int SeriesId,
    int AniDbAnimeId,
    ProviderKey Provider,
    int MediaId,
    string MediaTitle);

public sealed record SearchMappingRequest(int SeriesId, ProviderKey Provider, string Query);

public sealed record ApiError(string Error);
