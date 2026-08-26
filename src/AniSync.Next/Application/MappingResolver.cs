using AniSync.Next.Domain;
using AniSync.Next.Persistence;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AniSync.Next.Application;

internal sealed class MappingResolver(
    IPluginStateStore stateStore,
    IHttpClientFactory httpClientFactory,
    IClock clock,
    ILogger<MappingResolver> logger) : IMappingResolver
{
    public async Task<ProviderMapping?> ResolveAsync(
        ShokoSeriesState source,
        ProviderKey provider,
        CancellationToken cancellationToken)
    {
        var existing = await stateStore.GetMappingAsync(
            source.ShokoUsername, source.AniDbAnimeId, provider, cancellationToken);
        if (existing is not null) return existing;

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientNames.Mapping);
            var response = await client.GetFromJsonAsync<OfflineMappingResponse>(
                $"api/v2/ids?source=anidb&id={source.AniDbAnimeId}", cancellationToken);
            var mediaId = provider == ProviderKey.AniList ? response?.AniList : response?.MyAnimeList;
            if (mediaId is not > 0) return null;

            var mapping = new ProviderMapping(
                source.ShokoUsername,
                source.AniDbAnimeId,
                provider,
                mediaId.Value,
                source.Title,
                false,
                clock.UtcNow);
            await stateStore.SaveMappingAsync(mapping, cancellationToken);
            return mapping;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Could not resolve AniDB {AniDbId} through the mapping service", source.AniDbAnimeId);
            return null;
        }
    }

    public Task SaveAsync(ProviderMapping mapping, CancellationToken cancellationToken) =>
        stateStore.SaveMappingAsync(mapping with { IsUserVerified = true, UpdatedAt = clock.UtcNow }, cancellationToken);

    public Task<IReadOnlyList<ProviderMapping>> GetForUserAsync(string shokoUsername, CancellationToken cancellationToken) =>
        stateStore.GetMappingsAsync(shokoUsername, cancellationToken);

    public Task RemoveAsync(string shokoUsername, int aniDbAnimeId, ProviderKey provider, CancellationToken cancellationToken) =>
        stateStore.RemoveMappingAsync(shokoUsername, aniDbAnimeId, provider, cancellationToken);

    private sealed class OfflineMappingResponse
    {
        [JsonPropertyName("anilist")]
        public int? AniList { get; set; }

        [JsonPropertyName("myanimelist")]
        public int? MyAnimeList { get; set; }
    }
}

internal static class HttpClientNames
{
    public const string Mapping = "anisync-next-mapping";
    public const string MyAnimeList = "anisync-next-mal";
    public const string AniList = "anisync-next-anilist";
}

