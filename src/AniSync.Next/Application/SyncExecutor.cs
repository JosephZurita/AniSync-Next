using AniSync.Next.Domain;
using AniSync.Next.Persistence;

namespace AniSync.Next.Application;

internal sealed class SyncExecutor(
    IProviderRegistry providerRegistry,
    IPluginStateStore stateStore,
    IClock clock) : ISyncExecutor
{
    public async Task<SyncOutcome> ExecuteAsync(
        PlannedChange change,
        bool confirmedReview,
        CancellationToken cancellationToken)
    {
        if (change.Kind == ChangeKind.NoChange)
            return await RecordAsync(new SyncOutcome(SyncOutcomeKind.Unchanged, change, CompletedAt: clock.UtcNow,
                GroupId: change.GroupId ?? change.Id), cancellationToken);

        if (change.RequiresReview && !confirmedReview)
        {
            var outcome = new SyncOutcome(SyncOutcomeKind.QueuedForReview, change, CompletedAt: clock.UtcNow,
                GroupId: change.GroupId ?? change.Id);
            await stateStore.UpsertAsync(new ReviewItem(change.Id, change, clock.UtcNow), cancellationToken);
            return await RecordAsync(outcome, cancellationToken);
        }

        if (!change.IsActionable || change.ProviderMediaId is null)
        {
            var unresolved = new SyncOutcome(SyncOutcomeKind.QueuedForReview, change,
                "A verified provider mapping is required.", clock.UtcNow, change.GroupId ?? change.Id);
            await stateStore.UpsertAsync(new ReviewItem(change.Id, change, clock.UtcNow, unresolved.Message), cancellationToken);
            return await RecordAsync(unresolved, cancellationToken);
        }

        try
        {
            await providerRegistry.Get(change.Provider).ApplyAsync(change.ShokoUsername, change, cancellationToken);
            return await RecordAsync(new SyncOutcome(SyncOutcomeKind.Applied, change, CompletedAt: clock.UtcNow,
                GroupId: change.GroupId ?? change.Id), cancellationToken);
        }
        catch (ProviderException ex) when (ex.IsTransient)
        {
            throw;
        }
        catch (ProviderException ex)
        {
            var outcome = new SyncOutcome(SyncOutcomeKind.PermanentFailure, change, ex.Message, clock.UtcNow,
                change.GroupId ?? change.Id);
            await stateStore.UpsertAsync(new ReviewItem(change.Id, change, clock.UtcNow, ex.Message, 1), cancellationToken);
            return await RecordAsync(outcome, cancellationToken);
        }
    }

    private async Task<SyncOutcome> RecordAsync(SyncOutcome outcome, CancellationToken cancellationToken)
    {
        await stateStore.AppendAsync(outcome, cancellationToken);
        return outcome;
    }
}
