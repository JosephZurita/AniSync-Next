namespace AniSync.Next.Domain;

public interface ISyncPlanner
{
    PlannedChange Plan(
        ShokoSeriesState source,
        ProviderKey provider,
        int? providerMediaId,
        ProviderListState? destination,
        string snapshotToken,
        DateTimeOffset now,
        bool syncOnlyOnCompletion,
        bool syncRatings);
}

public interface ISyncProvider
{
    ProviderKey Key { get; }
    Task<ProviderAccount?> GetAccountAsync(string shokoUsername, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<int, ProviderListState>> GetListAsync(string shokoUsername, CancellationToken cancellationToken);
    Task<ProviderListState?> GetEntryAsync(string shokoUsername, int mediaId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProviderMediaSearchResult>> SearchAsync(string shokoUsername, string query, bool includeAdult, CancellationToken cancellationToken);
    Task<ProviderListState> ApplyAsync(string shokoUsername, PlannedChange change, CancellationToken cancellationToken);
}

public sealed record ProviderAccount(int Id, string Username, string? AvatarUrl = null);

public interface IMappingResolver
{
    Task<ProviderMapping?> ResolveAsync(ShokoSeriesState source, ProviderKey provider, CancellationToken cancellationToken);
    Task SaveAsync(ProviderMapping mapping, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProviderMapping>> GetForUserAsync(string shokoUsername, CancellationToken cancellationToken);
    Task RemoveAsync(string shokoUsername, int aniDbAnimeId, ProviderKey provider, CancellationToken cancellationToken);
}

public interface IReviewStore
{
    Task ReplaceForUserAsync(string shokoUsername, IReadOnlyCollection<ReviewItem> items, CancellationToken cancellationToken);
    Task UpsertAsync(ReviewItem item, CancellationToken cancellationToken);
    Task RemoveAsync(string shokoUsername, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReviewItem>> GetForUserAsync(string shokoUsername, CancellationToken cancellationToken);
}

public interface IHistoryStore
{
    Task AppendAsync(SyncOutcome outcome, CancellationToken cancellationToken);
    Task<IReadOnlyList<SyncOutcome>> GetForUserAsync(string shokoUsername, int limit, CancellationToken cancellationToken);
    Task ClearAsync(string shokoUsername, CancellationToken cancellationToken);
}

public interface IShokoStateReader
{
    Task<ShokoSeriesState?> GetSeriesStateAsync(string shokoUsername, int seriesId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ShokoSeriesState>> GetLibraryStateAsync(string shokoUsername, CancellationToken cancellationToken);
}

public interface ISyncExecutor
{
    Task<SyncOutcome> ExecuteAsync(PlannedChange change, bool confirmedReview, CancellationToken cancellationToken);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

