using AniSync.Next.Configuration;
using AniSync.Next.Domain;
using AniSync.Next.Persistence;

namespace AniSync.Next.Application;

public interface ISyncCoordinator
{
    Task<IReadOnlyList<ReviewItem>> RefreshAsync(string username, CancellationToken cancellationToken);
    Task<IReadOnlyList<SyncOutcome>> ApplyAsync(string username, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task ProcessSeriesAsync(string username, int seriesId, CancellationToken cancellationToken);
}

internal sealed class SyncCoordinator(
    IShokoStateReader shokoStateReader,
    IMappingResolver mappingResolver,
    IProviderRegistry providerRegistry,
    ISyncPlanner planner,
    ISyncExecutor executor,
    IPluginStateStore stateStore,
    IPluginConfigurationService configuration,
    IClock clock) : ISyncCoordinator
{
    public async Task<IReadOnlyList<ReviewItem>> RefreshAsync(string username, CancellationToken cancellationToken)
    {
        var sources = await shokoStateReader.GetLibraryStateAsync(username, cancellationToken);
        var settings = configuration.GetUserSettings(username);
        var connected = providerRegistry.All
            .Where(provider => configuration.GetAuthorization(username, provider.Key) is not null)
            .ToArray();

        var providerLists = new Dictionary<ProviderKey, IReadOnlyDictionary<int, ProviderListState>>();
        foreach (var provider in connected)
            providerLists[provider.Key] = await provider.GetListAsync(username, cancellationToken);

        var reviews = new List<ReviewItem>();
        foreach (var source in sources)
        {
            var groupId = Guid.NewGuid();
            foreach (var provider in connected)
            {
                var mapping = await mappingResolver.ResolveAsync(source, provider.Key, cancellationToken);
                ProviderListState? destination = null;
                if (mapping is not null)
                    providerLists[provider.Key].TryGetValue(mapping.MediaId, out destination);
                var token = SyncPlanner.CreateSnapshotToken(source, destination);
                var change = planner.Plan(source, provider.Key, mapping?.MediaId, destination, token,
                    clock.UtcNow, settings.SyncOnlyOnCompletion, settings.SyncRatings) with
                { GroupId = groupId };
                if (change.Kind != ChangeKind.NoChange)
                    reviews.Add(new ReviewItem(change.Id, change, clock.UtcNow));
            }
        }

        await stateStore.ReplaceForUserAsync(username, reviews, cancellationToken);
        return reviews;
    }

    public async Task<IReadOnlyList<SyncOutcome>> ApplyAsync(
        string username,
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        var stored = await stateStore.GetForUserAsync(username, cancellationToken);
        var selected = stored.Where(item => ids.Contains(item.Id)).ToArray();
        if (selected.Length != ids.Distinct().Count())
            throw new StalePreviewException("One or more selected changes no longer exist. Refresh the review list.");

        var settings = configuration.GetUserSettings(username);
        var outcomes = new List<SyncOutcome>(selected.Length);
        foreach (var item in selected)
        {
            var source = await shokoStateReader.GetSeriesStateAsync(username, item.Change.SeriesId, cancellationToken)
                ?? throw new StalePreviewException($"Series {item.Change.SeriesId} no longer exists.");
            var mapping = await mappingResolver.ResolveAsync(source, item.Change.Provider, cancellationToken);
            var destination = mapping is null
                ? null
                : await providerRegistry.Get(item.Change.Provider).GetEntryAsync(username, mapping.MediaId, cancellationToken);
            var currentToken = SyncPlanner.CreateSnapshotToken(source, destination);
            if (!string.Equals(currentToken, item.Change.SnapshotToken, StringComparison.Ordinal))
                throw new StalePreviewException($"{source.Title} changed after the preview. Refresh before applying it.");

            var current = planner.Plan(source, item.Change.Provider, mapping?.MediaId, destination,
                currentToken, clock.UtcNow, settings.SyncOnlyOnCompletion, settings.SyncRatings) with
            {
                GroupId = item.Change.GroupId ?? item.Change.Id,
            };
            if (current.Kind == ChangeKind.NoChange)
            {
                outcomes.Add(new SyncOutcome(SyncOutcomeKind.Unchanged, current, CompletedAt: clock.UtcNow));
                continue;
            }
            outcomes.Add(await executor.ExecuteAsync(current, confirmedReview: true, cancellationToken));
        }

        await stateStore.RemoveAsync(username, ids, cancellationToken);
        return outcomes;
    }

    public async Task ProcessSeriesAsync(string username, int seriesId, CancellationToken cancellationToken)
    {
        var settings = configuration.GetUserSettings(username);
        if (!settings.AutoSync) return;
        var source = await shokoStateReader.GetSeriesStateAsync(username, seriesId, cancellationToken);
        if (source is null) return;

        var groupId = Guid.NewGuid();
        ProviderException? retryableFailure = null;
        foreach (var provider in providerRegistry.All)
        {
            if (configuration.GetAuthorization(username, provider.Key) is null) continue;
            try
            {
                var mapping = await mappingResolver.ResolveAsync(source, provider.Key, cancellationToken);
                var destination = mapping is null
                    ? null
                    : await provider.GetEntryAsync(username, mapping.MediaId, cancellationToken);
                var token = SyncPlanner.CreateSnapshotToken(source, destination);
                var change = planner.Plan(source, provider.Key, mapping?.MediaId, destination, token,
                    clock.UtcNow, settings.SyncOnlyOnCompletion, settings.SyncRatings) with
                { GroupId = groupId };
                if (change.Kind != ChangeKind.NoChange)
                    await executor.ExecuteAsync(change, confirmedReview: false, cancellationToken);
            }
            catch (ProviderException exception) when (exception.IsTransient)
            {
                retryableFailure ??= exception;
            }
        }
        if (retryableFailure is not null) throw retryableFailure;
    }
}
