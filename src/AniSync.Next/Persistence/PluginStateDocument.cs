using AniSync.Next.Domain;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AniSync.Next.Persistence;

internal sealed class PluginStateDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<ProviderMapping> Mappings { get; set; } = [];
    public List<ReviewItem> Reviews { get; set; } = [];
    public List<SyncOutcome> History { get; set; } = [];
    public List<PersistedSyncTrigger> PendingWork { get; set; } = [];
}

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public sealed record PersistedSyncTrigger(
    Guid Id,
    string ShokoUsername,
    int SeriesId,
    string Reason,
    DateTimeOffset CreatedAt,
    int AttemptCount = 0,
    DateTimeOffset? NotBefore = null,
    string? LastError = null);

public interface IPluginStateStore : IReviewStore, IHistoryStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<ProviderMapping?> GetMappingAsync(string username, int aniDbAnimeId, ProviderKey provider, CancellationToken cancellationToken);
    Task SaveMappingAsync(ProviderMapping mapping, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProviderMapping>> GetMappingsAsync(string username, CancellationToken cancellationToken);
    Task RemoveMappingAsync(string username, int aniDbAnimeId, ProviderKey provider, CancellationToken cancellationToken);
    Task UpsertPendingAsync(PersistedSyncTrigger trigger, CancellationToken cancellationToken);
    Task RemovePendingAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<PersistedSyncTrigger>> GetPendingAsync(CancellationToken cancellationToken);
}
