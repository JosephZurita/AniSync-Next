using AniSync.Next.Domain;
using FluentAssertions;

namespace AniSync.Next.Tests;

public sealed class SyncPlannerTests
{
    private readonly SyncPlanner _planner = new();
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ForwardProgressIsSafeAndActionable()
    {
        var result = Plan(Source(progress: 5), Destination(progress: 3));

        result.Kind.Should().Be(ChangeKind.Advance);
        result.RequiresReview.Should().BeFalse();
        result.BeforeProgress.Should().Be(3);
        result.AfterProgress.Should().Be(5);
    }

    [Fact]
    public void CompletionSetsCompletedStatus()
    {
        var result = Plan(Source(progress: 12), Destination(progress: 11));

        result.Kind.Should().Be(ChangeKind.Complete);
        result.AfterStatus.Should().Be(CanonicalListStatus.Completed);
    }

    [Fact]
    public void IdenticalStateIsNoOp()
    {
        Plan(Source(progress: 5), Destination(progress: 5)).Kind.Should().Be(ChangeKind.NoChange);
    }

    [Fact]
    public void UnwatchBecomesExplicitDecreaseReview()
    {
        var result = Plan(Source(progress: 3), Destination(progress: 7));

        result.Kind.Should().Be(ChangeKind.Decrease);
        result.ReviewReason.Should().Be(ReviewReason.ProgressDecrease);
        result.RequiresReview.Should().BeTrue();
    }

    [Fact]
    public void MissingMappingIsReviewOnlyAndNotActionable()
    {
        var result = _planner.Plan(Source(progress: 4), ProviderKey.AniList, null, null,
            "snapshot", Now, false, true);

        result.Kind.Should().Be(ChangeKind.UnresolvedMapping);
        result.IsActionable.Should().BeFalse();
        result.ReviewReason.Should().Be(ReviewReason.MissingMapping);
    }

    [Fact]
    public void RatingDifferenceUsesCanonicalRawScore()
    {
        var result = Plan(Source(progress: 5, rating: 83), Destination(progress: 5, rating: 70));

        result.Kind.Should().Be(ChangeKind.Rating);
        result.BeforeRatingRaw.Should().Be(70);
        result.AfterRatingRaw.Should().Be(83);
    }

    [Fact]
    public void RatingDoesNotCreateAnUnwatchedEntryWithoutReview()
    {
        var result = Plan(Source(progress: 0, rating: 80), null);

        result.Kind.Should().Be(ChangeKind.Rating);
        result.ReviewReason.Should().Be(ReviewReason.RatingWouldCreateEntry);
    }

    [Fact]
    public void CompletionOnlyBlocksProgressButNotRatingOnExistingEntry()
    {
        var result = Plan(Source(progress: 5, rating: 90), Destination(progress: 2, rating: 70),
            syncOnlyOnCompletion: true);

        result.Kind.Should().Be(ChangeKind.Rating);
        result.BeforeProgress.Should().Be(2);
        result.AfterProgress.Should().Be(2);
        result.AfterStatus.Should().Be(result.BeforeStatus);
    }

    [Fact]
    public void ProviderDivergenceIsPlannedIndependently()
    {
        var source = Source(progress: 6);
        var mal = _planner.Plan(source, ProviderKey.MyAnimeList, 10, Destination(ProviderKey.MyAnimeList, 10, 6),
            "mal", Now, false, true);
        var aniList = _planner.Plan(source, ProviderKey.AniList, 20, Destination(ProviderKey.AniList, 20, 2),
            "anilist", Now, false, true);

        mal.Kind.Should().Be(ChangeKind.NoChange);
        aniList.Kind.Should().Be(ChangeKind.Advance);
    }

    [Fact]
    public void SnapshotTokenChangesForWatchUnwatchProviderOrRatingState()
    {
        var baseline = SyncPlanner.CreateSnapshotToken(Source(progress: 5), Destination(progress: 5));

        baseline.Should().NotBe(SyncPlanner.CreateSnapshotToken(Source(progress: 4), Destination(progress: 5)));
        baseline.Should().NotBe(SyncPlanner.CreateSnapshotToken(Source(progress: 5, rating: 90), Destination(progress: 5)));
        baseline.Should().NotBe(SyncPlanner.CreateSnapshotToken(Source(progress: 5), Destination(progress: 6)));
    }

    [Fact]
    public void UnwatchedUnratedSeriesDoesNotCreateProviderEntry()
    {
        Plan(Source(progress: 0, rating: null), null).Kind.Should().Be(ChangeKind.NoChange);
    }

    [Fact]
    public void CompletionOnlyDoesNotCreateIncompleteProviderEntry()
    {
        Plan(Source(progress: 4), null, syncOnlyOnCompletion: true).Kind.Should().Be(ChangeKind.NoChange);
    }

    [Fact]
    public void StatusDivergenceWithSameProgressIsCorrected()
    {
        var destination = Destination(progress: 12) with { Status = CanonicalListStatus.Watching };
        Plan(Source(progress: 12), destination).Kind.Should().Be(ChangeKind.Complete);
    }

    [Fact]
    public void DisabledRatingSyncProducesNoChange()
    {
        var source = Source(progress: 5, rating: 100);
        var destination = Destination(progress: 5, rating: 20);
        var result = _planner.Plan(source, ProviderKey.AniList, 99, destination, "snapshot", Now,
            false, false);

        result.Kind.Should().Be(ChangeKind.NoChange);
    }

    [Fact]
    public void DisabledRatingSyncDoesNotPiggybackRatingOntoProgressUpdate()
    {
        var result = _planner.Plan(Source(progress: 6, rating: 100), ProviderKey.AniList, 99,
            Destination(progress: 5, rating: 20), "snapshot", Now, false, false);

        result.Kind.Should().Be(ChangeKind.Advance);
        result.AfterRatingRaw.Should().Be(20);
    }

    private PlannedChange Plan(
        ShokoSeriesState source,
        ProviderListState? destination,
        bool syncOnlyOnCompletion = false) => _planner.Plan(
            source, ProviderKey.AniList, 99, destination, "snapshot", Now,
            syncOnlyOnCompletion, true);

    private static ShokoSeriesState Source(int progress, int? rating = 70) =>
        new("alice", 1, 2, "Series", progress, 12, rating);

    private static ProviderListState Destination(int progress, int? rating = 70) =>
        Destination(ProviderKey.AniList, 99, progress, rating);

    private static ProviderListState Destination(
        ProviderKey provider,
        int mediaId,
        int progress,
        int? rating = 70) => new(provider, mediaId, "Series", progress, 12,
        progress >= 12 ? CanonicalListStatus.Completed : CanonicalListStatus.Watching, rating);
}
