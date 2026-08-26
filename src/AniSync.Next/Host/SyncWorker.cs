using AniSync.Next.Application;
using AniSync.Next.Persistence;

namespace AniSync.Next.Host;

internal sealed class SyncWorker(
    ISyncTriggerQueue queue,
    IPluginStateStore stateStore,
    ISyncCoordinator coordinator,
    TimeProvider timeProvider,
    ILogger<SyncWorker> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _workerTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await stateStore.InitializeAsync(cancellationToken);
        foreach (var pending in await stateStore.GetPendingAsync(cancellationToken))
            queue.TryEnqueue(pending);
        _workerTask = RunAsync(_shutdown.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        if (_workerTask is null) return;
        try
        {
            await _workerTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _shutdown.Cancel();
            await _workerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public void Dispose() => _shutdown.Dispose();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var first in queue.Reader.ReadAllAsync(cancellationToken))
        {
            var coalesced = new Dictionary<(string User, int Series), PersistedSyncTrigger>(
                new TriggerKeyComparer());
            coalesced[(first.ShokoUsername, first.SeriesId)] = first;

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            while (queue.Reader.TryRead(out var next))
                coalesced[(next.ShokoUsername, next.SeriesId)] = next;

            foreach (var trigger in coalesced.Values.OrderBy(item => item.CreatedAt))
                await ProcessAsync(trigger, cancellationToken);
        }
    }

    private async Task ProcessAsync(PersistedSyncTrigger trigger, CancellationToken cancellationToken)
    {
        await stateStore.UpsertPendingAsync(trigger, cancellationToken);
        if (trigger.NotBefore is { } notBefore && notBefore > timeProvider.GetUtcNow())
            await Task.Delay(notBefore - timeProvider.GetUtcNow(), cancellationToken);

        try
        {
            await coordinator.ProcessSeriesAsync(trigger.ShokoUsername, trigger.SeriesId, cancellationToken);
            await stateStore.RemovePendingAsync(trigger.Id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var nextAttempt = trigger.AttemptCount + 1;
            if (nextAttempt >= 3)
            {
                logger.LogError(ex, "AniSync Next permanently failed {Reason} for {User}/{SeriesId}",
                    trigger.Reason, trigger.ShokoUsername, trigger.SeriesId);
                await stateStore.RemovePendingAsync(trigger.Id, cancellationToken);
                return;
            }

            var retryAfter = ex is ProviderException { RetryAfter: { } delay }
                ? delay
                : TimeSpan.FromSeconds(nextAttempt * nextAttempt * 2);
            var retry = trigger with
            {
                AttemptCount = nextAttempt,
                NotBefore = timeProvider.GetUtcNow() + retryAfter,
                LastError = ex.Message,
            };
            await stateStore.UpsertPendingAsync(retry, cancellationToken);
            queue.TryEnqueue(retry);
            logger.LogWarning(ex, "AniSync Next will retry {Reason} for {User}/{SeriesId}",
                trigger.Reason, trigger.ShokoUsername, trigger.SeriesId);
        }
    }

    private sealed class TriggerKeyComparer : IEqualityComparer<(string User, int Series)>
    {
        public bool Equals((string User, int Series) x, (string User, int Series) y) =>
            x.Series == y.Series && x.User.Equals(y.User, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string User, int Series) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.User), obj.Series);
    }
}
