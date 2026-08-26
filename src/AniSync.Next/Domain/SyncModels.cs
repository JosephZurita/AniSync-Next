namespace AniSync.Next.Domain;

public sealed record ShokoSeriesState(
    string ShokoUsername,
    int SeriesId,
    int AniDbAnimeId,
    string Title,
    int Progress,
    int TotalEpisodes,
    int? RatingRaw,
    string? ImageUrl = null)
{
    public CanonicalListStatus DesiredStatus => Progress <= 0
        ? CanonicalListStatus.Planning
        : TotalEpisodes > 0 && Progress >= TotalEpisodes
            ? CanonicalListStatus.Completed
            : CanonicalListStatus.Watching;
}

public sealed record ProviderListState(
    ProviderKey Provider,
    int MediaId,
    string Title,
    int Progress,
    int TotalEpisodes,
    CanonicalListStatus Status,
    int? RatingRaw,
    bool Exists = true);

public sealed record PlannedChange(
    Guid Id,
    string ShokoUsername,
    int SeriesId,
    int AniDbAnimeId,
    string Title,
    ProviderKey Provider,
    int? ProviderMediaId,
    ChangeKind Kind,
    ReviewReason ReviewReason,
    int BeforeProgress,
    int AfterProgress,
    CanonicalListStatus? BeforeStatus,
    CanonicalListStatus AfterStatus,
    int? BeforeRatingRaw,
    int? AfterRatingRaw,
    string SnapshotToken,
    DateTimeOffset CreatedAt,
    string? ImageUrl = null,
    Guid? GroupId = null)
{
    public bool RequiresReview => ReviewReason != ReviewReason.None;
    public bool IsActionable => Kind is not ChangeKind.NoChange and not ChangeKind.UnresolvedMapping;
}

public sealed record ReviewItem(
    Guid Id,
    PlannedChange Change,
    DateTimeOffset UpdatedAt,
    string? Error = null,
    int AttemptCount = 0);

public sealed record SyncOutcome(
    SyncOutcomeKind Kind,
    PlannedChange Change,
    string? Message = null,
    DateTimeOffset? CompletedAt = null,
    Guid? GroupId = null);

public sealed record ProviderMediaSearchResult(
    ProviderKey Provider,
    int MediaId,
    string Title,
    int TotalEpisodes,
    int? StartYear,
    string? ImageUrl);

public sealed record ProviderMapping(
    string ShokoUsername,
    int AniDbAnimeId,
    ProviderKey Provider,
    int MediaId,
    string MediaTitle,
    bool IsUserVerified,
    DateTimeOffset UpdatedAt);
