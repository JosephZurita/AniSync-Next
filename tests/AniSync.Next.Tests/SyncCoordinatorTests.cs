using AniSync.Next.Application;
using AniSync.Next.Configuration;
using AniSync.Next.Domain;
using AniSync.Next.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniSync.Next.Tests;

public sealed class SyncCoordinatorTests
{
    [Fact]
    public async Task RefreshRecomputesWatchAndUnwatchFromFreshShokoState()
    {
        using var directory = new TestDirectory();
        var source = new MutableShokoReader(State(5));
        var provider = new FakeProvider(ProviderState(2));
        var setup = Create(directory.Path, source, provider);

        var watched = await setup.Coordinator.RefreshAsync("alice", default);
        watched.Items.Should().ContainSingle().Which.Change.Kind.Should().Be(ChangeKind.Advance);

        source.Current = State(2);
        var unwatched = await setup.Coordinator.RefreshAsync("alice", default);

        unwatched.Items.Should().BeEmpty("the provider and fresh Shoko state now agree");
        (await setup.Store.GetForUserAsync("alice", default)).Should().BeEmpty("stale preview rows are replaced");
    }

    [Fact]
    public async Task RefreshDoesNotShowProviderRatingWhenShokoSeriesIsUnrated()
    {
        using var directory = new TestDirectory();
        var source = new MutableShokoReader(State(5) with { RatingRaw = null });
        var provider = new FakeProvider(ProviderState(5) with { RatingRaw = 80 });
        var setup = Create(directory.Path, source, provider);

        var preview = await setup.Coordinator.RefreshAsync("alice", default);

        preview.Items.Should().BeEmpty("an absent Shoko rating must preserve the provider score");
        (await setup.Store.GetForUserAsync("alice", default)).Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshVerifiesAnApparentlyMissingProviderEntryBeforeOfferingAnAddition()
    {
        using var directory = new TestDirectory();
        var source = new MutableShokoReader(State(12));
        var provider = new FakeProvider(ProviderState(12) with
        {
            Provider = ProviderKey.MyAnimeList,
            Status = CanonicalListStatus.Completed,
        })
        {
            OmitFromList = true,
        };
        var setup = Create(directory.Path, source, provider);

        var preview = await setup.Coordinator.RefreshAsync("alice", default);

        preview.Items.Should().BeEmpty("the per-title provider state confirms the entry is already complete");
        provider.EntryRequests.Should().Be(1);
        (await setup.Store.GetForUserAsync("alice", default)).Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshKeepsAnAdditionWhenDirectVerificationConfirmsTheEntryIsAbsent()
    {
        using var directory = new TestDirectory();
        var source = new MutableShokoReader(State(12));
        var provider = new FakeProvider(ProviderState(0) with { Provider = ProviderKey.MyAnimeList })
        {
            OmitFromList = true,
            EntryExists = false,
        };
        var setup = Create(directory.Path, source, provider);

        var preview = await setup.Coordinator.RefreshAsync("alice", default);

        preview.Items.Should().ContainSingle().Which.Change.Kind.Should().Be(ChangeKind.Complete);
        provider.EntryRequests.Should().Be(1);
    }

    [Fact]
    public async Task ApplyRejectsPreviewWhenShokoStateChangedAfterRefresh()
    {
        using var directory = new TestDirectory();
        var source = new MutableShokoReader(State(5));
        var provider = new FakeProvider(ProviderState(2));
        var setup = Create(directory.Path, source, provider);
        var preview = await setup.Coordinator.RefreshAsync("alice", default);
        source.Current = State(6);

        var action = () => setup.Coordinator.ApplyAsync("alice", [preview.Items.Single().Id], default);

        await action.Should().ThrowAsync<StalePreviewException>()
            .WithMessage("*changed after the preview*");
        provider.Applied.Should().BeEmpty();
    }

    [Fact]
    public async Task AutomaticSyncQueuesDecreaseInsteadOfApplyingIt()
    {
        using var directory = new TestDirectory();
        var source = new MutableShokoReader(State(3));
        var provider = new FakeProvider(ProviderState(7));
        var setup = Create(directory.Path, source, provider);

        await setup.Coordinator.ProcessSeriesAsync("alice", 1, default);

        provider.Applied.Should().BeEmpty();
        (await setup.Store.GetForUserAsync("alice", default)).Should().ContainSingle()
            .Which.Change.ReviewReason.Should().Be(ReviewReason.ProgressDecrease);
    }

    [Fact]
    public async Task TransientFailureInOneProviderDoesNotPreventTheOtherProvider()
    {
        using var directory = new TestDirectory();
        var store = new JsonPluginStateStore(directory.Path, NullLogger<JsonPluginStateStore>.Instance);
        var source = new MutableShokoReader(State(5));
        var mal = new FakeProvider(ProviderState(2) with { Provider = ProviderKey.MyAnimeList })
        {
            Failure = new ProviderException("temporary", true),
        };
        var aniList = new FakeProvider(ProviderState(2));
        var config = new FakeConfiguration();
        config.SaveAuthorization("alice", ProviderKey.MyAnimeList, new ProviderAuthorization { AccessToken = "mal" });
        config.SaveAuthorization("alice", ProviderKey.AniList, new ProviderAuthorization { AccessToken = "al" });
        var registry = new ProviderRegistry([mal, aniList]);
        var clock = new FixedClock();
        var coordinator = new SyncCoordinator(source, new AllMappings(), registry, new SyncPlanner(),
            new SyncExecutor(registry, store, clock), store, config, clock, NullLogger<SyncCoordinator>.Instance);

        var action = () => coordinator.ProcessSeriesAsync("alice", 1, default);

        await action.Should().ThrowAsync<ProviderException>().Where(error => error.IsTransient);
        mal.Applied.Should().ContainSingle();
        aniList.Applied.Should().ContainSingle("providers are failure-isolated within a trigger");
    }

    [Fact]
    public async Task RefreshReportsOneProviderFailureAndStillPreviewsTheOtherProvider()
    {
        using var directory = new TestDirectory();
        var store = new JsonPluginStateStore(directory.Path, NullLogger<JsonPluginStateStore>.Instance);
        var source = new MutableShokoReader(State(5));
        var mal = new FakeProvider(ProviderState(2) with { Provider = ProviderKey.MyAnimeList })
        {
            ListFailure = new ProviderException("MyAnimeList must be reconnected.", false),
        };
        var aniList = new FakeProvider(ProviderState(2));
        var config = new FakeConfiguration();
        config.SaveAuthorization("alice", ProviderKey.MyAnimeList, new ProviderAuthorization { AccessToken = "mal" });
        config.SaveAuthorization("alice", ProviderKey.AniList, new ProviderAuthorization { AccessToken = "al" });
        var registry = new ProviderRegistry([mal, aniList]);
        var clock = new FixedClock();
        var coordinator = new SyncCoordinator(source, new AllMappings(), registry, new SyncPlanner(),
            new SyncExecutor(registry, store, clock), store, config, clock, NullLogger<SyncCoordinator>.Instance);

        var result = await coordinator.RefreshAsync("alice", default);

        result.Items.Should().ContainSingle().Which.Change.Provider.Should().Be(ProviderKey.AniList);
        result.Failures.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Provider = ProviderKey.MyAnimeList,
            Error = "MyAnimeList must be reconnected.",
            IsTransient = false,
        });
        (await store.GetForUserAsync("alice", default)).Should().ContainSingle()
            .Which.Change.Provider.Should().Be(ProviderKey.AniList);
    }

