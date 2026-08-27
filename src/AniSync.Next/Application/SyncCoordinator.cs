using AniSync.Next.Configuration;
using AniSync.Next.Domain;
using AniSync.Next.Persistence;
using Microsoft.Extensions.Logging;

namespace AniSync.Next.Application;

public interface ISyncCoordinator
{
    Task<ReviewRefreshResult> RefreshAsync(string username, CancellationToken cancellationToken);
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
    IClock clock,
    ILogger<SyncCoordinator> logger) : ISyncCoordinator
{
    public async Task<ReviewRefreshResult> RefreshAsync(string username, CancellationToken cancellationToken)
    {
        var sources = await shokoStateReader.GetLibraryStateAsync(username, cancellationToken);
        var settings = configuration.GetUserSettings(username);
        var connected = providerRegistry.All
            .Where(provider => configuration.GetAuthorization(username, provider.Key) is not null)
            .ToArray();

        var providerLists = new Dictionary<ProviderKey, IReadOnlyDictionary<int, ProviderListState>>();
        var failures = new List<ProviderRefreshFailure>();
        foreach (var provider in connected)
        {
            try
            {
                providerLists[provider.Key] = await provider.GetListAsync(username, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ProviderException exception)
            {
                logger.LogWarning(exception, "Could not refresh {Provider} list for Shoko user {Username}",
                    provider.Key, username);
                failures.Add(ToRefreshFailure(provider.Key, exception));
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unexpected {Provider} list refresh failure for Shoko user {Username}",
                    provider.Key, username);
                failures.Add(new ProviderRefreshFailure(provider.Key,
                    $"{provider.Key} returned an unexpected response. Check the Shoko logs for details.", false));
            }
        }

        var reviews = new List<ReviewItem>();
        foreach (var source in sources)
        {
            var groupId = Guid.NewGuid();
            foreach (var provider in connected)
            {
                if (!providerLists.TryGetValue(provider.Key, out var providerList)) continue;
                var mapping = await mappingResolver.ResolveAsync(source, provider.Key, cancellationToken);
                ProviderListState? destination = null;
                if (mapping is not null)
                    providerList.TryGetValue(mapping.MediaId, out destination);

                PlannedChange Plan(ProviderListState? current)
                {
                    var token = SyncPlanner.CreateSnapshotToken(source, current);
                    return planner.Plan(source, provider.Key, mapping?.MediaId, current, token,
                        clock.UtcNow, settings.SyncOnlyOnCompletion, settings.SyncRatings) with
                    { GroupId = groupId };
                }

                var change = Plan(destination);
                if (mapping is not null && destination is null && change.Kind != ChangeKind.NoChange)
                {
                    try
                    {
                        destination = await provider.GetEntryAsync(username, mapping.MediaId, cancellationToken);
                        change = Plan(destination);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (ProviderException exception)
                    {
                        logger.LogWarning(exception,
                            "Could not verify missing {Provider} media {MediaId} for Shoko user {Username}",
                            provider.Key, mapping.MediaId, username);
                        failures.Add(ToRefreshFailure(provider.Key, exception));
                        continue;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception,
                            "Unexpected {Provider} media verification failure for media {MediaId} and Shoko user {Username}",
                            provider.Key, mapping.MediaId, username);
                        failures.Add(new ProviderRefreshFailure(provider.Key,
                            $"{provider.Key} returned an unexpected response. Check the Shoko logs for details.", false));
                        continue;
                    }
                }

                if (change.Kind != ChangeKind.NoChange)
                    reviews.Add(new ReviewItem(change.Id, change, clock.UtcNow));
            }
        }

        await stateStore.ReplaceForUserAsync(username, reviews, cancellationToken);
        return new ReviewRefreshResult(reviews, failures);
    }

    private static ProviderRefreshFailure ToRefreshFailure(ProviderKey provider, ProviderException exception)
    {
        var retryAfterSeconds = exception.RetryAfter is { } retryAfter
            ? (int?)Math.Clamp((int)Math.Ceiling(retryAfter.TotalSeconds), 0, int.MaxValue)
            : null;
        return new ProviderRefreshFailure(provider, exception.Message, exception.IsTransient, retryAfterSeconds);
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
        var completedIds = new List<Guid>(selected.Length);
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
                completedIds.Add(item.Id);
                continue;
            }
            var outcome = await executor.ExecuteAsync(current, confirmedReview: true, cancellationToken);
            outcomes.Add(outcome);
            if (outcome.Kind is SyncOutcomeKind.Applied or SyncOutcomeKind.Unchanged)
                completedIds.Add(item.Id);
        }

        if (completedIds.Count > 0)
            await stateStore.RemoveAsync(username, completedIds, cancellationToken);
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
