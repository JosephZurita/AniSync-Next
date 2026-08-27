using AniSync.Next.Application;
using AniSync.Next.Domain;
using AniSync.Next.Host;
using AniSync.Next.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniSync.Next.Tests;

public sealed class SyncWorkerTests
{
    [Fact]
    public async Task DuplicateTriggersAreCoalescedAndShutdownDrainsAcceptedWork()
    {
        using var directory = new TestDirectory();
        var store = new JsonPluginStateStore(directory.Path, NullLogger<JsonPluginStateStore>.Instance);
        var queue = new SyncTriggerQueue();
        var coordinator = new RecordingCoordinator();
        var worker = new SyncWorker(queue, store, coordinator, TimeProvider.System,
            NullLogger<SyncWorker>.Instance);
        var first = Trigger("alice", 1);
        queue.TryEnqueue(first).Should().BeTrue();
        queue.TryEnqueue(Trigger("ALICE", 1)).Should().BeTrue();

        await worker.StartAsync(default);
        await worker.StopAsync(default);

        coordinator.Calls.Should().Equal(("ALICE", 1));
        (await store.GetPendingAsync(default)).Should().BeEmpty();
        worker.Dispose();
    }

    [Fact]
    public async Task PendingWorkIsRestoredAndProcessedAfterRestart()
    {
        using var directory = new TestDirectory();
        var store = new JsonPluginStateStore(directory.Path, NullLogger<JsonPluginStateStore>.Instance);
        await store.UpsertPendingAsync(Trigger("alice", 9), default);
        var queue = new SyncTriggerQueue();
        var coordinator = new RecordingCoordinator();
        var worker = new SyncWorker(queue, store, coordinator, TimeProvider.System,
            NullLogger<SyncWorker>.Instance);

        await worker.StartAsync(default);
        await worker.StopAsync(default);

        coordinator.Calls.Should().Equal(("alice", 9));
        (await store.GetPendingAsync(default)).Should().BeEmpty();
        worker.Dispose();
    }

    private static PersistedSyncTrigger Trigger(string user, int series) =>
        new(Guid.NewGuid(), user, series, "watch", DateTimeOffset.UtcNow);

    private sealed class RecordingCoordinator : ISyncCoordinator
    {
        public List<(string User, int Series)> Calls { get; } = [];
        public Task ProcessSeriesAsync(string username, int seriesId, CancellationToken cancellationToken)
        {
            Calls.Add((username, seriesId));
            return Task.CompletedTask;
        }
        public Task<ReviewRefreshResult> RefreshAsync(string username, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SyncOutcome>> ApplyAsync(string username, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