    [Fact]
    public async Task RefreshContainsUnexpectedProviderPayloadFailures()
    {
        using var directory = new TestDirectory();
        var provider = new FakeProvider(ProviderState(2))
        {
            ListFailure = new InvalidOperationException("malformed provider payload"),
        };
        var setup = Create(directory.Path, new MutableShokoReader(State(5)), provider);

        var result = await setup.Coordinator.RefreshAsync("alice", default);

        result.Items.Should().BeEmpty();
        result.Failures.Should().ContainSingle().Which.Error.Should()
            .Be("AniList returned an unexpected response. Check the Shoko logs for details.");
    }

    private static Setup Create(string path, MutableShokoReader reader, FakeProvider provider)
    {
        var config = new FakeConfiguration();
        config.SaveAuthorization("alice", provider.Key, new ProviderAuthorization { AccessToken = "token" });
        var store = new JsonPluginStateStore(path, NullLogger<JsonPluginStateStore>.Instance);
        var registry = new ProviderRegistry([provider]);
        var planner = new SyncPlanner();
        var clock = new FixedClock();
        var mappings = new FixedMappings(provider.Key);
        var executor = new SyncExecutor(registry, store, clock);
        var coordinator = new SyncCoordinator(reader, mappings, registry, planner, executor, store, config, clock,
            NullLogger<SyncCoordinator>.Instance);
        return new Setup(coordinator, store);
    }

