using AniSync.Next.Application;
using AniSync.Next.Domain;
using AniSync.Next.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniSync.Next.Tests;

public sealed class SyncExecutorTests
{
    [Fact]
    public async Task NoChangeIsRecordedWithoutCallingProvider()
    {
        using var directory = new TestDirectory();
        var provider = new BehaviorProvider();
        var setup = Create(directory.Path, provider);

        var result = await setup.Executor.ExecuteAsync(Change(ChangeKind.NoChange), false, default);

        result.Kind.Should().Be(SyncOutcomeKind.Unchanged);
        provider.ApplyCount.Should().Be(0);
        (await setup.Store.GetForUserAsync("alice", 10, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task RiskyAutomaticChangeIsQueuedForReview()
    {
        using var directory = new TestDirectory();
        var provider = new BehaviorProvider();
        var setup = Create(directory.Path, provider);
        var change = Change(ChangeKind.Decrease, ReviewReason.ProgressDecrease);

        var result = await setup.Executor.ExecuteAsync(change, false, default);

        result.Kind.Should().Be(SyncOutcomeKind.QueuedForReview);
        provider.ApplyCount.Should().Be(0);
        (await setup.Store.GetForUserAsync("alice", default)).Should().ContainSingle();
    }

    [Fact]
    public async Task UnresolvedConfirmedChangeStillCannotExecute()
    {
        using var directory = new TestDirectory();
        var provider = new BehaviorProvider();
        var setup = Create(directory.Path, provider);
        var change = Change(ChangeKind.UnresolvedMapping, ReviewReason.MissingMapping) with { ProviderMediaId = null };

        var result = await setup.Executor.ExecuteAsync(change, true, default);

        result.Kind.Should().Be(SyncOutcomeKind.QueuedForReview);
        result.Message.Should().Contain("mapping");
    }

    [Fact]
    public async Task SafeChangeIsAppliedAndRecorded()
    {
        using var directory = new TestDirectory();
        var provider = new BehaviorProvider();
        var setup = Create(directory.Path, provider);

        var result = await setup.Executor.ExecuteAsync(Change(ChangeKind.Advance), false, default);

        result.Kind.Should().Be(SyncOutcomeKind.Applied);
        provider.ApplyCount.Should().Be(1);
    }

    [Fact]
    public async Task TransientFailuresEscapeForWorkerRetry()
    {
        using var directory = new TestDirectory();
        var provider = new BehaviorProvider { Failure = new ProviderException("later", true) };
        var setup = Create(directory.Path, provider);

        var action = () => setup.Executor.ExecuteAsync(Change(ChangeKind.Advance), false, default);

        await action.Should().ThrowAsync<ProviderException>().Where(exception => exception.IsTransient);
        (await setup.Store.GetForUserAsync("alice", 10, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task PermanentFailuresBecomeReviewableHistory()
    {
        using var directory = new TestDirectory();
        var provider = new BehaviorProvider { Failure = new ProviderException("reconnect", false) };
        var setup = Create(directory.Path, provider);

        var result = await setup.Executor.ExecuteAsync(Change(ChangeKind.Advance), false, default);

        result.Kind.Should().Be(SyncOutcomeKind.PermanentFailure);
        (await setup.Store.GetForUserAsync("alice", default)).Single().Error.Should().Be("reconnect");
        (await setup.Store.GetForUserAsync("alice", 10, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task ConfirmedTransientFailureIsReturnedAndRemainsReviewable()
    {
        using var directory = new TestDirectory();
        var provider = new BehaviorProvider { Failure = new ProviderException("try again", true) };
        var setup = Create(directory.Path, provider);

        var result = await setup.Executor.ExecuteAsync(Change(ChangeKind.Advance), true, default);

        result.Kind.Should().Be(SyncOutcomeKind.TransientFailure);
        result.Message.Should().Be("try again");
        (await setup.Store.GetForUserAsync("alice", default)).Should().ContainSingle();
    }

    [Fact]
    public async Task ProviderAcknowledgementMustMatchFreshReadBackBeforeReportingApplied()
    {
        using var directory = new TestDirectory();
        var provider = new BehaviorProvider
        {
            ReadBack = new ProviderListState(ProviderKey.AniList, 99, "Series", 1, 12,
                CanonicalListStatus.Watching, null),
        };
        var setup = Create(directory.Path, provider);

        var result = await setup.Executor.ExecuteAsync(Change(ChangeKind.Advance), true, default);

        result.Kind.Should().Be(SyncOutcomeKind.TransientFailure);
        result.Message.Should().Contain("read-back verification");
        (await setup.Store.GetForUserAsync("alice", default)).Should().ContainSingle();
    }

    private static Setup Create(string path, BehaviorProvider provider)
    {
        var store = new JsonPluginStateStore(path, NullLogger<JsonPluginStateStore>.Instance);
        return new(new SyncExecutor(new ProviderRegistry([provider]), store, new Clock(), new NullDiagnostics(),
            NullLogger<SyncExecutor>.Instance), store);
    }

    private static PlannedChange Change(ChangeKind kind, ReviewReason reason = ReviewReason.None) => new(
        Guid.NewGuid(), "alice", 1, 2, "Series", ProviderKey.AniList, 99, kind, reason,
        1, 2, CanonicalListStatus.Watching, CanonicalListStatus.Watching,
        null, null, "token", DateTimeOffset.UtcNow);

    private sealed record Setup(SyncExecutor Executor, JsonPluginStateStore Store);

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class BehaviorProvider : ISyncProvider
    {
        public ProviderKey Key => ProviderKey.AniList;
        public ProviderException? Failure { get; init; }
        public ProviderListState? ReadBack { get; init; }
        public int ApplyCount { get; private set; }
        private ProviderListState? _acknowledged;
        public Task<ProviderListState> ApplyAsync(string shokoUsername, PlannedChange change, CancellationToken cancellationToken)
        {
            ApplyCount++;
            if (Failure is not null) throw Failure;
            _acknowledged = new ProviderListState(Key, 99, "Series", 2, 12,
                CanonicalListStatus.Watching, null);
            return Task.FromResult(_acknowledged);
        }
        public Task<ProviderAccount?> GetAccountAsync(string shokoUsername, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProviderListState?> GetEntryAsync(string shokoUsername, int mediaId, CancellationToken cancellationToken) =>
            Task.FromResult(ReadBack ?? _acknowledged);
        public Task<IReadOnlyDictionary<int, ProviderListState>> GetListAsync(string shokoUsername, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderMediaSearchResult>> SearchAsync(string shokoUsername, string query, bool includeAdult, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