    private static ShokoSeriesState State(int progress) => new("alice", 1, 100, "Series", progress, 12, 70);
    private static ProviderListState ProviderState(int progress) => new(ProviderKey.AniList, 99,
        "Series", progress, 12, CanonicalListStatus.Watching, 70);

    private sealed record Setup(SyncCoordinator Coordinator, JsonPluginStateStore Store);

    private sealed class MutableShokoReader(ShokoSeriesState current) : IShokoStateReader
    {
        public ShokoSeriesState Current { get; set; } = current;
        public Task<ShokoSeriesState?> GetSeriesStateAsync(string shokoUsername, int seriesId, CancellationToken cancellationToken) => Task.FromResult<ShokoSeriesState?>(Current);
        public Task<IReadOnlyList<ShokoSeriesState>> GetLibraryStateAsync(string shokoUsername, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ShokoSeriesState>>([Current]);
    }

    private sealed class FixedMappings(ProviderKey provider) : IMappingResolver
    {
        public Task<ProviderMapping?> ResolveAsync(ShokoSeriesState source, ProviderKey key, CancellationToken cancellationToken) =>
            Task.FromResult<ProviderMapping?>(key == provider ? new ProviderMapping(source.ShokoUsername,
                source.AniDbAnimeId, key, 99, source.Title, true, DateTimeOffset.UtcNow) : null);
        public Task SaveAsync(ProviderMapping mapping, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<ProviderMapping>> GetForUserAsync(string shokoUsername, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProviderMapping>>([]);
        public Task RemoveAsync(string shokoUsername, int aniDbAnimeId, ProviderKey key, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AllMappings : IMappingResolver
    {
        public Task<ProviderMapping?> ResolveAsync(ShokoSeriesState source, ProviderKey key, CancellationToken cancellationToken) =>
            Task.FromResult<ProviderMapping?>(new(source.ShokoUsername, source.AniDbAnimeId, key, 99,
                source.Title, true, DateTimeOffset.UtcNow));
        public Task SaveAsync(ProviderMapping mapping, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<ProviderMapping>> GetForUserAsync(string shokoUsername, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProviderMapping>>([]);
        public Task RemoveAsync(string shokoUsername, int aniDbAnimeId, ProviderKey key, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeProvider(ProviderListState state) : ISyncProvider
    {
        public ProviderKey Key => state.Provider;
        public List<PlannedChange> Applied { get; } = [];
        public ProviderException? Failure { get; init; }
        public Exception? ListFailure { get; init; }
        public bool OmitFromList { get; init; }
        public bool EntryExists { get; init; } = true;
        public int EntryRequests { get; private set; }
        public Task<ProviderAccount?> GetAccountAsync(string shokoUsername, CancellationToken cancellationToken) => Task.FromResult<ProviderAccount?>(new(1, "remote"));
        public Task<IReadOnlyDictionary<int, ProviderListState>> GetListAsync(string shokoUsername, CancellationToken cancellationToken) =>
            ListFailure is not null
                ? Task.FromException<IReadOnlyDictionary<int, ProviderListState>>(ListFailure)
                : Task.FromResult<IReadOnlyDictionary<int, ProviderListState>>(OmitFromList
                    ? new Dictionary<int, ProviderListState>()
                    : new Dictionary<int, ProviderListState> { [state.MediaId] = state });
        public Task<ProviderListState?> GetEntryAsync(string shokoUsername, int mediaId, CancellationToken cancellationToken)
        {
            EntryRequests++;
            return Task.FromResult(EntryExists ? state : null);
        }
        public Task<IReadOnlyList<ProviderMediaSearchResult>> SearchAsync(string shokoUsername, string query, bool includeAdult, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProviderMediaSearchResult>>([]);
        public Task<ProviderListState> ApplyAsync(string shokoUsername, PlannedChange change, CancellationToken cancellationToken)
        {
            Applied.Add(change);
            if (Failure is not null) throw Failure;
            return Task.FromResult(state with { Progress = change.AfterProgress, Status = change.AfterStatus, RatingRaw = change.AfterRatingRaw });
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    }
}
